using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Lz.Aws.Keycloak;
using Lz.Aws.Lambda;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS foundation post-deploy:
///   1. Runs the system-init ECS task (CREATE DATABASE keycloak)
///   2. Scales Keycloak from 0 → 1
///   3. Waits for Keycloak to be healthy
///   4. Seeds Keycloak realms/clients/roles/groups from keycloakconfig YAML
///   5. Retrieves the Tailscale OIDC client secret and stores it in the system secret
/// Resolves all needed values from AWS using known naming conventions.
/// </summary>
public class AwsFoundationPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;

    public AwsFoundationPostDeployAction(SystemConfig config)
    {
        _config = config;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        var prefix = _config.SystemKey;
        var clusterName = $"{prefix}-cluster";
        var keycloakServiceName = $"{prefix}-keycloak";
        var initTaskFamily = $"{prefix}-system-init";

        var ecsClient = CreateEcsClient(_config.Region, _config.Profile);

        // Get network config from the Keycloak service (subnets + security groups)
        var svcResponse = await ecsClient.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = clusterName,
            Services = [keycloakServiceName],
        });

        var keycloakSvc = svcResponse.Services.FirstOrDefault()
            ?? throw new InvalidOperationException($"Service {keycloakServiceName} not found in cluster {clusterName}");

        var netConfig = keycloakSvc.NetworkConfiguration.AwsvpcConfiguration;
        var subnetIds = netConfig.Subnets;
        var securityGroups = netConfig.SecurityGroups;

        // Step 1-2: Run system-init task + scale Keycloak
        await AwsEcsPostDeployHelper.RunSystemInitAndScaleAsync(
            ecsClient,
            keycloakSvc.ClusterArn,
            initTaskFamily,
            keycloakServiceName,
            subnetIds,
            securityGroups.First());

        // Step 2.5: Deploy Keycloak themes to EFS (before seeding, so themes are
        // available when Keycloak boots and realms reference them)
        await DeployKeycloakThemesAsync();

        // Step 3-4: Seed Keycloak configuration (if config file exists)
        await SeedKeycloakAsync();
    }

    /// <summary>
    /// Discover keycloak seed config, wait for Keycloak healthy, and seed.
    /// </summary>
    private async Task SeedKeycloakAsync()
    {
        // Discover keycloakconfig.{systemkey}.{env}.yaml
        var seedConfig = ConfigLoader.DiscoverKeycloakSeedConfig(
            _config.SystemKey, _config.Environment);

        if (seedConfig == null)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("No keycloakconfig file found — skipping Keycloak seeding.");
            Console.WriteLine($"  (Looked for: keycloakconfig.{_config.SystemKey}.{_config.Environment}.yaml)");
            Console.ResetColor();
            return;
        }

        // Discover optional bootstrap credentials (credsconfig.{systemkey}.{env}.yaml)
        var credsConfig = ConfigLoader.DiscoverBootstrapCredsConfig(
            _config.SystemKey, _config.Environment);

        if (credsConfig != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Bootstrap credentials loaded from credsconfig.");
            Console.ResetColor();
        }

        Console.WriteLine();

        // When admin blocking is ON, the Keycloak admin API is only reachable via VPN
        // (auth.{domain} resolves to the internal ALB via private DNS). Seeding was
        // already done on the first deploy when blocking was OFF. Skip re-seeding.
        var adminBlockingEnabled = await CheckTailscaleAuthKeyExistsAsync();
        if (adminBlockingEnabled)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Skipping Keycloak seeding — admin blocking is ON.");
            Console.WriteLine("  Realm was seeded on the initial deploy. To re-seed, connect via VPN");
            Console.WriteLine($"  and use https://auth.{_config.SystemDomain}/admin/");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("Seeding Keycloak configuration...");

        var systemDomain = _config.SystemDomain;
        var keycloakUrl = $"https://auth.{systemDomain}";

        Console.WriteLine($"  Keycloak admin URL: {keycloakUrl}");

        // Get admin credentials from Secrets Manager
        var (adminUsername, adminPassword) = await GetAdminCredentialsAsync();
        Console.WriteLine("  Admin credentials retrieved from Secrets Manager.");

        // Create admin client and wait for Keycloak to be ready
        using var adminClient = new KeycloakAdminClient(keycloakUrl, adminUsername, adminPassword);
        await adminClient.WaitForReadyAsync(timeoutSeconds: 300, pollIntervalSeconds: 5);

        // Authenticate
        Console.WriteLine("  Authenticating to Keycloak...");
        await adminClient.AuthenticateAsync();
        Console.WriteLine("  Authenticated successfully.");

        // Seed (pass bootstrap creds for SMTP password + user creation)
        var seeder = new KeycloakSeeder(adminClient, seedConfig, credsConfig);
        await seeder.SeedAsync();

        // Retrieve Tailscale OIDC client secret and store in system secret
        await StoreTailscaleClientSecretAsync(adminClient);
    }

    /// <summary>
    /// Discover tenant keycloakconfig files, read themeSource from each,
    /// and deploy unique themes to EFS via the gate-checker Lambda.
    /// </summary>
    private async Task DeployKeycloakThemesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Checking for Keycloak themes to deploy...");

        // Discover all tenant keycloakconfig template files to find themeSource entries.
        // Convention: keycloakconfig.system.tenant.{env}.yaml in the monorepo root.
        var dir = Directory.GetCurrentDirectory();
        var pattern = $"keycloakconfig.system.tenant.{_config.Environment}.yaml";
        var configPath = ConfigLoader.DiscoverConfigFile(dir, pattern);

        if (configPath == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  No tenant keycloakconfig template found ({pattern}) — skipping theme deploy.");
            Console.ResetColor();
            return;
        }

        // Load the YAML to read themeSource (top-level field)
        var seedConfig = ConfigLoader.LoadKeycloakSeedConfig(configPath);
        if (seedConfig?.ThemeSource == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  No themeSource specified in keycloakconfig — skipping theme deploy.");
            Console.ResetColor();
            return;
        }

        var themeSource = seedConfig.ThemeSource;
        var themeName = Path.GetFileName(themeSource.TrimEnd('/'));

        // Resolve the theme source path relative to monorepo root
        var monorepoRoot = Path.GetDirectoryName(configPath)!;
        var themeSourcePath = Path.Combine(monorepoRoot, themeSource);

        if (!Directory.Exists(themeSourcePath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Theme source directory not found: {themeSourcePath}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"  Deploying theme '{themeName}' from {themeSource}...");

        // Themes bucket is in the shared-services account; derive name from SharedConfig
        var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
        var themesBucket = $"keycloak-themes-{sharedConfig.SharedSuffix}";

        var runner = new AwsLambdaThemeDeployRunner(_config, themesBucket);
        var success = await runner.DeployThemeAsync(themeName, themeSourcePath);

        if (!success)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Theme deployment failed — Keycloak will use default themes.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Look up the tailscale client in adminsauth, retrieve its secret,
    /// and store it in the system secret in Secrets Manager.
    /// </summary>
    private async Task StoreTailscaleClientSecretAsync(KeycloakAdminClient adminClient)
    {
        const string realm = "adminsauth";
        const string clientId = "tailscale";
        const string secretKey = "tailscale-oidc-client-secret";

        Console.WriteLine();
        Console.WriteLine("Retrieving Tailscale OIDC client secret...");

        var clientUuid = await adminClient.FindClientUuidAsync(realm, clientId);
        if (clientUuid == null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Client '{clientId}' not found in {realm} — skipping secret retrieval.");
            Console.ResetColor();
            return;
        }

        var clientSecret = await adminClient.GetClientSecretAsync(realm, clientUuid);
        if (string.IsNullOrEmpty(clientSecret))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  No secret available for '{clientId}' (is it a public client?).");
            Console.ResetColor();
            return;
        }

        // Store in the system secret
        var secretId = $"{_config.SystemKey}/system";
        var smClient = CreateSecretsManagerClient(_config.Region, _config.Profile);

        var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
        using var doc = JsonDocument.Parse(response.SecretString);
        var existing = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            existing[prop.Name] = prop.Value.GetString() ?? "";

        // Only update if changed
        if (existing.TryGetValue(secretKey, out var current) && current == clientSecret)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Tailscale client secret already stored in {secretId}.");
            Console.ResetColor();
            return;
        }

        existing[secretKey] = clientSecret;
        var updatedJson = JsonSerializer.Serialize(existing);
        await smClient.PutSecretValueAsync(new PutSecretValueRequest
        {
            SecretId = secretId,
            SecretString = updatedJson,
        });

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Tailscale client secret stored in {secretId} as '{secretKey}'.");
        Console.ResetColor();
    }

    /// <summary>
    /// Read admin credentials from {SystemKey}/system secret in Secrets Manager.
    /// </summary>
    private async Task<(string username, string password)> GetAdminCredentialsAsync()
    {
        var secretId = $"{_config.SystemKey}/system";
        var smClient = CreateSecretsManagerClient(_config.Region, _config.Profile);

        var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = secretId,
        });

        using var doc = JsonDocument.Parse(response.SecretString);
        var root = doc.RootElement;

        var username = root.GetProperty("keycloak-admin-username").GetString()
            ?? throw new InvalidOperationException("keycloak-admin-username not found in system secret");
        var password = root.GetProperty("keycloak-admin-password").GetString()
            ?? throw new InvalidOperationException("keycloak-admin-password not found in system secret");

        return (username, password);
    }

    /// <summary>
    /// Check if tailscale-auth-key exists in the system secret.
    /// Returns true if Tailscale is configured (admin blocking should be enabled).
    /// </summary>
    private async Task<bool> CheckTailscaleAuthKeyExistsAsync()
    {
        var secretId = $"{_config.SystemKey}/system";
        try
        {
            var smClient = CreateSecretsManagerClient(_config.Region, _config.Profile);
            var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretId,
            });

            using var doc = JsonDocument.Parse(response.SecretString);
            if (doc.RootElement.TryGetProperty("tailscale-auth-key", out var value))
            {
                var str = value.GetString();
                return !string.IsNullOrEmpty(str);
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
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

    private static AmazonSecretsManagerClient CreateSecretsManagerClient(string region, string? profile)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonSecretsManagerClient(credentials, regionEndpoint);
        }

        return new AmazonSecretsManagerClient(regionEndpoint);
    }
}
