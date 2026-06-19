using Lz.Aws.DynamoDB;
using Lz.Core.Config;

namespace Lz.Aws.Shared;

/// <summary>
/// Creates and deletes per-subtenant infrastructure imperatively (outside
/// Pulumi). Per-subtenant resources — the S3 assets bucket and the DynamoDB
/// table — are decoupled from the tenant Pulumi stack so that subtenants
/// can be added or removed without re-running <c>deploytenant</c>.
/// <para>
/// Called by three places:
/// <list type="bullet">
///   <item><c>lz deploytenant</c> post-deploy — first-time convenience so the
///     operator doesn't have to remember a separate step.</item>
///   <item><c>lz deploysubtenants</c> — the fast path for adding subtenants
///     to an already-deployed tenant.</item>
///   <item><c>lz destroysubtenant</c> — paired destroy.</item>
/// </list>
/// </para>
/// </summary>
public static class SubtenantProvisioner
{
    /// <summary>
    /// Ensure the S3 bucket and DynamoDB table for every subtenant listed
    /// on <paramref name="tenant"/> exist. Idempotent — existing resources
    /// have their policy/tags re-applied so console-side drift is corrected.
    /// </summary>
    /// <param name="accountId">AWS account ID — used in the subtenant bucket
    /// policy's CloudFront OAC trust condition. Without it, OAC-signed
    /// CloudFront reads return 403.</param>
    public static async Task EnsureAllAsync(
        SystemConfig system, TenantConfig tenant,
        string profile, string region, string accountId)
    {
        if (tenant.Subtenants == null || tenant.Subtenants.Count == 0) return;

        foreach (var (subtenantKey, _) in tenant.Subtenants)
            await EnsureOneAsync(system, tenant, subtenantKey, profile, region, accountId);
    }

    /// <summary>
    /// Ensure the S3 bucket and DynamoDB table for a single subtenant exist.
    /// </summary>
    public static async Task EnsureOneAsync(
        SystemConfig system, TenantConfig tenant, string subtenantKey,
        string profile, string region, string accountId)
    {
        var sk = system.SystemKey;
        var tk = tenant.TenantKey;
        var tags = new Dictionary<string, string>
        {
            { "System", sk },
            { "Tenant", tk },
            { "Subtenant", subtenantKey },
        };

        // Build CORS allowed-origins list from tenantconfig.CDN.Cors. Same
        // semantics as the Pulumi-managed tenant bucket
        // (AwsEcsExpressCloudFrontComponent.cs): AllowLocalhostDev=true
        // injects http(s)://localhost:* and AllowedOrigins entries are
        // passed through verbatim. When neither is set, the list is empty
        // and SubtenantBucketManager skips PutCORSConfiguration so an
        // existing manual config isn't dropped silently.
        var corsCfg = (tenant.CDN ?? new CdnConfig()).Cors ?? new CorsConfig();
        var corsOrigins = new List<string>();
        if (corsCfg.AllowLocalhostDev)
        {
            corsOrigins.Add("http://localhost:*");
            corsOrigins.Add("https://localhost:*");
        }
        if (corsCfg.AllowedOrigins != null)
            corsOrigins.AddRange(corsCfg.AllowedOrigins);

        // S3 assets bucket — {sk}-{tk}-{stk}-assets-{systemSuffix}
        var bucketName = SubtenantBucketManager.BucketName(
            sk, tk, subtenantKey, system.SystemSuffix);
        Console.WriteLine($"  subtenant '{subtenantKey}': ensuring bucket {bucketName}");
        var created = await SubtenantBucketManager.EnsureBucketAsync(
            profile, region, bucketName, accountId,
            new Dictionary<string, string>(tags) { { "Purpose", $"{subtenantKey}-assets" } },
            corsOrigins);
        Console.WriteLine(created
            ? $"    {bucketName} — created"
            : $"    {bucketName} — exists (policy re-applied)");

        // DynamoDB table — {sk}_{tk}_{stk}
        var tableName = $"{sk}_{tk}_{subtenantKey}";
        Console.WriteLine($"  subtenant '{subtenantKey}': ensuring table {tableName}");
        var tableCreated = await DynamoDbTableCreator.EnsureTableAsync(
            profile, region, tableName,
            new Dictionary<string, string>(tags) { { "Level", "subtenant" } });
        Console.WriteLine(tableCreated
            ? $"    {tableName} — created"
            : $"    {tableName} — exists");
    }

    /// <summary>
    /// Destroy the S3 bucket and DynamoDB table for a single subtenant.
    /// When <paramref name="forceEmptyBucket"/> is true the bucket is emptied
    /// before deletion (data loss — callers should confirm with the user).
    /// The DynamoDB table is always destroyed; no way to empty-first for a
    /// table.
    /// </summary>
    public static async Task DeleteOneAsync(
        SystemConfig system, TenantConfig tenant, string subtenantKey,
        string profile, string region, bool forceEmptyBucket)
    {
        var sk = system.SystemKey;
        var tk = tenant.TenantKey;

        var bucketName = SubtenantBucketManager.BucketName(
            sk, tk, subtenantKey, system.SystemSuffix);
        Console.WriteLine($"  deleting bucket {bucketName}");
        await SubtenantBucketManager.DeleteBucketAsync(profile, region, bucketName, forceEmptyBucket);

        var tableName = $"{sk}_{tk}_{subtenantKey}";
        Console.WriteLine($"  deleting table {tableName}");
        await DeleteDynamoTableAsync(profile, region, tableName);
    }

    private static async Task DeleteDynamoTableAsync(string profile, string region, string tableName)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException(
                $"Cannot resolve AWS credentials for profile '{profile}'.");

        using var client = new Amazon.DynamoDBv2.AmazonDynamoDBClient(
            credentials, Amazon.RegionEndpoint.GetBySystemName(region));

        try
        {
            await client.DeleteTableAsync(tableName);
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
        {
            // Already gone — no-op
        }
    }
}
