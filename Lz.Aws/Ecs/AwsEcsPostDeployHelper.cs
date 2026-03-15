using Amazon.ECS;
using Amazon.ECS.Model;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Ecs;

/// <summary>
/// Post-deploy imperative operations that run outside the Pulumi resource graph:
///   1. Run the system-init ECS task (CREATE DATABASE keycloak)
///   2. Scale Keycloak ECS service from 0 → 1
/// </summary>
public static class AwsEcsPostDeployHelper
{
    /// <summary>
    /// Run the system-init task, wait for it to complete, then scale Keycloak to 1.
    /// </summary>
    public static async Task RunSystemInitAndScaleAsync(
        AmazonECSClient client,
        string clusterArn,
        string initTaskFamily,
        string keycloakServiceName,
        List<string> subnetIds,
        string securityGroupId)
    {
        // Run the init task
        Console.WriteLine("Running system-init task (CREATE DATABASE keycloak)...");
        var taskArn = await RunInitTaskAsync(client, clusterArn, initTaskFamily, subnetIds, securityGroupId);
        Console.WriteLine($"  Task started: {taskArn}");

        // Wait for it to complete
        Console.WriteLine("  Waiting for init task to complete...");
        var exitCode = await WaitForTaskAsync(client, clusterArn, taskArn);

        if (exitCode != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  System-init task failed with exit code {exitCode}.");
            Console.Error.WriteLine("  Check CloudWatch logs for details.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  System-init task completed successfully.");
        Console.ResetColor();

        // Scale Keycloak from 0 → 1
        Console.WriteLine($"Scaling {keycloakServiceName} to 1...");
        await ScaleServiceAsync(client, clusterArn, keycloakServiceName, 1);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {keycloakServiceName} scaled to 1. Keycloak is starting.");
        Console.ResetColor();
    }

    private static async Task<string> RunInitTaskAsync(
        AmazonECSClient client,
        string clusterArn,
        string taskFamily,
        List<string> subnetIds,
        string securityGroupId)
    {
        var response = await client.RunTaskAsync(new RunTaskRequest
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
        });

        if (response.Tasks.Count == 0)
        {
            var failures = string.Join(", ", response.Failures.Select(f => $"{f.Reason}: {f.Detail}"));
            throw new InvalidOperationException($"Failed to start init task: {failures}");
        }

        return response.Tasks[0].TaskArn;
    }

    private static async Task<int> WaitForTaskAsync(
        AmazonECSClient client,
        string clusterArn,
        string taskArn,
        int pollIntervalSeconds = 5,
        int timeoutSeconds = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = clusterArn,
                Tasks = [taskArn],
            });

            var task = response.Tasks.FirstOrDefault()
                ?? throw new InvalidOperationException("Init task not found");

            if (task.LastStatus == "STOPPED")
            {
                var container = task.Containers.FirstOrDefault();
                return container?.ExitCode ?? -1;
            }

            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds));
        }

        throw new TimeoutException($"Init task did not complete within {timeoutSeconds}s");
    }

    private static async Task ScaleServiceAsync(
        AmazonECSClient client,
        string clusterArn,
        string serviceName,
        int desiredCount)
    {
        await client.UpdateServiceAsync(new UpdateServiceRequest
        {
            Cluster = clusterArn,
            Service = serviceName,
            DesiredCount = desiredCount,
        });
    }
}
