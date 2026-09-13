using Amazon.ECS;
using Amazon.ECS.Model;
using Lz.Aws.Docker;
using Task = System.Threading.Tasks.Task;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Compute;

namespace Lz.Aws.Ops;

/// <summary>
/// Outcome of a single service update attempt.
/// </summary>
public enum UpdateOutcome
{
    /// <summary>Running image already matches the latest ECR image; nothing done.</summary>
    UpToDate,
    /// <summary>Rolling redeploy requested (fire-and-forget; not verified).</summary>
    Deployed,
    /// <summary>Rolling redeploy requested AND verified (--wait): ECS reports the deployment this command
    /// started SUCCESSFUL, and every running task runs the digest.</summary>
    Verified,
    /// <summary>--dry-run: a redeploy would have been triggered.</summary>
    WouldDeploy,
    /// <summary>No image found for the tag in ECR — run 'lz deploycontainer' first.</summary>
    NoEcrImage,
    /// <summary>Service has no running tasks — run 'lz deploytenant' to bring it up.</summary>
    NoRunningTasks,
    /// <summary>Redeploy was triggered but did not land (--wait): rolled back — by the circuit breaker, an
    /// alarm or a lifecycle hook, with ECS's reason in the detail — stopped, finished running another
    /// digest, or timed out.</summary>
    Failed,
}

public record ContainerUpdateResult(string Service, UpdateOutcome Outcome, string Detail);

/// <summary>
/// Performs a zero-downtime container refresh for a tenant ECS service:
/// compares the digest of the running task(s) against the latest image in ECR
/// and, if they differ (or --force), issues an <c>UpdateService</c> with
/// <c>ForceNewDeployment=true</c> while leaving <c>DesiredCount</c> untouched.
///
/// Because the service is configured for a rolling deploy
/// (DeploymentMaximumPercent=200 / MinimumHealthyPercent=100, with a deployment
/// circuit breaker), ECS starts a new task, waits for it to pass the ALB health
/// check, then drains the old one — no scale-to-0 window, no downtime. This is
/// the fast path to run after 'lz deploycontainer', in place of the heavier
/// 'lz deploytenant' (which scales the service to 0 during the Pulumi 'up').
///
/// The task definition may name the image EITHER WAY, and the two need different
/// handling — see <see cref="DecideStrategy"/>. A tag-pinned definition re-pulls on a
/// forced deployment, which is what this class originally assumed unconditionally. A
/// DIGEST-pinned definition cannot: forcing it redeploys the same immutable digest, so
/// changing what runs requires registering a new revision. Getting that branch wrong is
/// not a loud failure — the rollout completes, the wait succeeds, and the command reports
/// "verified" having deployed nothing, then never converges because the next run re-observes
/// the same difference.
///
/// The "what's actually running" digest is read from the running tasks
/// (containers[].imageDigest), which works for both forms.
/// </summary>
public class AwsContainerUpdater
{
    /// <summary>How to make a service run a different image.</summary>
    public enum ContainerUpdateStrategy
    {
        /// <summary>Force a rolling redeploy; the definition's tag re-resolves on pull.</summary>
        ForceRedeploy,

        /// <summary>Register a revision naming the new digest, then point the service at it.</summary>
        RegisterNewRevision,
    }

    /// <summary>
    /// THE BRANCH, as a pure function so it can be tested without AWS and pinned by
    /// mutation. Registering a revision is required exactly when the image must CHANGE and
    /// the current definition names a digest — in every other case a forced redeploy is
    /// both sufficient and cheaper.
    ///
    /// <para>Note the second half matters as much as the first: when nothing is changing
    /// (an explicit <c>--force</c> on an up-to-date service) a forced redeploy is the only
    /// thing that does anything at all, so it must NOT be turned into a pointless new
    /// revision.</para>
    ///
    /// <para><b>A CHANGE OF REPOSITORY NEEDS A REVISION TOO</b>, whatever the definition names: a
    /// forced redeploy re-pulls the definition's own reference, never an image from another
    /// repository. Only the pipeline moves an image between repositories, so without the block
    /// <paramref name="repositoryChanging"/> is always false and this is the historic decision.</para>
    /// </summary>
    public static ContainerUpdateStrategy DecideStrategy(
        string? taskDefinitionImage, bool imageChanging, bool repositoryChanging = false)
        => imageChanging && (ImagePinPolicy.IsDigestPinned(taskDefinitionImage) || repositoryChanging)
            ? ContainerUpdateStrategy.RegisterNewRevision
            : ContainerUpdateStrategy.ForceRedeploy;

