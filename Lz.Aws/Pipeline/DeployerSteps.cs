using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  WHAT THE STEPS READ, as plain records behind small interfaces.
//
//  The Lambda handlers implement these with the AWS SDK and do nothing else; everything that
//  SEQUENCES the reads and acts on them is below, where a fake can drive it. That split is the
//  lesson of 2026-09-12 applied one level up from C1: C1 made the decisions pure, and this makes the
//  ORDER of fetch-decide-refuse testable too, which is where "read the record from the wrong bucket"
//  or "ask the registry before checking provenance" would otherwise live untested.
// ---------------------------------------------------------------------------------------------

/// <summary>One container in a task, as <c>DescribeTasks</c> reports it.</summary>
public sealed record ContainerSnapshot(string? Name, string? ImageDigest);

/// <summary>One task, as <c>DescribeTasks</c> reports it.</summary>
public sealed record TaskSnapshot(string? LastStatus, IReadOnlyList<ContainerSnapshot>? Containers);

/// <summary>One deployment of a service, as <c>DescribeServices</c> reports it.</summary>
public sealed record DeploymentSnapshot(string? Status, string? TaskDefinitionArn, string? RolloutState);

/// <summary>An ACTIVE service: the revision it names and the deployments under way.</summary>
public sealed record ServiceSnapshot(string TaskDefinitionArn, IReadOnlyList<DeploymentSnapshot>? Deployments);

/// <summary>An image in this account's registry, with its scan state.</summary>
public sealed record RegistryImage(string Digest, string? ScanStatus, IReadOnlyDictionary<string, int>? FindingCounts);

public interface IRecordStore
{
    /// <summary>The object's text, or null when there is no such object.</summary>
    Task<string?> ReadAsync(string bucket, string key);
}

public interface IRegistryImages
{
    /// <summary>The image, or null when the repository holds no image with that digest.</summary>
    Task<RegistryImage?> DescribeAsync(string repository, string digest);
}

public interface IServices
{
    /// <summary>The service, or null when there is no ACTIVE service by that name.</summary>
    Task<ServiceSnapshot?> DescribeAsync(string cluster, string service);

    /// <summary>The service's tasks whose desired status is RUNNING.</summary>
    Task<IReadOnlyList<TaskSnapshot>> RunningTasksAsync(string cluster, string service);
}

public interface IEvidenceWriter
{
    /// <summary>Write once. False when the key already existed, which a conditional write refuses.</summary>
    Task<bool> PutOnceAsync(string bucket, string key, string body);
}

public interface IHookReads
{
    /// <summary>The task definition a service revision runs, or null when the revision is unknown.</summary>
    Task<string?> TaskDefinitionOfRevisionAsync(string serviceRevisionArn);

    /// <summary><c>containerDefinitions[].image</c>, in order. Never logged: see <see cref="DeployEvidence"/>.</summary>
    Task<IReadOnlyList<string?>> ContainerImagesAsync(string taskDefinitionArn);

    /// <summary>Credentials for this account's registry.</summary>
    Task<(string Username, string Password)> RegistryCredentialsAsync();
}

public interface INotation
{
    /// <summary>
    /// Lay out the verifier, its plugin, the root certificate and the trust policy under
    /// <paramref name="layout"/>. Null when ready; otherwise why it could not be — a package built
    /// without the verifier is the expected case, and it must refuse rather than crash.
    /// </summary>
    Task<string?> InstallAsync(NotationLayout layout, string trustPolicyJson);

    /// <summary>Run <c>notation verify</c> on one reference.</summary>
    Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(
        NotationLayout layout, string reference, IReadOnlyDictionary<string, string> environment);
}

