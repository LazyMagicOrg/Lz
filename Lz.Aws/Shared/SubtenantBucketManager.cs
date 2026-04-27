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
    /// versioning). Returns true if the bucket was newly created, false if
    /// it already existed. Re-applies policy on every call so settings don't
    /// drift from the AWS console.
    /// </summary>
    public static async Task<bool> EnsureBucketAsync(
        string profile, string region, string bucketName,
        Dictionary<string, string>? tags = null)
    {
        using var client = CreateClient(profile, region);
        var created = await EnsureBucketCreatedAsync(client, bucketName);
        await ApplyStandardPolicyAsync(client, bucketName, tags);
        return created;
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
        IAmazonS3 client, string bucketName, Dictionary<string, string>? tags)
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
