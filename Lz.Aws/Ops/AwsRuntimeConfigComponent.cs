using Amazon.ECS;
using Amazon.SimpleSystemsManagement;
using Lz.Aws.Orchestration;
using Lz.Core.Config;
using Pulumi;
using Pulumi.Automation;

namespace Lz.Aws.Ops;

/// <summary>One runtime parameter as the foundation stack declares it.</summary>
/// <param name="Import">Whether the parameter exists in the account and so is imported, not created: an import of one that
/// does not exist fails the whole preview (measured, Pulumi 3.224, 2026-11-06), and once a parameter is in state the option is
/// ignored, so it is set from what the account holds at each run.</param>
public sealed record RuntimeParameterPlan(string Key, string LogicalName, string Name, string Value, bool Import);

/// <summary>
/// RUNTIME CONFIG AS FOUNDATION RESOURCES (DecoupledCd.md §12 P3 F, P-15): which parameters <c>RuntimeConfig</c> declares,
/// under which names, and whether a deploy that changed them must restart the services. Pure, so the plan is testable.
/// </summary>
public static class RuntimeConfigPlanner
{
    /// <summary>The Pulumi type of an SSM parameter, as a step reports it.</summary>
    public const string ParameterType = "aws:ssm/parameter:Parameter";

    /// <summary>The prefix of every runtime parameter's logical name, which is how a step is known to be one.</summary>
    public const string LogicalPrefix = "runtime-config-";

    public static string PathFor(SystemConfig config) => $"/{config.SystemKey}/{config.Environment}/";

    public static string NameFor(SystemConfig config, string key) => PathFor(config) + key;

    /// <summary>The logical name: <c>runtime-config-</c> and the key with <c>/</c> as <c>.</c>, which a key cannot contain.</summary>
    public static string LogicalNameFor(string key) => LogicalPrefix + key.Replace('/', '.');

