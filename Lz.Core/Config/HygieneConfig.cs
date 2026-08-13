namespace Lz.Core.Config;

/// <summary>
/// Opt-in cost-hygiene caps for resources that otherwise grow without bound
/// (untagged ECR images, S3 noncurrent object versions, never-expiring Lambda
/// log groups). When this block is omitted — or any individual field is null —
/// NOTHING changes and the deployed infrastructure is byte-identical to a
/// pre-hygiene deploy (the no-opt-in baseline for sibling systems), matching
/// the <see cref="DurabilityConfig"/>/<see cref="VectorStoreConfig"/> pattern.
/// </summary>
public class HygieneConfig
{
    /// <summary>
    /// When set, an ECR lifecycle policy is ensured on each repository at
    /// deploycontainer time, expiring UNTAGGED images older than this many
    /// days. Tagged images are never touched. Null = no lifecycle policy is
    /// written (existing policies, if any, are left alone).
    /// </summary>
    public int? EcrUntaggedImageRetentionDays { get; set; }

    /// <summary>
    /// When set, versioned buckets managed by lz (the per-subtenant assets
    /// bucket and the Pulumi state bucket) get a lifecycle rule expiring
    /// NONCURRENT object versions after this many days. Current versions are
    /// never touched. Null = no lifecycle configuration is written.
    /// </summary>
    public int? S3NoncurrentVersionExpirationDays { get; set; }

    /// <summary>
    /// When set, the tenant-service Lambda gets an EXPLICIT log group (named
    /// <c>/lz/lambda/{function}</c>, wired via the function's LoggingConfig)
    /// with this retention in days — replacing the auto-created never-expire
    /// group. The previously auto-created <c>/aws/lambda/{function}</c> group
    /// is not deleted (remove it manually once drained). Null = current
    /// behavior (auto-created group, no retention).
    /// </summary>
    public int? LambdaLogRetentionDays { get; set; }
}
