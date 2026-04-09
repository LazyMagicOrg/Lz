using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Post-deploy action for AppRunner topology.
/// Triggers AppRunner to pull the latest image from ECR.
/// Does NOT rebuild Docker images — that's handled by `lz deploycontainer`.
/// </summary>
public class AwsAppRunnerPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;
    private readonly SystemDefinition _system;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _tenantKey;
    private readonly TenantConfig? _tenantConfig;

    public AwsAppRunnerPostDeployAction(
        SystemConfig config,
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
    {
        _config = config;
        _system = system;
        _services = services;
        _tenantKey = tenantKey;
        _tenantConfig = tenantConfig;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        foreach (var svc in _services)
        {
            if (svc.Docker == null) continue;

            var serviceName = svc.Name;
            var sk = _config.SystemKey;
            var prefix = _tenantKey != null ? $"{sk}-{_tenantKey}-{serviceName}" : $"{sk}-{_config.Environment}-{serviceName}";

            Console.WriteLine($"  Triggering AppRunner deployment for {prefix}...");
            await StartDeploymentAsync(prefix);
        }
    }

    private async Task StartDeploymentAsync(string serviceName)
    {
        try
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_config.Profile, out var credentials))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Failed to resolve AWS credentials for profile '{_config.Profile}'");
                Console.ResetColor();
                return;
            }

            using var client = new Amazon.AppRunner.AmazonAppRunnerClient(
                credentials,
                Amazon.RegionEndpoint.GetBySystemName(_config.Region));

            var listResponse = await client.ListServicesAsync(
                new Amazon.AppRunner.Model.ListServicesRequest());

            var service = listResponse.ServiceSummaryList
                .FirstOrDefault(s => s.ServiceName == serviceName);

            if (service == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  AppRunner service '{serviceName}' not found — run 'lz deploytenant' first.");
                Console.ResetColor();
                return;
            }

            if (service.Status != "RUNNING")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  AppRunner service '{serviceName}' is {service.Status} — cannot start deployment. Fix the service state first.");
                Console.ResetColor();
                return;
            }

            await client.StartDeploymentAsync(
                new Amazon.AppRunner.Model.StartDeploymentRequest
                {
                    ServiceArn = service.ServiceArn,
                });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  AppRunner deployment started for {serviceName}.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Could not trigger AppRunner update for {serviceName}: {ex.Message}");
            Console.ResetColor();
        }
    }
}
