using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Signer;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// V-C'S PROBE PUSH (DecoupledCd.md §15.7 V-C, §15.8 items 3 and 4), run once by hand with <c>lz probeintakepush</c>, before V-D
/// makes the push live. The intake's push function pushes one staged Service image into a PROBE repository in the build
/// account, outside the pipeline's repositories: tag-immutable like them, with its own signing profile and rule, its name
/// extending no pipeline repository's name, so no replication or signing filter matches it, nothing replicates it, no
/// record names it and no sweep reads it. It pushes once, then the same manifest and tag again without reading the tag, then
/// once with a wrong <c>imageDigest</c>, and reads back the digest ECR holds and the image's signing status. The probe
/// repository, its rule, and the function with its role and grants are then removed (the signing profile stays: Signer only
/// cancels one, and a cancelled name cannot be used again).
/// </summary>
public static class IntakePushProbe
{
    public const string Tag = "probe";
    public const string WrongTag = "probe-wrong";
    public static readonly string WrongDigest = "sha256:" + new string('0', 64);

    public static string RepositoryFor(SystemConfig c) => $"{c.SystemKey}-intake-probe-{PipelineBootstrapPlanner.BuildSuffixFor(c)}";
    public static string ProfileFor(SystemConfig c) => $"{c.SystemKey}_intake_probe";
    public static string FunctionFor(SystemConfig c) => $"{c.SystemKey}-intake-push";