    /// <summary>
    /// The image a new revision names. <b>Without the pipeline, exactly the historic
    /// <see cref="TaskDefinitionRevision.RepinImage"/></b>: the container's current repository, re-pinned.
    /// Under it, built from parts, because the digest may be in a DIFFERENT repository from the one the
    /// container names now — re-pinning the current one would name a digest that repository does not
    /// have, which ECS discovers only when the new tasks fail to start.
    /// </summary>
    public static string ImageToRegister(string currentImage, ServiceImageSources sources, string repository, string digest)
        => sources.PipelineSource is { } pipeline
            ? TaskDefinitionRevision.PinnedImage(pipeline.RegistryHost, repository, digest)
            : TaskDefinitionRevision.RepinImage(currentImage, digest);

    private readonly string _profile;
    private readonly string _region;
    private readonly AmazonECSClient _ecs;

    // Only the pipeline's --digest check reads ECR through the SDK, so the client is made on first use:
    // a system without the block never creates one.
    private Amazon.ECR.AmazonECRClient? _ecr;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public AwsContainerUpdater(string profile, string region)
    {
        _profile = profile;
        _region = region;
        _ecs = CreateEcsClient(region, profile);
    }

    /// <summary>
    /// Resolve the ACTIVE ECS cluster name from a list of candidates. The cluster
    /// naming convention differs by topology — the legacy <c>Ecs</c> platform uses
    /// <c>{sk}-cluster</c> while the <c>Fargate</c> family (ecs-fargate-*,
    /// lambda-*) uses <c>{sk}-{env}-cluster</c> — so the caller passes both and we
    /// pick whichever actually exists. A single <c>DescribeClusters</c> call:
    /// non-existent names come back under Failures (no exception), so the existing
    /// ACTIVE cluster is the one to use. Returns null if none of the candidates exist.
    /// </summary>
    public async Task<string?> ResolveClusterAsync(IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return null;
        var resp = await _ecs.DescribeClustersAsync(
            new DescribeClustersRequest { Clusters = candidates.ToList() }, ct);
        // Preserve candidate order (caller lists the preferred convention first).
        foreach (var name in candidates)
        {
            var match = resp.Clusters.FirstOrDefault(
                c => string.Equals(c.ClusterName, name, StringComparison.Ordinal)
                     && c.Status == "ACTIVE");
            if (match != null) return match.ClusterName;
        }
        return null;
    }

    /// <summary>
    /// Compare-and-(maybe)-deploy a single tenant service, knowing only its repository — the historic
    /// signature, kept for callers outside Lz. It is the no-pipeline case exactly.
    /// </summary>
    public Task<ContainerUpdateResult> UpdateIfNewerAsync(
        string cluster, string ecsService, string ecrRepo, string tag,
        bool force, bool wait, bool dryRun, CancellationToken ct,
        string? targetDigest = null)
        => UpdateIfNewerAsync(cluster, ecsService, new ServiceImageSources("", ecrRepo, null),
            tag, force, wait, dryRun, ct, targetDigest);

