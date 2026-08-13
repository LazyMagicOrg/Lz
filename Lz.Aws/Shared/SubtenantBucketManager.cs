using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;

namespace Lz.Aws.Shared;

/// <summary>
/// Creates and destroys per-subtenant S3 assets buckets imperatively via the
/// AWS SDK — not Pulumi. Called by <c>lz deploytenant</c> (first-tenant
/// convenience), <c>lz deploysubtenants</c> (adding subtenants without a
/// Pulumi run), and <c>lz destroysubtenant</c>.
/// <para>
/// Bucket naming follows the established convention:
/// <c>{systemKey}-{tenantKey}-{subtenantKey}-assets-{suffix}</c>. On creation,
/// public access is blocked and versioning is enabled; tags identify the
/// subtenant. All operations are idempotent — safe to re-run.
/// </para>
/// </summary>
public static class SubtenantBucketManager
{
    /// <summary>
    /// Deterministic bucket name for a subtenant assets bucket.
    /// </summary>
    public static string BucketName(string systemKey, string tenantKey, string subtenantKey, string suffix)
        => $"{systemKey}-{tenantKey}-{subtenantKey}-assets-{suffix}";

    /// <summary>
    /// Ensure the bucket exists with the standard policy (BlockPublicAccess +
    /// versioning + SSE + tags + CloudFront OAC GetObject grant). Returns
    /// true if the bucket was newly created, false if it already existed.
    /// Re-applies policy on every call so settings don't drift from the AWS
    /// console.
    /// </summary>
    /// <param name="accountId">AWS account ID — used in the bucket policy's
    /// <c>AWS:SourceAccount</c> condition that scopes the CloudFront OAC
    /// grant to this account's distributions. Required because Pulumi-
    /// managed buckets (system, tenant) get this policy from their
    /// component; subtenant buckets are created imperatively here and need
    /// the same trust applied so OAC-signed requests aren't denied.</param>
    /// <param name="corsAllowedOrigins">Optional list of origins to allow
    /// in the bucket's CORS configuration. When non-empty, the bucket emits
    /// <c>Access-Control-Allow-Origin</c> on its own responses (including
    /// 4xx errors) — CloudFront passes these through. Used to unblock VS-
    /// hosted localhost dev WASM apps fetching cloud assets where
    /// CloudFront's CFResponse function doesn't fire (e.g. on origin 4xx
    /// that fails CustomErrorResponse SPA-fallback). When null/empty, no
    /// CORS configuration is set — bucket-level CORS responses won't carry
    /// the header, and CloudFront-level CORS via CFResponse remains the
    /// only path. Pulumi-managed tenant bucket gets the same configuration
    /// in <c>AwsEcsExpressCloudFrontComponent.cs</c> via
    /// <c>BucketCorsConfigurationV2</c>; this parameter is the imperative
    /// equivalent for subtenant buckets which Pulumi doesn't manage.</param>
    public static async Task<bool> EnsureBucketAsync(
        string profile, string region, string bucketName, string accountId,
        Dictionary<string, string>? tags = null,
        IReadOnlyList<string>? corsAllowedOrigins = null,
        int? noncurrentVersionExpirationDays = null)
    {
        using var client = CreateClient(profile, region);
        var created = await EnsureBucketCreatedAsync(client, bucketName);
        await ApplyStandardPolicyAsync(client, bucketName, accountId, tags, corsAllowedOrigins);

        // Hygiene opt-in: this bucket is VERSIONED while deployassets syncs with
        // --delete, so every redeploy strands noncurrent versions forever. When
        // configured, expire them; null = no lifecycle written (baseline).
        if (noncurrentVersionExpirationDays is int expireDays)
            await EnsureNoncurrentVersionExpirationAsync(client, bucketName, expireDays);

        return created;
    }

    /// <summary>
    /// Idempotently apply a lifecycle rule expiring NONCURRENT object versions
    /// after <paramref name="days"/> days. Current versions are never touched.
    /// PutLifecycleConfiguration REPLACES the bucket's lifecycle config — safe
    /// here because lz-managed buckets carry no other lifecycle rules.
    /// </summary>
    private static async Task EnsureNoncurrentVersionExpirationAsync(
        IAmazonS3 client, string bucketName, int days)
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
                        Id = "lz-hygiene-noncurrent-expire",
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

    /// <summary>
    /// Delete the bucket. When <paramref name="force"/> is true, empties the
    /// bucket (including all versions) before deleting — required if any
    /// object exists. Without <paramref name="force"/>, delete fails if the
    /// bucket is non-empty. Callers should confirm with the user before
    /// passing force=true (data loss).
    /// </summary>
    public static async Task DeleteBucketAsync(
        string profile, string region, string bucketName, bool force = false)
    {
        using var client = CreateClient(profile, region);

        // Does it exist? If not, nothing to do.
        try
        {
            await client.GetBucketLocationAsync(bucketName);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        if (force)
            await EmptyBucketAsync(client, bucketName);

        await client.DeleteBucketAsync(bucketName);
    }

    // -------------------------------------------------------------------

    private static AmazonS3Client CreateClient(string profile, string region)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException(
                $"Cannot resolve AWS credentials for profile '{profile}'.");

        return new AmazonS3Client(
            credentials, Amazon.RegionEndpoint.GetBySystemName(region));
    }

