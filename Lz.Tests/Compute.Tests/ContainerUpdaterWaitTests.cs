using Lz.Aws.Ops;
using static Lz.Aws.Ops.AwsContainerUpdater;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// How <c>lz updatecontainer --wait</c> decides a deploy landed (DecoupledCd.md §4.4.1).
///
/// <para><b>WHY THIS EXISTS.</b> The wait it replaced called a deploy "verified" when the service had one PRIMARY
/// deployment in COMPLETED, without asking whether that deployment was the one it started. Under the ECS
/// signature hook a refused deploy starts no task and rolls back in seconds, onto the previous deployment —
/// which then completes. The sequences below are what ECS actually produced on 2026-09-12, not a model of it.</para>
/// </summary>
public class ContainerUpdaterWaitTests
{
    private const string Signed = "sha256:1303a9132cd0117b53ff68f390cce55a212aeb9e67a5105c9c80c6d85d5d0d26";
    private const string Unsigned = "sha256:ba9773aa21a19e0ffd12d5f2cc2335fecb168c041cfd02a902655bb3b68bcadf";

    private const string Previous = "arn:aws:ecs:us-west-2:503947800380:service-deployment/scu-dev-cluster/scu-mp-aiphost/ViVmajfKLMGLA8UPunHYu";
    private const string Ours = "arn:aws:ecs:us-west-2:503947800380:service-deployment/scu-dev-cluster/scu-mp-aiphost/J4ZwX3-bQ2aM6ERMvsxuL";

    // ECS's statusReason for the refused deploy, verbatim.
    private const string HookRefusal =
        "Service deployment rolled back because PRE_SCALE_UP lifecycle hook(s) failed. Lifecycle hook target " +
        "arn:aws:lambda:us-west-2:503947800380:function:scu-dev-signature-hook returned FAILED status.";

    private static readonly string[] None = Array.Empty<string>();

