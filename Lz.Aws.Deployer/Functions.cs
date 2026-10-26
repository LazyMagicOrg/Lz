using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  THE HANDLERS. Stream in, stream out, so no serializer configuration stands between the state
//  machine's JSON and the code — a naming policy applied in the wrong place would rename a field the
//  definition reads, and nothing would say so until an execution failed on it.
//
//  Their names are a contract: DeployerHandlers builds the handler strings from them, and a test opens
//  the built zip and resolves every one.
// ---------------------------------------------------------------------------------------------

public sealed class VerifyFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var settings = VerifySettings.Read(Environment.GetEnvironmentVariable);
        Task<JsonObject> Verify(JsonObject s) => VerifyStep.RunAsync(
            s, settings,
            new S3RecordStore(Clients.S3.Value),
            new EcrImages(Clients.Ecr.Value),
            new EcsServices(Clients.Ecs.Value),
            new EcsTaskDefinitions(Clients.Ecs.Value),
            new S3ArtifactObjects(Clients.S3.Value));

        // A PROMOTED ENVIRONMENT (P5 stage F) admits a set by its predecessor's marker, then verifies each member here.
        if (Environment.GetEnvironmentVariable(DeployerEnvironment.Predecessor) is { Length: > 0 } predecessor)
        {
            var evidenceStore = DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.EvidenceStore);
            var set = await VerifySetStep.RunAsync(
                state,
                executionName,
                new PromotionSettings(predecessor,
                    DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.PredecessorEvidenceStore),
                    evidenceStore),
                settings,
                new S3VersionedObjects(Clients.S3.Value),
                new S3EvidenceProbe(Clients.S3.Value),
                new PromotionRevertReads(Clients.S3.Value, Clients.Sfn.Value, evidenceStore,
                    DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.StateMachine)),
                Verify);
            context.Logger.LogInformation($"{executionName}: admitted {set["markerId"]} from {predecessor}, {set["bundles"]!.AsArray().Count} bundle member(s) beside the image");
            return Io.Write(set);
        }

        var result = await Verify(state);

        context.Logger.LogInformation(result["class"]?.GetValue<string>() == "client"
            ? $"{executionName}: verified bundle {result["identity"]?["key"]} version {result["identity"]?["versionId"]} for {result["target"]?["bucket"]}"
            : $"{executionName}: verified {result["digest"]}; scan {result["scan"]?["verdict"]}");
        return Io.Write(result);
    }
}

/// <summary>Class 2 (P4 stage C): mirror the verified bundle into its web app's bucket and invalidate the app's path.</summary>
public sealed class DeployBundleFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await DeployBundleStep.RunAsync(
            state,
            executionName,
            DeployBundleSettings.Read(Environment.GetEnvironmentVariable),
            new S3ArtifactObjects(Clients.S3.Value),
            new S3AppBucket(Clients.S3.Value),
            new CloudFrontInvalidations(Clients.CloudFront.Value),
            () => DateTimeOffset.UtcNow);

        context.Logger.LogInformation(
            $"{executionName}: mirrored into {result["bucket"]}/{result["keyPrefix"]}: put {result["put"]!.ToJsonString()}, " +
            $"deleted {result["deleted"]}, unchanged {result["unchanged"]}; invalidations {result["invalidations"]!.ToJsonString()}");
        return Io.Write(result);
    }
}

/// <summary>Class 2 (P4 stage C): read the deployed bundle back, and hand Record its evidence.</summary>
public sealed class VerifyBundleFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await VerifyBundleStep.RunAsync(
            state, executionName, new S3AppBucket(Clients.S3.Value), new CloudFrontInvalidations(Clients.CloudFront.Value),
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: bundle {result["verdict"]}, {result["objects"]} objects, manifest {result["manifestSha256"]}");
        return Io.Write(result);
    }
}

/// <summary>Class 3 (DecoupledCd.md V-24): mirror the verified site into its website bucket, held to the environment's domain.</summary>
public sealed class DeploySiteFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await DeploySiteStep.RunAsync(
            state, executionName, "/tmp/lz-site", new S3ArtifactObjects(Clients.S3.Value), new S3AppBucket(Clients.S3.Value),
            () => DateTimeOffset.UtcNow);

        context.Logger.LogInformation(
            $"{executionName}: mirrored the site into {result["bucket"]}/{result["keyPrefix"]} for {result["domain"]}: put " +
            $"{result["put"]!.ToJsonString()}, deleted {result["deleted"]}, unchanged {result["unchanged"]}");
        return Io.Write(result);
    }
}

