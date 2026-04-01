namespace Lz.Core.Config;

/// <summary>
/// Configuration for the shared-services account.
/// Loaded from sharedconfig.yaml — there is a single shared-services account.
/// The shared-services account hosts centralized Keycloak and Tailscale admin.
/// </summary>
public class SharedConfig
{
    // --- Deployment Settings ---
    public string Profile { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string VpcCidr { get; set; } = string.Empty;

    /// <summary>
    /// Short random string for S3 bucket global uniqueness (e.g., "49d2-8357").
    /// Used in state bucket names and other globally-unique resources.
    /// </summary>
    public string SharedSuffix { get; set; } = string.Empty;

    // Pulumi state
    public StateConfig? State { get; set; }

    // --- Keycloak ---
    public SharedKeycloakConfig Keycloak { get; set; } = new();

    // --- Database ---
    public string DbInstanceClass { get; set; } = "db.t4g.micro";
    public int DbAllocatedStorage { get; set; } = 20;

    // --- Tailscale ---
    public string TailscaleInstanceType { get; set; } = "t4g.nano";
    public int TailscaleDesiredCapacity { get; set; } = 2;

    // --- Seed Data ---
    /// <summary>
    /// Seed data S3 bucket configuration. The bucket is created in the shared account
    /// and accessed cross-account by dev, test, and prod seeder tasks.
    /// If not specified, the bucket name is auto-generated as "{systemKey}--seeddata-{SharedSuffix}".
    /// </summary>
    public SeedDataConfig? SeedData { get; set; }

    // --- Cross-account access ---
    /// <summary>
    /// Account IDs allowed to read the shared system secret cross-account.
    /// Used to create a resource policy on the shared/system secret.
    /// </summary>
    public List<string> TrustedAccountIds { get; set; } = new();

    // --- Logging ---
    public int LogRetentionDays { get; set; } = 3;
}

public class SharedKeycloakConfig
{
    public string ImageTag { get; set; } = "26.5.0";
    public int Cpu { get; set; } = 512;
    public int Memory { get; set; } = 1024;
    public int DesiredCount { get; set; } = 2;

    /// <summary>
    /// EFS path for the Keycloak custom theme directory.
    /// Must match the theme name in keycloakthemes/{name}/.
    /// e.g., "/keycloak-themes/harmova"
    /// </summary>
    public string? ThemePath { get; set; }
}
