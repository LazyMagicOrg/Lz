using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS services post-deploy:
///   1. Uploads tenant config to SSM Parameter Store (if deploying a tenant)
///   2. Scales ECS services from 0 → configured count
///
/// Docker image building/pushing is handled separately by `lz deploycontainer`.
/// </summary>
public class AwsServicesPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;
    private readonly SystemDefinition _system;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _tenantKey;
    private readonly TenantConfig? _tenantConfig;

    public AwsServicesPostDeployAction(
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
        var sk = _config.SystemKey;
        var env = _config.Environment;
        var region = _config.Region;
        var profile = _config.Profile;
        var ecs = _config.ECS ?? new EcsConfig();

        // Upload tenant config to SSM Parameter Store (if deploying a tenant)
        if (_tenantKey != null)
        {
            var monorepoRoot = ConfigLoader.DiscoverMonorepoRoot(sk, env);
            if (monorepoRoot != null)
            {
                await UploadTenantConfigToSsmAsync(monorepoRoot, sk, env, region, profile);
            }
        }

        // Scale ECS services
        var servicesToScale = _services
            .Where(s => s.Docker != null)
            .Select(s => s.Name)
            .ToList();

        if (servicesToScale.Count == 0)
        {
            Console.WriteLine("No services to scale.");
            return;
        }

        var ecsClient = CreateEcsClient(region, profile);
        var clusterName = $"{sk}-cluster";
        var desiredCount = ecs.ServiceDesiredCount > 0 ? ecs.ServiceDesiredCount : 1;

        foreach (var serviceName in servicesToScale)
        {
            var ecsServiceName = _tenantKey != null
                ? $"{sk}-{_tenantKey}-{serviceName}"
                : $"{sk}-{serviceName}";
            Console.WriteLine($"Scaling {ecsServiceName} to {desiredCount}...");
            try
            {
                await ecsClient.UpdateServiceAsync(new UpdateServiceRequest
                {
                    Cluster = clusterName,
                    Service = ecsServiceName,
                    DesiredCount = desiredCount,
                    ForceNewDeployment = true, // Ensures tasks pick up the new image
                });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  {ecsServiceName} scaled to {desiredCount}.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Failed to scale {ecsServiceName}: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    // ---------------------------------------------------------------
    // SSM config provisioning
    // ---------------------------------------------------------------

    /// <summary>
    /// Uploads tenant configuration to SSM Parameter Store:
    ///   1. tenantconfig YAML → /{systemKey}/{tenantKey}/{env}/tenantconfig
    ///   2. smartstore.usersettings.json → /{systemKey}/{tenantKey}/{env}/smartstore-usersettings
    /// Each is stored as a separate String parameter to stay within the 4096-char standard tier limit.
    /// </summary>
    private async Task UploadTenantConfigToSsmAsync(
        string monorepoRoot, string prefix, string env, string region, string profile)
    {
        var sk = _config.SystemKey;
        var tk = _tenantKey!;

        // --- Upload tenantconfig YAML ---
        var configFilename = $"tenantconfig.{sk}.{tk}.{env}.yaml";
        var configSource = Path.Combine(monorepoRoot, configFilename);

        if (!File.Exists(configSource))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  No {configFilename} found — skipping SSM config upload.");
            Console.ResetColor();
        }
        else
        {
            var yamlContent = await File.ReadAllTextAsync(configSource);
            var paramName = $"/{sk}/{tk}/{env}/tenantconfig";

            Console.WriteLine($"Uploading tenant config to SSM: {paramName}");

            try
            {
                await AwsAccountResolver.WriteSsmParameterAsync(
                    profile, region, paramName, yamlContent,
                    description: $"Tenant config for {sk}/{tk}/{env}");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Tenant config uploaded to SSM ({yamlContent.Length} bytes).");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Failed to upload tenant config to SSM: {ex.Message}");
                Console.ResetColor();
            }
        }

        // --- Upload smartstore.usersettings.json (separate parameter) ---
        var userSettingsSource = Path.Combine(monorepoRoot, "smartstore.usersettings.json");

        if (!File.Exists(userSettingsSource))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  No smartstore.usersettings.json found — skipping.");
            Console.ResetColor();
            return;
        }

        var jsonContent = await File.ReadAllTextAsync(userSettingsSource);
        var userSettingsParam = $"/{sk}/{tk}/{env}/smartstore-usersettings";

        Console.WriteLine($"Uploading SmartStore user settings to SSM: {userSettingsParam}");

        try
        {
            await AwsAccountResolver.WriteSsmParameterAsync(
                profile, region, userSettingsParam, jsonContent,
                description: $"SmartStore usersettings.json for {sk}/{tk}/{env}");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  SmartStore user settings uploaded to SSM ({jsonContent.Length} bytes).");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: Failed to upload SmartStore user settings to SSM: {ex.Message}");
            Console.ResetColor();
        }
    }

    // ---------------------------------------------------------------
    // AWS client helpers
    // ---------------------------------------------------------------

    private static AmazonECSClient CreateEcsClient(string region, string profile)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"AWS profile '{profile}' not found.");

        return new AmazonECSClient(credentials, Amazon.RegionEndpoint.GetBySystemName(region));
    }
}
