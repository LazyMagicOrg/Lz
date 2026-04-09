using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Aws.DynamoDB;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Tenant post-deploy action for ECSExpress:
/// 1. Creates tenant + subtenant DynamoDB tables (idempotent)
/// 2. Triggers ECS service force-new-deployment
/// </summary>
public class AwsEcsExpressPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _tenantKey;
    private readonly TenantConfig? _tenantConfig;

    public AwsEcsExpressPostDeployAction(
        SystemConfig config,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
    {
        _config = config;
        _services = services;
        _tenantKey = tenantKey;
        _tenantConfig = tenantConfig;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        // --- Step 1: Ensure DynamoDB tables for tenant + subtenants ---
        if (_tenantKey != null)
        {
            await EnsureTenantTablesAsync();
        }

        // --- Step 2: Trigger ECS force-new-deployment for each service ---
        foreach (var svc in _services)
        {
            if (svc.Docker == null) continue;

            var serviceName = svc.Name;
            var sk = _config.SystemKey;
            var env = _config.Environment;
            var prefix = _tenantKey != null ? $"{sk}-{_tenantKey}-{serviceName}" : $"{sk}-{env}-{serviceName}";
            var clusterName = $"{sk}-{env}-cluster";

            Console.WriteLine($"  Triggering ECS force-new-deployment for {prefix}...");
            await ForceNewDeploymentAsync(clusterName, prefix);
        }
    }

    private async Task EnsureTenantTablesAsync()
    {
        var sk = _config.SystemKey;
        var tk = _tenantKey!;
        var profile = _config.Profile;
        var region = _config.Region;

        Console.WriteLine($"  Ensuring DynamoDB tables for tenant '{tk}'...");

        var baseTags = new Dictionary<string, string>
        {
            { "System", sk },
            { "Tenant", tk },
        };

        // Tenant table: {SystemKey}_{TenantKey}
        var tenantTable = $"{sk}_{tk}";
        var created = await DynamoDbTableCreator.EnsureTableAsync(
            profile, region, tenantTable,
            new Dictionary<string, string>(baseTags) { { "Level", "tenant" } });
        Console.WriteLine(created
            ? $"    {tenantTable} — created"
            : $"    {tenantTable} — exists");

        // Subtenant tables: {SystemKey}_{TenantKey}_{SubtenantKey}
        if (_tenantConfig?.Subtenants != null)
        {
            foreach (var sub in _tenantConfig.Subtenants)
            {
                var subTable = $"{sk}_{tk}_{sub.Key}";
                var subCreated = await DynamoDbTableCreator.EnsureTableAsync(
                    profile, region, subTable,
                    new Dictionary<string, string>(baseTags)
                    {
                        { "Subtenant", sub.Key },
                        { "Level", "subtenant" },
                    });
                Console.WriteLine(subCreated
                    ? $"    {subTable} — created"
                    : $"    {subTable} — exists");
            }
        }
    }

    private async Task ForceNewDeploymentAsync(string clusterName, string serviceName)
    {
        try
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_config.Profile, out var credentials))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Failed to resolve credentials for profile '{_config.Profile}'");
                Console.ResetColor();
                return;
            }

            using var client = new Amazon.ECS.AmazonECSClient(
                credentials,
                Amazon.RegionEndpoint.GetBySystemName(_config.Region));

            var describeResponse = await client.DescribeServicesAsync(
                new Amazon.ECS.Model.DescribeServicesRequest
                {
                    Cluster = clusterName,
                    Services = new List<string> { serviceName },
                });

            var service = describeResponse.Services.FirstOrDefault();
            if (service == null || service.Status != "ACTIVE")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ECS service '{serviceName}' not found or not active.");
                Console.ResetColor();
                return;
            }

            await client.UpdateServiceAsync(
                new Amazon.ECS.Model.UpdateServiceRequest
                {
                    Cluster = clusterName,
                    Service = serviceName,
                    ForceNewDeployment = true,
                });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ECS deployment triggered for {serviceName}.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Could not trigger ECS deployment for {serviceName}: {ex.Message}");
            Console.ResetColor();
        }
    }
}
