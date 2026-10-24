using System.Security.Cryptography;
using Amazon.CodeBuild;
using Amazon.EC2;
using Amazon.IdentityManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Core.Config;
using CodeBuild = Amazon.CodeBuild.Model;
using Ec2 = Amazon.EC2.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// GATE 2'S RESOURCES IN THE TARGET ACCOUNT (DecoupledCd.md §15.5, V-E), under <c>Pipeline.Gate2: provisioned</c> or <c>on</c>:
/// the results bucket and its expiry, the build's log group, role, security group and CodeBuild project in the private subnets,
/// and the staged Icecat fixture cache. Each is created or brought to the plan and read back. The decisions are pure and
/// tested; the calls around them are thin.
/// </summary>
public static class Gate2Bootstrapper
{
    // ---------------------------------------------------------------- inputs

    /// <summary>The hosts the tiers test: the one tenant's root domain, and its one subtenant's host on it.</summary>
    public static (string BaseHost, string ApexHost) HostsFor(IReadOnlyList<(string TenantKey, TenantConfig Config)> tenants)
    {
        if (tenants.Count != 1)
            throw new InvalidOperationException($"lz bootstrapdeployer refuses: Gate 2 tests one tenant's hosts, and {tenants.Count} tenant(s) are configured here.");
        var (key, tenant) = tenants[0];
        var apex = tenant.RootDomain?.ToLowerInvariant() ?? throw new InvalidOperationException($"lz bootstrapdeployer refuses: tenant {key} names no RootDomain.");
        var subtenants = tenant.Subtenants ?? new Dictionary<string, SubtenantEntry>();
        if (subtenants.Count != 1)
            throw new InvalidOperationException($"lz bootstrapdeployer refuses: Gate 2 tests one subtenant's host, and tenant {key} has {subtenants.Count}.");
        var (subKey, sub) = subtenants.Single();
        var label = string.IsNullOrWhiteSpace(sub.SubDomain) ? subKey : sub.SubDomain;
        return ($"{label.ToLowerInvariant()}.{apex}", apex);
    }

    /// <summary>The private subnets the network component tags <c>{sk}-private-subnet-*</c>, all in one VPC.</summary>
    public static (string VpcId, IReadOnlyList<string> SubnetIds) PrivateSubnetsFor(string sk, IReadOnlyList<(string Id, string VpcId, string? Name)> subnets)
    {
        var mine = subnets.Where(s => s.Name?.StartsWith($"{sk}-private-subnet-", StringComparison.Ordinal) == true).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        if (mine.Count == 0)
            throw new InvalidOperationException($"lz bootstrapdeployer refuses: no subnet here is tagged {sk}-private-subnet-*, so Gate 2's build has no private subnet to leave through the NAT from.");
        var vpcs = mine.Select(s => s.VpcId).Distinct().ToList();
        if (vpcs.Count != 1)
            throw new InvalidOperationException($"lz bootstrapdeployer refuses: the {sk}-private-subnet-* subnets are in {vpcs.Count} VPCs ({string.Join(", ", vpcs)}).");
        return (vpcs[0], mine.Select(s => s.Id).ToList());
    }

    public static async Task<DeployerGate2Inputs> ResolveInputsAsync(SystemConfig config, IAmazonEC2 ec2)
    {
        List<(string TenantKey, TenantConfig Config)> tenants;
        try { tenants = ConfigResolver.ResolveTenantConfigs(config.SystemKey, config.Environment); }
        catch (FileNotFoundException) { tenants = new(); }
        var (baseHost, apex) = HostsFor(tenants);

        var described = await ec2.DescribeSubnetsAsync(new Ec2.DescribeSubnetsRequest
        {
            Filters = new List<Ec2.Filter> { new() { Name = "tag:Name", Values = new List<string> { $"{config.SystemKey}-private-subnet-*" } } },
        });
        var (vpc, subnets) = PrivateSubnetsFor(config.SystemKey, (described.Subnets ?? new List<Ec2.Subnet>())
            .Select(s => (s.SubnetId, s.VpcId, (s.Tags ?? new List<Ec2.Tag>()).FirstOrDefault(t => t.Key == "Name")?.Value)).ToList());
        return new DeployerGate2Inputs(baseHost, apex, vpc, subnets);
    }

    // ---------------------------------------------------------------- the staged cache