    private static async Task<bool> EnsureBucketCreatedAsync(
        IAmazonS3 client, string bucketName)
    {
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });
            return true;
        }
        catch (AmazonS3Exception ex)
            when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
        {
            return false;
        }
    }

    private static async Task ApplyStandardPolicyAsync(
        IAmazonS3 client, string bucketName, string accountId, Dictionary<string, string>? tags,
        IReadOnlyList<string>? corsAllowedOrigins = null)
    {
        // Block all public access.
        await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucketName,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                BlockPublicPolicy = true,
                IgnorePublicAcls = true,
                RestrictPublicBuckets = true,
            },
        });

        // CloudFront OAC GetObject grant — without this, OAC-signed reads
        // from any CF distribution in this account are denied with 403.
        // Mirrors the policy Pulumi attaches to system/tenant assets buckets.
        // SourceAccount (rather than SourceArn naming a specific distribution)
        // means any CF distribution in the account can read; the assets are
        // public via CloudFront by design, so account-wide trust is fine.
        var policyJson =
            $"{{\"Version\":\"2012-10-17\",\"Statement\":[{{" +
                $"\"Sid\":\"AllowCloudFrontRead\"," +
                $"\"Effect\":\"Allow\"," +
                $"\"Principal\":{{\"Service\":\"cloudfront.amazonaws.com\"}}," +
                $"\"Action\":\"s3:GetObject\"," +
                $"\"Resource\":\"arn:aws:s3:::{bucketName}/*\"," +
                $"\"Condition\":{{\"StringEquals\":{{\"AWS:SourceAccount\":\"{accountId}\"}}}}" +
            $"}}]}}";
        await client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucketName,
            Policy = policyJson,
        });

        // Enable versioning.
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucketName,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        // Default server-side encryption with SSE-S3 (AES256). Matches what
        // AWS now applies by default on new buckets (post-2023), but being
        // explicit here protects against older accounts + makes intent clear.
        await client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucketName,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules = new List<ServerSideEncryptionRule>
                {
                    new()
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault
                        {
                            ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256,
                        },
                        BucketKeyEnabled = true,
                    },
                },
            },
        });

        // Apply tags (if any). PutBucketTagging replaces the entire tag set.
        var tagList = new List<Tag> { new() { Key = "ManagedBy", Value = "lz" } };
        if (tags != null)
            foreach (var (k, v) in tags)
                tagList.Add(new Tag { Key = k, Value = v });

        await client.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucketName,
            TagSet = tagList,
        });

        // CORS — when origins were supplied, configure bucket-level CORS so
        // S3 itself emits Access-Control-Allow-Origin on responses. Critical
        // for the localhost-dev WASM-app flow where CloudFront's CFResponse
        // function doesn't run on 4xx origin errors that fall through
        // CustomErrorResponses. PutCORSConfiguration *replaces* the existing
        // configuration; calling with no origins effectively no-ops (we
        // skip the call entirely so we don't drop a manually-applied config
        // when CORS is later disabled in tenantconfig). Operators who want
        // to remove CORS should run an explicit aws s3api delete-bucket-cors.
        if (corsAllowedOrigins != null && corsAllowedOrigins.Count > 0)
        {
            await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = bucketName,
                Configuration = new CORSConfiguration
                {
                    Rules = new List<CORSRule>
                    {
                        new CORSRule
                        {
                            AllowedMethods = new List<string> { "GET", "HEAD" },
                            AllowedOrigins = corsAllowedOrigins.ToList(),
                            AllowedHeaders = new List<string> { "*" },
                            ExposeHeaders = new List<string> { "ETag" },
                            MaxAgeSeconds = 3000,
                        }
                    }
                }
            });
        }
    }

    private static async Task EmptyBucketAsync(IAmazonS3 client, string bucketName)
    {
        // Delete all object versions (including delete markers — AWSSDK v4
        // returns both in the Versions list, distinguishable by IsDeleteMarker).
        // Iterate until empty.
        while (true)
        {
            var listResp = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                MaxKeys = 1000,
            });

            var versions = listResp.Versions ?? new List<S3ObjectVersion>();
            if (versions.Count == 0) return;

            var keys = versions
                .Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId })
                .ToList();

            await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucketName,
                Objects = keys,
                Quiet = true,
            });
        }
    }
}
