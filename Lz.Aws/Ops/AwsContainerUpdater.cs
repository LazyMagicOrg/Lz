using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime.CredentialManagement;
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
    /// <summary>Rolling redeploy requested AND verified healthy (--wait).</summary>
    Verified,
    /// <summary>--dry-run: a redeploy would have been triggered.</summary>
    WouldDeploy,
    /// <summary>No image found for the tag in ECR — run 'lz deploycontainer' first.</summary>
    NoEcrImage,
    /// <summary>Service has no running tasks — run 'lz deploytenant' to bring it up.</summary>
    NoRunningTasks,
    /// <summary>Redeploy was triggered but the rollout failed / rolled back (--wait).</summary>
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
    /// </summary>
    public static ContainerUpdateStrategy DecideStrategy(string? taskDefinitionImage, bool imageChanging)
        => imageChanging && ImagePinPolicy.IsDigestPinned(taskDefinitionImage)
            ? ContainerUpdateStrategy.RegisterNewRevision
            : ContainerUpdateStrategy.ForceRedeploy;
    private readonly string _profile;
    private readonly string _region;
    private readonly AmazonECSClient _ecs;

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
    /// Compare-and-(maybe)-deploy a single tenant service.
    /// </summary>
    public async Task<ContainerUpdateResult> UpdateIfNewerAsync(
        string cluster, string ecsService, string ecrRepo, string tag,
        bool force, bool wait, bool dryRun, CancellationToken ct,
        string? targetDigest = null)
    {
        // 1. The digest to deploy. An explicit --digest is taken as given — that is the
        //    rollback lever, and it necessarily names something a tag no longer points at,
        //    so resolving a tag here would defeat it. Otherwise resolve from the tag.
        var ecrDigest = !string.IsNullOrWhiteSpace(targetDigest)
            ? targetDigest
            : await EcrDeployer.GetImageDigestAsync(_profile, _region, ecrRepo, tag);

        if (string.IsNullOrEmpty(ecrDigest))
            return new(ecsService, UpdateOutcome.NoEcrImage,
                $"no '{tag}' image in ECR repo {ecrRepo} — run 'lz deploycontainer' first");

        // 2. Digest(s) of the image currently running in the service's task(s).
        var running = await GetRunningImageDigestsAsync(cluster, ecsService, ecrRepo, ct);
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
        var currentImage = await GetServiceTaskDefinitionImageAsync(cluster, ecsService, ecrRepo, ct);
        var strategy = DecideStrategy(currentImage, imageChanging: !alreadyCurrent);

        if (strategy == ContainerUpdateStrategy.RegisterNewRevision)
        {
            // The definition names a digest, so forcing would redeploy that same digest and
            // report success having changed nothing. Register a revision naming the target
            // and point the service at it — ONE UpdateService is one deployment, so
            // ForceNewDeployment is deliberately not also set here.
            var newArn = await RegisterRevisionWithImageAsync(
                cluster, ecsService, ecrRepo, ecrDigest, ct);

            await _ecs.UpdateServiceAsync(new UpdateServiceRequest
            {
                Cluster = cluster,
                Service = ecsService,
                TaskDefinition = newArn,
            }, ct);

            if (!wait)
                return new(ecsService, UpdateOutcome.Deployed,
                    $"rolling deploy requested via new revision {ShortArn(newArn)} (→ {Short(ecrDigest)})");
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
                    $"rolling deploy requested (→ {Short(ecrDigest)})");
        }

        // 5. Verify: block until the new task is healthy and the rollout
        //    completes, or the circuit breaker rolls it back.
        var (ok, reason) = await WaitForStableAsync(cluster, ecsService, ct);
        return ok
            ? new(ecsService, UpdateOutcome.Verified, reason)
            : new(ecsService, UpdateOutcome.Failed, reason);
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
    /// </summary>
    private async Task<string?> GetServiceTaskDefinitionImageAsync(
        string cluster, string ecsService, string ecrRepo, CancellationToken ct)
    {
        try
        {
            var svc = await _ecs.DescribeServicesAsync(new DescribeServicesRequest
            {
                Cluster = cluster,
                Services = new List<string> { ecsService },
            }, ct);

            var taskDefArn = svc.Services?.FirstOrDefault()?.TaskDefinition;
            if (string.IsNullOrEmpty(taskDefArn)) return null;

            var td = await _ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
            {
                TaskDefinition = taskDefArn,
            }, ct);

            return td.TaskDefinition?.ContainerDefinitions?
                .FirstOrDefault(c => c.Image != null && c.Image.Contains(ecrRepo, StringComparison.Ordinal))?
                .Image;
        }
        catch
        {
            // Unreadable service or definition: fall back to the historic behaviour.
            return null;
        }
    }

    /// <summary>
    /// Register a NEW revision of the service's current task definition, identical except
    /// that the container pulling from <paramref name="ecrRepo"/> names
    /// <c>{repo}@{digest}</c>. Returns the new revision's ARN.
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
        string cluster, string ecsService, string ecrRepo, string digest, CancellationToken ct)
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

        var target = td.ContainerDefinitions?
            .FirstOrDefault(c => c.Image != null && c.Image.Contains(ecrRepo, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"no container in {ShortArn(currentArn)} pulls from {ecrRepo}");

        // The repository URI without whatever it is currently pinned to, so this works
        // whether the definition names a tag or an existing digest.
        var repoUri = target.Image!;
        var at = repoUri.LastIndexOf('@');
        if (at >= 0) repoUri = repoUri[..at];
        else
        {
            var colon = repoUri.LastIndexOf(':');
            var slash = repoUri.LastIndexOf('/');
            if (colon > slash) repoUri = repoUri[..colon];
        }
        target.Image = $"{repoUri}@{digest}";

        var register = new RegisterTaskDefinitionRequest
        {
            Family = td.Family,
            TaskRoleArn = td.TaskRoleArn,
            ExecutionRoleArn = td.ExecutionRoleArn,
            NetworkMode = td.NetworkMode,
            ContainerDefinitions = td.ContainerDefinitions,
            Volumes = td.Volumes,
            PlacementConstraints = td.PlacementConstraints,
            RequiresCompatibilities = td.RequiresCompatibilities,
            Cpu = td.Cpu,
            Memory = td.Memory,
            PidMode = td.PidMode,
            IpcMode = td.IpcMode,
            ProxyConfiguration = td.ProxyConfiguration,
            EphemeralStorage = td.EphemeralStorage,
            RuntimePlatform = td.RuntimePlatform,
            Tags = described.Tags,
        };

        var registered = await _ecs.RegisterTaskDefinitionAsync(register, ct);
        return registered.TaskDefinition.TaskDefinitionArn;
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
                cluster, ecsService, ecrRepo, CancellationToken.None);

            return running.Count > 0 && running.All(d => d == ecrDigest);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Distinct image digests of the RUNNING tasks' container that pulls from
    /// the given ECR repo. Empty if the service has no running tasks.
    /// </summary>
    private async Task<List<string>> GetRunningImageDigestsAsync(
        string cluster, string ecsService, string ecrRepo, CancellationToken ct)
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
                        && (c.Image?.Contains(ecrRepo) ?? false))
            .Select(c => c.ImageDigest)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Poll the service until exactly one PRIMARY deployment remains in the
    /// COMPLETED rollout state with RunningCount == DesiredCount (success), or a
    /// deployment enters the FAILED state — the circuit breaker rolled back
    /// (failure). Times out after <see cref="WaitTimeout"/>.
    /// </summary>
    private async Task<(bool ok, string reason)> WaitForStableAsync(
        string cluster, string ecsService, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(WaitTimeout);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var resp = await _ecs.DescribeServicesAsync(new DescribeServicesRequest
            {
                Cluster = cluster,
                Services = new List<string> { ecsService },
            }, ct);

            var svc = resp.Services.FirstOrDefault();
            if (svc != null)
            {
                // Circuit breaker tripped on any deployment → rolled back.
                if (svc.Deployments.Any(d => d.RolloutState == DeploymentRolloutState.FAILED))
                    return (false,
                        "rollout FAILED — deployment circuit breaker rolled back to the previous image");

                var primary = svc.Deployments.FirstOrDefault(d => d.Status == "PRIMARY");
                if (svc.Deployments.Count == 1
                    && primary != null
                    && primary.RolloutState == DeploymentRolloutState.COMPLETED
                    && primary.RunningCount == primary.DesiredCount)
                {
                    return (true, "new task healthy; rollout COMPLETED");
                }
            }

            await Task.Delay(PollInterval, ct);
        }

        return (false,
            $"timed out after {WaitTimeout.TotalMinutes:0} min waiting for the rollout to stabilize " +
            "(deploy may still be in progress — check 'lz status')");
    }

    private static string Short(string digest) =>
        digest.StartsWith("sha256:", StringComparison.Ordinal)
            ? digest.Substring(7, Math.Min(12, digest.Length - 7))
            : digest[..Math.Min(12, digest.Length)];

    private static AmazonECSClient CreateEcsClient(string region, string profile)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"AWS profile '{profile}' not found.");

        return new AmazonECSClient(credentials, Amazon.RegionEndpoint.GetBySystemName(region));
    }
}
