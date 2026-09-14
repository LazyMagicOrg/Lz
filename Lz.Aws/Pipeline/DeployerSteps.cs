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

/// <summary>
/// One of a service's deployments as ECS's service-deployment record keeps it — how it ended, and in ECS's words
/// why — with the task definition its target revision runs (null when that revision could not be read).
/// </summary>
public sealed record ServiceDeploymentRecord(string? TaskDefinitionArn, string? Status, string? StatusReason, DateTime? CreatedAt);

/// <summary>
/// An image in this account's registry, with its scan state. <see cref="ScanStatus"/> is null when no
/// scan of the image exists at all, which is a different thing from a scan that failed.
/// </summary>
public sealed record RegistryImage(string Digest, string? ScanStatus, IReadOnlyDictionary<string, int>? FindingCounts);

/// <summary>A stored object as read: its text, and the object version S3 served — null when S3 named none.</summary>
public sealed record StoredRecord(string Json, string? VersionId);

public interface IRecordStore
{
    /// <summary>The object as read, or null when there is no such object.</summary>
    Task<StoredRecord?> ReadAsync(string bucket, string key);
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

    /// <summary>
    /// The service's recent deployment records. Read only to explain a roll that did not land, never to decide
    /// that one did — see <see cref="VerifyRolloutStep"/>.
    /// </summary>
    Task<IReadOnlyList<ServiceDeploymentRecord>> RecentDeploymentsAsync(string cluster, string service);
}

public interface IEvidenceWriter
{
    /// <summary>Write once. False when the key already existed, which a conditional write refuses.</summary>
    Task<bool> PutOnceAsync(string bucket, string key, string body);
}

