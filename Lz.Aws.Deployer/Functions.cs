using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.ECS.Model;
using Amazon.Lambda.Core;
using Lz.Aws.Ops;
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

        var result = await VerifyStep.RunAsync(
            state,
            VerifySettings.Read(Environment.GetEnvironmentVariable),
            new S3RecordStore(Clients.S3.Value),
            new EcrImages(Clients.Ecr.Value),
            new EcsServices(Clients.Ecs.Value));

        context.Logger.LogInformation($"{executionName}: verified {result["digest"]}; scan {result["scan"]?["verdict"]}");
        return Io.Write(result);
    }
}

public sealed class PrepareFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var target = DeployerInput.From(state).Target;

        var digest = (state["verified"] as JsonObject)?["digest"] is JsonValue v && v.TryGetValue<string>(out var d)
            ? d
            : throw new DeployRefused("verified", "the state has no verified digest; Prepare cannot run before Verify.");

        var image = TaskDefinitionRevision.PinnedImage(
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.Registry),
            target.Repository, digest);

        var ecs = Clients.Ecs.Value;

        // The service's CURRENT revision is the base, read now rather than at Verify: in prod an
        // approval can take a day, and a revision cloned from a stale read would undo whatever changed
        // in between.
        var service = (await ecs.DescribeServicesAsync(new DescribeServicesRequest
            {
                Cluster = target.Cluster,
                Services = new List<string> { target.Service },
            })).Services?.FirstOrDefault(s => s.Status == "ACTIVE")
            ?? throw new DeployRefused("target.service",
                $"there is no ACTIVE service '{target.Service}' in cluster '{target.Cluster}'.");

        var described = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = service.TaskDefinition,
            // Without this the tags come back empty and the new revision would silently lose them.
            Include = new List<string> { "TAGS" },
        });

        var container = TaskDefinitionRevision.SingleContainerNamed(described.TaskDefinition, target.Container);
        var previousImage = container.Image;
        container.Image = image;

        // THE DEFINITION NEVER LEAVES THIS FUNCTION. It carries a plaintext client secret; only the new
        // revision's ARN and the two image references are returned into execution state.
        var registered = await ecs.RegisterTaskDefinitionAsync(
            TaskDefinitionRevision.RegisterRequestFor(described.TaskDefinition, described.Tags));

        var result = new JsonObject
        {
            ["taskDefinitionArn"] = registered.TaskDefinition.TaskDefinitionArn,
            ["image"] = image,
            ["previousTaskDefinitionArn"] = service.TaskDefinition,
            ["previousImage"] = previousImage,
        };

        context.Logger.LogInformation($"{executionName}: registered {result["taskDefinitionArn"]} pinning {image}");
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
            DateTimeOffset.UtcNow);

        context.Logger.LogInformation($"{executionName}: failure recorded at {result["key"]} (written: {result["written"]})");
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
