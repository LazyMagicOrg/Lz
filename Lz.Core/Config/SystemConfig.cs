namespace Lz.Core.Config;

/// <summary>
/// Full model for systemconfig.{systemkey}.{env}.yaml.
/// SystemKey and Environment are derived from the filename, NOT from fields in the file.
/// This file serves dual purpose: deployment settings (consumed by Lz tool)
/// and runtime settings (consumed by running containers).
/// Platform-specific fields (compute shape, shared-account identifiers,
/// cross-account trust) live on platform-derived types like
/// <c>Lz.Aws.Config.AwsSystemConfig</c>.
/// </summary>
public class SystemConfig
{
    // --- Derived from filename (set by ConfigLoader) ---
    public string SystemKey { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;

    // --- Deployment Settings ---
    public string? AdminAuth { get; set; }
    public string? AdminEmail { get; set; }
    public string SystemSuffix { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? SystemDomain { get; set; }
    public string? DefaultTenant { get; set; }

    // Network
    public string VpcCidr { get; set; } = string.Empty;

    /// <summary>
    /// Optional DNS hostname of a centralized auth service. When set, per-environment
    /// auth deployment is skipped in favour of the shared central auth. Empty/null
    /// means auth is deployed per-environment. Interpreted by platform libraries.
    /// </summary>
    public string CentralAuthDomain { get; set; } = string.Empty;

    // Platform/topology selectors
    public string Platform { get; set; } = "aws";
    public string Topology { get; set; } = "ecs-fargate-keycloak";
    public StateConfig? State { get; set; }

    // CDN sizing/defaults
    public CdnConfig? CDN { get; set; }

    // Seed data — shared object-storage bucket used for tenant seeding and refresh
    public SeedDataConfig? SeedData { get; set; }

    // Backup — AWS Backup configuration for foundation EFS (see BackupConfig).
    // When omitted, defaults apply: enabled in prod/staging, disabled elsewhere.
    public BackupConfig? Backup { get; set; }

    // Behaviors — system-level routing rules
    public BehaviorsConfig? Behaviors { get; set; }

    // --- Runtime Application Settings ---
    public SecretsManagerConfig? SecretsManager { get; set; }
    public string? IntegrationSecretsPath { get; set; }
    public IntegrationsConfig? Integrations { get; set; }
    public Dictionary<string, AuthConfigEntry>? AuthConfigs { get; set; }
    public RequestRewriterConfig? RequestRewriter { get; set; }
    public RequestLoggingConfig? RequestLogging { get; set; }
    public VerboseLoggingConfig? Authentication { get; set; }
    public VerboseLoggingConfig? ShopModuleAuth { get; set; }
    public VerboseLoggingConfig? UsersModuleAuth { get; set; }
    public VerboseLoggingConfig? Keycloak { get; set; }
}