    // ---------------------------------------------------------------------------------------
    //  The verdict
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("PENDING")]
    [InlineData("IN_PROGRESS")]
    [InlineData("ROLLBACK_REQUESTED")]
    [InlineData("ROLLBACK_IN_PROGRESS")]
    [InlineData("STOP_REQUESTED")]
    [InlineData("A_STATUS_ECS_ADDS_LATER")]
    public void AnUnfinishedDeployment_IsWaitedFor(string? status)
        => Assert.Equal(RolloutWait.Waiting, JudgeDeployment(status, null, None, Signed).Verdict);

    [Theory]
    [InlineData("ROLLBACK_SUCCESSFUL")]
    [InlineData("ROLLBACK_FAILED")]
    [InlineData("STOPPED")]
    public void ARolledBackOrStoppedDeployment_DidNotLand_WhateverRuns(string status)
    {
        // Every running task on the digest does not rescue it: a refused FORCED redeploy of the digest already
        // running rolls back onto tasks that run exactly that digest.
        var (verdict, reason) = JudgeDeployment(status, HookRefusal, new[] { Signed }, Signed);

        Assert.Equal(RolloutWait.NotLanded, verdict);
        Assert.Contains(status, reason);
        Assert.Contains("PRE_SCALE_UP lifecycle hook(s) failed", reason);
    }

    [Fact]
    public void ARollbackWithoutAReason_StillDidNotLand()
    {
        var (verdict, reason) = JudgeDeployment("ROLLBACK_SUCCESSFUL", "  ", None, Signed);

        Assert.Equal(RolloutWait.NotLanded, verdict);
        Assert.Contains("ECS gave no reason", reason);
    }

    [Fact]
    public void Successful_LandsOnlyWhenEveryRunningTaskRunsTheDigest()
    {
        Assert.Equal(RolloutWait.Landed, JudgeDeployment("SUCCESSFUL", null, new[] { Signed }, Signed).Verdict);

        // Old and new together: the previous task still drains after ECS calls the deployment SUCCESSFUL
        // (measured: SUCCESSFUL at 20:43:59, the classic deployment completed at 20:44:37).
        Assert.Equal(RolloutWait.Waiting, JudgeDeployment("SUCCESSFUL", null, new[] { Unsigned, Signed }, Signed).Verdict);
        Assert.Equal(RolloutWait.Waiting, JudgeDeployment("SUCCESSFUL", null, None, Signed).Verdict);
    }

    [Fact]
    public void Successful_WithNothingOnTheDigest_DidNotLand()
    {
        var (verdict, reason) = JudgeDeployment("SUCCESSFUL", null, new[] { Unsigned }, Signed);

        Assert.Equal(RolloutWait.NotLanded, verdict);
        Assert.Contains(Signed.Substring(7, 12), reason);
    }

    // ---------------------------------------------------------------------------------------
    //  The loop
    // ---------------------------------------------------------------------------------------

    /// <summary>Hands out looks in order and repeats the last one; counts every read.</summary>
    private sealed class Ecs(params DeploymentLook?[] looks)
    {
        private int _next;
        public int Looks { get; private set; }
        public int DigestReads { get; private set; }
        public IReadOnlyList<string> Running { get; set; } = Array.Empty<string>();
        public Queue<IReadOnlyList<string>> RunningSequence { get; } = new();

        public Task<DeploymentLook?> Newest(CancellationToken _)
        {
            Looks++;
            var look = looks[Math.Min(_next, looks.Length - 1)];
            _next++;
            return Task.FromResult(look);
        }

        public Task<IReadOnlyList<string>> Digests(CancellationToken _)
        {
            DigestReads++;
            if (RunningSequence.Count > 0) Running = RunningSequence.Dequeue();
            return Task.FromResult(Running);
        }
    }

    private static Task<(bool Landed, string Reason)> Wait(Ecs ecs, string? baseline, string target, TimeSpan? timeout = null)
        => WaitForDeploymentAsync(baseline, target, ecs.Newest, ecs.Digests,
            timeout ?? TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None);

    [Fact]
    public async Task TheMeasuredRefusal_IsReportedWithEcssReason()
    {
        // As read on 2026-09-12: the previous deployment until ECS records ours, ours in progress, then rolled back.
        var ecs = new Ecs(
            new DeploymentLook(Previous, "SUCCESSFUL", null),
            new DeploymentLook(Ours, "IN_PROGRESS", null),
            new DeploymentLook(Ours, "ROLLBACK_SUCCESSFUL", HookRefusal)) { Running = new[] { Signed } };

        var (landed, reason) = await Wait(ecs, Previous, Unsigned);

        Assert.False(landed);
        Assert.Contains("ROLLBACK_SUCCESSFUL", reason);
        Assert.Contains("scu-dev-signature-hook returned FAILED", reason);
        Assert.Equal(0, ecs.DigestReads); // a rollback is decided by its status alone
    }

    [Fact]
    public async Task TheBaselineIsNeverJudged_EvenWhenItLooksLikeSuccess()
    {
        // A forced redeploy of the digest already running: the PREVIOUS deployment is SUCCESSFUL and every task
        // runs the target. Judging it would report success before ECS has even recorded ours — which is then refused.
        var ecs = new Ecs(
            new DeploymentLook(Previous, "SUCCESSFUL", null),
            new DeploymentLook(Previous, "SUCCESSFUL", null),
            new DeploymentLook(Ours, "ROLLBACK_SUCCESSFUL", HookRefusal)) { Running = new[] { Signed } };

        var (landed, _) = await Wait(ecs, Previous, Signed);

        Assert.False(landed);
        Assert.Equal(3, ecs.Looks);
    }

    [Fact]
    public async Task ALandedDeploy_WaitsForTheDrainThenSucceeds()
    {
        var ecs = new Ecs(
            null, // a service with no deployment record before this one
            new DeploymentLook(Ours, "PENDING", null),
            new DeploymentLook(Ours, "IN_PROGRESS", null),
            new DeploymentLook(Ours, "SUCCESSFUL", null));
        ecs.RunningSequence.Enqueue(new[] { Unsigned, Signed });
        ecs.RunningSequence.Enqueue(new[] { Signed });

        var (landed, reason) = await Wait(ecs, baseline: null, Signed);

        Assert.True(landed);
        Assert.Contains("SUCCESSFUL", reason);
        Assert.Equal(2, ecs.DigestReads);
    }

    [Fact]
    public async Task ADeploymentThatNeverAppears_TimesOut_AfterLookingAtLeastOnce()
    {
        var ecs = new Ecs(new DeploymentLook(Previous, "SUCCESSFUL", null)) { Running = new[] { Signed } };

        var (landed, reason) = await Wait(ecs, Previous, Signed, timeout: TimeSpan.Zero);

        Assert.False(landed);
        Assert.Contains("timed out", reason);
        Assert.Contains("not recorded", reason);
        Assert.Equal(1, ecs.Looks);
    }
}
