namespace Lz.Core.Config;

/// <summary>
/// Seed data configuration for shared S3 bucket.
/// Maps to the "SeedData:" section in systemconfig YAML.
/// The seed bucket is shared across accounts (dev, test, prod) and holds
/// EFS snapshots + database SQL files for tenant seeding and refresh.
/// </summary>
public class SeedDataConfig
{
    /// <summary>
    /// Name of the shared S3 seed data bucket.
    /// e.g., "med--seeddata-496a-f222"
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// AWS region where the seed bucket resides.
    /// May differ from the system region if the bucket is in another account.
    /// e.g., "us-west-2"
    /// </summary>
    public string Region { get; set; } = string.Empty;
}
