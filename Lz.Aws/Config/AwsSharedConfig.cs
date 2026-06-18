using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific extension of <see cref="SharedConfig"/>. Holds fields whose
/// semantics only make sense on AWS: centralized Keycloak sizing, Tailscale
/// subnet-router sizing, and cross-account trust for the shared system secret.
/// </summary>
public class AwsSharedConfig : SharedConfig
{
    /// <summary>Centralized Keycloak deployment sizing.</summary>
    public SharedKeycloakConfig Keycloak { get; set; } = new();

    // Tailscale subnet-router ASG sizing (shared across envs).
    public string TailscaleInstanceType { get; set; } = "t4g.nano";
    public int TailscaleDesiredCapacity { get; set; } = 2;

    /// <summary>
    /// AWS account IDs allowed to read the shared system secret cross-account.
    /// Used to author a resource policy on the shared/system secret.
    /// </summary>
    public List<string> TrustedAccountIds { get; set; } = new();
}

/// <summary>
/// Sizing + theme path for the centralized Keycloak ECS service.
/// </summary>
public class SharedKeycloakConfig
{
    public string ImageTag { get; set; } = "26.5.0";
    public int Cpu { get; set; } = 512;
    public int Memory { get; set; } = 1024;
    public int DesiredCount { get; set; } = 2;

    /// <summary>
    /// EFS path for the Keycloak custom theme directory.
    /// Must match the theme name in keycloakthemes/{name}/.
    /// </summary>
    public string? ThemePath { get; set; }
}