    /// <summary>
    /// Compare-and-(maybe)-deploy a single tenant service.
    /// </summary>
    public async Task<ContainerUpdateResult> UpdateIfNewerAsync(
        string cluster, string ecsService, ServiceImageSources sources, string tag,
        bool force, bool wait, bool dryRun, CancellationToken ct,
        string? targetDigest = null)
    {
        var ecrRepo = sources.WorkstationRepository;

        // 1. The digest to deploy, and the repository it is deployed from. An explicit --digest is
        //    taken as given — that is the rollback lever, and it necessarily names something a tag no
        //    longer points at, so resolving a tag here would defeat it. Otherwise resolve from the tag.
        //    Under the pipeline a --digest may be a pipeline image, so its repository is looked up.
        var repository = ecrRepo;
        string? ecrDigest;
        if (!string.IsNullOrWhiteSpace(targetDigest))
        {
            ecrDigest = targetDigest;
            if (sources.PipelineSource is { } pipeline)
            {
                var (found, refusal) = ImagePinPolicy.IsSha256Digest(targetDigest)
                    ? ImagePinPolicy.RepositoryForDigest(targetDigest, sources,
                        await DigestPresenceAsync(ecrRepo, targetDigest, ct),
                        await DigestPresenceAsync(pipeline.Repository, targetDigest, ct))
                    : ImagePinPolicy.RepositoryForDigest(targetDigest, sources, DigestPresence.Absent, DigestPresence.Absent);
                if (refusal != null)
                    return new(ecsService, UpdateOutcome.NoEcrImage, refusal);
                repository = found!;
            }
        }
        else
        {
            ecrDigest = await EcrDeployer.GetImageDigestAsync(_profile, _region, ecrRepo, tag);
        }

        if (string.IsNullOrEmpty(ecrDigest))
            return new(ecsService, UpdateOutcome.NoEcrImage,
                $"no '{tag}' image in ECR repo {ecrRepo} — run 'lz deploycontainer' first");

        // 2. Digest(s) of the image currently running in the service's task(s).
        var running = await GetRunningImageDigestsAsync(cluster, ecsService, sources, ct);
        if (running.Count == 0)
            return new(ecsService, UpdateOutcome.NoRunningTasks,
                "no running tasks — run 'lz deploytenant' to bring the service up first");

        // 3. Decide.
        var alreadyCurrent = running.All(d => d == ecrDigest);
        if (alreadyCurrent && !force)
            return new(ecsService, UpdateOutcome.UpToDate,
                $"running {Short(ecrDigest)} == ECR {tag}");

        if (dryRun)
            return new(ecsService, UpdateOutcome.WouldDeploy,
                force
                    ? $"would force-deploy (running {Short(running[0])}, ECR {Short(ecrDigest)})"
                    : $"would deploy: running {Short(running[0])} != ECR {Short(ecrDigest)}");

        // 4. Deploy. DesiredCount is intentionally omitted throughout so ECS keeps the
        //    current count and rolls the task — no downtime either way.
        //
        //    The newest deployment is read FIRST, so the wait can tell the deployment this command starts
        //    from every earlier one (see WaitForDeploymentAsync). Only when waiting: --no-wait reads nothing.
        var baseline = wait ? (await NewestServiceDeploymentAsync(cluster, ecsService, ct))?.Arn : null;
        var currentImage = await GetServiceTaskDefinitionImageAsync(cluster, ecsService, sources, ct);
        var repositoryChanging = sources.PipelineSource != null
                                 && !string.Equals(ImagePinPolicy.RepositoryOf(currentImage), repository, StringComparison.Ordinal);
        var strategy = DecideStrategy(currentImage, imageChanging: !alreadyCurrent, repositoryChanging);

        if (strategy == ContainerUpdateStrategy.RegisterNewRevision)
        {
            // The definition names a digest, so forcing would redeploy that same digest and
            // report success having changed nothing. Register a revision naming the target
            // and point the service at it — ONE UpdateService is one deployment, so
            // ForceNewDeployment is deliberately not also set here.
            var newArn = await RegisterRevisionWithImageAsync(
                cluster, ecsService, sources, repository, ecrDigest, ct);

            await _ecs.UpdateServiceAsync(new UpdateServiceRequest
            {
                Cluster = cluster,
                Service = ecsService,
                TaskDefinition = newArn,
            }, ct);

            if (!wait)
                return new(ecsService, UpdateOutcome.Deployed,
                    $"rolling deploy requested via new revision {ShortArn(newArn)} (→ {Target(sources, repository, ecrDigest)})");
        }
        else
        {
            await _ecs.UpdateServiceAsync(new UpdateServiceRequest
            {
                Cluster = cluster,
                Service = ecsService,
                ForceNewDeployment = true,
            }, ct);

            if (!wait)
                return new(ecsService, UpdateOutcome.Deployed,
                    $"rolling deploy requested (→ {Target(sources, repository, ecrDigest)})");
        }

        // 5. Verify: follow the deployment this command started until ECS finishes it, and require every
        //    running task to be on the digest — or report why ECS rolled it back.
        var (ok, reason) = await WaitForDeploymentAsync(
            baseline, ecrDigest,
            token => NewestServiceDeploymentAsync(cluster, ecsService, token),
            async token => await GetRunningImageDigestsAsync(cluster, ecsService, sources, token),
            WaitTimeout, PollInterval, ct);
        return ok
            ? new(ecsService, UpdateOutcome.Verified, reason)
            : new(ecsService, UpdateOutcome.Failed, reason);
    }