/// <summary>Class 3 (V-24): read the deployed site back, and hand Record its evidence.</summary>
public sealed class VerifySiteFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await VerifySiteStep.RunAsync(state, executionName, new S3AppBucket(Clients.S3.Value), DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: site {result["verdict"]}, {result["objects"]} objects, manifest {result["manifestSha256"]}");
        return Io.Write(result);
    }
}

public sealed class PrepareFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        // The decisions and the sequence are PrepareStep's, where a test drives them.
        var result = await PrepareStep.RunAsync(
            state,
            executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.Registry),
            new EcsServices(Clients.Ecs.Value),
            new EcsTaskDefinitions(Clients.Ecs.Value),
            Clients.Leases(),
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: registered {result["taskDefinitionArn"]} pinning {result["image"]}, holding the service's lease");
        return Io.Write(result);
    }
}

public sealed class VerifyRolloutFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await VerifyRolloutStep.RunAsync(
            state, executionName, new EcsServices(Clients.Ecs.Value), DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: rollout {result["verdict"]}");
        return Io.Write(result);
    }
}

public sealed class RecordFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await RecordStep.RunAsync(
            state,
            executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.EvidenceStore),
            new S3EvidenceWriter(Clients.S3.Value),
            Clients.Leases());

        context.Logger.LogInformation($"{executionName}: deploy recorded at {result["key"]} (written: {result["written"]}); lease {result["lease"]}");
        return Io.Write(result);
    }
}

/// <summary>
/// The promotion poller (P5 stage I), a promoted environment's only trigger: every minute, the newest set its predecessor passed
/// that is neither decided nor started here, unless a set is in flight or the hold is on or missing.
/// </summary>
public sealed class PromotionPollerFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        string Required(string name) => DeployerEnvironment.Required(Environment.GetEnvironmentVariable, name);
        var settings = new PromotionPollSettings(
            Required(DeployerEnvironment.Predecessor), Required(DeployerEnvironment.PredecessorEvidenceStore), Required(DeployerEnvironment.EvidenceStore),
            Required(DeployerEnvironment.StateMachine), Required(DeployerEnvironment.HoldParameter), Required(DeployerEnvironment.AlertsTopic),
            JsonNode.Parse(Required(DeployerEnvironment.PromotionTarget))!.AsObject());

        var result = await PromotionPollStep.RunAsync(
            settings,
            new PromotionReads(Clients.S3.Value, Clients.Ssm.Value, Clients.Sfn.Value),
            new SfnExecutionStarter(Clients.Sfn.Value),
            new S3EvidenceWriter(Clients.S3.Value),
            new SnsAlerts(Clients.Sns.Value),
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"promotion poll: {result.ToJsonString()}");
        return Io.Write(result);
    }
}

/// <summary>A revert's hold (P5 stage J), set before Prepare, in a revert only.</summary>
public sealed class SetHoldFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var result = await SetHoldStep.RunAsync(state, executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.HoldParameter), new SsmHoldWriter(Clients.Ssm.Value));
        context.Logger.LogInformation($"{executionName}: hold {result["hold"]} set {result["value"]}");
        return Io.Write(result);
    }
}

/// <summary>A revert's record (P5 stage J): reverts/{execution}.json, and no decision.</summary>
public sealed class RecordRevertFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var result = await RecordRevertStep.RunAsync(state, executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.EvidenceStore), new S3EvidenceWriter(Clients.S3.Value), DateTimeOffset.UtcNow);
        context.Logger.LogInformation($"{executionName}: revert recorded at {result["key"]} (written: {result["written"]})");
        return Io.Write(result);
    }
}

/// <summary>
/// The revert-start function (P5 stage J), invoked by a person through the operator set: {"revertTo": {"key", "versionId"}}
/// starts this deployer going back to that set, unless anything else is in flight.
/// </summary>
public sealed class RevertStartFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var request = JsonNode.Parse(await new StreamReader(input).ReadToEndAsync()) as JsonObject ?? new JsonObject();
        var result = await RevertStartStep.RunAsync(request,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.StateMachine),
            JsonNode.Parse(DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.PromotionTarget))!.AsObject(),
            new SfnPromotionExecutions(Clients.Sfn.Value), DateTimeOffset.UtcNow);
        context.Logger.LogInformation($"revert started: {result.ToJsonString()}");
        return Io.Write(result);
    }
}