/// <summary>
/// The Verify state (§4.4): read the record, check where it came from and what it claims, find the
/// image in this account's registry, apply the scan policy, and confirm the target service exists.
/// </summary>
public static class VerifyStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, VerifySettings settings, IRecordStore records, IRegistryImages registry, IServices services)
    {
        var input = DeployerInput.From(state);

        // BEFORE ANY READ. A record outside the build-record store has no authority, so it is not
        // fetched and then judged — it is never fetched.
        if (!string.Equals(input.Record.Bucket, settings.BuildRecordStore, StringComparison.Ordinal))
            throw new DeployRefused("record.bucket",
                $"the input names a record in '{input.Record.Bucket}', not the build-record store " +
                $"'{settings.BuildRecordStore}'. Records have authority only there.");

        var json = await records.ReadAsync(input.Record.Bucket, input.Record.Key)
            ?? throw new DeployRefused("record",
                $"there is no build record at s3://{input.Record.Bucket}/{input.Record.Key}.");

        BuildRecord record;
        try
        {
            record = BuildRecordFormat.Parse(json);
        }
        catch (InvalidOperationException ex)
        {
            throw new DeployRefused("record", ex.Message);
        }

        // ALL THREE, and every refusal reported — the evidence should show everything that was wrong.
        var refusals = new List<VerifyRefusal>();
        refusals.AddRange(DeployVerification.Provenance(record, input.Record, settings.BuildRecordStore));
        refusals.AddRange(DeployVerification.Verify(record, settings.AsPipeline()).Refusals);
        refusals.AddRange(DeployVerification.Target(record, input.Target, settings.ImageRepositories));
        if (refusals.Count > 0)
            throw new DeployRefused(refusals);

        // §4.4 step 3: the identity resolves in the LOCAL registry. The build account's copy is not
        // what this account's tasks pull, so it is not what is checked.
        var digest = record.Identity.Digest!;
        var image = await registry.DescribeAsync(input.Target.Repository, digest)
            ?? throw new DeployRefused("identity",
                $"{input.Target.Repository}@{digest} is not in this account's registry. Either " +
                "replication has not delivered it yet, or the record names an image that was never pushed.");

        var (verdict, reason) = DeployVerification.ScanFromStatus(
            image.ScanStatus, image.FindingCounts, settings.ScanBlockOn);

        if (verdict == ScanVerdict.NotYetAvailable)
            throw new ScanNotYetAvailable(reason);
        if (verdict == ScanVerdict.Block)
            throw new DeployRefused("scan", reason);

        var service = await services.DescribeAsync(input.Target.Cluster, input.Target.Service)
            ?? throw new DeployRefused("target.service",
                $"there is no ACTIVE service '{input.Target.Service}' in cluster '{input.Target.Cluster}'.");

        var shortCommit = record.BuiltFrom.Commit.Length > 12 ? record.BuiltFrom.Commit[..12] : record.BuiltFrom.Commit;

        return new JsonObject
        {
            ["class"] = record.Class,
            ["digest"] = digest,
            ["builtFrom"] = new JsonObject
            {
                ["repo"] = record.BuiltFrom.Repo,
                ["commit"] = record.BuiltFrom.Commit,
                ["lane"] = record.BuiltFrom.Lane,
                ["packages"] = new JsonObject(record.BuiltFrom.Packages
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
            },
            ["builtAt"] = record.BuiltAt,
            ["workflowRunId"] = record.WorkflowRunId,
            ["scan"] = new JsonObject
            {
                ["verdict"] = verdict.ToString(),
                ["status"] = image.ScanStatus,
                ["reason"] = reason,
            },
            ["previousTaskDefinition"] = service.TaskDefinitionArn,
            // What an approver reads (§4.5). Identities, not prose about them.
            ["summary"] =
                $"Deploy {input.Target.Repository}@{digest} to {input.Target.Cluster}/{input.Target.Service} " +
                $"(container {input.Target.Container}). Built from {record.BuiltFrom.Repo} {shortCommit} " +
                $"on the {record.BuiltFrom.Lane} lane, workflow run {record.WorkflowRunId}. Scan: {reason}. " +
                $"Replaces {service.TaskDefinitionArn}.",
        };
    }
}