    public static IReadOnlyList<RuntimeParameterPlan> Plan(SystemConfig config, IReadOnlySet<string> existing)
        => (config.RuntimeConfig?.Parameters ?? new Dictionary<string, string>())
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new RuntimeParameterPlan(p.Key, LogicalNameFor(p.Key), NameFor(config, p.Key), p.Value, existing.Contains(NameFor(config, p.Key))))
            .ToList();

    /// <summary>
    /// The keys of the runtime parameters whose VALUE a deploy's steps set: created, or updated or replaced with <c>value</c> among
    /// the properties that differ. An import changes nothing, and neither does an update of the description or tags alone - which
    /// every imported parameter gets once, since none set by hand has them (measured on dev's 34, 2026-11-06).
    /// </summary>
    public static IReadOnlyList<string> ChangedKeys(IEnumerable<PlannedStep> steps)
        => steps.Where(s => s.Type == ParameterType
                            && (s.Op is OperationType.Create
                                || (s.Op is OperationType.Update or OperationType.Replace or OperationType.CreateReplacement
                                    && (s.Diffs is null || s.Diffs.Contains("value")))))
            .Select(s => s.Urn[(s.Urn.LastIndexOf("::", StringComparison.Ordinal) + 2)..])
            .Where(n => n.StartsWith(LogicalPrefix, StringComparison.Ordinal))
            .Select(n => n[LogicalPrefix.Length..].Replace('.', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    /// <summary>The changed keys a service reads only at start, which a restart must follow.</summary>
    public static IReadOnlyList<string> ReadAtStart(IEnumerable<string> changed, IEnumerable<string>? readAtStart)
    {
        var prefixes = (readAtStart ?? Array.Empty<string>()).ToList();
        return changed.Where(k => prefixes.Any(p => RuntimeConfigConfig.IsUnder(k, p))).ToList();
    }
}

/// <summary>The parameters, as Pulumi resources in the foundation program.</summary>
public static class AwsRuntimeConfigComponent
{
    public static void Deploy(SystemConfig config, IReadOnlyList<RuntimeParameterPlan> plan)
    {
        foreach (var p in plan)
        {
            _ = new Pulumi.Aws.Ssm.Parameter(p.LogicalName, new Pulumi.Aws.Ssm.ParameterArgs
            {
                Name = p.Name,
                Type = "String",
                Value = p.Value,
                Tier = "Standard",
                Description = "lz RuntimeConfig (DecoupledCd P3 F): set by the config bundle; a hand edit is reverted at the next foundation deploy.",
                Tags = { { "System", config.SystemKey }, { "ManagedBy", "lz-pulumi" } },
            }, new CustomResourceOptions { ImportId = p.Import ? p.Name : null });
        }
    }
}

/// <summary>What runtime config needs from the account: the parameters that exist, and the restart.</summary>
public static class RuntimeConfigApply
{
    /// <summary>
    /// The names of the parameters under <paramref name="path"/>, from their metadata alone: DescribeParameters returns no value,
    /// so nothing SecureString is read, and the read-only Plan task may make the call.
    /// </summary>
    public static async Task<IReadOnlySet<string>> ExistingAsync(IAmazonSimpleSystemsManagement ssm, string path, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await ssm.DescribeParametersAsync(new Amazon.SimpleSystemsManagement.Model.DescribeParametersRequest
            {
                ParameterFilters = new List<Amazon.SimpleSystemsManagement.Model.ParameterStringFilter>
                {
                    new() { Key = "Path", Option = "Recursive", Values = new List<string> { path.TrimEnd('/') } },
                },
                MaxResults = 50,
                NextToken = token,
            }, ct);
            foreach (var p in page.Parameters ?? new()) names.Add(p.Name);
            token = string.IsNullOrEmpty(page.NextToken) ? null : page.NextToken;
        } while (token is not null);
        return names;
    }

    /// <summary>
    /// RESTART, for a changed value the services read only at start: a new deployment of every service in the system's cluster on
    /// its CURRENT task definition, so the image is the one running and the signature hook admits it as it did, then a wait until
    /// each rollout completes. A rollout that fails, or does not finish in time, is a failed deploy.
    /// </summary>
    public static async Task RestartAsync(IAmazonECS ecs, string cluster, IReadOnlyList<string> because, TimeSpan timeout, CancellationToken ct)
    {
        var services = new List<string>();
        string? token = null;
        do
        {
            var page = await ecs.ListServicesAsync(new Amazon.ECS.Model.ListServicesRequest { Cluster = cluster, NextToken = token }, ct);
            services.AddRange(page.ServiceArns ?? new());
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        Console.WriteLine($"Restarting {services.Count} service(s) in {cluster}: {string.Join(", ", because)} changed, and is read only at start.");
        foreach (var service in services)
            await ecs.UpdateServiceAsync(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = cluster, Service = service, ForceNewDeployment = true }, ct);

        var deadline = DateTimeOffset.UtcNow + timeout;
        foreach (var batch in services.Chunk(10))
        {
            while (true)
            {
                var described = await ecs.DescribeServicesAsync(new Amazon.ECS.Model.DescribeServicesRequest { Cluster = cluster, Services = batch.ToList() }, ct);
                var primaries = described.Services.Select(s => (s.ServiceName, Primary: s.Deployments.FirstOrDefault(d => d.Status == "PRIMARY"))).ToList();
                if (primaries.FirstOrDefault(p => p.Primary?.RolloutState == Amazon.ECS.DeploymentRolloutState.FAILED) is { ServiceName: not null } failed)
                    throw new InvalidOperationException($"the restart of {failed.ServiceName} failed: {failed.Primary!.RolloutStateReason}");
                if (primaries.All(p => p.Primary?.RolloutState == Amazon.ECS.DeploymentRolloutState.COMPLETED))
                    break;
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"the restart of {string.Join(", ", primaries.Where(p => p.Primary?.RolloutState != Amazon.ECS.DeploymentRolloutState.COMPLETED).Select(p => p.ServiceName))} did not complete in {timeout}.");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
        Console.WriteLine("  restarted; every rollout completed.");
    }
}
