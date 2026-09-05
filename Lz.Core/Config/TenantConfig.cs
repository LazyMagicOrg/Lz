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

    /// <summary>
    /// Container image digests resolved from ECR just before the Pulumi program is built,
    /// keyed by service name (value is the full <c>sha256:…</c>). Runtime-only — never
    /// serialized, never read from YAML.
    ///
    /// <para>Carried here rather than passed as a parameter because
    /// <c>ITenantServiceComponent.Deploy</c> is implemented directly by a sibling
    /// workspace's own plugin, so changing that signature is a compile break outside this
    /// repo. <c>CentralAuthDomain</c> above is the same pattern: set imperatively before
    /// the program is built.</para>
    ///
    /// <para>A MISSING entry is normal, not an error — it means no digest could be
    /// resolved (empty repository, absent tag, no AWS access) and the image reference falls
    /// back to the tag. Empty when digest pinning is not opted into, in which case nothing
    /// ever populates it and no AWS call is made.</para>
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public Dictionary<string, string> ResolvedImageDigests { get; } = new();

    /// <summary>
    /// Optional override for the per-tenant media S3 bucket name. When unset,
    /// the name is derived by convention: {sk}-{tk}-{stk}-media--{suffix}.
    /// Backs the Smartstore.AmazonS3 media storage provider.
    /// </summary>
    public string? MediaBucket { get; set; }

    /// <summary>
    /// SmartStore media storage backend: "s3" or "filesystem" (default).
    /// When "s3", tenant deploy seeds media directly into the media bucket and
    /// sets Media.Storage.Provider to the Smartstore.AmazonS3 provider, so the
    /// tenant comes up on S3 with no manual configuration or migration.
    /// </summary>
    public string MediaStorage { get; set; } = "filesystem";

    // Cross-account shared services — propagated from SystemConfig at runtime
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }

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

    /// <summary>
    /// Per-tenant override of <see cref="SystemConfig.BffEnabled"/>. Nullable
    /// three-state: <c>null</c> (unset) inherits the system value; <c>true</c>
    /// / <c>false</c> force the tenant on/off. Default <c>null</c> — a tenant
    /// that says nothing about the BFF gets exactly today's behaviour. Gates
    /// the <c>LZ_BFF_*</c> container env injection for this tenant. See
    /// <c>Platform/MultiTenantAuth.md §8.7</c>.
    /// </summary>
    public bool? BffEnabled { get; set; }

    /// <summary>
    /// Per-tenant override of <see cref="SystemConfig.BffAuthPool"/> — names
    /// the Cognito pool whose confidential BFF client this tenant's container
    /// uses. <c>null</c> (default) inherits the system value. Only consulted
    /// when the BFF is enabled for the tenant.
    /// </summary>
    public string? BffAuthPool { get; set; }

    /// <summary>
    /// When <c>true</c>, ALSO wire a SECOND BFF instance for the <c>consumerauth</c> pool
    /// (the <c>LZ_CBFF_*</c> env set: route <c>/cbff</c>, cookie <c>__cbff</c>, session table
    /// <c>{sk}_{tk}_cbff</c>, <c>lz-authname=consumerauth</c>) alongside the default tenantauth
    /// <c>/bff</c> instance. Requires the <c>consumerauth</c> pool to set
    /// <c>ProvisionBffClient: true</c> (+ <c>BffRoutePrefix: /cbff</c>) so the confidential client
    /// and its <c>auth_consumerauth_bff*</c> foundation outputs exist. Default <c>false</c> ⇒ the
    /// container is byte-for-byte identical to a single-pool BFF deploy. (§ multi-pool BFF)
    /// </summary>
    public bool? BffConsumerAuthEnabled { get; set; }

    /// <summary>
    /// When <c>true</c>, ALSO wire a THIRD BFF instance for the <c>systemauth</c> pool
    /// (the <c>LZ_ABFF_*</c> env set: route <c>/abff</c>, cookie <c>__abff</c>, session table
    /// <c>{sk}_{tk}_abff</c>, <c>lz-authname=systemauth</c>) alongside the default tenantauth
    /// <c>/bff</c> and the optional consumerauth <c>/cbff</c>. Requires the <c>systemauth</c> pool
    /// to set <c>ProvisionBffClient: true</c> (+ <c>BffRoutePrefix: /abff</c>) so the confidential
    /// client and its <c>auth_systemauth_bff*</c> foundation outputs exist. Default <c>false</c> ⇒
    /// the container is byte-for-byte identical to a deploy without it.
    ///
    /// <para>WHY A THIRD INSTANCE EXISTS: a platform-staff console signs in against a pool that is
    /// NOT the merchants' pool, so its browser session cannot share the tenantauth
    /// <c>/bff</c> — that instance is bound to one pool's confidential client. Repointing
    /// <c>BffAuthPool</c> instead would move the single <c>/bff</c> WHOLESALE and break every
    /// tenantauth app sharing it. See Scutara's Docs/specs/PlatformStaffPool.md.</para>
    /// </summary>
    public bool? BffSystemAuthEnabled { get; set; }

    /// <summary>
    /// M0-8: when <c>true</c>, add the CloudFront ordered behaviors that route the bare <c>/mcp</c>
    /// (Streamable HTTP) and the RFC 9728 PRM (<c>/.well-known/oauth-protected-resource</c>) to the API
    /// origin (AipHost), unstripped (same passthrough model as <c>/bff</c>). Default <c>false</c>/null ⇒
    /// the behaviors list is byte-for-byte identical to a non-MCP tenant. Pair with the pool's
    /// <c>McpResource</c> opt-in and AipHost's <c>Mcp:*</c> config. See specs/McpAgents.md M0-8.
    /// </summary>
    public bool? McpEnabled { get; set; }

    /// <summary>
    /// When <c>true</c>, inject the <c>SMARTSTORE_COGNITO_*</c> env set
    /// (AUTHORITY / CLIENTID / CLIENTSECRET / HOSTEDUIDOMAIN) into this tenant's
    /// storefront container, sourced from the foundation Cognito outputs for the
    /// <see cref="SmartstoreCognitoPool"/> pool. Requires that pool to set
    /// <c>ProvisionSmartstoreClient: true</c> so the confidential client and its
    /// <c>auth_{pool}_smartstoreClient*</c> foundation outputs exist. Default
    /// <c>null</c>/<c>false</c> ⇒ no env injection, so the task definition is
    /// byte-for-byte identical to a pre-Smartstore-Cognito deploy. Consumed by
    /// the <c>Smartstore.Cognito.Auth</c> module (env wins over DB settings). See
    /// <c>Platform/SMARTSTORE-COGNITO-AUTH.md §8</c>.
    /// </summary>
    public bool? SmartstoreCognitoEnabled { get; set; }

    /// <summary>
    /// Names the Cognito pool whose confidential Smartstore client this tenant's
    /// storefront uses. <c>null</c> (default) resolves to <c>consumerauth</c> —
    /// the pool consumers authenticate against. Only consulted when
    /// <see cref="SmartstoreCognitoEnabled"/> is true.
    /// </summary>
    public string? SmartstoreCognitoPool { get; set; }

    // --- SmartStore usersettings.json content ---
    // Loaded from smartstore.usersettings.json (same directory as tenantconfig YAML)
    // and written to shared file storage by the init runner. Stored separately to
    // avoid secret-store parameter-size limits.
    public Dictionary<string, object>? Smartstore { get; set; }
}
