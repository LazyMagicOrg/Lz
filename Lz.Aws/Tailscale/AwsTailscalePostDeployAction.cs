using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Lz.Core.Config;
using Lz.Core.Tailscale;
using Lz.Aws.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Tailscale;

/// <summary>
/// AWS Tailscale post-deploy action and auth key manager.
/// Post-deploy: waits for devices, approves routes, disables key expiry, configures split DNS.
/// Key manager: ensures a valid auth key exists in Secrets Manager (creates via API if needed).
/// </summary>
public class AwsTailscalePostDeployAction : IPostDeployAction, ITailscaleKeyManager
{
    private readonly SystemConfig _config;
    private readonly SystemDefinition? _system;

    public AwsTailscalePostDeployAction(SystemConfig config, SystemDefinition? system = null)
    {
        _config = config;
        _system = system;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        // 1. Retrieve the Tailscale API key
        var apiKey = await GetTailscaleApiKeyAsync();
        Console.WriteLine("  Tailscale API key retrieved from Secrets Manager.");

        using var client = new TailscaleApiClient(apiKey);

        var hostnamePrefix = $"{_config.SystemKey}-{_config.Environment}-efs";

        // 2. Snapshot current Tailscale device IDs before recycling, so we know
        //    exactly which devices to delete after their backing EC2 instances
        //    are terminated.
        var preRecycleDevices = (await client.ListDevicesAsync())
            .Where(d => d.Hostname.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase)
                     || d.Name.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Id)
            .ToHashSet();

        // 3. Recycle any instances running an older launch template version.
        var recycledCount = await RecycleStaleInstancesAsync();

        // 4. If instances were recycled, delete the pre-recycle Tailscale devices.
        //    We ONLY delete devices when we know their backing instances were just
        //    terminated. We never speculatively delete based on online/offline status
        //    or lastSeen — the Tailscale API can transiently report live devices as
        //    offline, and deleting a live device deauthorises it permanently.
        if (recycledCount > 0 && preRecycleDevices.Count > 0)
        {
            Console.WriteLine("  Waiting for recycled instances to disconnect...");
            await Task.Delay(TimeSpan.FromSeconds(15));

            await DeleteDevicesByIdAsync(client, preRecycleDevices, hostnamePrefix);
        }

        // 4. Wait for subnet router devices to register
        var devices = await client.WaitForDevicesAsync(hostnamePrefix, minCount: 1, timeoutSeconds: 180);

        // 5-6. For each device: approve routes + disable key expiry.
        //    Do NOT filter by Online status — the Tailscale API can transiently
        //    report connected devices as offline. Approving routes and disabling
        //    key expiry on an offline device is harmless and takes effect when
        //    it reconnects.
        var vpcCidr = _config.VpcCidr;
        var configuredCount = 0;

        foreach (var device in devices)
        {
            var displayName = !string.IsNullOrEmpty(device.Hostname) ? device.Hostname : device.Name;
            Console.WriteLine($"  Configuring device: {displayName} ({device.Id})");

            try
            {
                // Approve subnet routes
                await client.SetDeviceRoutesAsync(device.Id, [vpcCidr]);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Routes approved: {vpcCidr}");
                Console.ResetColor();

                // Disable key expiry
                await client.DisableKeyExpiryAsync(device.Id);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Key expiry disabled.");
                Console.ResetColor();

                configuredCount++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    Warning: Failed to configure {displayName}: {ex.Message}");
                Console.ResetColor();
            }
        }

        // 6. Split DNS — per-tenant domains are added by UpdateTenantSplitDnsAsync
        // during deploytenant. Foundation does not add split DNS entries because
        // the {systemKey}.private zone hosts don't match any ALB listener rules.
        // Tenant deploy adds RootDomain entries so VPN users can reach
        // shop.{RootDomain} via VPC DNS → per-tenant private zone → internal ALB.

