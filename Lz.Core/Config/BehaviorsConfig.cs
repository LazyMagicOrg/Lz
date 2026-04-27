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
    /// Whether unauthenticated access to this app should be redirected to the
    /// public landing page / login initiator at the CloudFront edge. Default
    /// <c>true</c> preserves the historical gate behavior for any config that
    /// pre-dates this property. Plugins that consume this (e.g. BCPlugin)
    /// emit it as a trailing element on the webapp behavior tuple so the
    /// CloudFront viewer-request function can apply it without a second KVS
    /// read. Subtenants typically host multiple apps, each with their own
    /// auth requirement — that's why this flag is per-app, not per-subtenant.
    /// </summary>
    public bool AuthRequired { get; set; } = true;
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