    /// <summary>Whether a replication or signing filter would take the probe repository (PREFIX_MATCH; WILDCARD_MATCH with <c>*</c>).</summary>
    public static bool FilterMatches(string filterType, string filter, string repository)
        => filterType == "PREFIX_MATCH"
            ? repository.StartsWith(filter, StringComparison.Ordinal)
            : System.Text.RegularExpressions.Regex.IsMatch(repository,
                "^" + string.Join(".*", filter.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$");

    public static async Task<JsonObject> RunAsync(SystemConfig config, string execution, bool keep)
    {
        var region = config.Region;
        var creds = AwsCredentialsFactory.ResolveOrThrow(config.Profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        T Client<T>(Func<Amazon.Runtime.AWSCredentials, Amazon.RegionEndpoint, T> withCreds, Func<Amazon.RegionEndpoint, T> ambient)
            => creds != null ? withCreds(creds, endpoint) : ambient(endpoint);

        using var s3 = Client<IAmazonS3>((c, e) => new AmazonS3Client(c, e), e => new AmazonS3Client(e));
        using var ecr = Client<IAmazonECR>((c, e) => new AmazonECRClient(c, e), e => new AmazonECRClient(e));
        using var signer = Client<IAmazonSigner>((c, e) => new AmazonSignerClient(c, e), e => new AmazonSignerClient(e));
        using var iam = Client<IAmazonIdentityManagementService>((c, e) => new AmazonIdentityManagementServiceClient(c, e), e => new AmazonIdentityManagementServiceClient(e));
        using var logs = Client<Amazon.CloudWatchLogs.IAmazonCloudWatchLogs>((c, e) => new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(c, e), e => new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(e));
        // A push takes minutes; the invoke waits for it.
        var lambdaConfig = new AmazonLambdaConfig { RegionEndpoint = endpoint, Timeout = TimeSpan.FromMinutes(16) };
        using var lambda = creds != null ? new AmazonLambdaClient(creds, lambdaConfig) : new AmazonLambdaClient(lambdaConfig);

        var account = await DeployerBootstrapper.ResolveAccountAsync(config.Profile, region);
        var plan = IntakeBuildPlanner.Plan(config, account) ?? throw new InvalidOperationException("this config has no intake to probe");
        var (repository, profile, function) = (RepositoryFor(config), ProfileFor(config), FunctionFor(config));
        var report = new JsonObject { ["execution"] = execution, ["repository"] = repository, ["signingProfile"] = profile };
        Console.WriteLine($"=== Probe push: {execution} -> {repository} (account {account}) ===");

        // NOTHING ELSE MAY TAKE THE PROBE (§14.4): no replication or signing filter of the registry matches its name.
        var registry = await ecr.DescribeRegistryAsync(new DescribeRegistryRequest());
        var replicating = (registry.ReplicationConfiguration?.Rules ?? new List<Amazon.ECR.Model.ReplicationRule>())
            .SelectMany(r => r.RepositoryFilters ?? new List<Amazon.ECR.Model.RepositoryFilter>())
            .Where(f => FilterMatches(f.FilterType.Value, f.Filter, repository)).Select(f => f.Filter).ToList();
        var signing = (await PipelineBootstrapper.SigningRulesAsync(ecr))
            .SelectMany(r => r.RepositoryFilters ?? new List<SigningRepositoryFilter>())
            .Where(f => FilterMatches(f.FilterType.Value, f.Filter, repository)).Select(f => f.Filter).ToList();
        if (replicating.Count + signing.Count > 0)
            throw new InvalidOperationException($"{repository} would be taken by the registry's filters {string.Join(", ", replicating.Concat(signing))}; the probe must match none");
        Console.WriteLine("  no replication or signing filter of the registry matches the probe's name.");

        // What the fetch staged: the image's key and its manifest digest.
        using var stagedObject = await s3.GetObjectAsync(new GetObjectRequest { BucketName = plan.EvidenceStore, Key = $"staged/{execution}.json" });
        var staged = JsonNode.Parse(await new StreamReader(stagedObject.ResponseStream).ReadToEndAsync())!;
        var manifestDigest = staged["manifestDigest"]?.GetValue<string>() ?? throw new InvalidOperationException($"{execution} staged no image");
        var key = (staged["staged"]?.AsArray() ?? new JsonArray())
            .Select(a => a!["key"]!.GetValue<string>()).Single(k => !Path.GetFileName(k).StartsWith("tests-", StringComparison.Ordinal));
        report["stagedKey"] = key;
        report["stagedManifestDigest"] = manifestDigest;
        Console.WriteLine($"  staged: {key}, manifest {manifestDigest}");

        var profileArn = await PipelineBootstrapper.EnsureSigningProfileAsync(signer, profile);
        var unversionedProfileArn = PipelineBootstrapPlanner.SigningProfileArn(region, account, profile);
        var repositoryArn = $"arn:aws:ecr:{region}:{account}:repository/{repository}";
        var ruleWritten = false;
        try
        {
            await EnsureRepositoryAsync(ecr, repository);

            var rule = PipelineBootstrapper.BuildSigningRule(profileArn, new[] { repository });
            var existing = await PipelineBootstrapper.SigningRulesAsync(ecr);
            await ecr.PutSigningConfigurationAsync(new PutSigningConfigurationRequest
            {
                SigningConfiguration = new SigningConfiguration { Rules = CrossAccount.MergeSigning(existing, new[] { rule }) },
            });
            ruleWritten = true;
            if (CrossAccount.SigningRulesNotHeld(await PipelineBootstrapper.SigningRulesAsync(ecr), new[] { rule }).Count > 0)
                throw new InvalidOperationException("the probe's signing rule does not read back");
            Console.WriteLine($"  signing rule: {profile} signs pushes to {repository} ({existing.Count} other rule(s) kept).");

            var policy = DeployerPlanner.Combine(DeployerPlanner.Logs(region, account, function), new object[]
            {
                new { Sid = "ReadTheStagedImage", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{plan.ScratchBucket}/{execution}/*" },
                new
                {
                    Sid = "PushToTheProbe", Effect = "Allow",
                    Action = new[] { "ecr:DescribeImages", "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload", "ecr:PutImage" },
                    Resource = repositoryArn,
                },
                new { Sid = "SignUnderTheProbesProfile", Effect = "Allow", Action = new[] { "signer:SignPayload" }, Resource = unversionedProfileArn },
            });
            var fn = new DeployerFunction(function, DeployerHandlers.IntakePush, $"{function}-fn", policy,
                new Dictionary<string, string> { [IntakeEnvironment.ScratchBucket] = plan.ScratchBucket },
                TimeoutSeconds: IntakeBuildPlanner.FetchTimeoutSeconds, MemoryMb: IntakeBuildPlanner.FetchMemoryMb,
                DeployerPackages.Deployer, InvokedByStateMachine: false);
            var packages = DeployerBootstrapper.LocatePackages(new[] { fn.Package });
            await DeployerBootstrapper.EnsureLogGroupAsync(logs, $"/aws/lambda/{function}", 14);
            var roleArn = await DeployerBootstrapper.EnsureRoleAsync(iam, fn.RoleName, plan.FunctionTrustPolicy,
                "lz intake: the push, for V-C's probe only (DecoupledCd.md §15.8 items 3 and 4).", ($"{fn.RoleName}-grant", fn.Policy), plan.DenyPolicy);
            await DeployerBootstrapper.EnsureFunctionAsync(lambda, fn, roleArn, packages[fn.Package], IntakeBuildPlanner.FetchEphemeralStorageMb);

            async Task<JsonNode> Push(string what, JsonObject request)
            {
                var invoked = await lambda.InvokeAsync(new InvokeRequest { FunctionName = function, Payload = request.ToJsonString() });
                var body = Encoding.UTF8.GetString(invoked.Payload.ToArray());
                if (invoked.FunctionError is { } error) throw new InvalidOperationException($"{what}: the push failed ({error}): {body}");
                var answer = JsonNode.Parse(body)!;
                Console.WriteLine($"  {what}: {answer.ToJsonString()}");
                return answer;
            }

            JsonObject Request(string tag, bool readTagFirst, string? imageDigest = null) => new()
            {
                ["key"] = key, ["repository"] = repository, ["tag"] = tag, ["readTagFirst"] = readTagFirst, ["imageDigest"] = imageDigest, ["probe"] = true,
            };
            report["first"] = (await Push("first push", Request(Tag, readTagFirst: true))).DeepClone();
            report["repeat"] = (await Push("the same manifest and tag again, the tag not read", Request(Tag, readTagFirst: false))).DeepClone();
            report["wrongDigest"] = (await Push($"a wrong imageDigest ({WrongDigest})", Request(WrongTag, readTagFirst: false, WrongDigest))).DeepClone();

            var held = await DigestAtAsync(ecr, repository, Tag);
            report["ecrDigest"] = held;
            report["digestKept"] = held == manifestDigest;
            report["wrongTagPresent"] = await DigestAtAsync(ecr, repository, WrongTag) is not null;
            Console.WriteLine($"  ECR holds {held} at '{Tag}' ({(held == manifestDigest ? "the staged manifest's digest" : "NOT the staged digest")}); " +
                              $"'{WrongTag}' {((bool)report["wrongTagPresent"]! ? "EXISTS" : "absent")}.");

            report["signing"] = await SigningStatusAsync(ecr, repository, manifestDigest, profileArn);
            report["artifacts"] = await ArtifactsAsync(ecr, repository);
        }
        finally
        {
            if (!keep)
            {
                await RemoveAsync(ecr, iam, lambda, repository, profileArn, ruleWritten, function, $"{function}-fn");
                report["removed"] = true;
            }
        }
        return report;
    }

    private static async Task EnsureRepositoryAsync(IAmazonECR ecr, string repository)
    {
        try
        {
            await ecr.CreateRepositoryAsync(new CreateRepositoryRequest { RepositoryName = repository, ImageTagMutability = ImageTagMutability.IMMUTABLE });
            Console.WriteLine($"  repository '{repository}' created, tag-immutable.");
        }
        catch (RepositoryAlreadyExistsException)
        {
            await ecr.PutImageTagMutabilityAsync(new PutImageTagMutabilityRequest { RepositoryName = repository, ImageTagMutability = ImageTagMutability.IMMUTABLE });
            Console.WriteLine($"  repository '{repository}' already exists; tag-immutable.");
        }
    }

    private static async Task<string?> DigestAtAsync(IAmazonECR ecr, string repository, string tag)
    {
        try
        {
            var found = await ecr.DescribeImagesAsync(new DescribeImagesRequest
            {
                RepositoryName = repository, ImageIds = new List<ImageIdentifier> { new() { ImageTag = tag } },
            });
            return found.ImageDetails?.SingleOrDefault()?.ImageDigest;
        }
        catch (ImageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The image's signing status under the probe's profile, waited for while it is still in progress (up to ten minutes).</summary>
    private static async Task<JsonNode> SigningStatusAsync(IAmazonECR ecr, string repository, string digest, string profileArn)
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var status = await ecr.DescribeImageSigningStatusAsync(new DescribeImageSigningStatusRequest
            {
                RepositoryName = repository, ImageId = new ImageIdentifier { ImageDigest = digest },
            });
            var all = JsonSerializer.SerializeToNode(status.SigningStatuses ?? new List<ImageSigningStatus>())!;
            var ours = (status.SigningStatuses ?? new List<ImageSigningStatus>()).FirstOrDefault(s => s.SigningProfileArn == profileArn);
            var waited = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
            if (ours is not null && ours.Status?.Value != "IN_PROGRESS" || waited > 600)
            {
                Console.WriteLine($"  signing status after {waited} s: {all.ToJsonString()}");
                return new JsonObject { ["afterSeconds"] = waited, ["statuses"] = all };
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Every entry in the probe repository: the image, and whatever managed signing pushed beside it.</summary>
    private static async Task<JsonNode> ArtifactsAsync(IAmazonECR ecr, string repository)
    {
        var images = await ecr.DescribeImagesAsync(new DescribeImagesRequest { RepositoryName = repository });
        var list = new JsonArray((images.ImageDetails ?? new List<ImageDetail>()).Select(i => (JsonNode?)new JsonObject
        {
            ["digest"] = i.ImageDigest, ["tags"] = new JsonArray((i.ImageTags ?? new List<string>()).Select(t => (JsonNode?)t).ToArray()),
            ["manifestMediaType"] = i.ImageManifestMediaType, ["artifactMediaType"] = i.ArtifactMediaType,
        }).ToArray());
        Console.WriteLine($"  the probe repository holds: {list.ToJsonString()}");
        return list;
    }

    private static async Task RemoveAsync(IAmazonECR ecr, IAmazonIdentityManagementService iam, IAmazonLambda lambda,
        string repository, string profileArn, bool ruleWritten, string function, string role)
    {
        Console.WriteLine("  removing the probe:");
        try { await ecr.DeleteRepositoryAsync(new DeleteRepositoryRequest { RepositoryName = repository, Force = true }); Console.WriteLine($"    repository '{repository}' deleted."); }
        catch (RepositoryNotFoundException) { }

        if (ruleWritten)
        {
            var rest = (await PipelineBootstrapper.SigningRulesAsync(ecr)).Where(r => r.SigningProfileArn != profileArn).ToList();
            if (rest.Count == 0) await ecr.DeleteSigningConfigurationAsync(new DeleteSigningConfigurationRequest());
            else await ecr.PutSigningConfigurationAsync(new PutSigningConfigurationRequest { SigningConfiguration = new SigningConfiguration { Rules = rest } });
            var left = (await PipelineBootstrapper.SigningRulesAsync(ecr)).Count(r => r.SigningProfileArn == profileArn);
            Console.WriteLine($"    signing rule removed ({rest.Count} other rule(s) kept; the probe's rules left: {left}).");
        }

        try { await lambda.DeleteFunctionAsync(new DeleteFunctionRequest { FunctionName = function }); Console.WriteLine($"    function '{function}' deleted."); }
        catch (Amazon.Lambda.Model.ResourceNotFoundException) { }

        try
        {
            foreach (var name in (await iam.ListRolePoliciesAsync(new ListRolePoliciesRequest { RoleName = role })).PolicyNames ?? new List<string>())
                await iam.DeleteRolePolicyAsync(new DeleteRolePolicyRequest { RoleName = role, PolicyName = name });
            await iam.DeleteRoleAsync(new DeleteRoleRequest { RoleName = role });
            Console.WriteLine($"    role '{role}' and its grants deleted.");
        }
        catch (NoSuchEntityException) { }
    }
}
