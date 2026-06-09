using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Ecs;

/// <summary>
/// Imperative ECS task runner for seed data export/import operations.
/// Launches the seeder ECS Fargate task and waits for it to complete.
/// Follows the AwsEcsPostDeployHelper pattern for RunTask/DescribeTasks.
/// </summary>
public class AwsSeedRunner
{
    private readonly SystemConfig _config;

    public AwsSeedRunner(SystemConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Run a seed export task: EFS + database → S3.
    /// </summary>
    /// <param name="s3Prefix">S3 prefix within the tenant key (e.g., "seed" or "data"). Default: "seed".</param>
    public async Task<bool> RunExportAsync(
        string tenantKey,
        string s3Prefix = "seed",
        bool skipEfs = false,
        string? baseUrl = null,
        string? clusterArn = null,
        List<string>? subnetIds = null,
        string? securityGroupId = null)
    {
        // EFS access points use /{sk}-{tk}-{env}/ prefix, but S3 uses just {tenantKey}/
        var efsPrefix = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        var argsList = new List<string>
        {
            "seed-export",
            "--env", _config.Environment,
            "--tenant", tenantKey,
            "--bucket", _config.SeedData!.Bucket,
            "--region", _config.SeedData.Region,
            "--efs-root", "/mnt/efs",
            "--efs-prefix", efsPrefix,
            "--s3-prefix", s3Prefix
        };
        if (skipEfs) argsList.Add("--skip-efs");

        return await RunSeedTaskAsync(tenantKey, argsList.ToArray(), "export", baseUrl, clusterArn, subnetIds, securityGroupId);
    }

    /// <summary>
    /// Run a seed import task: S3 → EFS + database.
    /// </summary>
    /// <param name="s3Prefix">S3 prefix within the tenant key (e.g., "seed" or "data"). Default: "seed".</param>
    public async Task<bool> RunImportAsync(
        string tenantKey,
        string sourceKey = "latest",
        string s3Prefix = "seed",
        string? baseUrl = null,
        string? clusterArn = null,
        List<string>? subnetIds = null,
        string? securityGroupId = null)
    {
        // EFS access points use /{sk}-{tk}-{env}/ prefix, but S3 uses just {tenantKey}/
        var efsPrefix = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        var args = new[]
        {
            "seed-import",
            "--env", _config.Environment,
            "--tenant", tenantKey,
            "--bucket", _config.SeedData!.Bucket,
            "--region", _config.SeedData.Region,
            "--source", sourceKey,
            "--efs-root", "/mnt/efs",
            "--efs-prefix", efsPrefix,
            "--s3-prefix", s3Prefix
        };

        return await RunSeedTaskAsync(tenantKey, args, "import", baseUrl, clusterArn, subnetIds, securityGroupId);
    }

    private async Task<bool> RunSeedTaskAsync(
        string tenantKey,
        string[] commandArgs,
        string operation,
        string? baseUrl,
        string? clusterArn,
        List<string>? subnetIds,
        string? securityGroupId)
    {
        // Pre-flight: verify the seeder image is actually in ECR. ECS will
        // surface a CannotPullContainerError in CloudWatch otherwise, which
        // is opaque from the caller's perspective. Bail with a clear message
        // pointing to `lz deployseeder`.
        if (!await EnsureSeederImageInEcrAsync())
            return false;

        var client = CreateEcsClient(_config.Region, _config.Profile);

        // Discover cluster and network config from existing ECS services if not provided
        if (clusterArn is null || subnetIds is null || securityGroupId is null)
        {
            var discovered = await DiscoverEcsConfigAsync(client);
            clusterArn ??= discovered.ClusterArn;
            subnetIds ??= discovered.SubnetIds;
            securityGroupId ??= discovered.SecurityGroupId;
        }

        var taskFamily = $"{_config.SystemKey}-seeder";

        Console.WriteLine($"Starting seed {operation} task for tenant '{tenantKey}'...");
        Console.WriteLine($"  Cluster: {clusterArn}");
        Console.WriteLine($"  Task: {taskFamily}");
        Console.WriteLine($"  Command: {string.Join(" ", commandArgs)}");

        // Run the task with command override
        var runResponse = await client.RunTaskAsync(new RunTaskRequest
        {
            Cluster = clusterArn,
            TaskDefinition = taskFamily,
            LaunchType = LaunchType.FARGATE,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    Subnets = subnetIds,
                    SecurityGroups = [securityGroupId],
                    AssignPublicIp = AssignPublicIp.DISABLED,
                },
            },
            Overrides = new TaskOverride
            {
                ContainerOverrides =
                [
                    new ContainerOverride
                    {
                        Name = "seeder",
                        Command = commandArgs.ToList(),
                        Environment = BuildEnvironmentOverrides(tenantKey, baseUrl),
                    }
                ],
            },
        });

        if (runResponse.Tasks.Count == 0)
        {
            var failures = string.Join(", ", runResponse.Failures.Select(f => $"{f.Reason}: {f.Detail}"));
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Failed to start seed task: {failures}");
            Console.ResetColor();
            return false;
        }

        string taskArn = runResponse.Tasks[0].TaskArn;
        Console.WriteLine($"  Task started: {taskArn}");
        Console.WriteLine($"  Waiting for task to complete (check CloudWatch: /ecs/{_config.SystemKey}-seeder)...");

        // Wait for completion
        var exitCode = await WaitForTaskAsync(client, clusterArn, taskArn);

