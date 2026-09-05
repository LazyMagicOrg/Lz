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
    /// When set, every <c>deploycontainer</c> push ALSO applies an immutable
    /// second tag <c>b-{yyyyMMdd-HHmmss}[-g{sha}]</c> alongside the moving
    /// <c>:latest</c>, and a lifecycle rule retains the newest this-many
    /// <c>b-</c>-prefixed images (older ones expire).
    /// <para>
    /// The two halves are ONE knob on purpose. Without the tag, every push
    /// orphans its predecessor as untagged and
    /// <see cref="EcrUntaggedImageRetentionDays"/> deletes it on a
    /// <c>sinceImagePushed</c> clock — so after one quiet week a repository holds
    /// <c>:latest</c> alone and there is nothing to roll back to. Without the
    /// retention count, nothing is ever untagged and the repository grows without
    /// bound. Setting one and not the other is a footgun either way, so this
    /// single value turns both on.
    /// </para>
    /// <para>
    /// The tag doubles as the only provenance an image carries: <c>:latest</c>
    /// says nothing about which commit is running, and the <c>-g{sha}</c> suffix
    /// is appended whenever the build context resolves to a git commit.
    /// </para>
    /// <para>
    /// Null = today's baseline: only <c>:latest</c> is pushed and only the
    /// untagged rule (if any) is written.
    /// </para>
    /// </summary>
    public int? EcrBuildTagRetentionCount { get; set; }

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
