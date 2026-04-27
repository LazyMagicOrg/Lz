namespace Lz.Core.Config;

/// <summary>
/// Full model for tenantconfig.{systemkey}.{tenantkey}.{env}.yaml.
/// SystemKey, TenantKey, and Environment are derived from the filename, NOT from fields in the file.
/// This file serves dual purpose: per-tenant deployment settings (consumed by Lz tool)
/// and per-tenant runtime settings (consumed by running containers, overriding system defaults).
/// Platform-specific fields (certificate ARNs, DNS zone IDs, cross-account
/// secret ARNs, compute sizing) live on platform-derived types like
/// <c>Lz.Aws.Config.AwsTenantConfig</c>.
/// </summary>
public class TenantConfig
{
    // --- Derived from filename (set by ConfigLoader) ---
    public string SystemKey { get; set; } = string.Empty;
    public string TenantKey { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Directory containing the tenantconfig YAML file (i.e., repo root).
    /// Set by ConfigLoader, used to resolve relative paths referenced by the config.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string ConfigDirectory { get; set; } = ".";

    // --- Deployment Settings ---
    public string RootDomain { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public BehaviorsConfig? Behaviors { get; set; }
    public Dictionary<string, SubtenantEntry>? Subtenants { get; set; }

    /// <summary>
    /// Previous domains that 301 redirect to RootDomain during domain transitions.
    /// Each legacy domain must have a DNS zone managed in the same account.
    /// </summary>
    public List<string>? LegacyDomains { get; set; }
    public string TenantSuffix { get; set; } = string.Empty;
    public string? Profile { get; set; }
    public string? Region { get; set; }

    /// <summary>
    /// Optional DNS hostname of a centralized auth service, propagated from
    /// SystemConfig at runtime. Empty/null means auth is per-environment.
    /// </summary>
    public string? CentralAuthDomain { get; set; }

    // Per-tenant CDN overrides
    public CdnConfig? CDN { get; set; }

    // --- Runtime Application Settings (override system defaults) ---
    public SecretsManagerConfig? SecretsManager { get; set; }
    public string? DefaultTenant { get; set; }
    public IntegrationsConfig? Integrations { get; set; }
    // AuthConfigs deliberately omitted: Cognito pools are system-scoped (one set
    // of pools per environment, shared by all tenants). Pool declarations live
    // on SystemConfig.AuthConfigs; per-tenant overrides don't exist because the
    // pools themselves aren't per-tenant.
    public RequestRewriterConfig? RequestRewriter { get; set; }
    public RequestLoggingConfig? RequestLogging { get; set; }
    public VerboseLoggingConfig? Authentication { get; set; }
    public VerboseLoggingConfig? ShopModuleAuth { get; set; }
    public VerboseLoggingConfig? UsersModuleAuth { get; set; }
    public VerboseLoggingConfig? Keycloak { get; set; }

    // --- SmartStore usersettings.json content ---
    // Loaded from smartstore.usersettings.json (same directory as tenantconfig YAML)
    // and written to shared file storage by the init runner. Stored separately to
    // avoid secret-store parameter-size limits.
    public Dictionary<string, object>? Smartstore { get; set; }
}
