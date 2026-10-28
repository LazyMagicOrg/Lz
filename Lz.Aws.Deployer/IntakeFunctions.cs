using System.Text.Json.Nodes;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  THE INTAKE'S HANDLERS (DecoupledCd.md §15.4, V-B), in the build account. Thin, as the deployer's are: read the
//  payload, build the adapters, call a step, write the answer. The decisions are in the linked Intake* files.
// ---------------------------------------------------------------------------------------------

/// <summary>The poller, every minute: what GitHub finished, against what the evidence and the machine say.</summary>
public sealed class IntakePollerFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var repositories = IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories));
        var app = IntakeClients.App(repositories);
        var result = await IntakePollStep.RunAsync(
            repositories, app,
            new S3IntakeEvidence(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.EvidenceStore)),
            new SfnIntakeExecutions(Clients.Sfn.Value, IntakeClients.Required(IntakeEnvironment.StateMachine)),
            IntakeClients.Alerts(),
            DateTimeOffset.UtcNow);
        context.Logger.LogInformation($"intake poll: started {result["started"]!.ToJsonString()}, alarmed {result["alarmed"]!.AsArray().Count}");
        return Io.Write(result);
    }
}

public sealed class IntakeCheckRunFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var repositories = IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories));
        var result = await IntakeCheckRunStep.RunAsync(state, repositories, IntakeClients.App(repositories));
        context.Logger.LogInformation($"{executionName}: run {result["verdict"]}");
        return Io.Write(result);
    }
}

public sealed class IntakeFetchFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var repositories = IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories));
        var result = await IntakeFetchStep.RunAsync(
            state, executionName, repositories, IntakeClients.App(repositories),
            new S3IntakeScratch(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.ScratchBucket)),
            new S3IntakeEvidence(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.EvidenceStore)),
            Path.Combine(Path.GetTempPath(), executionName));
        context.Logger.LogInformation($"{executionName}: fetch {result["verdict"]} {result["manifestDigest"]}");
        return Io.Write(result);
    }
}

/// <summary>Taken in, refused or faulted, by the state's <c>record</c> field, which the machine sets per branch.</summary>
public sealed class IntakeRecordFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var evidence = new S3IntakeEvidence(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.EvidenceStore));
        var repositories = IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories));
        var now = DateTimeOffset.UtcNow;
        var result = state["record"]?.GetValue<string>() switch
        {
            "taken-in" => await IntakeRecordStep.TakenInAsync(state, IntakeInput.From(state).In(repositories)?.Mode ?? "dry", evidence, now),
            "refused" => await IntakeRecordStep.RefusedAsync(state, evidence, IntakeClients.Alerts(), now, IntakeClients.Outcomes()),
            "fault" => await IntakeRecordStep.FaultAsync(state, executionName, evidence, IntakeClients.Alerts(), now, IntakeClients.Outcomes()),
            var other => throw new IntakeFault($"the machine asked to record '{other}', which is none of taken-in, refused or fault"),
        };
        context.Logger.LogInformation($"{executionName}: recorded {result["key"]} (written: {result["written"]})" +
                                      (result["outcome"] is { } told ? $"; outcomes list: {told}" : ""));
        return Io.Write(result);
    }
}

internal static class IntakeClients
{
    public static readonly Lazy<Amazon.SecretsManager.IAmazonSecretsManager> Secrets = new(() => new Amazon.SecretsManager.AmazonSecretsManagerClient());
    private static IntakeApp? _app;

    public static string Required(string name) => DeployerEnvironment.Required(Environment.GetEnvironmentVariable, name);

    /// <summary>One per execution environment, so its token is reused across invocations until it nears expiry.</summary>
    public static IntakeApp App(IReadOnlyList<IntakeRepository> repositories)
        => _app ??= new IntakeApp(new IntakeGitHub(new HttpClient()), Secrets.Value, Required(IntakeEnvironment.AppSecret), repositories);

    public static IIntakeAlerts Alerts() => new SnsIntakeAlerts(Clients.Sns.Value, Required(IntakeEnvironment.AlertsTopic));

    /// <summary>The chain's first environment's outcomes list, or null where its chain entry has none.</summary>
    public static IntakeOutcomes? Outcomes()
        => Environment.GetEnvironmentVariable(IntakeEnvironment.OutcomesTopic) is { Length: > 0 } topic
            ? new IntakeOutcomes(
                new IntakeOutcomeSettings(Required(IntakeEnvironment.OutcomesEnvironment), topic, Required(IntakeEnvironment.EvidenceStore),
                    Required(DeployerEnvironment.Region)),
                new SnsAlerts(Clients.Sns.Value))
            : null;
}

