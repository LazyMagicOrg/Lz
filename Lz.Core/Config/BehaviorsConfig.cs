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
}
