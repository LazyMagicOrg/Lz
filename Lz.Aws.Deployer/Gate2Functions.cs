using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  GATE 2'S HANDLERS (DecoupledCd.md §15.5, V-E), in the target account, thin as the others are. The decisions are in
//  Gate2Steps.cs, Gate2Bundles.cs and Gate2Verdict.cs.
// ---------------------------------------------------------------------------------------------

public sealed class Gate2StartFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        string Env(string name) => DeployerEnvironment.Required(Environment.GetEnvironmentVariable, name);
        var s3 = Clients.S3.Value;
        var evidence = new S3Gate2Objects(s3);

        var result = await Gate2StartStep.RunAsync(
            state, executionName, Gate2Set.Decode(Env(DeployerEnvironment.Gate2Set)), Clients.Leases(), Env(DeployerEnvironment.Gate2LeaseKey),
            new EcsServices(Clients.Ecs.Value), new S3Gate2Markers(new S3AppBucket(s3)),
            new Gate2RecordBundles(new S3RecordStore(s3), evidence, evidence, new S3Gate2Presigner(s3),
                Env(DeployerEnvironment.BuildRecordStore), Env(DeployerEnvironment.ArtifactStore), Env(DeployerEnvironment.EvidenceStore),
                Env(DeployerEnvironment.Gate2BrowserPrefix)),
            Env(DeployerEnvironment.Gate2ResultsBucket), DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: Gate 2 {result["verdict"]}{(result["reason"] is { } r ? $": {r}" : "")}");
        return Io.Write(result);
    }
}

/// <summary>
/// MarkPassed (P5 stage D): after a passed Gate 2, the set it found running written once as this environment's pass marker,
/// for the next environment in the chain to read.
/// </summary>
public sealed class MarkPassedFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        string Required(string name) => DeployerEnvironment.Required(Environment.GetEnvironmentVariable, name);
        var settings = new MarkPassedSettings(Required(DeployerEnvironment.MarkEnvironment), Required(DeployerEnvironment.EvidenceStore),
            System.Text.Json.JsonSerializer.Deserialize<List<MarkMember>>(Required(DeployerEnvironment.MarkMembers),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!);

        var result = await MarkPassedStep.RunAsync(state, executionName, settings,
            new S3Gate2Objects(Clients.S3.Value), new S3RecordStore(Clients.S3.Value), new S3EvidenceWriter(Clients.S3.Value), DateTimeOffset.UtcNow);
        context.Logger.LogInformation($"{executionName}: pass marker {result["key"]} for {result["members"]} member(s) (written: {result["written"]})");
        return Io.Write(result);
    }
}

public sealed class Gate2ResultFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        string Env(string name) => DeployerEnvironment.Required(Environment.GetEnvironmentVariable, name);
        var s3 = Clients.S3.Value;
        var evidence = new S3Gate2Objects(s3);

        var result = await Gate2ResultStep.RunAsync(
            state, executionName, Gate2Set.Decode(Env(DeployerEnvironment.Gate2Set)), Clients.Leases(), Env(DeployerEnvironment.Gate2LeaseKey),
            new EcsServices(Clients.Ecs.Value), new S3Gate2Markers(new S3AppBucket(s3)),
            new S3Gate2Results(s3, Env(DeployerEnvironment.Gate2ResultsBucket)),
            Env(DeployerEnvironment.EvidenceStore), new S3EvidenceWriter(s3), evidence,
            new SnsGate2Alerts(new SnsAlerts(Clients.Sns.Value), Env(DeployerEnvironment.AlertsTopic)), DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: Gate 2 {result["outcome"]}, written to {result["evidence"]}");
        return Io.Write(result);
    }
}

/// <summary>Each app's deployed bundle version, from its <c>lz/deploy.json</c>.</summary>
internal sealed class S3Gate2Markers(IAppBucket bucket) : IGate2Markers
{
    public async Task<string?> DeployedVersionAsync(string appBucket)
        => await bucket.ReadMarkerAsync(appBucket, BundleMarker.Key) is { } read ? BundleMarker.Parse(read.Json).Deployed?.BundleVersionId : null;
}

/// <summary>The build's result files, at <c>results/{execution}/{file}</c> in the results bucket; null when the build left none.</summary>
internal sealed class S3Gate2Results(IAmazonS3 s3, string bucket) : IGate2Results
{
    public Task<string?> ReadAsync(string execution, string file) => S3Gate2Objects.TextOrNullAsync(s3, bucket, $"{Gate2ResultStep.ResultsPrefix}{execution}/{file}");
}

/// <summary>Listing and reading S3 for Gate 2: the evidence store's <c>tests/</c> and <c>deploys/image/</c>, the build account's tests records.</summary>
internal sealed class S3Gate2Objects(IAmazonS3 s3) : IGate2EvidenceReader, IGate2RecordList
{
    public Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix) => new S3RecordKeys(s3).ListAsync(bucket, prefix, "");

    public Task<string?> ReadAsync(string bucket, string key) => TextOrNullAsync(s3, bucket, key);

    /// <summary>The object's text, or null when there is no such key. A denial throws rather than reading as absence.</summary>
    public static async Task<string?> TextOrNullAsync(IAmazonS3 s3, string bucket, string key)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }
}

/// <summary>A GET URL for one object version, signed by Gate2Start's role.</summary>
internal sealed class S3Gate2Presigner(IAmazonS3 s3) : IGate2Presigner
{
    public string GetUrl(string bucket, string key, string versionId, TimeSpan lifetime) => s3.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, VersionId = versionId, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(lifetime), Protocol = Protocol.HTTPS,
    });
}

/// <summary>The alerts topic, for Gate 2's advisory changes and its tests that passed only on their retry.</summary>
internal sealed class SnsGate2Alerts(IAlertPublisher publisher, string topicArn) : IGate2Alerts
{
    public Task PublishAsync(string subject, string message) => publisher.PublishAsync(topicArn, subject, message);
}
