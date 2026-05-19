namespace Lz.Core.Config;

/// <summary>
/// Full model for tenantconfig.{systemkey}.{tenantkey}.{env}.yaml.
/// SystemKey, TenantKey, and Environment are derived from the filename, NOT from fields in the file.
/// This file serves dual purpose: per-tenant deployment settings (consumed by Lz tool)
/// and per-tenant runtime settings (consumed by running containers, overriding system defaults).
/// </summary>
public class TenantConfig
{
    // --- Derived from filename (set by ConfigLoader) ---
    public string SystemKey { get; set; } = string.Empty;
    public string TenantKey { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Directory containing the tenantconfig YAML file (i.e., repo root).
    /// Set by ConfigLoader, used to resolve relative paths (e.g., CloudFront function JS files).
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string ConfigDirectory { get; set; } = ".";

    // --- Deployment Settings ---
    public string RootDomain { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? HostedZoneId { get; set; }
    public string? AcmCertificateArn { get; set; }
    public BehaviorsConfig? Behaviors { get; set; }
    public Dictionary<string, SubtenantEntry>? Subtenants { get; set; }

    /// <summary>
    /// Previous domains that 301 redirect to RootDomain during domain transitions.
    /// Each legacy domain must have a Route53 hosted zone in the same account.
    /// </summary>
    public List<string>? LegacyDomains { get; set; }
    public string TenantSuffix { get; set; } = string.Empty;
    public string? Profile { get; set; }
    public string? Region { get; set; }

    /// <summary>
    /// Optional override for the per-tenant media S3 bucket name. When unset,
    /// the name is derived by convention: {sk}-{tk}-{stk}-media--{suffix}.
    /// Backs the Smartstore.AmazonS3 media storage provider.
    /// </summary>
    public string? MediaBucket { get; set; }

    // Cross-account shared services — propagated from SystemConfig at runtime
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }
    public string? CentralAuthDomain { get; set; }

    // Per-tenant infrastructure overrides
    public EcsConfig? ECS { get; set; }
    public AppRunnerConfig? AppRunner { get; set; }
    public CdnConfig? CDN { get; set; }

    // --- Runtime Application Settings (override system defaults) ---
    public SecretsManagerConfig? SecretsManager { get; set; }
    public string? DefaultTenant { get; set; }
    public IntegrationsConfig? Integrations { get; set; }
    public Dictionary<string, AuthConfigEntry>? AuthConfigs { get; set; }
    public RequestRewriterConfig? RequestRewriter { get; set; }
    public RequestLoggingConfig? RequestLogging { get; set; }
    public VerboseLoggingConfig? Authentication { get; set; }
    public VerboseLoggingConfig? ShopModuleAuth { get; set; }
    public VerboseLoggingConfig? UsersModuleAuth { get; set; }
    public VerboseLoggingConfig? Keycloak { get; set; }

    // --- SmartStore usersettings.json content ---
    // Loaded from smartstore.usersettings.json (same directory as tenantconfig YAML).
    // Written to EFS smartstore-config/usersettings.json by the gate-checker Lambda.
    // Contains the full usersettings.json structure (Smartstore, Serilog, etc.).
    // Stored separately to avoid SSM Parameter Store 4096-char limit.
    public Dictionary<string, object>? Smartstore { get; set; }
}
