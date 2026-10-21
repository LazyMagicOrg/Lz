using System.Text.Json.Nodes;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.Lambda.Core;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  THE INTAKE'S PUSH (DecoupledCd.md §15.4, V-12), thin as the others are; the decisions are in the linked IntakePush.cs.
//  Measured first by V-C's probe (§15.8 items 3 and 4), which invokes it by hand against a probe repository and asks
//  for ECR's own answers back, as data, rather than a failed invocation.
// ---------------------------------------------------------------------------------------------

public sealed class IntakePushFunction
{
    private static readonly Lazy<IAmazonECR> Ecr = new(() => new AmazonECRClient());

    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var request = JsonNode.Parse(await Io.ReadAsync(input))?.AsObject() ?? throw new IntakeFault("the push was given no input");
        string Required(string name) => request[name]?.GetValue<string>() ?? throw new IntakeFault($"the push was given no '{name}'");
        var (key, repository, tag) = (Required("key"), Required("repository"), Required("tag"));
        var readTagFirst = request["readTagFirst"]?.GetValue<bool>() ?? true;
        var imageDigest = request["imageDigest"]?.GetValue<string>();
        var probe = request["probe"]?.GetValue<bool>() ?? false;

        var path = Path.Combine(Path.GetTempPath(), "push.zip");
        using (var got = await Clients.S3.Value.GetObjectAsync(new GetObjectRequest
               {
                   BucketName = IntakeClients.Required(IntakeEnvironment.ScratchBucket), Key = key,
               }))
            await got.WriteResponseStreamToFileAsync(path, append: false, CancellationToken.None);

        try
        {
            var result = await IntakePushStep.RunAsync(path, repository, tag, new EcrImageRegistry(Ecr.Value), readTagFirst, imageDigest);
            context.Logger.LogInformation($"{repository}:{tag}: {result["outcome"]} {result["ecrDigest"] ?? result["digestAtTag"]}");
            return Io.Write(result);
        }
        catch (AmazonECRException ex) when (probe)
        {
            // The probe measures ECR's refusals, so they come back as its answer.
            context.Logger.LogInformation($"{repository}:{tag}: ECR answered {ex.ErrorCode}: {ex.Message}");
            return Io.Write(new JsonObject
            {
                ["repository"] = repository, ["tag"] = tag, ["imageDigest"] = imageDigest, ["outcome"] = "ecr-refused",
                ["exception"] = ex.GetType().Name, ["errorCode"] = ex.ErrorCode, ["message"] = ex.Message,
            });
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal sealed class EcrImageRegistry(IAmazonECR ecr) : IImageRegistry
{
    public async Task<string?> DigestAtTagAsync(string repository, string tag)
    {
        try
        {
            var found = await ecr.DescribeImagesAsync(new DescribeImagesRequest
            {
                RepositoryName = repository, ImageIds = new List<ImageIdentifier> { new() { ImageTag = tag } },
            });
            return found.ImageDetails?.SingleOrDefault()?.ImageDigest;
        }
        catch (ImageNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlySet<string>> MissingBlobsAsync(string repository, IReadOnlyList<string> digests)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in digests.Chunk(100))   // the call's limit
        {
            var checkedNow = await ecr.BatchCheckLayerAvailabilityAsync(new BatchCheckLayerAvailabilityRequest
            {
                RepositoryName = repository, LayerDigests = chunk.ToList(),
            });
            // SDK v4: a collection with no members is null.
            foreach (var layer in checkedNow.Layers ?? new List<Layer>())
                if (layer.LayerAvailability != LayerAvailability.AVAILABLE) missing.Add(layer.LayerDigest);
            foreach (var failure in checkedNow.Failures ?? new List<LayerFailure>())
                missing.Add(failure.LayerDigest);
        }
        return missing;
    }

    public async Task UploadBlobAsync(string repository, string digest, long size, Stream content)
    {
        var started = await ecr.InitiateLayerUploadAsync(new InitiateLayerUploadRequest { RepositoryName = repository });
        var part = (int)Math.Max(started.PartSize ?? 0, 20L << 20);
        var buffer = new byte[part];
        long sent = 0;
        while (sent < size)
        {
            var filled = 0;
            int read;
            while (filled < buffer.Length && (read = await content.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled))) > 0)
                filled += read;
            if (filled == 0) throw new IntakeFault($"blob {digest} ended after {sent} of {size} bytes");
            await ecr.UploadLayerPartAsync(new UploadLayerPartRequest
            {
                RepositoryName = repository, UploadId = started.UploadId,
                PartFirstByte = sent, PartLastByte = sent + filled - 1,
                LayerPartBlob = new MemoryStream(buffer, 0, filled, writable: false),
            });
            sent += filled;
        }
        try
        {
            await ecr.CompleteLayerUploadAsync(new CompleteLayerUploadRequest
            {
                RepositoryName = repository, UploadId = started.UploadId, LayerDigests = new List<string> { digest },
            });
        }
        catch (LayerAlreadyExistsException)
        {
            // another push of the same blob finished first; ECR has it, under this digest
        }
    }

    public async Task<string> PutImageAsync(string repository, string tag, string manifest, string mediaType, string imageDigest)
        => (await ecr.PutImageAsync(new PutImageRequest
        {
            RepositoryName = repository, ImageTag = tag, ImageManifest = manifest, ImageManifestMediaType = mediaType, ImageDigest = imageDigest,
        })).Image.ImageId.ImageDigest;
}
