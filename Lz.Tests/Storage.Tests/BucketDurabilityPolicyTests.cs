using Lz.Aws.Storage;
using Lz.Core.Config;

namespace Lz.Tests.Storage.Tests;

/// <summary>
/// The pure decision behind versioning the content buckets. No AWS and no Pulumi — the
/// components and the ensurer only translate this.
/// </summary>
public class BucketDurabilityPolicyTests
{
    [Fact]
    public void NullDurability_IsNone_SoAnUnoptedSystemsPlanIsByteIdentical()
    {
        var decision = BucketDurabilityPolicy.ForContentBucket(null, new HygieneConfig { S3NoncurrentVersionExpirationDays = 30 });

        Assert.Equal(BucketDurabilityDecision.None, decision);
        Assert.False(decision.Any);
    }

    [Fact]
    public void DurabilityPresentButVersioningOff_IsNone()
    {
        // A Durability section that opts into the table protections but not this one must
        // not version buckets as a side effect — scu-dev ran exactly that shape for a day.
        var decision = BucketDurabilityPolicy.ForContentBucket(
            new DurabilityConfig { DeletionProtection = true, PointInTimeRecovery = true, BucketVersioning = false },
            new HygieneConfig { S3NoncurrentVersionExpirationDays = 30 });

        Assert.Equal(BucketDurabilityDecision.None, decision);
    }

    [Fact]
    public void OptedIn_CarriesTheHygieneWindowAsTheRollbackWindow()
    {
        var decision = BucketDurabilityPolicy.ForContentBucket(
            new DurabilityConfig { BucketVersioning = true },
            new HygieneConfig { S3NoncurrentVersionExpirationDays = 30 });

        Assert.True(decision.Versioning);
        Assert.Equal(30, decision.NoncurrentExpirationDays);
        Assert.True(decision.Any);
    }

    [Fact]
    public void OptedInWithoutAWindow_StillVersions_ButWithNoExpiry()
    {
        // The policy carries what it was given; REFUSING this combination is the validator's
        // job, not the policy's, so the two can be reasoned about separately.
        var decision = BucketDurabilityPolicy.ForContentBucket(new DurabilityConfig { BucketVersioning = true }, null);

        Assert.True(decision.Versioning);
        Assert.Null(decision.NoncurrentExpirationDays);
    }

    [Fact]
    public void ScutarasLiveConfig_VersionsWithAThirtyDayWindow()
    {
        // Pins the outcome the 2026-09-04 live audit demanded for the nine unversioned buckets.
        var scutaraDev = (
            new DurabilityConfig { DeletionProtection = true, PointInTimeRecovery = true, BucketVersioning = true },
            new HygieneConfig { EcrUntaggedImageRetentionDays = 7, EcrBuildTagRetentionCount = 20, S3NoncurrentVersionExpirationDays = 30 });

        var decision = BucketDurabilityPolicy.ForContentBucket(scutaraDev.Item1, scutaraDev.Item2);

        Assert.Equal(new BucketDurabilityDecision(true, 30), decision);
    }
}
