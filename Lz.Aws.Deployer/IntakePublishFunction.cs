using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  THE PUBLISH (DecoupledCd.md §15.4, V-D), after Gate 1's pass, thin as the others are; the decisions are in the linked
//  IntakePublish.cs. A dry repository's publish returns at once.
// ---------------------------------------------------------------------------------------------

public sealed class IntakePublishFunction
{
    private static readonly Lazy<IAmazonECR> Ecr = new(() => new AmazonECRClient());

    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var s3 = Clients.S3.Value;
        var work = Path.Combine(Path.GetTempPath(), "publish");
        try
        {
            var result = await IntakePublishStep.RunAsync(
                state, executionName,
                IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories)),
                PublishTargets.Decode(IntakeClients.Required(IntakeEnvironment.PublishTargets)),
                IntakeClients.Required(IntakeEnvironment.BuildRecordStore), IntakeClients.Required(IntakeEnvironment.ArtifactStore),
                new S3ScratchReader(s3, IntakeClients.Required(IntakeEnvironment.ScratchBucket)),
                new EcrImageRegistry(Ecr.Value), new EcrImageSignatures(Ecr.Value),
                new S3PublishStores(s3, IntakeClients.Required(IntakeEnvironment.BuildRecordStore), IntakeClients.Required(IntakeEnvironment.ArtifactStore)),
                work);
            context.Logger.LogInformation($"{executionName}: publish {result["outcome"]} {result["image"]?["digest"]}");
            return Io.Write(result);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}

internal sealed class S3ScratchReader(IAmazonS3 s3, string bucket) : IScratchReader
{
    public async Task GetFileAsync(string key, string path)
    {
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
        await got.WriteResponseStreamToFileAsync(path, append: false, CancellationToken.None);
    }
}

internal sealed class EcrImageSignatures(IAmazonECR ecr) : IImageSignatures
{
    public async Task<string?> StatusAsync(string repository, string digest, string profileArn)
    {
        var status = await ecr.DescribeImageSigningStatusAsync(new DescribeImageSigningStatusRequest
        {
            RepositoryName = repository, ImageId = new ImageIdentifier { ImageDigest = digest },
        });
        // SDK v4: a collection with no members is null.
        return (status.SigningStatuses ?? new List<ImageSigningStatus>()).FirstOrDefault(s => s.SigningProfileArn == profileArn)?.Status?.Value;
    }
}

/// <summary>
/// The two write-once stores: every write a conditional PutObject, and every read of a key first asks whether it exists.
///
/// <para>EXISTENCE BY LISTING THE EXACT KEY, not by a HEAD or GET that fails. S3 answers a request for a missing key with
/// 404 only to a caller who may list the bucket, and with 403 to anyone else; whether a list grant narrowed by
/// <c>s3:prefix</c> counts is not documented, and the first live publish (2026-10-21, run 39668794761) met the 403 on
/// the artifact store. A listing whose prefix is the key itself is what the narrowed grant does admit, so a missing
/// key reads as absent and any 403 left is a real refusal.</para>
/// </summary>
internal sealed class S3PublishStores(IAmazonS3 s3, string recordStore, string artifactStore) : IPublishStores
{
    private async Task<bool> ExistsAsync(string bucket, string key)
    {
        var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = key, MaxKeys = 10 });
        return (listed.S3Objects ?? new List<S3Object>()).Any(o => o.Key == key);
    }

    public async Task<IReadOnlyList<string>> RecordKeysAsync(string prefix)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = recordStore, Prefix = prefix, ContinuationToken = token });
            keys.AddRange((page.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return keys;
    }

    public async Task<string?> RecordAsync(string key)
    {
        if (!await ExistsAsync(recordStore, key)) return null;
        try
        {
            using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = recordStore, Key = key });
            return await new StreamReader(got.ResponseStream).ReadToEndAsync();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> PutRecordOnceAsync(string key, string json)
    {
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = recordStore, Key = key, ContentBody = json, ContentType = "application/json", IfNoneMatch = "*",
            });
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<StoredArtifact?> ArtifactAsync(string key)
    {
        if (!await ExistsAsync(artifactStore, key)) return null;
        try
        {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = artifactStore, Key = key, ChecksumMode = ChecksumMode.ENABLED });
            var sha = head.ChecksumSHA256 is { } b64 && head.ChecksumType?.Value == "FULL_OBJECT"
                ? Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant()
                : null;
            return new StoredArtifact(head.VersionId, sha);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredArtifact> PutArtifactOnceAsync(string key, string path, string sha256)
    {
        // ONE PutObject, never a multipart upload, whose SHA-256 is only a composite that Verify refuses (§15.4).
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = artifactStore, Key = key, FilePath = path, IfNoneMatch = "*",
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)),
        });
        return new StoredArtifact(put.VersionId, sha256);
    }
}