/// <summary>
/// What Prepare reads and registers — through the SDK's own task-definition types rather than plain records,
/// because the step copies a whole definition field by field and those types are exactly that shape.
/// </summary>
public interface ITaskDefinitions
{
    /// <summary>The definition with its tags, which come back empty unless they are asked for.</summary>
    Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string taskDefinitionArn);

    /// <summary>Register a revision; the new revision's ARN.</summary>
    Task<string> RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request);
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
/// The Verify state (§4.4): read the record, check where it came from and what it claims, then, by its class — for an
/// image, find it in this account's registry, apply the scan policy, and confirm the target service exists and runs a
/// revision with the target container; for a client bundle (P4 stage C), confirm the bucket is its repository's web app
/// and that the store holds the version and checksum the record names.
/// </summary>
public static class VerifyStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, VerifySettings settings, IRecordStore records, IRegistryImages registry, IServices services,
        ITaskDefinitions definitions, IArtifactObjects artifacts)
    {
        // The record's location only: what the target must name depends on the class, which the record says.
        var location = DeployerInput.RecordFrom(state);

        // BEFORE ANY READ. A record outside the build-record store has no authority, so it is not
        // fetched and then judged — it is never fetched.
        if (!string.Equals(location.Bucket, settings.BuildRecordStore, StringComparison.Ordinal))
            throw new DeployRefused("record.bucket",
                $"the input names a record in '{location.Bucket}', not the build-record store " +
                $"'{settings.BuildRecordStore}'. Records have authority only there.");

        var stored = await records.ReadAsync(location.Bucket, location.Key)
            ?? throw new DeployRefused("record",
                $"there is no build record at s3://{location.Bucket}/{location.Key}.");

        // THE VERSION READ IS THE RECORD'S IDENTITY (§14.3), carried into the evidence: a key can gain a second version,
        // after a delete, and the evidence must say which one this deploy was judged on. A store that serves no version
        // cannot say — S3 names none in an unversioned bucket and "null" for an object written while versioning was off —
        // so that is refused, not recorded as unknown.
        if (string.IsNullOrEmpty(stored.VersionId) || stored.VersionId == "null")
            throw new DeployRefused("record",
                $"s3://{location.Bucket}/{location.Key} was served without an object version, so the evidence could " +
                "not name the record this deploy acted on. The build-record store must keep versioning on.");

        BuildRecord record;
        try
        {
            // With the artifact store, which a bundle record's identity must name exactly (P4 stage A). Without one — an
            // environment with no client target — every bundle record is refused here.
            record = BuildRecordFormat.Parse(stored.Json, settings.ArtifactStore);
        }
        catch (InvalidOperationException ex)
        {
            throw new DeployRefused("record", ex.Message);
        }

        // EVERY CHECK, and every refusal reported — the evidence should show everything that was wrong.
        var refusals = new List<VerifyRefusal>();
        refusals.AddRange(DeployVerification.Provenance(record, location, settings.BuildRecordStore));
        refusals.AddRange(DeployVerification.Verify(record, settings.AsPipeline()).Refusals);

        switch (record.Class)
        {
            case "image":
                break;

            case "client":
                return await ClientAsync(state, settings, artifacts, record, stored, refusals);

            default:
                refusals.Add(new VerifyRefusal("class",
                    $"this deployer deploys class 'image' (DecoupledCd.md P2) and class 'client' (P4 stage C); the record is " +
                    $"'{record.Class}'. Other classes are not modelled here rather than modelled badly."));
                throw new DeployRefused(refusals);
        }

        var input = DeployerInput.From(state);
        refusals.AddRange(DeployVerification.Target(record, input.Target, settings.ImageRepositories));
        if (refusals.Count > 0)
            throw new DeployRefused(refusals);

        // §4.4 step 3: the identity resolves in the LOCAL registry. The build account's copy is not
        // what this account's tasks pull, so it is not what is checked.
        //
        // NOT THERE YET IS WAITED ON, not refused (§14.1 item 2). Only now, with every check above passed, so a
        // record that had no right to name the image never reaches this. When the definition's retries run
        // out, this is the error the execution fails with — the same outcome a refusal had, five minutes later.
        var digest = record.Identity.Digest!;
        var image = await registry.DescribeAsync(input.Target.Repository, digest)
            ?? throw new ImageNotYetReplicated(
                $"{input.Target.Repository}@{digest} is not in this account's registry yet. Either replication " +
                "has not delivered it — it usually arrives within seconds of the record — or the record names an " +
                "image that never replicated here.");

        var (verdict, reason) = DeployVerification.ScanFromStatus(
            image.ScanStatus, image.FindingCounts, settings.ScanBlockOn);

        // ONLY A PASS CONTINUES. Written as the one allowed case rather than the two refused ones, so a
        // verdict added later stops the deploy instead of falling through to it.
        switch (verdict)
        {
            case ScanVerdict.Pass:
                break;
            case ScanVerdict.NotYetAvailable:
                throw new ScanNotYetAvailable(reason);
            default:
                throw new DeployRefused("scan", reason);
        }

        var service = await services.DescribeAsync(input.Target.Cluster, input.Target.Service)
            ?? throw new DeployRefused("target.service",
                $"there is no ACTIVE service '{input.Target.Service}' in cluster '{input.Target.Cluster}'.");

        // THE CONTAINER, BEFORE ANY GATE (DecoupledCd.md §14.3): a target naming a container the running revision does not
        // have is refused here, not by Prepare after a person approved it. Only the name is judged; nothing of the
        // definition — which carries a secret — enters the result.
        var (running, _) = await definitions.DescribeAsync(service.TaskDefinitionArn);
        try
        {
            Lz.Aws.Ops.TaskDefinitionRevision.SingleContainerNamed(running, input.Target.Container);
        }
        catch (InvalidOperationException ex)
        {
            throw new DeployRefused("target.container", ex.Message);
        }

        var shortCommit = ShortCommit(record);

        return new JsonObject
        {
            ["class"] = record.Class,
            ["digest"] = digest,
            ["recordVersionId"] = stored.VersionId,
            ["builtFrom"] = BuiltFrom(record),
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

    /// <summary>
    /// A client bundle (P4 stage C): its repository's web app, and the zip the record names, by version and S3's checksum.
    /// Nothing is downloaded, and nothing is written.
    /// </summary>
    private static async Task<JsonObject> ClientAsync(
        JsonObject state, VerifySettings settings, IArtifactObjects artifacts, BuildRecord record, StoredRecord stored,
        List<VerifyRefusal> refusals)
    {
        var (target, targetRefusals) = DeployVerification.ClientTargetFor(
            record, DeployerInput.BundleBucketFrom(state), settings.ClientTargets ?? Array.Empty<ClientTarget>());
        refusals.AddRange(targetRefusals);
        if (refusals.Count > 0)
            throw new DeployRefused(refusals);

        // ONLY NOW is the artifact looked up: a record with no right to this bucket never has its zip asked about.
        var identity = record.Identity;
        var head = await artifacts.HeadAsync(identity.Bucket!, identity.Key!, identity.VersionId!);
        var artifactRefusals = DeployVerification.BundleArtifact(identity, head, BundleArchive.MaxZipBytes);
        if (artifactRefusals.Count > 0)
            throw new DeployRefused(artifactRefusals);

        var app = target!;
        return new JsonObject
        {
            ["class"] = record.Class,
            ["identity"] = new JsonObject
            {
                ["bucket"] = identity.Bucket,
                ["key"] = identity.Key,
                ["versionId"] = identity.VersionId,
                ["sha256"] = identity.Sha256,
                ["bytes"] = head!.ContentLength,
            },
            ["target"] = ClientTargets.ToJson(app),
            ["recordVersionId"] = stored.VersionId,
            ["builtFrom"] = BuiltFrom(record),
            ["builtAt"] = record.BuiltAt,
            ["workflowRunId"] = record.WorkflowRunId,
            // What an approver reads (§4.5). Identities, not prose about them.
            ["summary"] =
                $"Deploy bundle s3://{identity.Bucket}/{identity.Key} version {identity.VersionId} (sha256 {identity.Sha256}) as " +
                $"web app {app.App} into s3://{app.Bucket}/{app.KeyPrefix}, invalidating {app.InvalidationPath} on " +
                $"{string.Join(", ", app.Distributions)}. Built from {record.BuiltFrom.Repo} {ShortCommit(record)} on the " +
                $"{record.BuiltFrom.Lane} lane, workflow run {record.WorkflowRunId}.",
        };
    }

    private static string ShortCommit(BuildRecord record)
        => record.BuiltFrom.Commit.Length > 12 ? record.BuiltFrom.Commit[..12] : record.BuiltFrom.Commit;

    private static JsonObject BuiltFrom(BuildRecord record)
    {
        var builtFrom = new JsonObject
        {
            ["repo"] = record.BuiltFrom.Repo,
            ["commit"] = record.BuiltFrom.Commit,
            ["lane"] = record.BuiltFrom.Lane,
            ["packages"] = new JsonObject(record.BuiltFrom.Packages
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        };
        // The branch, when the record names one, so the evidence says which line was deployed. Absent from records
        // written before it existed, and left absent rather than invented.
        if (record.BuiltFrom.Ref is { } gitRef)
            builtFrom["ref"] = gitRef;
        return builtFrom;
    }
}

/// <summary>
/// The Prepare state (§4.6): register a revision of the service's CURRENT task definition with one
/// container's image pinned to <c>{registry}/{repository}@{digest}</c>. The only function that writes
/// before Deploy.
///
/// <para>EXTRACTED FROM ITS HANDLER on 2026-09-12 (DecoupledCd.md §14.2): the only step that writes was the
/// only one no test drove, and its return value lands in execution state and from there in kept evidence.</para>
///
/// <para>THE DEFINITION NEVER LEAVES THIS STEP. It carries a plaintext client secret; what goes back into
/// state is the new revision's ARN and the two image references, and nothing copied from the definition.</para>
/// </summary>
public static class PrepareStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string registry, IServices services, ITaskDefinitions definitions)
    {
        var target = DeployerInput.From(state).Target;

        // The digest Verify established — never one from the input, which names only where to deploy.
        var digest = (state["verified"] as JsonObject)?["digest"] is JsonValue v && v.TryGetValue<string>(out var d)
            ? d
            : throw new DeployRefused("verified", "the state has no verified digest; Prepare cannot run before Verify.");

        var image = Lz.Aws.Ops.TaskDefinitionRevision.PinnedImage(registry, target.Repository, digest);

        // The service's CURRENT revision is the base, read now rather than at Verify: in prod an approval
        // can take a day, and a revision cloned from a stale read would undo whatever changed in between.
        var service = await services.DescribeAsync(target.Cluster, target.Service)
            ?? throw new DeployRefused("target.service",
                $"there is no ACTIVE service '{target.Service}' in cluster '{target.Cluster}'.");

        var (definition, tags) = await definitions.DescribeAsync(service.TaskDefinitionArn);

        // NEVER AN OLDER BUILD OVER A NEWER ONE (DecoupledCd.md P2 stage D2). Judged against the revision the service
        // is set to now — which an UpdateService changes at once, so a newer build still rolling counts — and before
        // anything is registered, so a refusal leaves nothing behind.
        var builtAt = (state["verified"] as JsonObject)?["builtAt"] is JsonValue b && b.TryGetValue<string>(out var at)
            ? at
            : throw new DeployRefused("verified", "the state has no verified builtAt; Prepare cannot run before Verify.");
        var (verdict, ordering) = DeployOrdering.Judge(
            builtAt, tags?.FirstOrDefault(t => t.Key == DeployOrdering.BuiltAtTag)?.Value, DeployerInput.AllowsOlderBuild(state));
        if (verdict == DeployOrdering.Verdict.Superseded)
            throw new DeploySuperseded(ordering);

        var container = Lz.Aws.Ops.TaskDefinitionRevision.SingleContainerNamed(definition, target.Container);
        var previousImage = container.Image;
        container.Image = image;

        var registered = await definitions.RegisterAsync(
            Lz.Aws.Ops.TaskDefinitionRevision.RegisterRequestFor(definition, DeployOrdering.TagsFor(tags, builtAt)));

        return new JsonObject
        {
            ["taskDefinitionArn"] = registered,
            ["image"] = image,
            ["previousTaskDefinitionArn"] = service.TaskDefinitionArn,
            ["previousImage"] = previousImage,
            ["ordering"] = ordering,
        };
    }
}

