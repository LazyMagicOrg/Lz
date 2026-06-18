using Lz.Core.Keycloak;
using System.Text.Json;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Core.Interfaces;

namespace Lz.Aws.Ecs;

/// <summary>
/// Seeds per-tenant Keycloak realms via the shared-services Keycloak Admin API.
/// Connects to the shared Keycloak instance using admin credentials from the
/// shared/system secret, then seeds realms/clients/roles/groups from the
/// given KeycloakSeedConfig (either a tenant-specific file or a resolved template).
///
/// Unlike the foundation post-deploy (which skips seeding when admin blocking is ON
/// because the public auth endpoint was used during initial bootstrap), tenant seeding
/// uses the CentralAuthDomain which is reachable via VPN. Developers running
/// deploytenant are expected to be on the VPN.
/// </summary>
public class AwsTenantKeycloakSeeder : ITenantKeycloakSeeder
{
    private readonly SystemConfig _config;

    public AwsTenantKeycloakSeeder(SystemConfig config)
    {
        _config = config;
    }

    private string? _tenantKey;

    public async Task SeedAsync(KeycloakSeedConfig seedConfig, string tenantKey)
    {
        _tenantKey = tenantKey;
        Console.WriteLine();
        Console.WriteLine("Seeding tenant Keycloak configuration...");

        // Connect to the shared Keycloak instance via CentralAuthDomain (reachable via VPN)
        var keycloakUrl = $"https://{_config.CentralAuthDomain}";
        Console.WriteLine($"  Keycloak URL: {keycloakUrl}");

        // Get admin credentials from shared/system secret
        var (adminUsername, adminPassword) = await GetAdminCredentialsAsync();
        Console.WriteLine("  Admin credentials retrieved from shared/system secret.");

        // Create admin client, authenticate, and seed
        using var adminClient = new KeycloakAdminClient(keycloakUrl, adminUsername, adminPassword);
        await adminClient.WaitForReadyAsync(timeoutSeconds: 120, pollIntervalSeconds: 5);

        Console.WriteLine("  Authenticating to Keycloak...");
        await adminClient.AuthenticateAsync();
        Console.WriteLine("  Authenticated successfully.");

        // Seed (no bootstrap creds needed — SMTP password is already in the config from template replacement)
        var seeder = new KeycloakSeeder(adminClient, seedConfig);
        await seeder.SeedAsync();

        // Store confidential client secrets in the tenant secret
        await StoreClientSecretsAsync(adminClient, seedConfig);
    }

    /// <summary>
    /// After seeding, retrieve client secrets for all confidential (non-public) clients
    /// and store them in the tenant secret with key format: keycloak_{realmName}_{clientId}
    /// </summary>
    private async Task StoreClientSecretsAsync(KeycloakAdminClient adminClient, KeycloakSeedConfig seedConfig)
    {
        if (seedConfig.Realms == null) return;

        var tenantSecretId = $"{_config.SystemKey}/{_tenantKey}";
        var smClient = CreateSecretsManagerClient(_config.Region, _config.Profile);

        // Read current tenant secret
        Dictionary<string, object> secretData;
        try
        {
            var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = tenantSecretId,
            });
            using var doc = JsonDocument.Parse(response.SecretString);
            secretData = JsonSerializer.Deserialize<Dictionary<string, object>>(doc.RootElement.GetRawText())
                ?? new Dictionary<string, object>();
        }
        catch (ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: Tenant secret '{tenantSecretId}' not found — skipping client secret storage");
            Console.ResetColor();
            return;
        }

        var stored = 0;
        foreach (var (realmName, realmConfig) in seedConfig.Realms)
        {
            if (realmName == "master" || realmConfig.Clients == null) continue;

            foreach (var clientDef in realmConfig.Clients)
            {
                // Skip public clients — they don't have secrets
                if (clientDef.PublicClient == true) continue;

                var secretKey = $"keycloak_{realmName}_{clientDef.ClientId}";

                // Skip if already stored
                if (secretData.ContainsKey(secretKey)) continue;

                var clientUuid = await adminClient.FindClientUuidAsync(realmName, clientDef.ClientId);
                if (clientUuid == null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  WARNING: Client '{clientDef.ClientId}' not found in realm '{realmName}'");
                    Console.ResetColor();
                    continue;
                }

                var clientSecret = await adminClient.GetClientSecretAsync(realmName, clientUuid);
                if (string.IsNullOrEmpty(clientSecret))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  WARNING: No secret for client '{clientDef.ClientId}' in realm '{realmName}'");
                    Console.ResetColor();
                    continue;
                }

                secretData[secretKey] = clientSecret;
                stored++;
            }
        }

        if (stored > 0)
        {
            await smClient.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = tenantSecretId,
                SecretString = JsonSerializer.Serialize(secretData),
            });
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Stored {stored} Keycloak client secret(s) in '{tenantSecretId}'");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  All Keycloak client secrets already stored.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Read admin credentials from shared/system secret.
    /// </summary>
    private async Task<(string username, string password)> GetAdminCredentialsAsync()
    {
        var smClient = CreateSecretsManagerClient(
            _config.Aws().SharedRegion ?? _config.Region,
            _config.Aws().SharedProfile ?? _config.Profile);

        var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = "shared/system",
        });

        using var doc = JsonDocument.Parse(response.SecretString);
        var root = doc.RootElement;

        var username = root.GetProperty("keycloak-admin-username").GetString()
            ?? throw new InvalidOperationException("keycloak-admin-username not found in shared/system secret");
        var password = root.GetProperty("keycloak-admin-password").GetString()
            ?? throw new InvalidOperationException("keycloak-admin-password not found in shared/system secret");

        return (username, password);
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