/// <summary>
/// The VerifyRollout state (§4.7): one look at the service, and a verdict. Waiting is the
/// definition's job — a <see cref="RolloutStillRolling"/> is retried there, and the retry limit is
/// what turns a roll that never converges into a failure.
/// </summary>
public static class VerifyRolloutStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IServices services, DateTimeOffset now)
    {
        var input = DeployerInput.From(state);

        var verified = state["verified"] as JsonObject
            ?? throw new DeployRefused("verified", "the state has no Verify result; VerifyRollout cannot run first.");
        var deploy = state["deploy"] as JsonObject
            ?? throw new DeployRefused("deploy", "the state has no Prepare result; there is no revision to look for.");

        var digest = Text(verified, "digest");
        var cls = Text(verified, "class");
        var repo = (verified["builtFrom"] as JsonObject) is { } bf ? Text(bf, "repo") : null;
        var taskDefinition = Text(deploy, "taskDefinitionArn");

        if (digest is null || cls is null || repo is null || taskDefinition is null)
            throw new DeployRefused("state",
                "the state is missing verified.digest, verified.class, verified.builtFrom.repo or " +
                "deploy.taskDefinitionArn.");

        var service = await services.DescribeAsync(input.Target.Cluster, input.Target.Service)
            ?? throw new RolloutNotDeployed(
                $"the service '{input.Target.Service}' is no longer ACTIVE, so nothing this execution deployed is running.");

        var (rolloutState, problem) = DeploymentStateOf(service.Deployments, taskDefinition);
        if (problem != null)
            throw new RolloutNotDeployed(problem);

        var running = DeployVerification.RunningDigests(
            await services.RunningTasksAsync(input.Target.Cluster, input.Target.Service), input.Target.Container);

        var verdict = DeployVerification.Rollout(running, digest, rolloutState);
        var onDigest = running.Count(d => string.Equals(d, digest, StringComparison.Ordinal));

        switch (verdict)
        {
            case RolloutVerdict.StillRolling:
                throw new RolloutStillRolling(
                    $"rollout {rolloutState ?? "state unknown"}; {onDigest} of {running.Count} running task(s) on {digest}.");

            case RolloutVerdict.NotDeployed:
                throw new RolloutNotDeployed(
                    $"rollout {rolloutState ?? "state unknown"} with {onDigest} of {running.Count} running task(s) " +
                    $"on {digest}. The deploy did not take — the service is running something else.");
        }

        return new JsonObject
        {
            ["verdict"] = verdict.ToString(),
            ["runningDigests"] = new JsonArray(running.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            ["evidence"] = new JsonObject
            {
                ["key"] = DeployEvidence.DeployedKey(cls, repo, executionName),
                ["body"] = DeployEvidence.Deployed(executionName, now, input, verified, deploy, running),
            },
        };
    }

    /// <summary>
    /// Find THIS execution's deployment among the service's deployments, and say whether its roll
    /// can still be judged.
    ///
    /// <para>NOT SIMPLY "THE PRIMARY". When the circuit breaker rolls a failed roll back, ECS makes a
    /// NEW primary deployment for the previous revision — so reading the primary would report the
    /// rollback's state and never see that ours FAILED. Ours is found by the revision it deploys.</para>
    /// </summary>
    /// <returns>
    /// The rollout state to judge, or a problem that means this deploy did not land: no deployment of
    /// the revision exists any more, or a newer deployment has superseded it. A FAILED deployment is
    /// returned as a state, not a problem, because <see cref="DeployVerification.Rollout"/> already
    /// knows what FAILED means.
    /// </returns>
    public static (string? RolloutState, string? Problem) DeploymentStateOf(
        IReadOnlyList<DeploymentSnapshot>? deployments, string taskDefinitionArn)
    {
        var list = deployments ?? Array.Empty<DeploymentSnapshot>();
        var ours = list
            .Where(d => string.Equals(d.TaskDefinitionArn, taskDefinitionArn, StringComparison.Ordinal))
            .OrderByDescending(d => string.Equals(d.Status, "PRIMARY", StringComparison.Ordinal))
            .FirstOrDefault();

        if (ours is null)
            return (null,
                $"the service has no deployment of {taskDefinitionArn}. It was superseded, so this " +
                "execution's revision is not what runs.");

        if (string.Equals(ours.RolloutState, "FAILED", StringComparison.Ordinal))
            return ("FAILED", null);

        if (!string.Equals(ours.Status, "PRIMARY", StringComparison.Ordinal))
        {
            var primary = list.FirstOrDefault(d => string.Equals(d.Status, "PRIMARY", StringComparison.Ordinal));
            return (null,
                $"this execution's deployment of {taskDefinitionArn} is {ours.Status ?? "not primary"}; " +
                $"the primary deployment is {primary?.TaskDefinitionArn ?? "unknown"}. A newer deploy superseded it.");
        }

        return (ours.RolloutState, null);
    }

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>
/// The RecordFailure state: whatever failed, write it down once. The state machine then goes to a
/// Fail state, so an execution that recorded a failure still reports failure.
/// </summary>
public static class RecordFailureStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, string evidenceStore, IEvidenceWriter writer, DateTimeOffset now)
    {
        var key = DeployEvidence.FailureKey(executionName);

        // ALREADY WRITTEN IS SUCCESS. The key is the execution name, so an existing object was written
        // by this same execution — a Lambda retry after a write that landed. Failing here would bury the
        // real failure under an evidence error.
        var written = await writer.PutOnceAsync(evidenceStore, key, DeployEvidence.Failed(executionName, now, state));

        return new JsonObject { ["key"] = key, ["written"] = written };
    }
}