    /// <summary>One look at a service's newest deployment, as ECS's service-deployment record has it.</summary>
    public sealed record DeploymentLook(string Arn, string? Status, string? StatusReason);

    /// <summary>What one look at the deployment this command started says.</summary>
    public enum RolloutWait
    {
        /// <summary>Not finished, or finished while the previous task still drains: look again.</summary>
        Waiting,

        /// <summary>ECS finished the deployment and every running task runs the digest.</summary>
        Landed,

        /// <summary>It will not land: ECS rolled it back or stopped it, or finished it running something else.</summary>
        NotLanded,
    }

    /// <summary>
    /// THE VERDICT, as a pure function: whether the deployment this command started landed, from its
    /// service-deployment status and the digests the service's running tasks report.
    ///
    /// <para><b>A COMPLETED DEPLOYMENT IS NOT PROOF.</b> The wait this replaced succeeded when the service had
    /// one PRIMARY deployment in COMPLETED, and failed only if a poll caught a deployment in FAILED. It never
    /// asked whether that PRIMARY was the deployment it had started. Under the ECS signature hook
    /// (DecoupledCd.md §4.4.1), measured 2026-09-12 with `lz updatecontainer --digest` onto an unsigned image:
    /// the deployment went ROLLBACK_SUCCESSFUL under five seconds after it began, and no task started. ECS
    /// rolled back by making the previous deployment PRIMARY again, and it was COMPLETED seventeen seconds
    /// later. Read inside those seventeen seconds, the service listed that deployment alone — the refused one
    /// was already gone. So a refusal leaves a FAILED state that a 15-second poll can miss, followed by exactly
    /// the completed single PRIMARY that the old wait called "verified". Here ECS's status for the
    /// deployment this command started names the outcome, and even SUCCESSFUL counts only once the running
    /// tasks agree.</para>
    /// </summary>
    /// <param name="status">The status of the deployment this command started, or null while ECS has not
    /// recorded it yet.</param>
    /// <param name="statusReason">ECS's own reason, e.g. "Service deployment rolled back because PRE_SCALE_UP
    /// lifecycle hook(s) failed…".</param>
    /// <param name="runningDigests">Distinct digests of the service container in RUNNING tasks. Read only
    /// once the status is SUCCESSFUL; an empty list otherwise.</param>
    public static (RolloutWait Verdict, string Reason) JudgeDeployment(
        string? status, string? statusReason, IReadOnlyList<string> runningDigests, string targetDigest)
    {
        switch (status)
        {
            case null:
                return (RolloutWait.Waiting, "ECS has not recorded the deployment yet");

            case "SUCCESSFUL":
                if (runningDigests.Count > 0 && runningDigests.All(d => string.Equals(d, targetDigest, StringComparison.Ordinal)))
                    return (RolloutWait.Landed, $"deployment SUCCESSFUL; every running task runs {Short(targetDigest)}");

                // Finished and NOTHING runs the digest: the service is healthy and running the wrong thing.
                if (runningDigests.Count > 0 && !runningDigests.Contains(targetDigest, StringComparer.Ordinal))
                    return (RolloutWait.NotLanded,
                        $"ECS reports the deployment SUCCESSFUL, but no running task runs {Short(targetDigest)} " +
                        $"(running {string.Join(", ", runningDigests.Select(Short))})");

                // Old and new side by side, or none running yet: the previous task is still draining.
                return (RolloutWait.Waiting, "deployment SUCCESSFUL; the previous task is still draining");

            case "ROLLBACK_SUCCESSFUL":
            case "ROLLBACK_FAILED":
            case "STOPPED":
                return (RolloutWait.NotLanded,
                    $"deployment {status}: {(string.IsNullOrWhiteSpace(statusReason) ? "ECS gave no reason" : statusReason.Trim())}");

            // PENDING, IN_PROGRESS, and the three on the way to a rollback or stop — waited out, so the
            // verdict carries the final status (a ROLLBACK_FAILED leaves the service in a different state
            // from a ROLLBACK_SUCCESSFUL) — and any status ECS adds later, which the timeout bounds.
            default:
                return (RolloutWait.Waiting, $"deployment {status}");
        }
    }

