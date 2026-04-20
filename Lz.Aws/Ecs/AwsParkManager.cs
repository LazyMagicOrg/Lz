using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;

namespace Lz.Aws.Ecs;

/// <summary>
/// Parks/unparks a tenant by toggling a "parked" flag in CloudFront KVS.
/// The CloudFront viewer-request function checks this flag and rewrites all
/// requests to /park/index.html when parked. No distribution config changes —
/// Pulumi state stays clean and deploytenant works normally.
///
/// Park: uploads maintenance page to assets S3 bucket, sets KVS parked=true.
/// Unpark: sets KVS parked=false (or deletes the key).
/// </summary>
public class AwsParkManager
{
    private readonly string _systemKey;
    private readonly string _profile;
    private readonly string _region;

    public AwsParkManager(string systemKey, string profile, string region)
    {
        _systemKey = systemKey;
        _profile = profile;
        _region = region;
    }

    /// <summary>
    /// Park a tenant: upload maintenance page and set KVS parked=true.
    /// </summary>
    public async Task ParkAsync(
        string tenantKey,
        string tenantSuffix,
        string env,
        string domain,
        string parkPageFolder,
        List<string>? legacyDomains = null)
    {
        var prefix = $"{_systemKey}-{tenantKey}";
        var kvsName = $"{prefix}-kvs";
        var parkBucketName = $"{_systemKey}-{tenantKey}--webapp-park-{tenantSuffix}";

        // 1. Upload park page to park S3 bucket at wwwroot/park/
        Console.WriteLine($"  Uploading park page to s3://{parkBucketName}/wwwroot/park/...");
        await UploadParkPageAsync(parkBucketName, parkPageFolder);

        // 2. Find the KVS and set parked:{domain}=true for each domain
        using var cfClient = CreateCloudFrontClient();
        var kvsArn = await FindKvsArnAsync(cfClient, kvsName);
        if (kvsArn == null)
            throw new InvalidOperationException(
                $"CloudFront KeyValueStore '{kvsName}' not found. " +
                $"Has deploytenant been run for this tenant?");

        var allDomains = new List<string> { domain };
        if (legacyDomains != null)
            allDomains.AddRange(legacyDomains);

        foreach (var d in allDomains)
        {
            var key = d.ToLowerInvariant();
            var value = System.Text.Json.JsonSerializer.Serialize(new { parked = true });
            Console.WriteLine($"  Setting KVS '{kvsName}' {key}={value}");
            await PutKvsKeyAsync(cfClient, kvsArn, key, value);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Tenant '{tenantKey}' ({domain}) is now parked.");
        Console.WriteLine($"  Run 'lz unpark --tenantkey {tenantKey}' to restore.");
        Console.ResetColor();
    }

    /// <summary>
    /// Unpark a tenant: set KVS parked=false.
    /// </summary>
    public async Task UnparkAsync(string tenantKey, string domain, List<string>? legacyDomains = null)
    {
        var prefix = $"{_systemKey}-{tenantKey}";
        var kvsName = $"{prefix}-kvs";

        using var cfClient = CreateCloudFrontClient();
        var kvsArn = await FindKvsArnAsync(cfClient, kvsName);
        if (kvsArn == null)
            throw new InvalidOperationException(
                $"CloudFront KeyValueStore '{kvsName}' not found.");

        var allDomains = new List<string> { domain };
        if (legacyDomains != null)
            allDomains.AddRange(legacyDomains);

        foreach (var d in allDomains)
        {
            var key = d.ToLowerInvariant();
            var value = System.Text.Json.JsonSerializer.Serialize(new { parked = false });
            Console.WriteLine($"  Setting KVS '{kvsName}' {key}={value}");
            await PutKvsKeyAsync(cfClient, kvsArn, key, value);
        }

        // Invalidate CloudFront cache so the parked page response is cleared
        // from edge caches. Without this, browsers may get the cached park page
        // even though the function no longer rewrites to /park/index.html.
        var distributionId = await Webapp.WebappDeployer.FindDistributionIdAsync(
            domain, _profile, _region);
        if (!string.IsNullOrEmpty(distributionId))
        {
            Console.WriteLine("  Invalidating CloudFront cache...");
            await cfClient.CreateInvalidationAsync(new CreateInvalidationRequest
            {
                DistributionId = distributionId,
                InvalidationBatch = new InvalidationBatch
                {
                    CallerReference = DateTime.UtcNow.Ticks.ToString(),
                    Paths = new Paths { Quantity = 1, Items = new List<string> { "/*" } },
                },
            });
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Tenant '{tenantKey}' ({domain}) is now unparked.");
        Console.ResetColor();
    }

    /// <summary>
    /// Check if a tenant is parked by reading the KVS "parked" key.
    /// </summary>
    public async Task<bool> IsParkedAsync(string tenantKey, string domain)
    {
        var kvsName = $"{_systemKey}-{tenantKey}-kvs";
        try
        {
            using var cfClient = CreateCloudFrontClient();
            var kvsArn = await FindKvsArnAsync(cfClient, kvsName);
            if (kvsArn == null) return false;

            var kvClient = new Amazon.CloudFrontKeyValueStore.AmazonCloudFrontKeyValueStoreClient(
                GetCredentials(), Amazon.RegionEndpoint.GetBySystemName(_region));
            try
            {
                var getKeyResponse = await kvClient.GetKeyAsync(
                    new Amazon.CloudFrontKeyValueStore.Model.GetKeyRequest
                    {
                        KvsARN = kvsArn,
                        Key = domain.ToLowerInvariant(),
                    });
                var config = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(getKeyResponse.Value);
                return config.TryGetProperty("parked", out var parked) && parked.GetBoolean();
            }
            catch (Amazon.CloudFrontKeyValueStore.Model.ResourceNotFoundException)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Find a KVS ARN by name.
    /// </summary>
    private static async Task<string?> FindKvsArnAsync(AmazonCloudFrontClient cfClient, string kvsName)
    {
        var listResponse = await cfClient.ListKeyValueStoresAsync(new ListKeyValueStoresRequest());
        var kvs = listResponse.KeyValueStoreList?.Items?
            .FirstOrDefault(k => k.Name == kvsName);
        return kvs?.ARN;
    }

    /// <summary>
    /// Put a key-value pair in the KVS.
    /// Uses the CloudFront KeyValueStore API (separate from the CloudFront API).
    /// </summary>
    private async Task PutKvsKeyAsync(AmazonCloudFrontClient cfClient, string kvsArn, string key, string value)
    {
        var kvClient = new Amazon.CloudFrontKeyValueStore.AmazonCloudFrontKeyValueStoreClient(
            GetCredentials(), Amazon.RegionEndpoint.GetBySystemName(_region));

        // Get current ETag (required for updates)
        var descResponse = await kvClient.DescribeKeyValueStoreAsync(
            new Amazon.CloudFrontKeyValueStore.Model.DescribeKeyValueStoreRequest { KvsARN = kvsArn });
        var etag = descResponse.ETag;

        await kvClient.PutKeyAsync(
            new Amazon.CloudFrontKeyValueStore.Model.PutKeyRequest
            {
                KvsARN = kvsArn,
                IfMatch = etag,
                Key = key,
                Value = value,
            });
    }

    /// <summary>
    /// Upload all files from the park page folder to the assets S3 bucket
    /// under the wwwroot/park/ prefix.
    /// </summary>
    private async Task UploadParkPageAsync(string bucketName, string parkPageFolder)
    {
        using var s3Client = CreateS3Client();

        var files = Directory.GetFiles(parkPageFolder, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(parkPageFolder, file).Replace('\\', '/');
            var key = $"wwwroot/park/{relativePath}";
            var contentType = GetContentType(relativePath);

            await s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                FilePath = file,
                ContentType = contentType,
                Headers = { CacheControl = "no-cache, no-store, must-revalidate" },
            });
            Console.WriteLine($"    Uploaded: {key}");
        }
    }

    private Amazon.Runtime.AWSCredentials? GetCredentials()
    {
        var chain = new CredentialProfileStoreChain();
        chain.TryGetAWSCredentials(_profile, out var credentials);
        return credentials;
    }

    private AmazonCloudFrontClient CreateCloudFrontClient()
    {
        var creds = GetCredentials();
        var region = Amazon.RegionEndpoint.GetBySystemName(_region);
        return creds != null
            ? new AmazonCloudFrontClient(creds, region)
            : new AmazonCloudFrontClient(region);
    }

    private AmazonS3Client CreateS3Client()
    {
        var creds = GetCredentials();
        var region = Amazon.RegionEndpoint.GetBySystemName(_region);
        return creds != null
            ? new AmazonS3Client(creds, region)
            : new AmazonS3Client(region);
    }

    private static string GetContentType(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