/// <summary>
/// Which of two builds is newer, for Prepare (DecoupledCd.md P2 stage D2).
///
/// <para>THE BUILD TIME TRAVELS ON THE REVISION. Prepare tags every revision it registers with the record's
/// <c>builtAt</c>, and reads that tag off the service's current revision before the next one: executions run
/// independently, so an older build still waiting on its scan could otherwise finish after a newer one and put the
/// older image back. What remains is the second between one execution's Prepare and its Deploy.</para>
///
/// <para>NO TAG IS NOT A REFUSAL. A revision registered outside the deployer — by <c>lz deploytenant</c>, or before this
/// existed — carries none, and there is then nothing to compare. A tag that is not a time IS refused: only Prepare
/// writes it, so a value it cannot read means something else did, and guessing which build is newer is the one
/// thing this exists not to do.</para>
/// </summary>
public static class DeployOrdering
{
    /// <summary>The tag Prepare writes on the revisions it registers.</summary>
    public const string BuiltAtTag = "lz:builtAt";

    public enum Verdict { Proceed, Superseded }

    public static (Verdict Verdict, string Reason) Judge(string recordBuiltAt, string? runningBuiltAt, bool allowOlderBuild)
    {
        var ours = Instant(recordBuiltAt)
            ?? throw new DeployRefused("verified.builtAt",
                $"the record's builtAt is '{recordBuiltAt}', which is not a UTC time; the build's age cannot be judged.");

        if (runningBuiltAt is null)
            return (Verdict.Proceed,
                $"the service's current revision records no build time (it was not registered by the deployer), so this build ({recordBuiltAt}) is not compared.");

        var running = Instant(runningBuiltAt)
            ?? throw new DeployRefused(BuiltAtTag,
                $"the service's current revision is tagged {BuiltAtTag}='{runningBuiltAt}', which is not a time. Only the " +
                "deployer writes that tag; refusing rather than guessing which build is newer.");

        if (running <= ours)
            return (Verdict.Proceed, $"this build ({recordBuiltAt}) is not older than the one the service runs ({runningBuiltAt}).");

        return allowOlderBuild
            ? (Verdict.Proceed,
                $"this build ({recordBuiltAt}) is older than the one the service runs ({runningBuiltAt}), and the input says allowOlderBuild.")
            : (Verdict.Superseded,
                $"the service already runs a newer build ({runningBuiltAt}) than this record's ({recordBuiltAt}); deploying it would " +
                "put the older image back. Nothing was registered. To deploy an older build on purpose, start the execution with " +
                "\"allowOlderBuild\": true.");
    }

