using Lz.Core.Config;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Single source of truth for the Backend-For-Frontend (BFF) runtime wiring
/// that is shared across topologies (EcsExpress + Lambda). Keeps the gating
/// rule and the <c>LZ_BFF_*</c> env-var set identical wherever the container
/// task/function is defined.
///
/// <para>
/// EVERYTHING here is gated by <see cref="IsEnabled"/>. When the BFF is off for
/// a tenant (the default), callers add NOTHING — no env vars, no stack
/// reference — so the task/function definition is byte-for-byte identical to a
/// pre-BFF deploy.
/// </para>
/// </summary>
internal static class BffWiring
{
    /// <summary>
    /// BFF active for this tenant? True only when the tenant explicitly opts in
    /// (<c>BffEnabled: true</c>). The service/data components see only
    /// <see cref="TenantConfig"/> (not the system config), so enablement is
    /// self-contained on the tenant: an unset tenant inherits "off". The
    /// system-level default is applied at config-merge time
    /// (<c>ConfigMerger.GetEffectiveBffEnabled</c>) for call sites that have
    /// both configs.
    /// </summary>
    public static bool IsEnabled(TenantConfig tenantConfig) => tenantConfig.BffEnabled == true;

    /// <summary>The Cognito pool key the BFF client lives on (default tenantauth).</summary>
    public static string ResolvePool(TenantConfig tenantConfig) =>
        !string.IsNullOrWhiteSpace(tenantConfig.BffAuthPool) ? tenantConfig.BffAuthPool! : "tenantauth";

    /// <summary>
    /// Build the BFF runtime env-var pairs for a tenant's container. Sourced
    /// from <see cref="TenantConfig"/>, the foundation Cognito outputs (via
    /// <see cref="BffStackOutputs"/>), and derived conventions. The
    /// <c>LZ_BFF_CLIENT_SECRET</c> value comes from the foundation stack
    /// reference; the per-tenant Secrets Manager secret also carries it for
    /// the secret-based read path.
    ///
    /// <para>
    /// Returns a list of <c>(name, Output&lt;string&gt;)</c>. Pulumi resolves
    /// the outputs at apply time; callers serialise them into the container
    /// definition. Call ONLY when <see cref="IsEnabled"/> is true.
    /// </para>
    /// </summary>
    public static List<(string Name, Output<string> Value)> BuildEnv(
        TenantConfig tenantConfig, ComponentResource parent)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var region = tenantConfig.Region ?? "us-west-2";
        var pool = ResolvePool(tenantConfig);
        var rootDomain = tenantConfig.RootDomain;

        var foundation = new BffStackOutputs(tenantConfig, parent);

        // SSM Parameter path holding the Data Protection key ring (§8.4).
        var dpParam = $"/{sk}/{env}/bff/dataprotection";
        // Session DynamoDB table — DEDICATED table {sk}_{tk}_bff (id/sk schema),
        // provisioned by AwsEcsExpressPostDeployAction.EnsureTenantTablesAsync. Kept
        // separate from the app's data table {sk}_{tk} (PK/SK envelope) so the BFF
        // session store never collides with the app repo. (§8.4)
        var sessionTable = $"{sk}_{tk}_bff";
        // Cookie domain spans subtenants: .{RootDomain} (§8.12).
        var cookieDomain = string.IsNullOrWhiteSpace(rootDomain) ? string.Empty : $".{rootDomain}";
        // Per-pool session TTL (hours). The infra default mirrors the
        // employee-pool recommendation; the confidential client's
        // RefreshTokenValidity is the authoritative bound.
        var sessionTtlHours = "12";

        // Data Protection application name — identical across all tasks/Lambda
        // so the key ring is shared (§8.4). Keyed by system+env+pool.
        var dpAppName = $"{sk}-{env}-bff-{pool}";
        var authName = pool;

        var lit = (string s) => Output.Create(s);

        // LZ_BFF_METADATA_URL is the Cognito ISSUER discovery URL
        // (https://cognito-idp.{region}.amazonaws.com/{poolId}/.well-known/openid-configuration),
        // so discovery + issuer/JWKS validation are correct. Derive LZ_BFF_AUTHORITY from it (issuer =
        // metadata URL minus the discovery suffix) so the Authority field actually holds the OIDC
        // issuer, NOT the Hosted-UI/custom domain (foundation.Authority(pool)). This makes the
        // ResolvedMetadataUrl fallback resolve to the correct discovery URL even if METADATA_URL is unset.
        // TODO(authority-decoupling): export the issuer and the Hosted-UI/custom domain as SEPARATE
        // foundation outputs (e.g. auth_{pool}_issuer + auth_{pool}_hostedUiDomain) and use the
        // Hosted-UI domain only for the authorize/logout browser host. For now the issuer is derived here.
        const string DiscoverySuffix = "/.well-known/openid-configuration";
        var metadataUrl = foundation.MetadataUrl(pool);
        var issuerAuthority = metadataUrl.Apply(url =>
            url.EndsWith(DiscoverySuffix, StringComparison.OrdinalIgnoreCase)
                ? url[..^DiscoverySuffix.Length]
                : url);

