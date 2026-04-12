namespace Lz.Core.Config;

/// <summary>
/// Full model for systemconfig.{systemkey}.{env}.yaml.
/// SystemKey and Environment are derived from the filename, NOT from fields in the file.
/// This file serves dual purpose: deployment settings (consumed by Lz tool)
/// and runtime settings (consumed by running containers).
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

    // Central auth — Keycloak is deployed in the shared-services account, not per-environment.
    // The domain of the shared-services Keycloak (e.g., "auth.meadowsservices.com").
    public string CentralAuthDomain { get; set; } = string.Empty;

    // Cross-account shared services — references the shared-services account.
    // SharedProfile is from YAML (e.g., "monro-shared"). The rest are resolved by CLI at startup.
    public string? SharedProfile { get; set; }
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }
    public string? SharedRegion { get; set; }
    public List<string> TrustedAccountIds { get; set; } = new();

    // NEW fields (not in current config, will be added)
    public string Platform { get; set; } = "aws";
    public string Topology { get; set; } = "ecs";
    public StateConfig? State { get; set; }

    // Infrastructure sizing
    public EcsConfig? ECS { get; set; }
    public AppRunnerConfig? AppRunner { get; set; }
    public CdnConfig? CDN { get; set; }

    // Seed data — shared S3 bucket for EFS + database seeding/refresh
    public SeedDataConfig? SeedData { get; set; }

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
