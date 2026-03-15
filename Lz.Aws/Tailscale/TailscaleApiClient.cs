using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lz.Aws.Tailscale;

/// <summary>
/// HTTP client for the Tailscale API v2.
/// Handles device management, route approval, key expiry, split DNS, and auth key lifecycle.
/// Auth: Basic auth with API key as username, empty password.
/// </summary>
public class TailscaleApiClient : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://api.tailscale.com/api/v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TailscaleApiClient(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Tailscale API uses Basic auth: API key as username, empty password
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    // ---------------------------------------------------------------
    // Devices
    // ---------------------------------------------------------------

    /// <summary>
    /// List all devices in the tailnet associated with this API key.
    /// Uses the "-" tailnet identifier (auto-resolved from API key).
    /// </summary>
    public async Task<List<TailscaleDevice>> ListDevicesAsync()
    {
        var response = await _http.GetAsync($"{BaseUrl}/tailnet/-/devices");
        await EnsureSuccessAsync(response, "list devices");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var devices = new List<TailscaleDevice>();
        if (doc.RootElement.TryGetProperty("devices", out var devicesArray))
        {
            foreach (var d in devicesArray.EnumerateArray())
            {
                DateTime? lastSeen = null;
                if (d.TryGetProperty("lastSeen", out var lastSeenProp) &&
                    lastSeenProp.ValueKind == JsonValueKind.String)
                {
                    DateTime.TryParse(lastSeenProp.GetString(), out var parsed);
                    if (parsed != default) lastSeen = parsed;
                }

                devices.Add(new TailscaleDevice
                {
                    Id = d.GetProperty("id").GetString() ?? "",
                    NodeId = d.TryGetProperty("nodeId", out var nodeId) ? nodeId.GetString() ?? "" : "",
                    Name = d.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Hostname = d.TryGetProperty("hostname", out var hostname) ? hostname.GetString() ?? "" : "",
                    Online = d.TryGetProperty("online", out var online) && online.GetBoolean(),
                    LastSeen = lastSeen,
                });
            }
        }

        return devices;
    }

    /// <summary>
    /// Wait for devices matching a hostname prefix to appear.
    /// Polls every 10 seconds until at least minCount devices are found or timeout.
    /// </summary>
    public async Task<List<TailscaleDevice>> WaitForDevicesAsync(
        string hostnamePrefix, int minCount = 1, int timeoutSeconds = 180)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        Console.WriteLine($"  Waiting for Tailscale devices matching '{hostnamePrefix}*'...");

        while (DateTime.UtcNow < deadline)
        {
            var allDevices = await ListDevicesAsync();
            var matching = allDevices
                .Where(d => d.Hostname.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase)
                         || d.Name.StartsWith(hostnamePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count >= minCount)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Found {matching.Count} device(s) matching '{hostnamePrefix}*'.");
                Console.ResetColor();
                return matching;
            }

            Console.Write(".");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        throw new TimeoutException(
            $"Timed out waiting for {minCount} device(s) matching '{hostnamePrefix}*' " +
            $"after {timeoutSeconds}s");
    }

    /// <summary>
    /// Delete a device from the tailnet.
    /// Used to clean up stale device registrations from previous ASG instances.
    /// </summary>
    public async Task DeleteDeviceAsync(string deviceId)
    {
        var response = await _http.DeleteAsync($"{BaseUrl}/device/{deviceId}");
        await EnsureSuccessAsync(response, $"delete device {deviceId}");
    }

    // ---------------------------------------------------------------
    // Routes
    // ---------------------------------------------------------------

    /// <summary>
    /// Set the enabled subnet routes for a device.
    /// This replaces all existing routes — pass the full list.
    /// </summary>
    public async Task SetDeviceRoutesAsync(string deviceId, string[] routes)
    {
        var body = new { routes };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{BaseUrl}/device/{deviceId}/routes", content);
        await EnsureSuccessAsync(response, $"set routes for device {deviceId}");
    }

    // ---------------------------------------------------------------
    // Key Expiry
    // ---------------------------------------------------------------

    /// <summary>
    /// Disable key expiry for a device so it doesn't need periodic re-authentication.
    /// </summary>
    public async Task DisableKeyExpiryAsync(string deviceId)
    {
        var body = new { keyExpiryDisabled = true };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{BaseUrl}/device/{deviceId}/key", content);
        await EnsureSuccessAsync(response, $"disable key expiry for device {deviceId}");
    }

    // ---------------------------------------------------------------
    // Split DNS
    // ---------------------------------------------------------------

    /// <summary>
    /// Configure split DNS for the tailnet.
    /// Maps hostnames to nameservers — e.g., auth-admin.domain → VPC DNS resolver.
    /// Uses PATCH to merge entries with existing split DNS config.
    /// Endpoint: PATCH /api/v2/tailnet/-/dns/split-dns
    /// </summary>
    public async Task SetSplitDnsAsync(Dictionary<string, string[]> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Patch, $"{BaseUrl}/tailnet/-/dns/split-dns")
        {
            Content = content,
        };

        var response = await _http.SendAsync(request);
        await EnsureSuccessAsync(response, "set split DNS");
    }

    // ---------------------------------------------------------------
    // Auth Keys
    // ---------------------------------------------------------------

    /// <summary>
    /// Create a reusable, preauthorized auth key for subnet router instances.
    /// The key is returned only at creation time — store it immediately.
    /// Endpoint: POST /api/v2/tailnet/-/keys
    /// </summary>
    public async Task<TailscaleAuthKey> CreateAuthKeyAsync(int expirySeconds = 7776000) // 90 days
    {
        var body = new
        {
            capabilities = new
            {
                devices = new
                {
                    create = new
                    {
                        reusable = true,
                        ephemeral = false,
                        preauthorized = true,
                    }
                }
            },
            expirySeconds,
            description = "Auto-managed by lz deployment tool",
        };

        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{BaseUrl}/tailnet/-/keys", content);
        await EnsureSuccessAsync(response, "create auth key");

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        return new TailscaleAuthKey
        {
            Id = root.GetProperty("id").GetString() ?? "",
            Key = root.GetProperty("key").GetString() ?? "",
            Expires = root.TryGetProperty("expires", out var exp) ? exp.GetString() ?? "" : "",
        };
    }

    /// <summary>
    /// Check if an auth key is still valid (not expired, not revoked, not invalid).
    /// Returns false if the key is expired, revoked, invalid, or will expire within 24 hours.
    /// Also returns false on any API error (treat unknown state as invalid).
    /// Endpoint: GET /api/v2/tailnet/-/keys/{keyId}
    /// </summary>
    public async Task<bool> IsAuthKeyValidAsync(string keyId)
    {
        try
        {
            var response = await _http.GetAsync($"{BaseUrl}/tailnet/-/keys/{keyId}");
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check explicit invalid flag
            if (root.TryGetProperty("invalid", out var invalid) && invalid.GetBoolean())
                return false;

            // Check revoked
            if (root.TryGetProperty("revoked", out var revoked))
            {
                var revokedStr = revoked.GetString();
                if (!string.IsNullOrEmpty(revokedStr))
                    return false;
            }

            // Check expiry — invalid if less than 24 hours remaining
            if (root.TryGetProperty("expires", out var expires))
            {
                var expiryStr = expires.GetString();
                if (DateTime.TryParse(expiryStr, out var expiryDate))
                    return expiryDate > DateTime.UtcNow.AddHours(24);
            }

            return true;
        }
        catch
        {
            // Treat any error as invalid — safer to create a new key
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Internal
    // ---------------------------------------------------------------

    /// <summary>
    /// Check response and include the response body in the error message for debugging.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Tailscale API error ({operation}): {(int)response.StatusCode} {response.ReasonPhrase}" +
                (string.IsNullOrEmpty(body) ? "" : $" — {body}"));
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

/// <summary>
/// Auth key created via the Tailscale API.
/// The Key value is only available at creation time — store it immediately.
/// Use Id to later check validity via IsAuthKeyValidAsync.
/// </summary>
public class TailscaleAuthKey
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Expires { get; set; } = "";
}

/// <summary>
/// Minimal representation of a Tailscale device.
/// </summary>
public class TailscaleDevice
{
    public string Id { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public bool Online { get; set; }
    public DateTime? LastSeen { get; set; }
}