/// <summary>
/// The intake App (V-17) as the steps see it: its key read from Secrets Manager (JSON <c>{"clientId", "privateKey"}</c>), a
/// JWT, the installation found from the first repository, and an installation token narrowed to the configured
/// repositories and to Actions and Contents read, kept until ten minutes before GitHub's hour runs out.
/// </summary>
internal sealed class IntakeApp(IntakeGitHub github, Amazon.SecretsManager.IAmazonSecretsManager secrets, string secretId,
    IReadOnlyList<IntakeRepository> repositories) : IIntakeRuns, IIntakeGitHub
{
    private string? _token;
    private DateTimeOffset _tokenUntil;

    private async Task<string> TokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenUntil) return _token;
        var secret = JsonNode.Parse((await secrets.GetSecretValueAsync(new Amazon.SecretsManager.Model.GetSecretValueRequest { SecretId = secretId })).SecretString)
            ?? throw new IntakeFault($"the intake App's secret {secretId} is empty");
        var jwt = GitHubAppJwt.Create(secret["clientId"]!.GetValue<string>(), secret["privateKey"]!.GetValue<string>(), DateTimeOffset.UtcNow);
        var installation = await github.InstallationIdAsync(jwt, repositories[0].Repo, CancellationToken.None);
        _token = await github.InstallationTokenAsync(jwt, installation, repositories.Select(r => r.RepositoryId).Distinct().ToList(), CancellationToken.None);
        _tokenUntil = DateTimeOffset.UtcNow.AddMinutes(50);
        return _token;
    }

    public async Task<IReadOnlyList<GitHubRun>> CompletedRunsAsync(string repo, string workflowFile, DateTimeOffset since)
        => await github.CompletedRunsAsync(await TokenAsync(), repo, workflowFile, since, CancellationToken.None);

    public async Task<GitHubRun?> RunAttemptAsync(string repo, long runId, int attempt)
        => await github.RunAttemptAsync(await TokenAsync(), repo, runId, attempt, CancellationToken.None);

    public async Task<IReadOnlyList<GitHubJob>> JobsAsync(string repo, long runId, int attempt)
        => await github.JobsAsync(await TokenAsync(), repo, runId, attempt, CancellationToken.None);

    public async Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string repo, long runId)
        => await github.ArtifactsAsync(await TokenAsync(), repo, runId, CancellationToken.None);

    public async Task DownloadAsync(string repo, long artifactId, string path)
    {
        await using var source = await github.DownloadArtifactAsync(await TokenAsync(), repo, artifactId, CancellationToken.None);
        await using var file = File.Create(path);
        await source.CopyToAsync(file);
    }
}

/// <summary>The intake's evidence store: listed by prefix, written once (a write without If-None-Match is refused by its policy).</summary>
internal sealed class S3IntakeEvidence(IAmazonS3 s3, string bucket) : IIntakeEvidenceStore
{
    public async Task<IReadOnlyList<string>> KeysAsync(string prefix)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token });
            keys.AddRange((page.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return keys;
    }

    public Task<bool> PutOnceAsync(string key, string body) => new S3EvidenceWriter(s3).PutOnceAsync(bucket, key, body);
}

/// <summary>The scratch bucket, written once per key: a retry of a try that staged finds its own object and moves on.</summary>
internal sealed class S3IntakeScratch(IAmazonS3 s3, string bucket) : IIntakeScratch
{
    public async Task PutFileAsync(string key, string path)
    {
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, FilePath = path, IfNoneMatch = "*" });
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // Already staged under this execution's key: written by this same try before a retry of the step.
        }
    }
}

internal sealed class SfnIntakeExecutions(Amazon.StepFunctions.IAmazonStepFunctions sfn, string stateMachineArn) : IIntakeExecutions
{
    public async Task<IReadOnlyList<(string Name, string Status)>> SinceAsync(DateTimeOffset since)
    {
        var found = new List<(string, string)>();
        string? token = null;
        do
        {
            var page = await sfn.ListExecutionsAsync(new Amazon.StepFunctions.Model.ListExecutionsRequest
            {
                StateMachineArn = stateMachineArn, MaxResults = 1000, NextToken = token,
            });
            var executions = page.Executions ?? new List<Amazon.StepFunctions.Model.ExecutionListItem>();
            found.AddRange(executions.Select(e => (e.Name, e.Status?.Value ?? "")));
            // Newest first: stop once a page reaches past the window.
            token = executions.Count > 0 && executions[^1].StartDate >= since.UtcDateTime ? page.NextToken : null;
        } while (token is not null);
        return found;
    }

    public Task<bool> StartAsync(string name, string input) => new SfnExecutions(sfn, Clients.Ssm.Value).StartAsync(stateMachineArn, name, input);
}

internal sealed class SnsIntakeAlerts(Amazon.SimpleNotificationService.IAmazonSimpleNotificationService sns, string topicArn) : IIntakeAlerts
{
    public Task PublishAsync(string subject, string message) => new SnsAlerts(sns).PublishAsync(topicArn, subject, message);
}
