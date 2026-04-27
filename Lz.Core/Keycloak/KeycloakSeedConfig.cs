namespace Lz.Core.Keycloak;

/// <summary>
/// Deserialization model for keycloakconfig.{systemkey}.{env}.yaml.
/// Captures the realm configuration to seed into a fresh Keycloak instance.
/// YAML uses camelCase naming — requires CamelCaseNamingConvention deserializer.
/// </summary>
public class KeycloakSeedConfig
{
    public string? GeneratedAt { get; set; }
    public string? KeycloakUrl { get; set; }
    public string? BaselineRealm { get; set; }
    public string? Note { get; set; }
    public Dictionary<string, RealmSeedConfig>? Realms { get; set; }

    /// <summary>
    /// Path to the Keycloak theme directory, relative to the monorepo root.
    /// e.g., "keycloakthemes/harmova". When set, the theme is deployed to EFS
    /// during the foundation post-deploy phase.
    /// </summary>
    public string? ThemeSource { get; set; }
}

public class RealmSeedConfig
{
    // Unmanaged attribute policy: ENABLED, ADMIN_EDIT, ADMIN_VIEW, or DISABLED.
    // Controls whether custom user attributes can be set outside Keycloak's managed profile.
    public string? UnmanagedAttributePolicy { get; set; }

    // Realm-level settings (displayName, loginTheme, registrationAllowed, etc.)
    public Dictionary<string, object>? RealmSettings { get; set; }

    // SMTP server configuration (host, port, user, password, etc.)
    public Dictionary<string, string>? Smtp { get; set; }

    // OAuth/OIDC clients to create
    public List<ClientSeedConfig>? Clients { get; set; }

    // Realm roles to create
    public List<RealmRoleSeedConfig>? RealmRoles { get; set; }

    // Groups to create with role assignments
    public List<GroupSeedConfig>? Groups { get; set; }

    // Groups to assign as realm default groups (users auto-join on creation)
    public List<string>? DefaultGroups { get; set; }

    // Client scope definitions to create (by name)
    public List<string>? AddedClientScopeDefinitions { get; set; }

    // Default client scopes to add/remove
    public ClientScopeChanges? DefaultClientScopes { get; set; }

    // Optional client scopes to add/remove
    public ClientScopeChanges? OptionalClientScopes { get; set; }

    // Identity provider skeletons
    public List<IdpSeedConfig>? IdentityProviders { get; set; }

    // Required action overrides
    public List<RequiredActionSeedConfig>? RequiredActions { get; set; }

    // Auth flows that need manual configuration
    public List<string>? CustomAuthenticationFlows { get; set; }

    // Informational (master realm only)
    public string? Note { get; set; }
    public List<string>? RealmClients { get; set; }
}

public class ClientSeedConfig
{
    public string ClientId { get; set; } = string.Empty;
    public bool? PublicClient { get; set; }
    public bool? ServiceAccountsEnabled { get; set; }
    public bool? StandardFlowEnabled { get; set; }
    public bool? DirectAccessGrantsEnabled { get; set; }
    public bool? ImplicitFlowEnabled { get; set; }
    public string? RootUrl { get; set; }
    public string? BaseUrl { get; set; }
    public string? AdminUrl { get; set; }
    public string? Protocol { get; set; }
    public List<string>? RedirectUris { get; set; }
    public List<string>? WebOrigins { get; set; }

    // Arbitrary client attributes (e.g., pkce.code.challenge.method)
    public Dictionary<string, string>? Attributes { get; set; }

    // Client roles to assign to this client's service account user.
    // Key = target client ID (e.g., "realm-management"), Value = list of role names.
    // Only applicable when serviceAccountsEnabled = true.
    public Dictionary<string, List<string>>? ServiceAccountClientRoles { get; set; }

    // Protocol mappers to create on this client (e.g., user attribute → JWT claim).
    public List<ProtocolMapperSeedConfig>? ProtocolMappers { get; set; }

    // Protocol mappers to create on the client's dedicated client scope
    // (e.g., "storeapp-dedicated"). Keycloak routes token claims through the
    // dedicated scope, so mappers often need to exist there as well.
    public List<ProtocolMapperSeedConfig>? DedicatedScopeMappers { get; set; }
}

/// <summary>
/// Protocol mapper definition for a Keycloak client.
/// Maps user attributes, roles, or other sources to JWT token claims.
/// </summary>
public class ProtocolMapperSeedConfig
{
    public string Name { get; set; } = string.Empty;
    public string ProtocolMapper { get; set; } = string.Empty; // e.g., "oidc-usermodel-attribute-mapper"
    public Dictionary<string, string>? Config { get; set; }
}

public class RealmRoleSeedConfig
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class GroupSeedConfig
{
    public string Name { get; set; } = string.Empty;
    public List<string>? RealmRoles { get; set; }
}

public class ClientScopeChanges
{
    public List<string>? Added { get; set; }
    public List<string>? Removed { get; set; }
}

public class IdpSeedConfig
{
    public string Alias { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
}

public class RequiredActionSeedConfig
{
    public string Alias { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DefaultAction { get; set; }
}