/// <summary>A promoted environment's decision (P5 stage F): deployed after the set, refused or failed after RecordFailure.</summary>
public sealed class DecideFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await DecisionStep.RunAsync(
            state,
            executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.EvidenceStore),
            new S3EvidenceWriter(Clients.S3.Value),
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: decided {result["outcome"]} at {result["key"]} (written: {result["written"]})");
        return Io.Write(result);
    }
}

public sealed class RecordFailureFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));

        var result = await RecordFailureStep.RunAsync(
            state,
            executionName,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.EvidenceStore),
            new S3EvidenceWriter(Clients.S3.Value),
            DateTimeOffset.UtcNow,
            Clients.Leases());

        context.Logger.LogInformation($"{executionName}: failure recorded at {result["key"]} (written: {result["written"]}); lease {result["lease"]}");
        return Io.Write(result);
    }
}

/// <summary>
/// The trigger (P2 stage D): EventBridge invokes this with a build record's forwarded S3 event, and it starts the
/// deployer under the name the record implies. A refusal or conflict is thrown, so Lambda retries it and then puts it,
/// with the message, in the dead-letter queue.
/// </summary>
public sealed class StartFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var result = await StartStep.RunAsync(
            await Io.ReadAsync(input),
            TriggerSettings.Read(Environment.GetEnvironmentVariable),
            new S3RecordStore(Clients.S3.Value),
            new SfnExecutions(Clients.Sfn.Value, Clients.Ssm.Value));

        context.Logger.LogInformation(result["skipped"] is JsonObject skipped
            ? $"skipped {skipped["record"]}: {skipped["reason"]}"
            : $"started {result["started"]!.ToJsonString()}; already started {result["duplicates"]!.ToJsonString()}");
        return Io.Write(result);
    }
}

/// <summary>
/// The sweep for images no build record names (P2 stage D3), run on a schedule. The schedule's event says only that it is
/// time, so nothing in it is read; what the run found is logged and returned.
/// </summary>
public sealed class CorroborateFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var result = await CorroborateStep.RunAsync(
            CorroborateSettings.Read(Environment.GetEnvironmentVariable),
            new EcrRepositoryImages(Clients.Ecr.Value),
            new S3ArtifactVersions(Clients.S3.Value),
            new S3RecordKeys(Clients.S3.Value),
            new S3RecordStore(Clients.S3.Value),
            new S3EvidenceProbe(Clients.S3.Value),
            new S3EvidenceWriter(Clients.S3.Value),
            new SnsAlerts(Clients.Sns.Value),
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation(
            $"corroborated {result["corroborated"]!.AsArray().Count}; anomalies {result["anomalies"]!.ToJsonString()}; " +
            $"already recorded {result["alreadyRecorded"]!.AsArray().Count}; too new to judge {result["tooNew"]!.AsArray().Count}");
        return Io.Write(result);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class SignatureHookFunction
{
    /// <summary>Where Notation is laid out. Lambda's only writable directory is /tmp.</summary>
    private const string LayoutRoot = "/tmp/lz-notation";

    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var json = await Io.ReadAsync(input);

        HookStatus status;
        string reason;
        try
        {
            var packageRoot = Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT") ?? AppContext.BaseDirectory;

            (status, reason) = await SignatureHookStep.RunAsync(
                json,
                HookSettings.Read(Environment.GetEnvironmentVariable),
                new HookReads(Clients.Ecs.Value, Clients.Ecr.Value),
                new NotationCli(packageRoot),
                NotationLayout.Under(LayoutRoot));
        }
        catch (Exception ex)
        {
            // The step already turns its own failures into FAILED; this catches configuration read
            // before it ran. Either way the answer is a refusal with a reason in the log.
            status = HookStatus.FAILED;
            reason = $"the hook could not start: {ex.GetType().Name}: {ex.Message}";
        }

        context.Logger.LogInformation($"hookStatus={status}: {reason}");
        return Io.Write(SignatureHook.Response(status));
    }
}

internal static class Io
{
    public static async Task<string> ReadAsync(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public static Stream Write(JsonObject result) => Write(result.ToJsonString());

    public static Stream Write(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));
}