    /// <summary>
    /// Follow the deployment an <c>UpdateService</c> started until <see cref="JudgeDeployment"/> gives a
    /// verdict, or the timeout does.
    ///
    /// <para>OURS IS THE NEWEST DEPLOYMENT THAT IS NOT <paramref name="baselineArn"/>, the newest one read
    /// before the change. That needs no clock: a record ECS has not written yet is still the baseline, so it
    /// is waited for. ECS lists service deployments newest first, measured with <c>--max-results 3</c> on a
    /// service with more than three.</para>
    ///
    /// <para>Looks at least once, whatever the timeout. Public and static, with its AWS reads passed in, so the
    /// loop itself is tested — the sequence ECS produced for a refused deploy included.</para>
    /// </summary>
    public static async Task<(bool Landed, string Reason)> WaitForDeploymentAsync(
        string? baselineArn,
        string targetDigest,
        Func<CancellationToken, Task<DeploymentLook?>> newestDeployment,
        Func<CancellationToken, Task<IReadOnlyList<string>>> runningDigests,
        TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var last = "ECS has not recorded the deployment yet";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var newest = await newestDeployment(ct);
            var ours = newest != null && !string.Equals(newest.Arn, baselineArn, StringComparison.Ordinal) ? newest : null;

            var running = string.Equals(ours?.Status, "SUCCESSFUL", StringComparison.Ordinal)
                ? await runningDigests(ct)
                : Array.Empty<string>();

            var (verdict, reason) = JudgeDeployment(ours?.Status, ours?.StatusReason, running, targetDigest);
            switch (verdict)
            {
                case RolloutWait.Landed: return (true, reason);
                case RolloutWait.NotLanded: return (false, reason);
            }

            last = reason;
            if (DateTime.UtcNow >= deadline)
                return (false,
                    $"timed out after {timeout.TotalMinutes:0} min waiting for the rollout ({last}) — the deploy " +
                    "may still be in progress; check 'lz status'");

            await Task.Delay(pollInterval, ct);
        }
    }

    /// <summary>Short "family:revision" from a task-definition ARN, for log lines.</summary>
    private static string ShortArn(string arn)
    {
        var slash = arn.LastIndexOf('/');
        return slash >= 0 ? arn[(slash + 1)..] : arn;
    }

    /// <summary>
    /// The image string the SERVICE'S CURRENT task-definition revision names for the
    /// container that pulls from <paramref name="ecrRepo"/>, or null if it cannot be read.
    /// This is what decides the branch — not the running task's digest, which is a digest
    /// either way and so cannot tell a pinned definition from a tag-pinned one.
    ///
    /// <para>Swallows read failures into null ON PURPOSE — for <c>updatecontainer</c> an unknown
    /// answer must fall back to the historic force-deploy (see
    /// <see cref="DecideStrategy"/>). The tenant-deploy path must NOT inherit that: it uses
    /// <see cref="ReadServiceImageAsync"/>, which keeps failure distinct from absence.</para>
    /// </summary>
    private async Task<string?> GetServiceTaskDefinitionImageAsync(
        string cluster, string ecsService, ServiceImageSources sources, CancellationToken ct)
    {
        try
        {
            var (_, containers) = await ReadServiceTaskDefinitionContainersAsync(cluster, ecsService, ct);
            return ImagePinPolicy.SelectContainerImage(containers, sources);
        }
        catch
        {
            // Unreadable service or definition: fall back to the historic behaviour.
            return null;
        }
    }

    /// <summary>
    /// The throwing core behind both service-image reads. Genuine absence never throws —
    /// DescribeServices reports a missing service under Failures with an empty list — so an
    /// exception out of here is always a real read failure, never "no service".
    ///
    /// <para>Only an ACTIVE service counts. DescribeServices still returns a service that
    /// <c>destroytenant</c> left DRAINING or INACTIVE, and reading ITS definition would make a
    /// recreated service inherit whatever the torn-down one last ran.</para>
    /// </summary>
    private async Task<(bool ServiceActive, IReadOnlyList<DefinedContainer> Containers)> ReadServiceTaskDefinitionContainersAsync(
        string cluster, string ecsService, CancellationToken ct)
    {
        var svc = await _ecs.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = cluster,
            Services = new List<string> { ecsService },
        }, ct);

        var service = svc.Services?.FirstOrDefault(x => x.Status == "ACTIVE");
        var taskDefArn = service?.TaskDefinition;
        if (string.IsNullOrEmpty(taskDefArn)) return (false, Array.Empty<DefinedContainer>());

        var td = await _ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = taskDefArn,
        }, ct);

        // SDK v4: a collection with no members is null.
        var containers = (td.TaskDefinition?.ContainerDefinitions ?? new List<ContainerDefinition>())
            .Select(c => new DefinedContainer(c.Name, c.Image))
            .ToList();
        return (true, containers);
    }

    /// <summary>
    /// Register a NEW revision of the service's current task definition, identical except
    /// that the service's container names <paramref name="repository"/>@<paramref name="digest"/>
    /// (<see cref="ImageToRegister"/>). Returns the new revision's ARN.
    ///
    /// <para><b>Every register-able field must be copied deliberately.</b> RegisterTaskDefinition
    /// does not inherit from the previous revision — anything omitted is silently dropped, so
    /// a missing field here becomes a task that starts without its role, its volumes or its
    /// platform and fails in a way that looks unrelated. Tags need the explicit
    /// <c>Include</c> on the describe or they come back empty and would be lost.</para>
    ///
    /// <para>The copied container definitions carry environment values including a plaintext
    /// client secret, so this must never be logged, serialized to a temp file, or echoed.</para>
    /// </summary>
    private async Task<string> RegisterRevisionWithImageAsync(
        string cluster, string ecsService, ServiceImageSources sources, string repository, string digest,
        CancellationToken ct)
    {
        var svc = await _ecs.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = cluster,
            Services = new List<string> { ecsService },
        }, ct);

        var currentArn = svc.Services?.FirstOrDefault()?.TaskDefinition
            ?? throw new InvalidOperationException(
                $"cannot read the current task definition for {ecsService} in {cluster}");

        var described = await _ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = currentArn,
            Include = new List<string> { "TAGS" },
        }, ct);

        var td = described.TaskDefinition;

        // WITHOUT THE PIPELINE, the historic selection: the container whose image mentions the
        // repository. Under it, by name — after a pipeline deploy no image mentions the workstation's.
        var target = sources.PipelineSource is null
            ? td.ContainerDefinitions?
                  .FirstOrDefault(c => c.Image != null && c.Image.Contains(sources.WorkstationRepository, StringComparison.Ordinal))
              ?? throw new InvalidOperationException(
                  $"no container in {ShortArn(currentArn)} pulls from {sources.WorkstationRepository}")
            : TaskDefinitionRevision.SingleContainerNamed(td, sources.ContainerName);

        // Without the pipeline: the repository URI without whatever it is currently pinned to, so this
        // works whether the definition names a tag or an existing digest.
        target.Image = ImageToRegister(target.Image!, sources, repository, digest);

        // THE FIELD COPY IS SHARED with the decoupled-CD deployer's Prepare function — see
        // TaskDefinitionRevision for why it must not exist twice.
        var register = TaskDefinitionRevision.RegisterRequestFor(td, described.Tags);

        var registered = await _ecs.RegisterTaskDefinitionAsync(register, ct);
        return registered.TaskDefinition.TaskDefinitionArn;
    }

    /// <summary>
    /// What image the service's current task definition names for the service's container
    /// (<see cref="ImagePinPolicy.ClassifyServiceContainers"/>), as a <see cref="ServiceImageRead"/>: the digest when
    /// the definition is pinned, <see cref="ServiceImageState.NotDigestPinned"/> for a
    /// pre-pinning tag-form revision, <see cref="ServiceImageState.NoService"/> when no ACTIVE
    /// cluster or service exists — and <see cref="ServiceImageState.Unreadable"/> when the read
    /// itself failed.
    ///
    /// <para><b>A read failure is NOT "no service".</b> Both absence shapes are reported by the
    /// API without an exception (a missing cluster or service comes back under Failures), so
    /// the catch below only ever sees real errors — throttling, AccessDenied, an expired SSO
    /// session, a missing profile. Those are returned as Unreadable, which
    /// <see cref="ImagePinPolicy.ChooseDigest"/> refuses; collapsing them to null would send a
    /// tenant deploy to ECR <c>:latest</c> and silently undo a rollback.</para>
    ///
    /// <para>Used by <c>SystemDeployment.ResolveImageDigestsAsync</c> so that Pulumi declares
    /// what the service already runs rather than what <c>:latest</c> points at.</para>
    /// </summary>
    public static async Task<ServiceImageRead> ReadServiceImageAsync(
        string profile, string region, IReadOnlyList<string> clusterCandidates,
        string ecsService, ServiceImageSources sources)
    {
        try
        {
            var updater = new AwsContainerUpdater(profile, region);
            var cluster = await updater.ResolveClusterAsync(clusterCandidates, CancellationToken.None);
            if (cluster is null) return ServiceImageRead.NoService;

            var (active, containers) = await updater.ReadServiceTaskDefinitionContainersAsync(
                cluster, ecsService, CancellationToken.None);
            return ImagePinPolicy.ClassifyServiceContainers(active, containers, sources);
        }
        catch (Exception ex)
        {
            return ServiceImageRead.Unreadable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the service's RUNNING tasks already carry the digest ECR serves for
    /// <paramref name="tag"/>. Used by the post-deploy action to skip an unnecessary
    /// force-new-deployment.
    ///
    /// <para>Returns FALSE whenever it cannot establish agreement — no ECR access, absent
    /// tag, unreadable service, no running tasks. The caller treats false as "go ahead and
    /// force", so an unknown answer preserves the historic behaviour rather than silently
    /// skipping a deploy that was needed.</para>
    /// </summary>
    public static async Task<bool> RunningMatchesRegistryAsync(
        string profile, string region, string cluster, string ecsService, string ecrRepo, string tag)
    {
        try
        {
            var ecrDigest = await EcrDeployer.GetImageDigestAsync(profile, region, ecrRepo, tag);
            if (string.IsNullOrEmpty(ecrDigest)) return false;

            var updater = new AwsContainerUpdater(profile, region);
            var running = await updater.GetRunningImageDigestsAsync(
                cluster, ecsService, new ServiceImageSources("", ecrRepo, null), CancellationToken.None);

            return running.Count > 0 && running.All(d => d == ecrDigest);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Distinct image digests of the RUNNING tasks' container — the service's, by
    /// <see cref="ImagePinPolicy.IsTheServicesContainer"/>. Empty if the service has no running tasks.
    /// </summary>
    private async Task<List<string>> GetRunningImageDigestsAsync(
        string cluster, string ecsService, ServiceImageSources sources, CancellationToken ct)
    {
        var list = await _ecs.ListTasksAsync(new ListTasksRequest
        {
            Cluster = cluster,
            ServiceName = ecsService,
            DesiredStatus = DesiredStatus.RUNNING,
        }, ct);

        if (list.TaskArns == null || list.TaskArns.Count == 0)
            return new();

        var desc = await _ecs.DescribeTasksAsync(new DescribeTasksRequest
        {
            Cluster = cluster,
            Tasks = list.TaskArns,
        }, ct);

        return desc.Tasks
            .SelectMany(t => t.Containers)
            .Where(c => !string.IsNullOrEmpty(c.ImageDigest)
                        && ImagePinPolicy.IsTheServicesContainer(c.Name, c.Image, sources))
            .Select(c => c.ImageDigest)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The service's newest deployment record, or null when it has none. The first page is enough: ECS lists
    /// service deployments newest first. The newest by <c>CreatedAt</c> is still picked, so the verdict never
    /// rests on the order alone.
    /// </summary>
    private async Task<DeploymentLook?> NewestServiceDeploymentAsync(string cluster, string ecsService, CancellationToken ct)
    {
        var response = await _ecs.ListServiceDeploymentsAsync(new ListServiceDeploymentsRequest
        {
            Cluster = cluster,
            Service = ecsService,
            MaxResults = 20,
        }, ct);

        // SDK v4: a collection with no members is null.
        var newest = response.ServiceDeployments?
            .OrderByDescending(d => d.CreatedAt ?? DateTime.MinValue)
            .FirstOrDefault();

        return newest is null
            ? null
            : new DeploymentLook(newest.ServiceDeploymentArn, newest.Status?.Value, newest.StatusReason);
    }

    /// <summary>
    /// Whether <paramref name="repository"/> holds <paramref name="digest"/>, as a tri-state: a failed read
    /// is never taken as absent.
    /// </summary>
    private async Task<DigestPresence> DigestPresenceAsync(string repository, string digest, CancellationToken ct)
    {
        try
        {
            _ecr ??= CreateEcrClient(_region, _profile);
            var found = await _ecr.DescribeImagesAsync(new Amazon.ECR.Model.DescribeImagesRequest
            {
                RepositoryName = repository,
                ImageIds = new List<Amazon.ECR.Model.ImageIdentifier> { new() { ImageDigest = digest } },
            }, ct);

            // SDK v4: a collection with no members is null.
            return found.ImageDetails?.Any(d => string.Equals(d.ImageDigest, digest, StringComparison.Ordinal)) == true
                ? DigestPresence.Present
                : DigestPresence.Absent;
        }
        catch (Amazon.ECR.Model.ImageNotFoundException)
        {
            return DigestPresence.Absent;
        }
        catch (Amazon.ECR.Model.RepositoryNotFoundException)
        {
            return DigestPresence.Absent;
        }
        catch (Exception)
        {
            return DigestPresence.Unreadable;
        }
    }

    /// <summary>What a log line says was deployed: the digest, and under the pipeline its repository too.</summary>
    private static string Target(ServiceImageSources sources, string repository, string digest)
        => sources.PipelineSource is null ? Short(digest) : $"{repository}@{Short(digest)}";

    private static string Short(string digest) =>
        digest.StartsWith("sha256:", StringComparison.Ordinal)
            ? digest.Substring(7, Math.Min(12, digest.Length - 7))
            : digest[..Math.Min(12, digest.Length)];

    private static Amazon.ECR.AmazonECRClient CreateEcrClient(string region, string profile)
    {
        var credentials = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        return credentials != null
            ? new Amazon.ECR.AmazonECRClient(credentials, endpoint)
            : new Amazon.ECR.AmazonECRClient(endpoint);
    }

    private static AmazonECSClient CreateEcsClient(string region, string profile)
    {
        // ResolveOrThrow keeps the old refusal for a NAMED profile that will not resolve, and
        // returns null for an EMPTY one — the ambient case, where the client resolves its own.
        var credentials = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        return credentials != null
            ? new AmazonECSClient(credentials, endpoint)
            : new AmazonECSClient(endpoint);
    }
}