        // 7. Summary
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Tailscale configuration complete: {configuredCount} device(s) configured.");
        Console.ResetColor();
    }

    /// <summary>
    /// Calculate the VPC DNS resolver IP from the VPC CIDR.
    /// AWS VPC DNS is always at the base IP + 2.
    /// e.g., "10.10.0.0/16" → "10.10.0.2"
    /// </summary>
    internal static string CalculateVpcDnsResolver(string vpcCidr)
    {
        var cidrParts = vpcCidr.Split('/');
        var ipBytes = IPAddress.Parse(cidrParts[0]).GetAddressBytes();

        // Add 2 to the IP address (big-endian byte array)
        // Start from the least significant byte
        var carry = 2;
        for (var i = ipBytes.Length - 1; i >= 0 && carry > 0; i--)
        {
            var sum = ipBytes[i] + carry;
            ipBytes[i] = (byte)(sum & 0xFF);
            carry = sum >> 8;
        }

        return new IPAddress(ipBytes).ToString();
    }

    // ---------------------------------------------------------------
    // ITailscaleKeyManager — auth key lifecycle
    // ---------------------------------------------------------------

    /// <summary>
    /// Ensure a valid Tailscale auth key exists in the shared/system secret.
    /// If the key is missing, expired, or revoked, creates a new one via the API.
    /// Call this BEFORE deploying the Tailscale ASG so instances boot with a valid key.
    /// </summary>
    public async Task EnsureAuthKeyAsync()
    {
        var secret = await GetSharedSecretAsync();

        var apiKey = GetSecretValue(secret, "tailscale-api-key")
            ?? throw new InvalidOperationException("tailscale-api-key not found in shared/system secret");

        using var client = new TailscaleApiClient(apiKey);

        // Check if existing auth key is still valid
        var existingKeyId = GetSecretValue(secret, "tailscale-auth-key-id");
        var existingAuthKey = GetSecretValue(secret, "tailscale-auth-key");

        if (!string.IsNullOrEmpty(existingKeyId) && !string.IsNullOrEmpty(existingAuthKey))
        {
            var isValid = await client.IsAuthKeyValidAsync(existingKeyId);
            if (isValid)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  Tailscale auth key is valid.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Existing Tailscale auth key is expired or invalid.");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("  No Tailscale auth key found.");
        }

        // Create a new auth key via the API
        Console.WriteLine("  Creating new Tailscale auth key via API...");
        var newKey = await client.CreateAuthKeyAsync();

        // Write the new key + key ID to Secrets Manager
        await UpdateAuthKeyInSecretAsync(secret, newKey.Key, newKey.Id);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Auth key created (expires: {newKey.Expires}).");
        Console.ResetColor();
    }

    // ---------------------------------------------------------------
    // ITailscaleKeyManager — SSH key lifecycle
    // ---------------------------------------------------------------

    /// <summary>
    /// Ensure an SSH key pair exists in the shared/system secret for SFTP
    /// access to Tailscale router instances. Generates a new RSA 4096-bit
    /// key pair if one doesn't already exist.
    /// </summary>
    public async Task EnsureSshKeyAsync()
    {
        var secret = await GetSharedSecretAsync();

        var existingPubKey = GetSecretValue(secret, "tailscale-ssh-public-key");
        if (!string.IsNullOrEmpty(existingPubKey))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Tailscale SSH key is present.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("  Generating SSH key pair for Tailscale SFTP access...");

        using var rsa = RSA.Create(4096);
        var privateKeyPem = rsa.ExportRSAPrivateKeyPem();
        var publicKeyOpenSsh = FormatOpenSshPublicKey(rsa);

        secret["tailscale-ssh-private-key"] = privateKeyPem;
        secret["tailscale-ssh-public-key"] = publicKeyOpenSsh;

        await UpdateSecretAsync(secret);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  SSH key pair generated and stored in Secrets Manager.");
        Console.ResetColor();
    }

    /// <summary>
    /// Format an RSA public key in OpenSSH authorized_keys format (ssh-rsa AAAA...).
    /// Wire format: string("ssh-rsa") + mpint(e) + mpint(n), then base64-encoded.
    /// </summary>
    private static string FormatOpenSshPublicKey(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);

        using var ms = new MemoryStream();

        // Write "ssh-rsa" string
        WriteSshBytes(ms, "ssh-rsa"u8.ToArray());

        // Write exponent (e)
        WriteSshMpint(ms, parameters.Exponent!);

        // Write modulus (n)
        WriteSshMpint(ms, parameters.Modulus!);

        return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} lz-tailscale-efs";
    }

    /// <summary>
    /// Write a length-prefixed byte array in SSH wire format.
    /// </summary>
    private static void WriteSshBytes(MemoryStream ms, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);
        ms.Write(len, 0, 4);
        ms.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Write an SSH mpint (multi-precision integer) — big-endian unsigned,
    /// with a leading 0x00 byte if the high bit is set (to prevent being
    /// interpreted as negative).
    /// </summary>
    private static void WriteSshMpint(MemoryStream ms, byte[] value)
    {
        if (value[0] >= 0x80)
        {
            // Prepend 0x00 to indicate positive number
            var padded = new byte[value.Length + 1];
            padded[0] = 0;
            Buffer.BlockCopy(value, 0, padded, 1, value.Length);
            WriteSshBytes(ms, padded);
        }
        else
        {
            WriteSshBytes(ms, value);
        }
    }

    // ---------------------------------------------------------------
    // Tailscale device cleanup
    // ---------------------------------------------------------------

    /// <summary>
    /// Delete specific Tailscale devices by their IDs.
    /// Used after RecycleStaleInstancesAsync to remove the device registrations
    /// for instances that were just terminated. We know exactly which devices
    /// to remove because we snapshotted them before recycling.
    /// </summary>
    private static async Task DeleteDevicesByIdAsync(
        TailscaleApiClient client, HashSet<string> deviceIds, string hostnamePrefix)
    {
        // Re-fetch to get current state and display names
        var allDevices = await client.ListDevicesAsync();
        var toDelete = allDevices
            .Where(d => deviceIds.Contains(d.Id)
                     && (d.Hostname.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase)
                      || d.Name.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (toDelete.Count == 0)
            return;

        Console.WriteLine($"  Removing {toDelete.Count} recycled device(s)...");

        foreach (var device in toDelete)
        {
            var displayName = !string.IsNullOrEmpty(device.Hostname) ? device.Hostname : device.Name;
            try
            {
                await client.DeleteDeviceAsync(device.Id);
                Console.WriteLine($"    Removed: {displayName} ({device.Id})");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    Warning: Could not remove {displayName}: {ex.Message}");
                Console.ResetColor();
            }
        }
    }


    // ---------------------------------------------------------------
    // Instance recycling
    // ---------------------------------------------------------------

    /// <summary>
    /// Compare each running ASG instance's launch template version against
    /// the latest version. Terminate any that are stale — ASG auto-replaces
    /// them with instances using the current launch template.
    /// Returns the number of instances terminated.
    /// </summary>
    private async Task<int> RecycleStaleInstancesAsync()
    {
        var prefix = _config.SystemKey;
        var asgName = $"{prefix}-tailscale-asg";
        var ltName = $"{prefix}-tailscale-lt";
        var region = _config.Region;
        var profile = _config.Profile;
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        using var ec2Client = CreateAwsClient<AmazonEC2Client>(regionEndpoint, profile);
        using var asgClient = CreateAwsClient<AmazonAutoScalingClient>(regionEndpoint, profile);

        // Get latest launch template version number
        var ltResponse = await ec2Client.DescribeLaunchTemplateVersionsAsync(
            new DescribeLaunchTemplateVersionsRequest
            {
                LaunchTemplateName = ltName,
                Versions = ["$Latest"],
            });

        if (ltResponse.LaunchTemplateVersions.Count == 0)
        {
            Console.WriteLine("  Launch template not found — skipping instance recycle.");
            return 0;
        }

        var latestVersion = ltResponse.LaunchTemplateVersions[0].VersionNumber;

        // Get running instances in the ASG
        var asgResponse = await asgClient.DescribeAutoScalingGroupsAsync(
            new DescribeAutoScalingGroupsRequest
            {
                AutoScalingGroupNames = [asgName],
            });

        if (asgResponse.AutoScalingGroups.Count == 0)
        {
            Console.WriteLine("  ASG not found — skipping instance recycle.");
            return 0;
        }

        var instances = asgResponse.AutoScalingGroups[0].Instances;
        var staleInstances = new List<string>();

        foreach (var instance in instances)
        {
            if (instance.LaunchTemplate == null) continue;

            // Parse version — ASG may report "$Latest" or a numeric version
            var versionStr = instance.LaunchTemplate.Version;
            if (long.TryParse(versionStr, out var instanceVersion) && instanceVersion < latestVersion)
            {
                staleInstances.Add(instance.InstanceId);
            }
        }

        if (staleInstances.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  All {instances.Count} instance(s) are on launch template v{latestVersion}.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Recycling {staleInstances.Count} instance(s) on older launch template versions...");
        Console.ResetColor();

        foreach (var instanceId in staleInstances)
        {
            Console.WriteLine($"    Terminating {instanceId} (ASG will replace)...");
            await asgClient.TerminateInstanceInAutoScalingGroupAsync(
                new TerminateInstanceInAutoScalingGroupRequest
                {
                    InstanceId = instanceId,
                    ShouldDecrementDesiredCapacity = false,
                });
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {staleInstances.Count} stale instance(s) terminated — ASG replacing with v{latestVersion}.");
        Console.ResetColor();
        return staleInstances.Count;
    }

    // ---------------------------------------------------------------
    // Secrets Manager helpers
    // ---------------------------------------------------------------

    private const string SharedSecretId = "shared/system";

    /// <summary>
    /// Read the shared/system secret and return all key-value pairs.
    /// </summary>
    private async Task<Dictionary<string, string>> GetSharedSecretAsync()
    {
        var profile = _config.Aws().SharedProfile ?? _config.Profile;
        var region = !string.IsNullOrEmpty(_config.Aws().SharedRegion) ? _config.Aws().SharedRegion : _config.Region;
        var smClient = CreateSecretsManagerClient(region, profile);

        var response = await smClient.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = SharedSecretId,
        });

        using var doc = JsonDocument.Parse(response.SecretString);
        var dict = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.GetString() ?? "";
        }

        return dict;
    }

    /// <summary>
    /// Retrieve the Tailscale API key from Secrets Manager.
    /// </summary>
    private async Task<string> GetTailscaleApiKeyAsync()
    {
        var secret = await GetSharedSecretAsync();
        return GetSecretValue(secret, "tailscale-api-key")
            ?? throw new InvalidOperationException("tailscale-api-key not found in shared/system secret");
    }

    /// <summary>
    /// Update the auth key and key ID in the shared/system secret.
    /// </summary>
    private async Task UpdateAuthKeyInSecretAsync(
        Dictionary<string, string> currentSecret, string authKey, string keyId)
    {
        currentSecret["tailscale-auth-key"] = authKey;
        currentSecret["tailscale-auth-key-id"] = keyId;
        await UpdateSecretAsync(currentSecret);
    }

    /// <summary>
    /// Write the full secret dictionary back to Secrets Manager.
    /// Read-modify-write: preserves all existing keys in the secret.
    /// </summary>
    private async Task UpdateSecretAsync(Dictionary<string, string> secret)
    {
        var newJson = JsonSerializer.Serialize(secret);

        var profile = _config.Aws().SharedProfile ?? _config.Profile;
        var region = !string.IsNullOrEmpty(_config.Aws().SharedRegion) ? _config.Aws().SharedRegion : _config.Region;
        var smClient = CreateSecretsManagerClient(region, profile);

        await smClient.PutSecretValueAsync(new PutSecretValueRequest
        {
            SecretId = SharedSecretId,
            SecretString = newJson,
        });
    }

    private static string? GetSecretValue(Dictionary<string, string> secret, string key)
    {
        return secret.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private static AmazonSecretsManagerClient CreateSecretsManagerClient(string region, string? profile)
        => CreateAwsClient<AmazonSecretsManagerClient>(
            Amazon.RegionEndpoint.GetBySystemName(region), profile);

    private static T CreateAwsClient<T>(Amazon.RegionEndpoint region, string? profile)
        where T : Amazon.Runtime.AmazonServiceClient
    {
        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return (T)Activator.CreateInstance(typeof(T), credentials, region)!;
        }

        return (T)Activator.CreateInstance(typeof(T), region)!;
    }
}
