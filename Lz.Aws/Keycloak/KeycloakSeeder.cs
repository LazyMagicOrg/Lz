using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Keycloak;

/// <summary>
/// Orchestrates Keycloak configuration seeding from a KeycloakSeedConfig model.
/// Ports the logic from kc-upload.ps1: creates realms, clients, roles, groups,
/// scopes, and identity providers via the Keycloak Admin REST API.
/// All operations are idempotent (safe to run repeatedly).
/// </summary>
public class KeycloakSeeder
{
    private readonly KeycloakAdminClient _client;
    private readonly KeycloakSeedConfig _config;
    private readonly BootstrapCredsConfig? _creds;

    private int _created;
    private int _updated;
    private int _skipped;
    private int _warned;

    public KeycloakSeeder(KeycloakAdminClient client, KeycloakSeedConfig config,
        BootstrapCredsConfig? creds = null)
    {
        _client = client;
        _config = config;
        _creds = creds;
    }

    /// <summary>
    /// Execute the full seeding sequence (matching kc-upload.ps1 steps 1-10).
    /// </summary>
    public async System.Threading.Tasks.Task SeedAsync()
    {
        if (_config.Realms == null || _config.Realms.Count == 0)
        {
            Console.WriteLine("  No realms to seed.");
            return;
        }

        // Get existing realms
        var existingRealms = await _client.GetRealmsAsync();
        var existingRealmNames = existingRealms
            .Where(r => r.TryGetProperty("realm", out _))
            .Select(r => r.GetProperty("realm").GetString()!)
            .ToHashSet();

        foreach (var (realmName, realmConfig) in _config.Realms)
        {
            // Skip master realm (informational only)
            if (realmName == "master")
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n--- Skipping master realm (read-only metadata) ---");
                Console.ResetColor();
                continue;
            }

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"\n=== Realm: {realmName} ===");
            Console.ResetColor();