    /// <summary>
    /// The cache files to upload: those missing from the staged copy, or there with other bytes (the staged object's ETag, which
    /// for a single-part upload under SSE-S3 is its MD5). Nothing is ever deleted: a GTIN dropped from the list leaves a file
    /// no test reads.
    /// </summary>
    public static IReadOnlyList<string> ToStage(IReadOnlyDictionary<string, string> localMd5, IReadOnlyDictionary<string, string> stagedETags)
        => localMd5.Where(l => !stagedETags.TryGetValue(l.Key, out var etag) || !string.Equals(etag.Trim('"'), l.Value, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>The workspace's own cache, which the e2e tier reads on a workstation (E2ETestPlan Q2: fetched, never committed).</summary>
    public static string LocalCacheDir(string workspace) => Path.Combine(workspace, "repos", "Service", "ServiceTest", "E2E", "Fixtures", "cache");

    // ---------------------------------------------------------------- the project

    /// <summary>The project as CodeBuild is asked for it: no source, no artifacts, the fixed buildspec and variables, in the private subnets.</summary>
    public static (CodeBuild.ProjectSource Source, CodeBuild.ProjectArtifacts Artifacts, CodeBuild.ProjectEnvironment Environment,
        CodeBuild.VpcConfig Vpc, CodeBuild.LogsConfig Logs) ProjectSettings(DeployerGate2Project p, string securityGroupId) => (
        new CodeBuild.ProjectSource { Type = SourceType.NO_SOURCE, Buildspec = p.Buildspec },
        new CodeBuild.ProjectArtifacts { Type = ArtifactsType.NO_ARTIFACTS },
        new CodeBuild.ProjectEnvironment
        {
            Type = EnvironmentType.LINUX_CONTAINER, Image = p.Image, ComputeType = new ComputeType(p.ComputeType), PrivilegedMode = false,
            ImagePullCredentialsType = ImagePullCredentialsType.CODEBUILD,
            EnvironmentVariables = p.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new CodeBuild.EnvironmentVariable { Name = e.Key, Value = e.Value, Type = EnvironmentVariableType.PLAINTEXT }).ToList(),
        },
        new CodeBuild.VpcConfig { VpcId = p.VpcId, Subnets = p.SubnetIds.ToList(), SecurityGroupIds = new List<string> { securityGroupId } },
        new CodeBuild.LogsConfig { CloudWatchLogs = new CodeBuild.CloudWatchLogsConfig { Status = LogsConfigStatusType.ENABLED, GroupName = p.LogGroupName } });

    // ---------------------------------------------------------------- the apply

    public static async Task ApplyAsync(
        PipelineDeployer plan, string region, IAmazonS3 s3, IAmazonIdentityManagementService iam, IAmazonEC2 ec2, IAmazonCodeBuild codeBuild,
        Amazon.CloudWatchLogs.IAmazonCloudWatchLogs logs, int logRetentionDays, string localCacheDir)
    {
        var g = plan.Gate2 ?? throw new InvalidOperationException("no Gate 2 in this plan");
        var p = g.Project;

        await EnsureResultsBucketAsync(s3, g.ResultsBucket, region, p.ResultsExpiry);
        await StageFixturesAsync(s3, g.ResultsBucket, localCacheDir);
        await DeployerBootstrapper.EnsureLogGroupAsync(logs, p.LogGroupName, logRetentionDays);
        var roleArn = await DeployerBootstrapper.EnsureRoleAsync(iam, p.RoleName, p.TrustPolicy,
            "lz Gate 2 build (DecoupledCd.md V-E): its logs, its results, the test logins and the e2e tier's Cognito calls.",
            ($"{p.RoleName}-grant", p.RolePolicy), plan.DenyPolicy);
        var securityGroup = await EnsureSecurityGroupAsync(ec2, p.SecurityGroupName, p.VpcId);
        var (source, artifacts, environment, vpc, logsConfig) = ProjectSettings(p, securityGroup);

        var existing = (await codeBuild.BatchGetProjectsAsync(new CodeBuild.BatchGetProjectsRequest { Names = new List<string> { g.ProjectName } })).Projects ?? new List<CodeBuild.Project>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (existing.Count == 0)
                    await codeBuild.CreateProjectAsync(new CodeBuild.CreateProjectRequest
                    {
                        Name = g.ProjectName, Description = "lz Gate 2 (DecoupledCd.md V-E): the e2e and browser tiers against this environment, fixed buildspec.",
                        Source = source, Artifacts = artifacts, Environment = environment, ServiceRole = roleArn, VpcConfig = vpc,
                        TimeoutInMinutes = p.TimeoutMinutes, LogsConfig = logsConfig,
                    });
                else
                    await codeBuild.UpdateProjectAsync(new CodeBuild.UpdateProjectRequest
                    {
                        Name = g.ProjectName, Source = source, Artifacts = artifacts, Environment = environment, ServiceRole = roleArn, VpcConfig = vpc,
                        TimeoutInMinutes = p.TimeoutMinutes, LogsConfig = logsConfig,
                    });
                break;
            }
            // A role created seconds ago is not yet assumable, nor yet holding its VPC grant (measured on Gate 1's for the first).
            catch (CodeBuild.InvalidInputException ex) when (attempt < 10 &&
                (ex.Message.Contains("sts:AssumeRole", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ec2:", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        var read = (await codeBuild.BatchGetProjectsAsync(new CodeBuild.BatchGetProjectsRequest { Names = new List<string> { g.ProjectName } })).Projects.Single();
        if (read.Source?.Buildspec != p.Buildspec || read.ServiceRole != roleArn || read.VpcConfig?.VpcId != p.VpcId
            || !(read.VpcConfig?.Subnets ?? new List<string>()).Order().SequenceEqual(p.SubnetIds.Order())
            || read.TimeoutInMinutes != p.TimeoutMinutes)
            throw new InvalidOperationException($"Gate 2's project '{g.ProjectName}' does not read back as planned (buildspec, role, VPC, subnets or timeout).");
        Console.WriteLine($"  project '{g.ProjectName}' {(existing.Count == 0 ? "created" : "updated")} and read back: {p.Image}, {p.ComputeType}, in {p.VpcId} " +
                          $"({string.Join(", ", p.SubnetIds)}) with {securityGroup}, {p.TimeoutMinutes} min, buildspec as planned.");
    }

    private static async Task EnsureResultsBucketAsync(IAmazonS3 s3, string bucket, string region, IReadOnlyList<Gate2Expiry> expiry)
    {
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
            Console.WriteLine($"  results bucket '{bucket}' already exists.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await s3.PutBucketAsync(S3BucketRequests.Create(bucket, region));
            Console.WriteLine($"  results bucket '{bucket}' created.");
        }
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration { BlockPublicAcls = true, BlockPublicPolicy = true, IgnorePublicAcls = true, RestrictPublicBuckets = true },
        });
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules = expiry.Select(e => new LifecycleRule
                {
                    Id = $"expire-{e.Prefix.TrimEnd('/')}",
                    Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = e.Prefix } },
                    Expiration = new LifecycleRuleExpiration { Days = e.Days },
                }).ToList(),
            },
        });
        Console.WriteLine($"      public access blocked; {string.Join(", ", expiry.Select(e => $"{e.Prefix} expires after {e.Days} days"))}; fixtures/ kept.");
    }

    private static async Task StageFixturesAsync(IAmazonS3 s3, string bucket, string localCacheDir)
    {
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = Gate2ProjectPlanner.FixturesPrefix, ContinuationToken = token });
            foreach (var o in page.S3Objects ?? new List<S3Object>()) staged[o.Key[Gate2ProjectPlanner.FixturesPrefix.Length..]] = o.ETag;
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);

        if (!Directory.Exists(localCacheDir))
        {
            if (staged.Count == 0)
                throw new InvalidOperationException(
                    $"lz bootstrapdeployer refuses: no Icecat fixture cache is staged in {bucket}/{Gate2ProjectPlanner.FixturesPrefix}, and none is at {localCacheDir} to stage. " +
                    "Fetch it there (ProductLoader icecat fetch, E2ETestPlan) and run again: seven blocking e2e tests read it.");
            Console.WriteLine($"  fixtures: {staged.Count} staged, and no local cache at {localCacheDir} to compare with; kept as they are.");
            return;
        }

        var local = Directory.GetFiles(localCacheDir, "*.json").ToDictionary(Path.GetFileName, f => Convert.ToHexString(MD5.HashData(File.ReadAllBytes(f))).ToLowerInvariant(), StringComparer.Ordinal)!;
        var upload = ToStage(local!, staged);
        foreach (var name in upload)
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Gate2ProjectPlanner.FixturesPrefix + name, FilePath = Path.Combine(localCacheDir, name), ContentType = "application/json" });
        Console.WriteLine($"  fixtures: {local.Count} in the local cache, {upload.Count} uploaded, {local.Count - upload.Count} already staged; nothing deleted.");
    }

    private static async Task<string> EnsureSecurityGroupAsync(IAmazonEC2 ec2, string name, string vpcId)
    {
        var found = await ec2.DescribeSecurityGroupsAsync(new Ec2.DescribeSecurityGroupsRequest
        {
            Filters = new List<Ec2.Filter> { new() { Name = "group-name", Values = new List<string> { name } }, new() { Name = "vpc-id", Values = new List<string> { vpcId } } },
        });
        if ((found.SecurityGroups ?? new List<Ec2.SecurityGroup>()).FirstOrDefault() is { } existing)
        {
            if ((existing.IpPermissions ?? new List<Ec2.IpPermission>()).Count > 0)
                throw new InvalidOperationException($"security group '{name}' ({existing.GroupId}) admits inbound traffic; Gate 2's build takes none. Remove its inbound rules.");
            Console.WriteLine($"  security group '{name}' already exists ({existing.GroupId}): no inbound rule.");
            return existing.GroupId;
        }
        var created = await ec2.CreateSecurityGroupAsync(new Ec2.CreateSecurityGroupRequest
        {
            GroupName = name, VpcId = vpcId,
            Description = "lz Gate 2 build (DecoupledCd V-E): outbound only, through the NAT",
            TagSpecifications = new List<Ec2.TagSpecification>
            {
                new() { ResourceType = ResourceType.SecurityGroup, Tags = new List<Ec2.Tag> { new() { Key = "Name", Value = name } } },
            },
        });
        Console.WriteLine($"  security group '{name}' created ({created.GroupId}): no inbound rule, the default outbound.");
        return created.GroupId;
    }
}