        if (exitCode == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Seed {operation} completed successfully.");
            Console.ResetColor();
            return true;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  Seed {operation} failed with exit code {exitCode}.");
            Console.Error.WriteLine($"  Check CloudWatch logs: /ecs/{_config.SystemKey}-seeder");
            Console.ResetColor();
            return false;
        }
    }

    private List<Amazon.ECS.Model.KeyValuePair> BuildEnvironmentOverrides(string tenantKey, string? baseUrl)
    {
        var envVars = new List<Amazon.ECS.Model.KeyValuePair>
        {
            new() { Name = "TENANT_KEY", Value = tenantKey },
            new() { Name = "ENVIRONMENT", Value = _config.Environment },
        };
        if (!string.IsNullOrEmpty(baseUrl))
            envVars.Add(new() { Name = "BASE_URL", Value = baseUrl });
        return envVars;
    }

    /// <summary>
    /// Discover cluster ARN, subnet IDs, and security group from existing ECS services.
    /// </summary>
    private async Task<(string ClusterArn, List<string> SubnetIds, string SecurityGroupId)> DiscoverEcsConfigAsync(
        AmazonECSClient client)
    {
        // List clusters to find ours
        var clusterName = $"{_config.SystemKey}-cluster";
        var clustersResponse = await client.DescribeClustersAsync(new DescribeClustersRequest
        {
            Clusters = [clusterName],
        });

        var cluster = clustersResponse.Clusters.FirstOrDefault()
            ?? throw new InvalidOperationException($"ECS cluster '{clusterName}' not found. Is the foundation deployed?");

        // List services to discover network config
        var servicesResponse = await client.ListServicesAsync(new ListServicesRequest
        {
            Cluster = cluster.ClusterArn,
        });

        if (servicesResponse.ServiceArns.Count == 0)
            throw new InvalidOperationException("No ECS services found in cluster. Is the foundation fully deployed?");

        // Describe first service for its network config
        var describeResponse = await client.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = cluster.ClusterArn,
            Services = [servicesResponse.ServiceArns[0]],
        });

        var service = describeResponse.Services.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not describe ECS service for network config");

        var vpcConfig = service.NetworkConfiguration?.AwsvpcConfiguration
            ?? throw new InvalidOperationException("Service has no awsvpc network configuration");

        return (
            cluster.ClusterArn,
            vpcConfig.Subnets,
            vpcConfig.SecurityGroups.FirstOrDefault()
                ?? throw new InvalidOperationException("Service has no security group configured")
        );
    }

    private static async Task<int> WaitForTaskAsync(
        AmazonECSClient client,
        string clusterArn,
        string taskArn,
        int pollIntervalSeconds = 10,
        int timeoutSeconds = 3600) // 1 hour timeout for large seed operations
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = clusterArn,
                Tasks = [taskArn],
            });

            // AWS SDK v4 returns null for empty collection properties (v3 returned an empty list).
            var task = response.Tasks?.FirstOrDefault()
                ?? throw new InvalidOperationException("Seed task not found");

            if (task.LastStatus == "STOPPED")
            {
                var container = task.Containers?.FirstOrDefault();
                var exitCode = container?.ExitCode ?? -1;

                if (task.StoppedReason is not null)
                    Console.WriteLine($"  Stopped reason: {task.StoppedReason}");

                return exitCode;
            }

            Console.Write(".");
            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds));
        }

        throw new TimeoutException($"Seed task did not complete within {timeoutSeconds}s");
    }

    private static AmazonECSClient CreateEcsClient(string region, string? profile)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonECSClient(credentials, regionEndpoint);
        }

        return new AmazonECSClient(regionEndpoint);
    }

    private static AmazonECRClient CreateEcrClient(string region, string? profile)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonECRClient(credentials, regionEndpoint);
        }

        return new AmazonECRClient(regionEndpoint);
    }

    /// <summary>
    /// Verify that <c>{SystemKey}-seeder:latest</c> exists in ECR before
    /// launching the ECS task. The ECS task definition references the image
    /// by tag — if it's missing the task starts, fails to pull, and stops
    /// with <c>CannotPullContainerError</c> only visible in CloudWatch.
    /// </summary>
    /// <returns>true if the image exists; false (with error already printed) otherwise.</returns>
    private async Task<bool> EnsureSeederImageInEcrAsync()
    {
        var repoName = $"{_config.SystemKey}-seeder";
        using var ecr = CreateEcrClient(_config.Region, _config.Profile);

        try
        {
            await ecr.DescribeImagesAsync(new DescribeImagesRequest
            {
                RepositoryName = repoName,
                ImageIds = [new ImageIdentifier { ImageTag = "latest" }],
            });
            return true;
        }
        catch (ImageNotFoundException)
        {
            PrintSeederImageMissingError(repoName, "tag 'latest' not found in repository");
            return false;
        }
        catch (RepositoryNotFoundException)
        {
            PrintSeederImageMissingError(repoName, "repository does not exist");
            return false;
        }
        catch (Exception ex)
        {
            // Don't block on transient ECR / permission errors — let ECS try to
            // pull and surface its own diagnostic if there's a real problem.
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: could not verify seeder image in ECR ({ex.GetType().Name}: {ex.Message}). Continuing.");
            Console.ResetColor();
            return true;
        }
    }

    private static void PrintSeederImageMissingError(string repoName, string detail)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"  Error: Seeder image '{repoName}:latest' not available in ECR ({detail}).");
        Console.Error.WriteLine($"  Run 'lz deployseeder' to build the ETL Dockerfile and push it to ECR, then retry.");
        Console.ResetColor();
    }
}
