using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Task = System.Threading.Tasks.Task;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Topologies;

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
        var ecs = _config.Aws().ECS ?? new EcsConfig();

        // Upload tenant config to SSM Parameter Store (if deploying a tenant)
        if (_tenantKey != null)
        {
            var monorepoRoot = ConfigLoader.DiscoverMonorepoRoot(sk, env);
            if (monorepoRoot != null)
            {
                await UploadTenantConfigToSsmAsync(monorepoRoot, sk, env, region, profile);
            }
        }

        // Ensure foundation service credentials before scaling
        if (_tenantKey == null && _services.Any(s => s.Name.Equals("livekit", StringComparison.OrdinalIgnoreCase)))
        {
            await EnsureLiveKitCredentialsAsync(region, profile);
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
    // Foundation service credentials
    // ---------------------------------------------------------------

    private async Task EnsureLiveKitCredentialsAsync(string region, string profile)
    {
        var secretName = $"{_config.SystemKey}/system";
        var smClient = CreateSecretsManagerClient(region, profile);

        try
        {
            var resp = await smClient.GetSecretValueAsync(new Amazon.SecretsManager.Model.GetSecretValueRequest
            {
                SecretId = secretName
            });
            var secretData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(resp.SecretString)
                ?? new Dictionary<string, string>();

            bool changed = false;

            if (!secretData.ContainsKey("livekit-api-key") || string.IsNullOrEmpty(secretData["livekit-api-key"]))
            {
                secretData["livekit-api-key"] = $"API{GenerateRandomString(12)}";
                changed = true;
            }

            if (!secretData.ContainsKey("livekit-api-secret") || string.IsNullOrEmpty(secretData["livekit-api-secret"]))
            {
                secretData["livekit-api-secret"] = GenerateRandomString(40);
                changed = true;
            }

            // Composite value for LIVEKIT_KEYS env var
            var apiKey = secretData.GetValueOrDefault("livekit-api-key", "");
            var apiSecret = secretData.GetValueOrDefault("livekit-api-secret", "");
            var expectedKeys = $"{apiKey}: {apiSecret}";
            if (secretData.GetValueOrDefault("livekit-keys", "") != expectedKeys)
            {
                secretData["livekit-keys"] = expectedKeys;
                changed = true;
            }

            if (changed)
            {
                await smClient.PutSecretValueAsync(new Amazon.SecretsManager.Model.PutSecretValueRequest
                {
                    SecretId = secretName,
                    SecretString = System.Text.Json.JsonSerializer.Serialize(secretData),
                });
                Console.WriteLine("  LiveKit API credentials generated and stored in system secret.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  LiveKit API credentials already exist in system secret.");
                Console.ResetColor();
            }
        }
        catch (Amazon.SecretsManager.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: System secret '{secretName}' not found. LiveKit credentials not stored.");
            Console.ResetColor();
        }
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[length];
        random.GetBytes(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static Amazon.SecretsManager.AmazonSecretsManagerClient CreateSecretsManagerClient(string region, string? profile)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new Amazon.SecretsManager.AmazonSecretsManagerClient(credentials, regionEndpoint);
        }
        return new Amazon.SecretsManager.AmazonSecretsManagerClient(regionEndpoint);
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

            // Replace self-referencing placeholders before upload.
            // The tenantconfig YAML uses <<rootdomain>>, <<centralauthdomain>>, etc.
            // as placeholders that reference the config's own fields.
            if (_tenantConfig != null)
            {
                yamlContent = yamlContent.Replace("<<rootdomain>>", _tenantConfig.RootDomain);
                yamlContent = yamlContent.Replace("<<centralauthdomain>>", _tenantConfig.CentralAuthDomain ?? "");

                var legacyDomain = _tenantConfig.LegacyDomains?.FirstOrDefault();
                if (!string.IsNullOrEmpty(legacyDomain))
                {
                    yamlContent = yamlContent.Replace("<<legacydomain>>", legacyDomain);
                }
                else
                {
                    // Remove entire lines containing the placeholder
                    yamlContent = string.Join("\n",
                        yamlContent.Split('\n').Where(line => !line.Contains("<<legacydomain>>")));
                }
            }

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
