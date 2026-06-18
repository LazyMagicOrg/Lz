namespace Lz.Core.Config;

/// <summary>
/// Behaviors hierarchy — defines CDN routing rules for APIs, Assets, and WebApps.
/// System → Tenant → Subtenant override chain.
/// </summary>
public class BehaviorsConfig
{
    public List<ApiBehavior>? Apis { get; set; }
    public List<AssetBehavior>? Assets { get; set; }
    public List<WebAppBehavior>? WebApps { get; set; }
    public List<StaticSiteBehavior>? StaticSites { get; set; }
}

public class ApiBehavior
{
    public string Path { get; set; } = string.Empty;
    public string ApiName { get; set; } = string.Empty;
}

public class AssetBehavior
{
    public string Path { get; set; } = string.Empty;
}

public class WebAppBehavior
{
    public string Path { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the auth pool that authenticates this app. Must match a key in
    /// <c>SystemConfig.AuthConfigs</c> (e.g. <c>"plannerauth"</c>,
    /// <c>"tenantauth"</c>, <c>"systemauth"</c>) or be <c>null</c>/empty for
    /// "no authentication required" (public access).
    ///
    /// Cascade rule: leaf-dominant by <see cref="Path"/>. Tenant config can
    /// override the system value for a given path; subtenant config can
    /// override the tenant value.
    /// <list type="bullet">
    ///   <item><description><see cref="AuthConfig"/> is <c>null</c> →
    ///     inherit from the next-higher level.</description></item>
    ///   <item><description><see cref="AuthConfig"/> is <c>""</c> (empty
    ///     string) → explicit-public override, regardless of what the parent
    ///     specified.</description></item>
    ///   <item><description><see cref="AuthConfig"/> is non-empty → gated
    ///     by the named pool.</description></item>
    /// </list>
    ///
    /// At override sites, restating <see cref="AppName"/> is allowed and is
    /// a no-op for cascade ownership — see
    /// <see cref="ConfigMerger.ResolveWebApps"/> for the full rule. The
    /// previous schema documented "tenant/subtenant entries may supply only
    /// (Path, AuthConfig) — they don't redeclare AppName"; that constraint
    /// no longer exists. Restate AppName for clarity if you like.
    ///
    /// Plugins (e.g. BCPlugin) consume this to emit:
    /// <list type="bullet">
    ///   <item><description>A <c>gated</c> bit (0|1) at position 5 of the
    ///     webapp behavior tuple in the per-host KVS routing entry — derived
    ///     from <c>AuthConfig != null</c>. CFRequest reads this to decide
    ///     whether to redirect unauthenticated traffic; it does not read the
    ///     auth name from this position.</description></item>
    ///   <item><description>The full resolved <c>{ path, name, authConfig }</c>
    ///     mapping in a separate per-host <c>{host}-auth</c> KVS entry. Read
    ///     by auth-related CF functions (CFAuthConfig) to surface
    ///     <c>apps[].authConfig</c> in the <c>/config</c> response.</description></item>
    /// </list>
    ///
    /// Subtenants can override which pool authenticates the same app — this
    /// is the architectural reason the property exists at this granularity
    /// and not just at the system level.
    /// </summary>
    public string? AuthConfig { get; set; }
}

/// <summary>
/// Per-subtenant static site (Hugo or similar) served at a path prefix
/// (e.g. "/explore/"). Bucket naming mirrors webapp convention:
///   {sk}-{tk}-{stk}-webapp-{AppName}-{sts}
/// Content lives under /wwwroot/{prefix}/ in the bucket. No lz-auth gate
/// is applied — static-site behaviours are always public.
/// </summary>
public class StaticSiteBehavior
{
    public string Path { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
}
