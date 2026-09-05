using Lz.Core.Config;

namespace Lz.Aws.Storage;

/// <summary>
/// What durability protections to apply to an lz-managed content bucket, derived purely
/// from config. <see cref="NoncurrentExpirationDays"/> is the ROLLBACK WINDOW: how long a
/// version overwritten or deleted by a <c>--delete</c> sync stays restorable.
/// </summary>
public readonly record struct BucketDurabilityDecision(bool Versioning, int? NoncurrentExpirationDays)
{
    /// <summary>Apply nothing — the byte-identical, no-opt-in baseline.</summary>
    public static readonly BucketDurabilityDecision None = new(false, null);

    /// <summary>True when there is anything to do.</summary>
    public bool Any => Versioning;
}

/// <summary>
/// The pure decision behind versioning the asset, web-app and static-site buckets. SDK-free
/// and Pulumi-free on purpose, exactly like <see cref="Lz.Aws.DynamoDB.TableDurabilityPolicy"/>:
/// the Pulumi components and <see cref="BucketDurabilityEnsurer"/> only translate it.
///
/// <para>WHY THIS EXISTS. Every publish path in this system — <c>deployassets</c>,
/// <c>deploywebapp</c>, <c>deploystaticsite</c>, the Website's GitHub workflow — mirrors a
/// local tree into a bucket with <c>aws s3 sync --delete</c>. On an unversioned bucket that
/// makes a bad publish PERMANENT: there is no delete marker and no noncurrent version, so
/// nothing can be restored, and enabling versioning afterwards resurrects nothing. A live
/// audit on 2026-09-04 found nine such buckets holding 1,199 objects. The per-subtenant
/// assets bucket and the Pulumi state bucket were already versioned; this brings the rest
/// under the same protection.</para>
///
/// <para>THE LIFECYCLE IS NOT OPTIONAL when versioning is. The console bundles are
/// republished in full on every deploy (573 and 579 objects), so a versioned bucket with no
/// noncurrent expiry grows by a whole bundle per deploy, forever. The window comes from
/// <see cref="HygieneConfig.S3NoncurrentVersionExpirationDays"/> — the same knob the
/// subtenant and state buckets already use — and the config validator refuses versioning
/// without it.</para>
///
/// <para>ONE-WAY, and worth saying at the decision rather than at the call: S3 versioning can
/// be SUSPENDED but never disabled. Turning this on is permanent for the bucket.</para>
/// </summary>
public static class BucketDurabilityPolicy
{
    /// <summary>
    /// Decision for a content bucket (system/tenant assets, the CDN assets bucket, the web-app
    /// and static-site buckets). A null <paramref name="durability"/> — section omitted — or
    /// <c>BucketVersioning: false</c> yields <see cref="BucketDurabilityDecision.None"/>, so an
    /// un-opted-in system emits a byte-identical plan and makes no extra API call.
    /// </summary>
    public static BucketDurabilityDecision ForContentBucket(DurabilityConfig? durability, HygieneConfig? hygiene)
        => durability is { BucketVersioning: true }
            ? new BucketDurabilityDecision(true, hygiene?.S3NoncurrentVersionExpirationDays)
            : BucketDurabilityDecision.None;
}
