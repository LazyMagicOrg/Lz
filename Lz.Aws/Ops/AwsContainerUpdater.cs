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
/// The task definition pins the image by tag (e.g. ':latest'), so a forced
/// deployment re-pulls the tag without needing a new task-def revision. The
/// "what's actually running" digest is read from the running tasks
/// (containers[].imageDigest), since the tag-pinned task def can't reveal it.
/// </summary>
public class AwsContainerUpdater
{
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
        bool force, bool wait, bool dryRun, CancellationToken ct)
    {
        // 1. Latest digest available in ECR for the tag.
        var ecrDigest = await EcrDeployer.GetImageDigestAsync(_profile, _region, ecrRepo, tag);
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

        // 4. Force a rolling redeploy. DesiredCount is intentionally omitted so
        //    ECS keeps the current count and rolls the task — no downtime.
        await _ecs.UpdateServiceAsync(new UpdateServiceRequest
        {
            Cluster = cluster,
            Service = ecsService,
            ForceNewDeployment = true,
        }, ct);

        if (!wait)
            return new(ecsService, UpdateOutcome.Deployed,
                $"rolling deploy requested (→ {Short(ecrDigest)})");

        // 5. Verify: block until the new task is healthy and the rollout
        //    completes, or the circuit breaker rolls it back.
        var (ok, reason) = await WaitForStableAsync(cluster, ecsService, ct);
        return ok
            ? new(ecsService, UpdateOutcome.Verified, reason)
            : new(ecsService, UpdateOutcome.Failed, reason);
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
