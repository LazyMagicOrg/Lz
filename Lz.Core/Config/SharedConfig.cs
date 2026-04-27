namespace Lz.Core.Config;

/// <summary>
/// Configuration for the shared-services account.
/// Loaded from sharedconfig.yaml — there is a single shared-services account.
/// Platform-specific extras (central auth, VPN sizing, cross-account trust)
/// live on the derived <c>Lz.Aws.Config.AwsSharedConfig</c>.
/// </summary>
public class SharedConfig
{
    // --- Deployment Settings ---
    public string Profile { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string VpcCidr { get; set; } = string.Empty;

    /// <summary>
    /// Short random string for bucket/resource global uniqueness (e.g., "49d2-8357").
    /// Used in state bucket names and other globally-unique resources.
    /// </summary>
    public string SharedSuffix { get; set; } = string.Empty;

    // Pulumi state
    public StateConfig? State { get; set; }

    // --- Database ---
    public string DbInstanceClass { get; set; } = "db.t4g.micro";
    public int DbAllocatedStorage { get; set; } = 20;

    // --- Seed Data ---
    /// <summary>
    /// Seed data bucket configuration. Shared across environments (dev, test, prod).
    /// If not specified, the bucket name is auto-generated from system key + suffix.
    /// </summary>
    public SeedDataConfig? SeedData { get; set; }

    // --- Logging ---
    public int LogRetentionDays { get; set; } = 3;
}
