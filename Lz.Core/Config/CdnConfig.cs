namespace Lz.Core.Config;

/// <summary>
/// CDN deployment configuration section — shared between systemconfig and tenantconfig.
/// Maps to the "CDN:" section in YAML.
/// </summary>
public class CdnConfig
{
    public string PriceClass { get; set; } = "PriceClass_100";
    public string DefaultRootObject { get; set; } = "app/index.html";
}
