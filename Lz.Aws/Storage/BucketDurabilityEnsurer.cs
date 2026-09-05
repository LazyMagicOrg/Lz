using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;

namespace Lz.Aws.Storage;

/// <summary>
/// Imperative half of <see cref="BucketDurabilityPolicy"/>: idempotently apply a
/// <see cref="BucketDurabilityDecision"/> to a bucket that already exists. For the buckets
/// created outside Pulumi — <c>deploywebapp</c>'s console buckets and the plugin's static-site
/// buckets — this runs on every ensure, so opting a deployed system in takes effect on the
/// next publish without a Pulumi run, the same way the DynamoDB durability ensure does.
///
/// <para>A <see cref="BucketDurabilityDecision.None"/> decision returns before any client is
/// created: zero calls, byte-identical behaviour for a system that has not opted in.</para>
///
/// <para><b><c>PutLifecycleConfiguration</c> REPLACES the bucket's whole lifecycle config.</b>
/// That is safe here because lz-managed content buckets carry no other rules — verified
/// live across all nine on 2026-09-05 — but it is the reason a hand-applied rule on one of
/// these buckets is silently wiped by the next deploy, and why any future rule must be
/// added HERE rather than in the console.</para>
/// </summary>
public static class BucketDurabilityEnsurer
{
    /// <summary>The single lifecycle rule id lz writes, shared with the subtenant bucket path.</summary>
    public const string LifecycleRuleId = "lz-hygiene-noncurrent-expire";

    /// <summary>
    /// Enable versioning, and write the noncurrent-version expiry rule when a window is set.
    /// Both calls are idempotent PUTs, so re-running on an already-protected bucket is a no-op
    /// in effect. Returns true when anything was applied.
    /// </summary>
    public static async Task<bool> EnsureAsync(
        string profile, string region, string bucketName, BucketDurabilityDecision decision)
    {
        if (!decision.Any) return false;

        using var client = CreateClient(profile, region);

        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucketName,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        if (decision.NoncurrentExpirationDays is int days)
        {
            await client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucketName,
                Configuration = new LifecycleConfiguration
                {
                    Rules = new List<LifecycleRule>
                    {
                        new()
                        {
                            Id = LifecycleRuleId,
                            Status = LifecycleRuleStatus.Enabled,
                            Filter = new LifecycleFilter(), // whole bucket
                            NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration
                            {
                                NoncurrentDays = days,
                            },
                        },
                    },
                },
            });
        }

        Console.WriteLine(
            $"    {bucketName} — versioning ENABLED" +
            (decision.NoncurrentExpirationDays is int d
                ? $", noncurrent versions expire after {d} days"
                : ", no noncurrent expiry configured"));
        return true;
    }

    private static AmazonS3Client CreateClient(string profile, string region)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"Cannot resolve credentials for profile '{profile}'");
        return new AmazonS3Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region));
    }
}