    /// <summary>The current revision's tags with <see cref="BuiltAtTag"/> set to this build's time, every other tag kept.</summary>
    public static List<Amazon.ECS.Model.Tag> TagsFor(List<Amazon.ECS.Model.Tag>? current, string recordBuiltAt)
        => (current ?? new List<Amazon.ECS.Model.Tag>())
            .Where(t => t.Key != BuiltAtTag)
            .Append(new Amazon.ECS.Model.Tag { Key = BuiltAtTag, Value = recordBuiltAt })
            .ToList();

    /// <summary>A build time as the records and the bundle marker write it, UTC only; null for anything else.</summary>
    internal static DateTimeOffset? ParseUtc(string value) => Instant(value);

    // UTC only: a time without a zone would be read in whatever zone the function runs in.
    private static DateTimeOffset? Instant(string value)
        => value.EndsWith('Z')
           && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
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
            throw new RolloutNotDeployed(await ExplainAsync(services, input.Target, taskDefinition, problem));

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
                throw new RolloutNotDeployed(await ExplainAsync(services, input.Target, taskDefinition,
                    $"rollout {rolloutState ?? "state unknown"} with {onDigest} of {running.Count} running task(s) " +
                    $"on {digest}. The deploy did not take — the service is running something else."));
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

    /// <summary>
    /// Why this execution's roll did not land: ECS's own words when its deployment record says the deployment was
    /// rolled back or stopped, otherwise what was observed.
    ///
    /// <para><b>ONLY EVER ON THE WAY TO A FAILURE.</b> The record is read after the verdict is already
    /// NotDeployed, so a read that fails — no permission, a throttle — cannot turn a landed roll into a failed
    /// one; it only leaves the observed reason, and says the record could not be read.</para>
    /// </summary>
    private static async Task<string> ExplainAsync(IServices services, DeployTarget target, string taskDefinitionArn, string observed)
    {
        IReadOnlyList<ServiceDeploymentRecord> records;
        try
        {
            records = await services.RecentDeploymentsAsync(target.Cluster, target.Service);
        }
        catch (Exception ex)
        {
            return $"{observed} (ECS's deployment record could not be read: {ex.GetType().Name}.)";
        }

        return NotDeployedReason(records, taskDefinitionArn, observed);
    }

