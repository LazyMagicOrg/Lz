using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lz.Core.Keycloak;

/// <summary>
/// HTTP client for the Keycloak Admin REST API.
/// Handles authentication, CRUD for realms/clients/roles/groups/scopes,
/// and idempotent error handling (409 Conflict = already exists).
/// </summary>
public class KeycloakAdminClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _adminUsername;
    private readonly string _adminPassword;
    private string? _accessToken;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public KeycloakAdminClient(string baseUrl, string adminUsername, string adminPassword)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _adminUsername = adminUsername;
        _adminPassword = adminPassword;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ---------------------------------------------------------------
    // Authentication
    // ---------------------------------------------------------------

    /// <summary>
    /// Authenticate to the master realm and obtain a bearer token.
    /// </summary>
    public async System.Threading.Tasks.Task AuthenticateAsync()
    {
        var tokenUrl = $"{_baseUrl}/realms/master/protocol/openid-connect/token";
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "admin-cli"),
            new KeyValuePair<string, string>("username", _adminUsername),
            new KeyValuePair<string, string>("password", _adminPassword),
        });

        var response = await _http.PostAsync(tokenUrl, body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in token response");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>
    /// Wait for Keycloak to be ready by polling GET /realms/master.
    /// </summary>
    public async System.Threading.Tasks.Task WaitForReadyAsync(
        int timeoutSeconds = 300, int pollIntervalSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var url = $"{_baseUrl}/realms/master";

        Console.WriteLine($"Waiting for Keycloak at {_baseUrl} to be ready...");

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  Keycloak is ready.");
                    Console.ResetColor();
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not ready yet
            }
            catch (TaskCanceledException)
            {
                // Timeout on individual request — not ready yet
            }

            Console.Write(".");
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds));
        }

        throw new TimeoutException($"Keycloak did not become ready within {timeoutSeconds}s");
    }

    // ---------------------------------------------------------------
    // Realms
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetRealmsAsync()
    {
        return await GetListAsync($"{_baseUrl}/admin/realms");
    }

    public async Task<bool> CreateRealmAsync(string realmName)
    {
        var body = new { realm = realmName, enabled = true };
        return await PostAsync($"{_baseUrl}/admin/realms", body);
    }

    public async System.Threading.Tasks.Task UpdateRealmAsync(string realmName, Dictionary<string, object?> settings)
    {
        await PutAsync($"{_baseUrl}/admin/realms/{realmName}", settings);
    }

    // ---------------------------------------------------------------
    // Clients
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetClientsAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/clients?first=0&max=200");
    }

    public async Task<bool> CreateClientAsync(string realm, Dictionary<string, object?> clientDef)
    {
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/clients", clientDef);
    }

    /// <summary>
    /// Find a client's internal UUID by its clientId string (e.g., "tailscale").
    /// Returns null if not found.
    /// </summary>
    public async Task<string?> FindClientUuidAsync(string realm, string clientId)
    {
        var clients = await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients?clientId={Uri.EscapeDataString(clientId)}");
        foreach (var c in clients)
        {
            if (c.TryGetProperty("clientId", out var cid) && cid.GetString() == clientId
                && c.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Retrieve the client secret for a confidential client.
    /// Returns null if the client is public or has no secret.
    /// </summary>
    public async Task<string?> GetClientSecretAsync(string realm, string clientUuid)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/client-secret";
        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var value))
                return value.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the service account user for a confidential client.
    /// GET /admin/realms/{realm}/clients/{clientUuid}/service-account-user
    /// </summary>
    public async Task<JsonElement?> GetServiceAccountUserAsync(string realm, string clientUuid)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/service-account-user";
        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets available client roles for a user (roles not yet assigned).
    /// GET /admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}/available
    /// </summary>
    public async Task<List<JsonElement>> GetAvailableClientRolesForUserAsync(
        string realm, string userId, string clientUuid)
    {
        return await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}/available");
    }

    /// <summary>
    /// Gets currently assigned client roles for a user.
    /// GET /admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}
    /// </summary>
    public async Task<List<JsonElement>> GetAssignedClientRolesForUserAsync(
        string realm, string userId, string clientUuid)
    {
        return await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}");
    }

    /// <summary>
    /// Assigns client roles to a user.
    /// POST /admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}
    /// </summary>
    public async System.Threading.Tasks.Task AssignClientRolesToUserAsync(
        string realm, string userId, string clientUuid, List<Dictionary<string, string>> roles)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/users/{userId}/role-mappings/clients/{clientUuid}";
        var json = JsonSerializer.Serialize(roles, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);

        if (response.StatusCode != HttpStatusCode.Conflict)
            response.EnsureSuccessStatusCode();
    }

    // ---------------------------------------------------------------
    // Realm Roles
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetRolesAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/roles");
    }

    public async Task<bool> CreateRoleAsync(string realm, string name, string? description)
    {
        var body = new { name, description = description ?? "" };
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/roles", body);
    }

    public async Task<JsonElement?> GetRoleByNameAsync(string realm, string roleName)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/roles/{Uri.EscapeDataString(roleName)}";
        try
        {
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Groups
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetGroupsAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/groups");
    }

    public async Task<bool> CreateGroupAsync(string realm, string name)
    {
        var body = new { name };
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/groups", body);
    }

    public async Task<string?> FindGroupIdAsync(string realm, string groupName)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/groups?search={Uri.EscapeDataString(groupName)}&exact=true";
        var groups = await GetListAsync(url);
        foreach (var g in groups)
        {
            if (g.TryGetProperty("name", out var name) && name.GetString() == groupName
                && g.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        return null;
    }

    public async Task<List<JsonElement>> GetGroupRoleMappingsAsync(string realm, string groupId)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/groups/{groupId}/role-mappings/realm");
    }

    public async System.Threading.Tasks.Task AssignRolesToGroupAsync(
        string realm, string groupId, List<Dictionary<string, string>> roles)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/groups/{groupId}/role-mappings/realm";
        var json = JsonSerializer.Serialize(roles, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);

        if (response.StatusCode != HttpStatusCode.Conflict)
            response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Adds a group to the realm's default groups (users auto-join on creation).
    /// PUT /admin/realms/{realm}/default-groups/{groupId}
    /// </summary>
    public async System.Threading.Tasks.Task SetDefaultGroupAsync(string realm, string groupId)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/default-groups/{groupId}";
        var content = new StringContent("", Encoding.UTF8, "application/json");
        var response = await _http.PutAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets the realm's current default groups.
    /// GET /admin/realms/{realm}/default-groups
    /// </summary>
    public async Task<List<JsonElement>> GetDefaultGroupsAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/default-groups");
    }

    // ---------------------------------------------------------------
    // Users
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetUsersAsync(string realm, string? username = null)
    {
        var url = $"{_baseUrl}/admin/realms/{realm}/users?first=0&max=200";
        if (username != null)
            url += $"&username={Uri.EscapeDataString(username)}&exact=true";
        return await GetListAsync(url);
    }

    /// <summary>
    /// Create a user. Returns true if created, false if 409 (already exists).
    /// </summary>
    public async Task<bool> CreateUserAsync(string realm, Dictionary<string, object?> userDef)
    {
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/users", userDef);
    }

    /// <summary>
    /// Find a user's internal UUID by username. Returns null if not found.
    /// </summary>
    public async Task<string?> FindUserIdAsync(string realm, string username)
    {
        var users = await GetUsersAsync(realm, username);
        foreach (var u in users)
        {
            if (u.TryGetProperty("username", out var uname) && uname.GetString() == username
                && u.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Set a user's password.
    /// </summary>
    public async System.Threading.Tasks.Task SetUserPasswordAsync(
        string realm, string userId, string password, bool temporary = false)
    {
        var body = new { type = "password", value = password, temporary };
        await PutAsync($"{_baseUrl}/admin/realms/{realm}/users/{userId}/reset-password", body);
    }

    /// <summary>
    /// Add a user to a group.
    /// </summary>
    public async System.Threading.Tasks.Task AddUserToGroupAsync(
        string realm, string userId, string groupId)
    {
        await PutAsync($"{_baseUrl}/admin/realms/{realm}/users/{userId}/groups/{groupId}", null);
    }

    // ---------------------------------------------------------------
    // Client Scopes
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetClientScopesAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/client-scopes");
    }

    public async Task<bool> CreateClientScopeAsync(string realm, string name)
    {
        var body = new { name, protocol = "openid-connect" };
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/client-scopes", body);
    }

    public async System.Threading.Tasks.Task AddDefaultScopeAsync(string realm, string scopeId)
    {
        await PutAsync($"{_baseUrl}/admin/realms/{realm}/default-default-client-scopes/{scopeId}", null);
    }

    public async System.Threading.Tasks.Task RemoveDefaultScopeAsync(string realm, string scopeId)
    {
        await DeleteAsync($"{_baseUrl}/admin/realms/{realm}/default-default-client-scopes/{scopeId}");
    }

    public async System.Threading.Tasks.Task AddOptionalScopeAsync(string realm, string scopeId)
    {
        await PutAsync($"{_baseUrl}/admin/realms/{realm}/default-optional-client-scopes/{scopeId}", null);
    }

    public async System.Threading.Tasks.Task RemoveOptionalScopeAsync(string realm, string scopeId)
    {
        await DeleteAsync($"{_baseUrl}/admin/realms/{realm}/default-optional-client-scopes/{scopeId}");
    }

    // ---------------------------------------------------------------
    // Protocol Mappers (on clients)
    // ---------------------------------------------------------------

    /// <summary>
    /// Get protocol mappers for a client.
    /// GET /admin/realms/{realm}/clients/{clientUuid}/protocol-mappers/models
    /// </summary>
    public async Task<List<JsonElement>> GetClientProtocolMappersAsync(string realm, string clientUuid)
    {
        return await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/protocol-mappers/models");
    }

    /// <summary>
    /// Create a protocol mapper on a client. Returns true if created, false if 409 (already exists).
    /// POST /admin/realms/{realm}/clients/{clientUuid}/protocol-mappers/models
    /// </summary>
    public async Task<bool> CreateClientProtocolMapperAsync(
        string realm, string clientUuid, Dictionary<string, object?> mapperDef)
    {
        return await PostAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/protocol-mappers/models",
            mapperDef);
    }

    // ---------------------------------------------------------------
    // Client's assigned scopes (including dedicated scopes)
    // ---------------------------------------------------------------

    /// <summary>
    /// Get the default client scopes assigned to a specific client.
    /// GET /admin/realms/{realm}/clients/{clientUuid}/default-client-scopes
    /// </summary>
    public async Task<List<JsonElement>> GetClientDefaultScopesAsync(string realm, string clientUuid)
    {
        return await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/default-client-scopes");
    }

    /// <summary>
    /// Find the UUID of a client's dedicated scope (type "None").
    /// NOTE: In Keycloak 26+, dedicated scopes are hidden from all standard
    /// REST API endpoints (client-scopes, default-client-scopes, optional-client-scopes).
    /// Client-level protocol mappers are equivalent to dedicated scope mappers,
    /// so callers should use CreateClientProtocolMapperAsync instead.
    /// This method is retained for compatibility with older Keycloak versions.
    /// </summary>
    public async Task<string?> FindDedicatedScopeUuidAsync(string realm, string clientUuid, string scopeName)
    {
        // Try 1: default scopes
        var defaultScopes = await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/default-client-scopes");
        foreach (var s in defaultScopes)
        {
            if (s.TryGetProperty("name", out var n) && n.GetString() == scopeName
                && s.TryGetProperty("id", out var id))
                return id.GetString();
        }

        // Try 2: optional scopes
        var optionalScopes = await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/clients/{clientUuid}/optional-client-scopes");
        foreach (var s in optionalScopes)
        {
            if (s.TryGetProperty("name", out var n) && n.GetString() == scopeName
                && s.TryGetProperty("id", out var id))
                return id.GetString();
        }

        // Try 3: general client-scopes endpoint (includes dedicated in some versions)
        var allScopes = await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/client-scopes");
        foreach (var s in allScopes)
        {
            if (s.TryGetProperty("name", out var n) && n.GetString() == scopeName
                && s.TryGetProperty("id", out var id))
                return id.GetString();
        }

        return null;
    }

    // ---------------------------------------------------------------
    // Protocol Mappers (on client scopes)
    // ---------------------------------------------------------------

    /// <summary>
    /// Get protocol mappers for a client scope (e.g., "storeapp-dedicated").
    /// GET /admin/realms/{realm}/client-scopes/{scopeUuid}/protocol-mappers/models
    /// </summary>
    public async Task<List<JsonElement>> GetScopeProtocolMappersAsync(string realm, string scopeUuid)
    {
        return await GetListAsync(
            $"{_baseUrl}/admin/realms/{realm}/client-scopes/{scopeUuid}/protocol-mappers/models");
    }

    /// <summary>
    /// Create a protocol mapper on a client scope. Returns true if created, false if 409.
    /// POST /admin/realms/{realm}/client-scopes/{scopeUuid}/protocol-mappers/models
    /// </summary>
    public async Task<bool> CreateScopeProtocolMapperAsync(
        string realm, string scopeUuid, Dictionary<string, object?> mapperDef)
    {
        return await PostAsync(
            $"{_baseUrl}/admin/realms/{realm}/client-scopes/{scopeUuid}/protocol-mappers/models",
            mapperDef);
    }

    // ---------------------------------------------------------------
    // Identity Providers
    // ---------------------------------------------------------------

    public async Task<List<JsonElement>> GetIdpInstancesAsync(string realm)
    {
        return await GetListAsync($"{_baseUrl}/admin/realms/{realm}/identity-provider/instances");
    }

    public async Task<bool> CreateIdpSkeletonAsync(string realm, string alias, string providerId)
    {
        var body = new { alias, providerId, enabled = true, config = new Dictionary<string, string>() };
        return await PostAsync($"{_baseUrl}/admin/realms/{realm}/identity-provider/instances", body);
    }

    // ---------------------------------------------------------------
    // Required Actions
    // ---------------------------------------------------------------

    public async System.Threading.Tasks.Task UpdateRequiredActionAsync(
        string realm, string alias, bool enabled, bool defaultAction)
    {
        var body = new { alias, enabled, defaultAction };
        await PutAsync(
            $"{_baseUrl}/admin/realms/{realm}/authentication/required-actions/{Uri.EscapeDataString(alias)}",
            body);
    }

    // ---------------------------------------------------------------
    // Internal HTTP helpers
    // ---------------------------------------------------------------

    private async Task<List<JsonElement>> GetListAsync(string url)
    {
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>
    /// POST with 409 idempotency. Returns true if created, false if already existed.
    /// </summary>
    private async Task<bool> PostAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return false; // Already exists — idempotent success

        response.EnsureSuccessStatusCode();
        return true;
    }

    private async System.Threading.Tasks.Task PutAsync(string url, object? body)
    {
        HttpContent? content = null;
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await _http.PutAsync(url, content);

        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Keycloak PUT {response.StatusCode}: {errorBody}");
                Console.ResetColor();
            }
            response.EnsureSuccessStatusCode();
        }
    }

    private async System.Threading.Tasks.Task DeleteAsync(string url)
    {
        var response = await _http.DeleteAsync(url);
        // Ignore 404 on delete (already removed)
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
