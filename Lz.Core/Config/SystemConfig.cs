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

    // Durability — deletion-protection + point-in-time recovery for the
    // per-subtenant vault/PII DynamoDB table (see DurabilityConfig). When
    // omitted, NOTHING is applied and the emitted table is byte-identical to a
    // pre-durability deploy (the MagicPets/no-opt-in baseline).
    public DurabilityConfig? Durability { get; set; }

    // VectorStore — OpenSearch Serverless (aoss) collection for the semantic-
    // matching backend (see VectorStoreConfig). When omitted, nothing aoss-
    // related is provisioned and no OpenSearch env/IAM reaches the tenant
    // service — the no-opt-in baseline.
    public VectorStoreConfig? VectorStore { get; set; }

    // Behaviors — system-level routing rules
    public BehaviorsConfig? Behaviors { get; set; }

    // --- Runtime Application Settings ---
    public SecretsManagerConfig? SecretsManager { get; set; }

    // RequiredSecrets — secrets that must exist (with listed JSON keys) before
    // deploysystem proceeds; missing values are prompted for or supplied via
    // --secret (see RequiredSecretConfig). Absent = nothing checked.
    public List<RequiredSecretConfig>? RequiredSecrets { get; set; }
    public string? IntegrationSecretsPath { get; set; }
    public IntegrationsConfig? Integrations { get; set; }
    public Dictionary<string, AuthConfigEntry>? AuthConfigs { get; set; }

    /// <summary>
    /// System-wide master switch for the Backend-For-Frontend (BFF) auth
    /// feature. Default <c>false</c>. When false, NO BFF runtime env vars are
    /// injected into container task/function definitions, so the deployed
    /// task definition is byte-for-byte identical to a pre-BFF deploy. A
    /// tenant can override this via <c>TenantConfig.BffEnabled</c>. Turning
    /// this on is independent of (but normally paired with) per-pool
    /// <c>ProvisionBffClient</c> on the AWS auth-config entry, which controls
    /// the confidential Cognito client. See <c>Platform/MultiTenantAuth.md
    /// §8.7, §8.14</c>.
    /// </summary>
    public bool BffEnabled { get; set; } = false;

    /// <summary>
    /// Names the Cognito pool (an <see cref="AuthConfigs"/> key, e.g.
    /// <c>tenantauth</c>) whose confidential BFF client the running container
    /// uses. Only consulted when <see cref="BffEnabled"/> is true; selects
    /// which <c>auth_{pool}_bff*</c> foundation outputs feed the
    /// <c>LZ_BFF_*</c> env vars. Default <c>tenantauth</c> — the employee pool
    /// that the BFF MVP targets (MultiTenantAuth.md §8.9).
    /// </summary>
    public string BffAuthPool { get; set; } = "tenantauth";
    public RequestRewriterConfig? RequestRewriter { get; set; }
    public RequestLoggingConfig? RequestLogging { get; set; }
    public VerboseLoggingConfig? Authentication { get; set; }
    public VerboseLoggingConfig? ShopModuleAuth { get; set; }
    public VerboseLoggingConfig? UsersModuleAuth { get; set; }
    public VerboseLoggingConfig? Keycloak { get; set; }
}