        var list = new List<(string, Output<string>)>
        {
            // Activates the BFF hosting-startup assembly inside AppHost.
            ("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", lit("LazyMagic.OIDC.Bff")),
            ("LZ_BFF_ENABLED", lit("true")),
            ("LZ_BFF_PROVIDER", lit("cognito")),
            ("LZ_BFF_AUTHORITY", issuerAuthority),
            ("LZ_BFF_METADATA_URL", metadataUrl),
            ("LZ_BFF_CLIENT_ID", foundation.ClientId(pool)),
            ("LZ_BFF_CLIENT_SECRET", foundation.ClientSecret(pool)),
            ("LZ_BFF_SCOPES", lit("openid profile email")),
            ("LZ_BFF_COOKIE_DOMAIN", lit(cookieDomain)),
            ("LZ_BFF_COOKIE_NAME", lit("__bff")),
            ("LZ_BFF_SESSION_TABLE", lit(sessionTable)),
            ("LZ_BFF_SESSION_TTL_HOURS", lit(sessionTtlHours)),
            ("LZ_BFF_DP_PARAM", lit(dpParam)),
            ("LZ_BFF_DP_APPNAME", lit(dpAppName)),
            ("LZ_BFF_AUTHNAME", lit(authName)),
            ("LZ_BFF_AWS_REGION", lit(region)),
            ("LZ_BFF_ACCESS_TOKEN_SKEW_SECONDS", lit("60")),
        };

        // SECOND pool instance — consumerauth at /cbff (LZ_CBFF_*). Appended ONLY when the tenant
        // opts in (BffConsumerAuthEnabled). Requires the consumerauth pool to have ProvisionBffClient
        // + BffRoutePrefix:/cbff so the foundation exports auth_consumerauth_bff* outputs. Default off
        // ⇒ the list above is the exact single-pool set (byte-for-byte unchanged for existing tenants).
        // The DP key ring + app name are SHARED with the default instance (the cookie codec is shared
        // in the apphost), so no LZ_CBFF_DP_* is emitted.
        if (tenantConfig.BffConsumerAuthEnabled == true)
        {
            const string consumerPool = "consumerauth";
            var consumerMeta = foundation.MetadataUrl(consumerPool);
            var consumerIssuer = consumerMeta.Apply(url =>
                url.EndsWith(DiscoverySuffix, StringComparison.OrdinalIgnoreCase)
                    ? url[..^DiscoverySuffix.Length]
                    : url);
            const string consumerTtlHours = "720"; // consumer sessions ≈ 30 days (§8.14)

            list.AddRange(new List<(string, Output<string>)>
            {
                ("LZ_CBFF_ENABLED", lit("true")),
                ("LZ_CBFF_PROVIDER", lit("cognito")),
                ("LZ_CBFF_AUTHORITY", consumerIssuer),
                ("LZ_CBFF_METADATA_URL", consumerMeta),
                ("LZ_CBFF_CLIENT_ID", foundation.ClientId(consumerPool)),
                ("LZ_CBFF_CLIENT_SECRET", foundation.ClientSecret(consumerPool)),
                ("LZ_CBFF_SCOPES", lit("openid profile email")),
                ("LZ_CBFF_ROUTE_PREFIX", lit("/cbff")),
                ("LZ_CBFF_COOKIE_DOMAIN", lit(cookieDomain)),
                ("LZ_CBFF_COOKIE_NAME", lit("__cbff")),
                ("LZ_CBFF_SESSION_TABLE", lit($"{sk}_{tk}_cbff")),
                ("LZ_CBFF_SESSION_TTL_HOURS", lit(consumerTtlHours)),
                ("LZ_CBFF_AUTHNAME", lit(consumerPool)),
                ("LZ_CBFF_AWS_REGION", lit(region)),
                ("LZ_CBFF_ACCESS_TOKEN_SKEW_SECONDS", lit("60")),
            });
        }

        return list;
    }

    /// <summary>The SSM Parameter Store path prefix for the Data Protection key ring.</summary>
    public static string DataProtectionParamPath(string sk, string env) => $"/{sk}/{env}/bff/dataprotection";
}
