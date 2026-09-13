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

        // The decisions and the sequence are PrepareStep's, where a test drives them.
        var result = await PrepareStep.RunAsync(
            state,
            DeployerEnvironment.Required(Environment.GetEnvironmentVariable, DeployerEnvironment.Registry),
            new EcsServices(Clients.Ecs.Value),
            new EcsTaskDefinitions(Clients.Ecs.Value));

        context.Logger.LogInformation($"{executionName}: registered {result["taskDefinitionArn"]} pinning {result["image"]}");
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
            new S3EvidenceWriter(Clients.S3.Value));

        context.Logger.LogInformation($"{executionName}: deploy recorded at {result["key"]} (written: {result["written"]})");
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