/// <summary>
/// The ECS <c>PRE_SCALE_UP</c> hook (§4.4.1): which images the new revision runs, whether every one
/// verifies against the trust policy, and what to tell ECS.
/// </summary>
public static class SignatureHookStep
{
    public static async Task<(HookStatus Status, string Reason)> RunAsync(
        string eventJson, HookSettings settings, IHookReads aws, INotation notation, NotationLayout layout)
    {
        // EVERYTHING INSIDE ONE CATCH THAT ANSWERS FAILED. ECS would roll back on an exception too,
        // but then the only record of why is a stack trace; this way the log says what was refused.
        try
        {
            var hook = SignatureHook.ParseEvent(eventJson);

            var taskDefinition = await aws.TaskDefinitionOfRevisionAsync(hook.TargetServiceRevisionArn);
            if (taskDefinition is null)
                return (HookStatus.FAILED,
                    $"service revision {hook.TargetServiceRevisionArn} could not be read, so its images are unknown.");

            var images = SignatureHook.ImagesFrom(
                await aws.ContainerImagesAsync(taskDefinition), settings.RegistryScopes);

            // Refusals decide before the verifier is even installed: there is no point laying out
            // Notation to check a tag.
            if (images.Count == 0 || images.Any(i => i.Refusal != null))
                return SignatureHook.Decide(images, Array.Empty<ImageVerification>());

            var policy = SignatureHook.TrustPolicy(settings.TrustedProfiles, settings.RegistryScopes);
            var notReady = await notation.InstallAsync(layout, policy);
            if (notReady != null)
                return (HookStatus.FAILED, $"the verifier is not usable: {notReady}");

            var (username, password) = await aws.RegistryCredentialsAsync();
            var environment = layout.Environment(username, password);

            var results = new List<ImageVerification>();
            foreach (var reference in images.Select(i => i.DigestReference!).Distinct(StringComparer.Ordinal))
            {
                var (exit, stdout, stderr) = await notation.VerifyAsync(layout, reference, environment);
                results.Add(SignatureHook.InterpretNotation(reference, exit, stdout, stderr));
            }

            return SignatureHook.Decide(images, results);
        }
        catch (Exception ex)
        {
            return (HookStatus.FAILED, $"the hook could not reach a verdict: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
