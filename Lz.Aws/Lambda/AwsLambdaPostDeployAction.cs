using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Lambda;

/// <summary>
/// Tenant-phase post-deploy for the lambda-* topologies. Two halves:
///
/// <para>1. Everything the EcsExpress tenant post-deploy does, reused with an
/// EMPTY services list so its ECS force-new-deployment loop no-ops: the
/// {sk}_{tk} data table, the BFF session tables, and apex-alias verification.</para>
///
/// <para>2. CONSISTENCY WITH THE OTHER TOPOLOGIES: roll each host-layer
/// function to the latest pushed image. On ECS, deploytenant's task cycle
/// re-pulls <c>:latest</c> as a side effect, so operators rightly expect a
/// tenant deploy to leave the tenant on current code. Lambda resolves the image
/// digest only at <c>UpdateFunctionCode</c> time, so without this step a
/// deploytenant silently leaves STALE code running (observed live 2026-07-30 —
/// CloudTrail showed Pulumi had never issued an UpdateFunctionCode). The roll
/// is digest-compared (<see cref="AwsLambdaContainerUpdater"/>): a function
/// just created by this very deploy, or an unchanged image, is an up-to-date
/// no-op — better behaved than the ECS bounce, no gratuitous cold starts.</para>
/// </summary>
public class AwsLambdaPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _tenantKey;
    private readonly TenantConfig? _tenantConfig;
    private readonly EcsExpress.AwsEcsExpressPostDeployAction _inner;

    public AwsLambdaPostDeployAction(
        SystemConfig config, IReadOnlyList<ServiceDefinition> services,
        string? tenantKey, TenantConfig? tenantConfig)
    {
        _config = config;
        _services = services;
        _tenantKey = tenantKey;
        _tenantConfig = tenantConfig;
        _inner = new EcsExpress.AwsEcsExpressPostDeployAction(
            config, Array.Empty<ServiceDefinition>(), tenantKey, tenantConfig);
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        // Tenant/BFF tables + apex verification (the ECS deployment loop no-ops).
        await _inner.ExecuteAsync(outputs);

        if (string.IsNullOrEmpty(_tenantKey) || _tenantConfig is null || _services.Count == 0)
            return;

        var profile = _tenantConfig.Profile ?? _config.Profile;
        var region = _tenantConfig.Region ?? _config.Region;
        var updater = new AwsLambdaContainerUpdater(profile, region);

        Console.WriteLine("Ensuring tenant function(s) run the latest pushed image...");
        foreach (var svc in _services)
        {
            // Must match AwsLambdaTenantServiceComponent (function name) and
            // deploycontainer (ECR repo) naming.
            var functionName = $"{_config.SystemKey}-{_tenantKey}-{svc.Name}";
            var ecrRepo =
                $"{_config.SystemKey}-{_tenantConfig.TenantSuffix}-{_config.Environment}-{_tenantKey}-{svc.Name}";

            var result = await updater.UpdateIfNewerAsync(
                functionName, ecrRepo, "latest",
                force: false, wait: true, dryRun: false, CancellationToken.None);
            Console.WriteLine($"  [{result.Outcome}] {result.Service}: {result.Detail}");
            if (result.Outcome == Ecs.UpdateOutcome.Failed)
                throw new InvalidOperationException(
                    $"Function code roll failed for {functionName}: {result.Detail}");
        }
    }
}