            // 1. Create realm if it doesn't exist
            if (!existingRealmNames.Contains(realmName))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Creating realm '{realmName}'...");
                Console.ResetColor();
                await _client.CreateRealmAsync(realmName);
                _created++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  Realm exists");
                Console.ResetColor();
            }

            // 2. Update realm settings + SMTP
            await UpdateRealmSettingsAsync(realmName, realmConfig);

            // 3. Create client scope definitions
            await CreateClientScopeDefinitionsAsync(realmName, realmConfig);

            // 4. Create/update clients
            await CreateClientsAsync(realmName, realmConfig);

            // 4b. Assign service account client roles
            await AssignServiceAccountClientRolesAsync(realmName, realmConfig);

            // 4c. Create protocol mappers on clients
            await CreateClientProtocolMappersAsync(realmName, realmConfig);

            // 5. Create realm roles
            await CreateRealmRolesAsync(realmName, realmConfig);

            // 6. Create groups and assign roles
            await CreateGroupsAsync(realmName, realmConfig);

            // 6b. Set default groups for the realm
            await SetDefaultGroupsAsync(realmName, realmConfig);

            // 7. Bootstrap users (from credsconfig)
            await CreateBootstrapUsersAsync(realmName);

            // 8. Identity providers
            await CreateIdentityProvidersAsync(realmName, realmConfig);

            // 9-10. Default and optional client scopes
            await ManageClientScopesAsync(realmName, realmConfig);

            // 11. Required actions
            await UpdateRequiredActionsAsync(realmName, realmConfig);

            // 12. Custom authentication flows (informational only)
            if (realmConfig.CustomAuthenticationFlows is { Count: > 0 })
            {
                var flows = string.Join(", ", realmConfig.CustomAuthenticationFlows);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  NOTE: Custom authentication flows must be configured manually: {flows}");
                Console.ResetColor();
                _warned++;
            }
        }

        // Summary
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\n=== Keycloak Seeding Summary ===");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Created:  {_created}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Updated:  {_updated}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Skipped:  {_skipped}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Warnings: {_warned}");
        Console.ResetColor();
    }

    // ---------------------------------------------------------------
    // Step 2: Realm settings + SMTP
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task UpdateRealmSettingsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.RealmSettings == null && config.Smtp == null) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Updating realm settings...");
        Console.ResetColor();

        var realmUpdate = new Dictionary<string, object?>();

        // Fields removed from Keycloak's RealmRepresentation in v26+
        var unsupportedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "frontendUrl" };

        if (config.RealmSettings != null)
        {
            foreach (var (key, value) in config.RealmSettings)
            {
                if (unsupportedFields.Contains(key))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    Skipping unsupported realm setting '{key}'");
                    Console.ResetColor();
                    continue;
                }
                realmUpdate[key] = value;
            }
        }

        if (config.Smtp != null)
        {
            var smtpServer = new Dictionary<string, string>();
            foreach (var (key, value) in config.Smtp)
            {
                if (key == "password" && value == "**********")
                {
                    // Use bootstrap creds if available
                    if (!string.IsNullOrEmpty(_creds?.SmtpPassword))
                    {
                        smtpServer[key] = _creds.SmtpPassword;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("    SMTP password applied from credsconfig");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("    WARNING: SMTP password is masked — set it manually in Keycloak admin console");
                        Console.ResetColor();
                        _warned++;
                    }
                    continue;
                }
                smtpServer[key] = value;
            }
            realmUpdate["smtpServer"] = smtpServer;
        }

        await _client.UpdateRealmAsync(realmName, realmUpdate);
        _updated++;
    }

    // ---------------------------------------------------------------
    // Step 3: Client scope definitions
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateClientScopeDefinitionsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.AddedClientScopeDefinitions is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Client scope definitions...");
        Console.ResetColor();

        var existingScopes = await _client.GetClientScopesAsync(realmName);
        var existingScopeNames = existingScopes
            .Where(s => s.TryGetProperty("name", out _))
            .Select(s => s.GetProperty("name").GetString()!)
            .ToHashSet();

        foreach (var scopeName in config.AddedClientScopeDefinitions)
        {
            if (existingScopeNames.Contains(scopeName))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Scope '{scopeName}' exists");
                Console.ResetColor();
                _skipped++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Creating scope '{scopeName}'");
                Console.ResetColor();
                await _client.CreateClientScopeAsync(realmName, scopeName);
                _created++;
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 4: Clients
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateClientsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.Clients is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Clients...");
        Console.ResetColor();

        var existingClients = await _client.GetClientsAsync(realmName);
        var existingClientIds = existingClients
            .Where(c => c.TryGetProperty("clientId", out _))
            .Select(c => c.GetProperty("clientId").GetString()!)
            .ToHashSet();

        foreach (var clientDef in config.Clients)
        {
            if (existingClientIds.Contains(clientDef.ClientId))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Client '{clientDef.ClientId}' exists");
                Console.ResetColor();
                _skipped++;
                continue;
            }

            var body = new Dictionary<string, object?> { ["clientId"] = clientDef.ClientId };
            if (clientDef.PublicClient.HasValue) body["publicClient"] = clientDef.PublicClient.Value;
            if (clientDef.ServiceAccountsEnabled.HasValue) body["serviceAccountsEnabled"] = clientDef.ServiceAccountsEnabled.Value;
            if (clientDef.StandardFlowEnabled.HasValue) body["standardFlowEnabled"] = clientDef.StandardFlowEnabled.Value;
            if (clientDef.DirectAccessGrantsEnabled.HasValue) body["directAccessGrantsEnabled"] = clientDef.DirectAccessGrantsEnabled.Value;
            if (clientDef.ImplicitFlowEnabled.HasValue) body["implicitFlowEnabled"] = clientDef.ImplicitFlowEnabled.Value;
            if (clientDef.RootUrl != null) body["rootUrl"] = clientDef.RootUrl;
            if (clientDef.BaseUrl != null) body["baseUrl"] = clientDef.BaseUrl;
            if (clientDef.AdminUrl != null) body["adminUrl"] = clientDef.AdminUrl;
            if (clientDef.Protocol != null) body["protocol"] = clientDef.Protocol;
            if (clientDef.RedirectUris != null) body["redirectUris"] = clientDef.RedirectUris;
            if (clientDef.WebOrigins != null) body["webOrigins"] = clientDef.WebOrigins;
            if (clientDef.Attributes is { Count: > 0 }) body["attributes"] = clientDef.Attributes;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    Creating client '{clientDef.ClientId}'");
            Console.ResetColor();
            await _client.CreateClientAsync(realmName, body);
            _created++;
        }
    }

    // ---------------------------------------------------------------
    // Step 4b: Service account client role assignments
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task AssignServiceAccountClientRolesAsync(
        string realmName, RealmSeedConfig config)
    {
        if (config.Clients is not { Count: > 0 }) return;

        var clientsWithRoles = config.Clients
            .Where(c => c.ServiceAccountClientRoles is { Count: > 0 })
            .ToList();

        if (clientsWithRoles.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Service account client roles...");
        Console.ResetColor();

        foreach (var clientDef in clientsWithRoles)
        {
            // Find the client's UUID
            var clientUuid = await _client.FindClientUuidAsync(realmName, clientDef.ClientId);
            if (clientUuid == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: Client '{clientDef.ClientId}' not found — cannot assign service account roles");
                Console.ResetColor();
                _warned++;
                continue;
            }

            // Get the service account user
            var saUser = await _client.GetServiceAccountUserAsync(realmName, clientUuid);
            if (saUser == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: No service account user for client '{clientDef.ClientId}'");
                Console.ResetColor();
                _warned++;
                continue;
            }

            var saUserId = saUser.Value.GetProperty("id").GetString()!;

            foreach (var (targetClientId, roleNames) in clientDef.ServiceAccountClientRoles!)
            {
                // Find the target client's UUID (e.g., realm-management)
                var targetUuid = await _client.FindClientUuidAsync(realmName, targetClientId);
                if (targetUuid == null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    WARNING: Target client '{targetClientId}' not found in realm '{realmName}'");
                    Console.ResetColor();
                    _warned++;
                    continue;
                }

                // Get already-assigned roles to avoid duplicates
                var assignedRoles = await _client.GetAssignedClientRolesForUserAsync(realmName, saUserId, targetUuid);
                var assignedNames = assignedRoles
                    .Where(r => r.TryGetProperty("name", out _))
                    .Select(r => r.GetProperty("name").GetString()!)
                    .ToHashSet();

                // Get available roles to find IDs
                var availableRoles = await _client.GetAvailableClientRolesForUserAsync(realmName, saUserId, targetUuid);

                var rolesToAdd = new List<Dictionary<string, string>>();
                foreach (var roleName in roleNames)
                {
                    if (assignedNames.Contains(roleName))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"    '{clientDef.ClientId}' already has '{targetClientId}/{roleName}'");
                        Console.ResetColor();
                        _skipped++;
                        continue;
                    }

                    var role = availableRoles.FirstOrDefault(r =>
                        r.TryGetProperty("name", out var n) && n.GetString() == roleName);

                    if (role.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    WARNING: Role '{roleName}' not found in client '{targetClientId}'");
                        Console.ResetColor();
                        _warned++;
                        continue;
                    }

                    rolesToAdd.Add(new Dictionary<string, string>
                    {
                        ["id"] = role.GetProperty("id").GetString()!,
                        ["name"] = role.GetProperty("name").GetString()!
                    });
                }

                if (rolesToAdd.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"    Assigning {rolesToAdd.Count} '{targetClientId}' role(s) to '{clientDef.ClientId}' service account");
                    Console.ResetColor();
                    await _client.AssignClientRolesToUserAsync(realmName, saUserId, targetUuid, rolesToAdd);
                    _updated++;
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 4c: Protocol mappers on clients
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateClientProtocolMappersAsync(
        string realmName, RealmSeedConfig config)
    {
        if (config.Clients is not { Count: > 0 }) return;

        var clientsWithMappers = config.Clients
            .Where(c => c.ProtocolMappers is { Count: > 0 })
            .ToList();

        if (clientsWithMappers.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Client protocol mappers...");
        Console.ResetColor();

        foreach (var clientDef in clientsWithMappers)
        {
            var clientUuid = await _client.FindClientUuidAsync(realmName, clientDef.ClientId);
            if (clientUuid == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: Client '{clientDef.ClientId}' not found — cannot create protocol mappers");
                Console.ResetColor();
                _warned++;
                continue;
            }

            // Get existing mappers to avoid duplicates
            var existingMappers = await _client.GetClientProtocolMappersAsync(realmName, clientUuid);
            var existingMapperNames = existingMappers
                .Where(m => m.TryGetProperty("name", out _))
                .Select(m => m.GetProperty("name").GetString()!)
                .ToHashSet();

            foreach (var mapperDef in clientDef.ProtocolMappers!)
            {
                if (existingMapperNames.Contains(mapperDef.Name))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"    Mapper '{mapperDef.Name}' exists on '{clientDef.ClientId}'");
                    Console.ResetColor();
                    _skipped++;
                    continue;
                }

                var body = new Dictionary<string, object?>
                {
                    ["name"] = mapperDef.Name,
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = mapperDef.ProtocolMapper,
                    ["consentRequired"] = false,
                };

                if (mapperDef.Config is { Count: > 0 })
                {
                    body["config"] = mapperDef.Config;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Creating mapper '{mapperDef.Name}' on '{clientDef.ClientId}'");
                Console.ResetColor();
                await _client.CreateClientProtocolMapperAsync(realmName, clientUuid, body);
                _created++;
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 5: Realm roles
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateRealmRolesAsync(string realmName, RealmSeedConfig config)
    {
        if (config.RealmRoles is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Realm roles...");
        Console.ResetColor();

        var existingRoles = await _client.GetRolesAsync(realmName);
        var existingRoleNames = existingRoles
            .Where(r => r.TryGetProperty("name", out _))
            .Select(r => r.GetProperty("name").GetString()!)
            .ToHashSet();

        foreach (var roleDef in config.RealmRoles)
        {
            if (existingRoleNames.Contains(roleDef.Name))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Role '{roleDef.Name}' exists");
                Console.ResetColor();
                _skipped++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Creating role '{roleDef.Name}'");
                Console.ResetColor();
                await _client.CreateRoleAsync(realmName, roleDef.Name, roleDef.Description);
                _created++;
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 6: Groups + role assignments
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateGroupsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.Groups is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Groups...");
        Console.ResetColor();

        var existingGroups = await _client.GetGroupsAsync(realmName);
        var existingGroupNames = existingGroups
            .Where(g => g.TryGetProperty("name", out _))
            .Select(g => g.GetProperty("name").GetString()!)
            .ToHashSet();

        foreach (var groupDef in config.Groups)
        {
            if (!existingGroupNames.Contains(groupDef.Name))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Creating group '{groupDef.Name}'");
                Console.ResetColor();
                await _client.CreateGroupAsync(realmName, groupDef.Name);
                _created++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Group '{groupDef.Name}' exists");
                Console.ResetColor();
            }

            // Assign realm roles to group
            if (groupDef.RealmRoles is not { Count: > 0 }) continue;

            var groupId = await _client.FindGroupIdAsync(realmName, groupDef.Name);
            if (groupId == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: Could not find group '{groupDef.Name}' for role assignment");
                Console.ResetColor();
                _warned++;
                continue;
            }

            var currentMappings = await _client.GetGroupRoleMappingsAsync(realmName, groupId);
            var currentRoleNames = currentMappings
                .Where(m => m.TryGetProperty("name", out _))
                .Select(m => m.GetProperty("name").GetString()!)
                .ToHashSet();

            var rolesToAdd = new List<Dictionary<string, string>>();
            foreach (var roleName in groupDef.RealmRoles)
            {
                if (currentRoleNames.Contains(roleName)) continue;

                var roleObj = await _client.GetRoleByNameAsync(realmName, roleName);
                if (roleObj == null)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    WARNING: Role '{roleName}' not found for group '{groupDef.Name}'");
                    Console.ResetColor();
                    _warned++;
                    continue;
                }

                var roleId = roleObj.Value.GetProperty("id").GetString()!;
                var rName = roleObj.Value.GetProperty("name").GetString()!;
                rolesToAdd.Add(new Dictionary<string, string> { ["id"] = roleId, ["name"] = rName });
            }

            if (rolesToAdd.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"    Assigning {rolesToAdd.Count} role(s) to group '{groupDef.Name}'");
                Console.ResetColor();
                await _client.AssignRolesToGroupAsync(realmName, groupId, rolesToAdd);
                _updated++;
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 6b: Default groups
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task SetDefaultGroupsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.DefaultGroups is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Default groups...");
        Console.ResetColor();

        // Get current default groups
        var currentDefaults = await _client.GetDefaultGroupsAsync(realmName);
        var currentDefaultNames = currentDefaults
            .Where(g => g.TryGetProperty("name", out _))
            .Select(g => g.GetProperty("name").GetString()!)
            .ToHashSet();

        foreach (var groupName in config.DefaultGroups)
        {
            if (currentDefaultNames.Contains(groupName))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    '{groupName}' is already a default group");
                Console.ResetColor();
                _skipped++;
                continue;
            }

            var groupId = await _client.FindGroupIdAsync(realmName, groupName);
            if (groupId == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: Group '{groupName}' not found — cannot set as default");
                Console.ResetColor();
                _warned++;
                continue;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    Setting '{groupName}' as default group");
            Console.ResetColor();
            await _client.SetDefaultGroupAsync(realmName, groupId);
            _updated++;
        }
    }

    // ---------------------------------------------------------------
    // Step 7: Bootstrap users (from credsconfig)
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateBootstrapUsersAsync(string realmName)
    {
        if (_creds?.KeycloakUsers == null ||
            !_creds.KeycloakUsers.TryGetValue(realmName, out var users) ||
            users.Count == 0)
            return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Bootstrap users...");
        Console.ResetColor();

        foreach (var userDef in users)
        {
            var existingId = await _client.FindUserIdAsync(realmName, userDef.Username);
            if (existingId != null)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    User '{userDef.Username}' exists");
                Console.ResetColor();
                _skipped++;
                continue;
            }

            // Create user
            var body = new Dictionary<string, object?>
            {
                ["username"] = userDef.Username,
                ["enabled"] = true,
            };
            if (userDef.Email != null) body["email"] = userDef.Email;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    Creating user '{userDef.Username}'");
            Console.ResetColor();
            await _client.CreateUserAsync(realmName, body);
            _created++;

            // Set password
            var userId = await _client.FindUserIdAsync(realmName, userDef.Username);
            if (userId == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: Could not find user '{userDef.Username}' after creation");
                Console.ResetColor();
                _warned++;
                continue;
            }

            if (!string.IsNullOrEmpty(userDef.Password))
            {
                await _client.SetUserPasswordAsync(realmName, userId, userDef.Password, userDef.Temporary);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Password set for '{userDef.Username}' (temporary: {userDef.Temporary})");
                Console.ResetColor();
            }

            // Add to groups
            if (userDef.Groups is { Count: > 0 })
            {
                foreach (var groupName in userDef.Groups)
                {
                    var groupId = await _client.FindGroupIdAsync(realmName, groupName);
                    if (groupId != null)
                    {
                        await _client.AddUserToGroupAsync(realmName, userId, groupId);
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"    Added '{userDef.Username}' to group '{groupName}'");
                        Console.ResetColor();
                        _updated++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    WARNING: Group '{groupName}' not found for user '{userDef.Username}'");
                        Console.ResetColor();
                        _warned++;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 8: Identity providers
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task CreateIdentityProvidersAsync(string realmName, RealmSeedConfig config)
    {
        if (config.IdentityProviders is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Identity providers...");
        Console.ResetColor();

        var existingIdps = await _client.GetIdpInstancesAsync(realmName);
        var existingAliases = existingIdps
            .Where(i => i.TryGetProperty("alias", out _))
            .Select(i => i.GetProperty("alias").GetString()!)
            .ToHashSet();

        foreach (var idpDef in config.IdentityProviders)
        {
            if (existingAliases.Contains(idpDef.Alias))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    IdP '{idpDef.Alias}' exists");
                Console.ResetColor();
                _skipped++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    Creating IdP skeleton '{idpDef.Alias}'");
                Console.ResetColor();
                try
                {
                    await _client.CreateIdpSkeletonAsync(realmName, idpDef.Alias, idpDef.ProviderId);
                    _created++;
                }
                catch
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    WARNING: Failed to create IdP '{idpDef.Alias}' — configure manually");
                    Console.ResetColor();
                    _warned++;
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    WARNING: IdP '{idpDef.Alias}' created as skeleton — configure client secret and endpoints manually");
                Console.ResetColor();
                _warned++;
            }
        }
    }

    // ---------------------------------------------------------------
    // Steps 8-9: Default and optional client scopes
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task ManageClientScopesAsync(string realmName, RealmSeedConfig config)
    {
        // Build scope name → ID lookup
        var allScopes = await _client.GetClientScopesAsync(realmName);
        var scopeIdLookup = new Dictionary<string, string>();
        foreach (var scope in allScopes)
        {
            if (scope.TryGetProperty("name", out var name) && scope.TryGetProperty("id", out var id))
                scopeIdLookup[name.GetString()!] = id.GetString()!;
        }

        // Default client scopes
        if (config.DefaultClientScopes != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Default client scopes...");
            Console.ResetColor();

            if (config.DefaultClientScopes.Added != null)
            {
                foreach (var scopeName in config.DefaultClientScopes.Added)
                {
                    if (scopeIdLookup.TryGetValue(scopeName, out var scopeId))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"    Adding default scope '{scopeName}'");
                        Console.ResetColor();
                        await _client.AddDefaultScopeAsync(realmName, scopeId);
                        _updated++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    WARNING: Scope '{scopeName}' not found");
                        Console.ResetColor();
                        _warned++;
                    }
                }
            }

            if (config.DefaultClientScopes.Removed != null)
            {
                foreach (var scopeName in config.DefaultClientScopes.Removed)
                {
                    if (scopeIdLookup.TryGetValue(scopeName, out var scopeId))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"    Removing default scope '{scopeName}'");
                        Console.ResetColor();
                        await _client.RemoveDefaultScopeAsync(realmName, scopeId);
                        _updated++;
                    }
                }
            }
        }

        // Optional client scopes
        if (config.OptionalClientScopes != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Optional client scopes...");
            Console.ResetColor();

            if (config.OptionalClientScopes.Added != null)
            {
                foreach (var scopeName in config.OptionalClientScopes.Added)
                {
                    if (scopeIdLookup.TryGetValue(scopeName, out var scopeId))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"    Adding optional scope '{scopeName}'");
                        Console.ResetColor();
                        await _client.AddOptionalScopeAsync(realmName, scopeId);
                        _updated++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    WARNING: Scope '{scopeName}' not found");
                        Console.ResetColor();
                        _warned++;
                    }
                }
            }

            if (config.OptionalClientScopes.Removed != null)
            {
                foreach (var scopeName in config.OptionalClientScopes.Removed)
                {
                    if (scopeIdLookup.TryGetValue(scopeName, out var scopeId))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"    Removing optional scope '{scopeName}'");
                        Console.ResetColor();
                        await _client.RemoveOptionalScopeAsync(realmName, scopeId);
                        _updated++;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Step 10: Required actions
    // ---------------------------------------------------------------

    private async System.Threading.Tasks.Task UpdateRequiredActionsAsync(string realmName, RealmSeedConfig config)
    {
        if (config.RequiredActions is not { Count: > 0 }) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Required actions...");
        Console.ResetColor();

        foreach (var actionDef in config.RequiredActions)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"    Updating required action '{actionDef.Alias}'");
            Console.ResetColor();
            await _client.UpdateRequiredActionAsync(realmName, actionDef.Alias, actionDef.Enabled, actionDef.DefaultAction);
            _updated++;
        }
    }
}
