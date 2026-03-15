using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Core.Config;

namespace Lz.Aws;

/// <summary>
/// Bootstraps the Pulumi state backend (S3 bucket + KMS key) for an AWS account.
/// Parses bucket name from State.Backend and KMS alias from State.SecretsProvider.
/// Idempotent: skips resources that already exist.
/// </summary>
public static class AwsStateBootstrapper
{
    public static async Task BootstrapAsync(string profile, string region, StateConfig state)
    {
        var bucketName = ParseBucketName(state.Backend);
        var kmsAlias = ParseKmsAlias(state.SecretsProvider);

        Console.WriteLine("Bootstrapping Pulumi state backend...");
        Console.WriteLine($"  Bucket: {bucketName}");
        Console.WriteLine($"  KMS:    {kmsAlias}");
        Console.WriteLine($"  Region: {region}");
        Console.WriteLine($"  Profile: {profile}");
        Console.WriteLine();

        await EnsureS3BucketAsync(profile, region, bucketName);
        await EnsureKmsKeyAsync(profile, region, kmsAlias);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Bootstrap complete!");
        Console.ResetColor();
    }

    private static async Task EnsureS3BucketAsync(string profile, string region, string bucketName)
    {
        var client = CreateS3Client(profile, region);

        // Check if bucket exists
        try
        {
            await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucketName });
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  S3 bucket '{bucketName}' already exists. Skipping.");
            Console.ResetColor();
            return;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Bucket doesn't exist — create it
        }

        // Create bucket
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Creating S3 bucket '{bucketName}'...");
        Console.ResetColor();

        var request = new PutBucketRequest
        {
            BucketName = bucketName,
            BucketRegionName = region,
        };
        // us-east-1 must not set BucketRegionName (AWS quirk)
        if (region == "us-east-1")
            request.BucketRegionName = null;

        await client.PutBucketAsync(request);

        // Enable versioning
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucketName,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        // Enable encryption (aws:kms with bucket key)
        await client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucketName,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault
                        {
                            ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AWSKMS,
                        },
                        BucketKeyEnabled = true,
                    },
                ],
            },
        });

        // Block public access
        await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucketName,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = true,
            },
        });

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  S3 bucket created and configured.");
        Console.ResetColor();
    }

    private static async Task EnsureKmsKeyAsync(string profile, string region, string kmsAlias)
    {
        var client = CreateKmsClient(profile, region);

        // Check if alias already exists
        try
        {
            await client.DescribeKeyAsync(new DescribeKeyRequest { KeyId = kmsAlias });
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  KMS alias '{kmsAlias}' already exists. Skipping.");
            Console.ResetColor();
            return;
        }
        catch (NotFoundException)
        {
            // Alias doesn't exist — create key + alias
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Creating KMS key...");
        Console.ResetColor();

        var keyResponse = await client.CreateKeyAsync(new CreateKeyRequest
        {
            Description = $"Pulumi secrets encryption ({kmsAlias})",
        });

        var keyId = keyResponse.KeyMetadata.KeyId;
        Console.WriteLine($"  Key created: {keyId}");

        await client.CreateAliasAsync(new CreateAliasRequest
        {
            AliasName = kmsAlias,
            TargetKeyId = keyId,
        });

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  KMS alias created: {kmsAlias}");
        Console.ResetColor();
    }

    /// <summary>
    /// Parse bucket name from a Pulumi S3 backend URL.
    /// e.g., "s3://med-dev-pulumi-state-4498-a704?region=us-west-2" → "med-dev-pulumi-state-4498-a704"
    /// </summary>
    internal static string ParseBucketName(string backendUrl)
    {
        if (!backendUrl.StartsWith("s3://"))
            throw new ArgumentException($"Expected s3:// backend URL, got: {backendUrl}");

        var withoutScheme = backendUrl[5..]; // strip "s3://"
        var queryIndex = withoutScheme.IndexOf('?');
        return queryIndex >= 0 ? withoutScheme[..queryIndex] : withoutScheme;
    }

    /// <summary>
    /// Parse KMS alias from a Pulumi secrets provider URL.
    /// e.g., "awskms://alias/med-dev-pulumi-key-4498-a704" → "alias/med-dev-pulumi-key-4498-a704"
    /// </summary>
    internal static string ParseKmsAlias(string secretsProviderUrl)
    {
        if (!secretsProviderUrl.StartsWith("awskms://"))
            throw new ArgumentException($"Expected awskms:// secrets provider URL, got: {secretsProviderUrl}");

        return secretsProviderUrl[9..]; // strip "awskms://"
    }

    private static AmazonS3Client CreateS3Client(string profile, string region)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonS3Client(credentials, regionEndpoint);
        }

        return new AmazonS3Client(regionEndpoint);
    }

    private static AmazonKeyManagementServiceClient CreateKmsClient(string profile, string region)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonKeyManagementServiceClient(credentials, regionEndpoint);
        }

        return new AmazonKeyManagementServiceClient(regionEndpoint);
    }
}
