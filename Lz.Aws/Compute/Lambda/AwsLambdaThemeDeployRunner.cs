using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using System.IO.Compression;
using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;

namespace Lz.Aws.Compute.Lambda;

/// <summary>
/// Deploys a Keycloak theme to EFS by:
///   1. Creating a tarball from the local theme directory
///   2. Uploading it to the shared-account themes S3 bucket
///   3. Invoking the shared-account gate-checker Lambda with check_type=deploy_theme
///   4. Cleaning up the S3 tarball
/// Both S3 and Lambda are in the shared-services account (where Keycloak + EFS live),
/// so we use SharedProfile/SharedRegion for all AWS calls.
/// </summary>
public class AwsLambdaThemeDeployRunner : IThemeDeployRunner
{
    private readonly SystemConfig _config;
    private readonly string _themesBucket;

    /// <param name="config">System config (provides SharedProfile, SharedRegion).</param>
    /// <param name="themesBucket">
    /// Name of the S3 bucket in the shared-services account for staging theme tarballs.
    /// Typically "keycloak-themes-{SharedSuffix}".
    /// </param>
    public AwsLambdaThemeDeployRunner(SystemConfig config, string themesBucket)
    {
        _config = config;
        _themesBucket = themesBucket;
    }

    public async Task<bool> DeployThemeAsync(string themeName, string themeSourcePath)
    {
        if (!Directory.Exists(themeSourcePath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Theme source directory not found: {themeSourcePath}");
            Console.ResetColor();
            return false;
        }

        if (string.IsNullOrEmpty(_themesBucket))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("  Themes bucket not configured — cannot stage theme tarball.");
            Console.ResetColor();
            return false;
        }

        var s3Key = $"{themeName}.tar.gz";
        var tempTarball = Path.Combine(Path.GetTempPath(), $"theme-{themeName}-{Guid.NewGuid():N}.tar.gz");

        try
        {
            // Step 1: Create tarball from theme directory
            Console.WriteLine($"  Creating theme tarball from {themeSourcePath}...");
            CreateTarGz(tempTarball, themeSourcePath);
            var tarSize = new FileInfo(tempTarball).Length;
            Console.WriteLine($"  Tarball created: {tarSize:N0} bytes");

            // Step 2: Ensure bucket exists, then upload to S3 (shared-services account)
            using var s3Client = CreateSharedS3Client();
            await EnsureBucketExistsAsync(s3Client);

            Console.WriteLine($"  Uploading to s3://{_themesBucket}/{s3Key}...");
            await s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _themesBucket,
                Key = s3Key,
                FilePath = tempTarball,
            });
            Console.WriteLine("  Upload complete.");

            // Step 3: Invoke shared gate-checker Lambda
            var functionName = "shared-gate-checker";
            var payload = new
            {
                check_type = "deploy_theme",
                s3_bucket = _themesBucket,
                s3_key = s3Key,
                theme_name = themeName,
            };

            Console.WriteLine($"  Invoking {functionName} (deploy_theme)...");

            using var lambdaClient = CreateSharedLambdaClient();
            var payloadJson = JsonSerializer.Serialize(payload);
            var response = await lambdaClient.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.RequestResponse,
                Payload = payloadJson,
            });

            if (response.FunctionError != null)
            {
                using var errReader = new StreamReader(response.Payload);
                var errBody = await errReader.ReadToEndAsync();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Lambda error: {response.FunctionError}");
                Console.Error.WriteLine($"    {errBody}");
                Console.ResetColor();
                return false;
            }

            using var reader = new StreamReader(response.Payload);
            var responseJson = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("passed", out var passedProp))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: Unexpected Lambda response: {responseJson}");
                Console.ResetColor();
                return false;
            }

            var passed = passedProp.GetBoolean();
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp)
                ? reasonProp.GetString() ?? ""
                : "";

            if (passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Theme '{themeName}' deployed: {reason}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Theme deploy failed: {reason}");
                Console.ResetColor();
            }

            return passed;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Warning: Lambda 'shared-gate-checker' not found. Run 'lz deployshared' first.");
            Console.ResetColor();
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  Theme deploy error: {ex.Message}");
            Console.ResetColor();
            return false;
        }
        finally
        {
            // Clean up local temp file
            if (File.Exists(tempTarball))
                File.Delete(tempTarball);

            // Clean up S3 tarball (best-effort)
            try
            {
                using var s3Client = CreateSharedS3Client();
                await s3Client.DeleteObjectAsync(_themesBucket, s3Key);
            }
            catch
            {
                // Non-fatal — tarball in S3 is small and will be overwritten on next deploy
            }
        }
    }

    /// <summary>
    /// Create the themes S3 bucket if it doesn't already exist.
    /// Uses PutBucket which is idempotent — succeeds silently if the bucket
    /// already exists and is owned by the same account.
    /// </summary>
    private async Task EnsureBucketExistsAsync(AmazonS3Client s3Client)
    {
        try
        {
            await s3Client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = _themesBucket,
                UseClientRegion = true,
            });
            // Block all public access
            await s3Client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
            {
                BucketName = _themesBucket,
                PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                {
                    BlockPublicAcls = true,
                    BlockPublicPolicy = true,
                    IgnorePublicAcls = true,
                    RestrictPublicBuckets = true,
                },
            });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou")
        {
            // Bucket exists in our account — nothing to do
        }
    }

    /// <summary>
    /// Create a .tar.gz archive of a directory's contents (not the directory itself).
    /// The archive contains the immediate children (login/, account/, email/) at the root.
    /// </summary>
    private static void CreateTarGz(string outputPath, string sourceDir)
    {
        using var fileStream = File.Create(outputPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        System.Formats.Tar.TarFile.CreateFromDirectory(sourceDir, gzipStream, includeBaseDirectory: false);
    }

    /// <summary>
    /// Create an AWS client using the shared-services account credentials.
    /// Both S3 and Lambda for theme deploy live in the shared account.
    /// </summary>
    private (string region, string? profile) GetSharedCredentials()
    {
        var region = _config.Aws().SharedRegion ?? _config.Region;
        var profile = _config.Aws().SharedProfile ?? _config.Profile;
        return (region, profile);
    }

    private AmazonLambdaClient CreateSharedLambdaClient()
    {
        var (region, profile) = GetSharedCredentials();
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        var lambdaConfig = new AmazonLambdaConfig
        {
            RegionEndpoint = regionEndpoint,
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonLambdaClient(credentials, lambdaConfig);
        }

        return new AmazonLambdaClient(lambdaConfig);
    }

    private AmazonS3Client CreateSharedS3Client()
    {
        var (region, profile) = GetSharedCredentials();
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonS3Client(credentials, regionEndpoint);
        }

        return new AmazonS3Client(regionEndpoint);
    }
}