    /// <summary>
    /// THE REASON, as a pure function. The newest record whose target revision runs this execution's task
    /// definition — Prepare registers a new one per execution, so there is one — decides: rolled back or stopped
    /// gives ECS's status and statusReason; anything else, or no such record, keeps <paramref name="observed"/>.
    ///
    /// <para>WHY (DecoupledCd.md §14.2): a deployment ECS's signature hook refuses starts no task and leaves the
    /// service's deployment list within seconds, so <see cref="DeploymentStateOf"/> finds it gone and could only
    /// say "superseded". Measured 2026-09-12: the record said <c>ROLLBACK_SUCCESSFUL</c>, "Service deployment
    /// rolled back because PRE_SCALE_UP lifecycle hook(s) failed. …" — which is what failure evidence should
    /// carry.</para>
    /// </summary>
    public static string NotDeployedReason(IReadOnlyList<ServiceDeploymentRecord>? records, string taskDefinitionArn, string observed)
    {
        var ours = (records ?? Array.Empty<ServiceDeploymentRecord>())
            .Where(r => string.Equals(r.TaskDefinitionArn, taskDefinitionArn, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue)
            .FirstOrDefault();

        return ours?.Status is "ROLLBACK_SUCCESSFUL" or "ROLLBACK_FAILED" or "STOPPED"
            ? $"ECS reports this execution's deployment {ours.Status}: " +
              (string.IsNullOrWhiteSpace(ours.StatusReason) ? "it gave no reason." : ours.StatusReason.Trim())
            : observed;
    }

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>
/// The Record state (§4.7): write this execution's deploy evidence, once.
///
/// <para>A FUNCTION, NOT THE SDK INTEGRATION IT REPLACED. Record was <c>aws-sdk:s3:putObject</c> with
/// <c>IfNoneMatch: *</c> and neither Retry nor Catch (DecoupledCd.md §14.2): a transient S3 fault after a
/// landed roll ended the execution with nothing written anywhere, and a retry would have met its own
/// conditional write — a 412 that an SDK integration can only report as an error, which would have sent a
/// landed deploy to RecordFailure. Here an object already at the key is success, exactly as for failures: the
/// key names this execution, so the object was written by it.</para>
/// </summary>
public static class RecordStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, string evidenceStore, IEvidenceWriter writer)
    {
        var verified = state["verified"] as JsonObject
            ?? throw new DeployRefused("verified", "the state has no Verify result; Record cannot run first.");
        var evidence = (state["rollout"] as JsonObject)?["evidence"] as JsonObject
            ?? throw new DeployRefused("rollout", "the state has no rollout evidence; Record cannot run before VerifyRollout.");

        var cls = Text(verified, "class");
        var repo = verified["builtFrom"] is JsonObject builtFrom ? Text(builtFrom, "repo") : null;
        var body = Text(evidence, "body");
        if (cls is null || repo is null || body is null)
            throw new DeployRefused("state",
                "the state is missing verified.class, verified.builtFrom.repo or rollout.evidence.body.");

        // THE KEY IS RECOMPUTED, not taken from state. VerifyRollout derives it the same way, so a key that
        // disagrees means the state is not what this machine wrote — and evidence is not put where no reader
        // would look for it.
        var key = DeployEvidence.DeployedKey(cls, repo, executionName);
        if (!string.Equals(Text(evidence, "key"), key, StringComparison.Ordinal))
            throw new DeployRefused("rollout.evidence.key",
                $"the state's evidence key is '{Text(evidence, "key")}', not '{key}', which this execution's record implies.");

        var written = await writer.PutOnceAsync(evidenceStore, key, body);
        return new JsonObject { ["key"] = key, ["written"] = written };
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
