using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The intake's run check and the poller's choice (DecoupledCd.md §15.4, §5.3 rules 1-4; §15.7 V-A). Both decide from
/// GitHub's answers and the intake's own evidence, so both are pure, and every refusal and every skip is pinned here.
/// </summary>
public class IntakeRunsTests
{
    private static readonly IntakeSource Service = new("Scutara/ScutaraService", 1001, 77, ".github/workflows/build-aiphost.yml");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

    private static GitHubRun Run(long id = 5000, int attempt = 1, string status = "completed", string? conclusion = "success",
        TimeSpan? finishedAgo = null) => new(
        id, attempt, 1001, 77, 1001, ".github/workflows/build-aiphost.yml", "main", "push", status, conclusion,
        Now - TimeSpan.FromMinutes(30), Now - (finishedAgo ?? TimeSpan.FromMinutes(1)));

    private static readonly GitHubJob HostedJob = new(9, "GitHub Actions", new[] { "ubuntu-latest" });

    // ---- the run check -------------------------------------------------------------------------

    [Fact]
    public void AGoodRunPasses()
        => Assert.Equal(RunCheckKind.Passed, RunCheck.Check(Service, Run(), new[] { HostedJob }).Kind);

    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public void AnUnfinishedAttemptIsNotYetRatherThanRefused(string status)
        => Assert.Equal(RunCheckKind.NotYet, RunCheck.Check(Service, Run(status: status, conclusion: null), new[] { HostedJob }).Kind);

    public static TheoryData<string, Func<GitHubRun, GitHubRun>> Wrong => new()
    {
        { "not success", r => r with { Conclusion = "failure" } },
        { "not success", r => r with { Conclusion = null } },
        { "repository id", r => r with { RepositoryId = 2002, HeadRepositoryId = 2002 } },
        { "owner id", r => r with { OwnerId = 78 } },
        { "a fork", r => r with { HeadRepositoryId = 3003 } },
        { "the workflow is", r => r with { Path = ".github/workflows/other.yml" } },
        { "the branch is", r => r with { HeadBranch = "claude/x" } },
        { "the event is", r => r with { Event = "workflow_dispatch" } },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public void EachWrongFactRefusesAndIsNamed(string named, Func<GitHubRun, GitHubRun> make)
    {
        var result = RunCheck.Check(Service, make(Run()), new[] { HostedJob });
        Assert.Equal(RunCheckKind.Refused, result.Kind);
        Assert.Contains(result.Reasons, r => r.Contains(named, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFailingCheckIsNamedNotOnlyTheFirst()
    {
        var result = RunCheck.Check(Service, Run() with { HeadBranch = "dev", Event = "pull_request" }, new[] { HostedJob });
        Assert.Equal(2, result.Reasons.Count);
    }

    [Fact]
    public void ASelfHostedRunnerIsRefused()
        => Assert.Equal(RunCheckKind.Refused,
            RunCheck.Check(Service, Run(), new[] { HostedJob, new GitHubJob(10, "Default", new[] { "self-hosted", "linux" }) }).Kind);

    [Fact]
    public void AHostedGroupWithASelfHostedLabelIsRefused()
        => Assert.Equal(RunCheckKind.Refused,
            RunCheck.Check(Service, Run(), new[] { new GitHubJob(10, "GitHub Actions", new[] { "self-hosted" }) }).Kind);

    [Fact]
    public void UnreadableJobsAreRefusedNotPassed()
    {
        Assert.Equal(RunCheckKind.Refused, RunCheck.Check(Service, Run(), Array.Empty<GitHubJob>()).Kind);
        Assert.Equal(RunCheckKind.Refused, RunCheck.Check(Service, Run(), null).Kind);
    }

    // ---- the poller ----------------------------------------------------------------------------

    private static IntakePollPlan Poll(IReadOnlyList<GitHubRun> runs, Dictionary<string, IntakeAttemptState>? evidence = null)
        => IntakePoller.Choose(Service, runs, Now, evidence ?? new());

    [Fact]
    public void ANewSuccessfulAttemptIsStartedUnderItsName()
    {
        var plan = Poll(new[] { Run(attempt: 2) });
        Assert.Equal(new IntakeStart("1001-5000-2", 5000, 2, 0), Assert.Single(plan.Starts));
        Assert.Empty(plan.Alarms);
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("completed", "failure")]
    [InlineData("completed", "cancelled")]
    public void OnlyCompletedSuccessfulAttemptsAreTaken(string status, string? conclusion)
        => Assert.Empty(Poll(new[] { Run(status: status, conclusion: conclusion) }).Starts);

    [Fact]
    public void AnAttemptPastTheRetentionIsLeft()
        => Assert.Empty(Poll(new[] { Run(finishedAgo: TimeSpan.FromHours(25)) }).Starts);

    [Theory]
    [InlineData(IntakeOutcome.TakenIn)]
    [InlineData(IntakeOutcome.Refused)]
    public void ADecidedAttemptIsNeverOfferedAgain(IntakeOutcome outcome)
    {
        var plan = Poll(new[] { Run(finishedAgo: TimeSpan.FromMinutes(30)) }, new() { ["1001-5000-1"] = new(Outcome: outcome) });
        Assert.Empty(plan.Starts);
        Assert.Empty(plan.Alarms);
    }

    [Fact]
    public void ARunningAttemptIsNotStartedTwice()
        => Assert.Empty(Poll(new[] { Run() }, new() { ["1001-5000-1"] = new(Running: true) }).Starts);

    [Theory]
    [InlineData(0, "1001-5000-1-r1")]
    [InlineData(2, "1001-5000-1-r3")]
    public void AFaultIsRetriedUnderTheNextName(int faulted, string expected)
        => Assert.Equal(expected, Assert.Single(Poll(new[] { Run() }, new() { ["1001-5000-1"] = new(FaultedTry: faulted) }).Starts).ExecutionName);

    [Fact]
    public void AFaultOnTheLastRetryAlarmsAndStartsNothing()
    {
        var plan = Poll(new[] { Run() }, new() { ["1001-5000-1"] = new(FaultedTry: 3) });
        Assert.Empty(plan.Starts);
        Assert.Contains(plan.Alarms, a => a.Contains("last retry"));
    }

    [Fact]
    public void AnIntakeThatClosedWithNothingRecordedAlarmsAndIsNotRestarted()
    {
        var plan = Poll(new[] { Run() }, new() { ["1001-5000-1"] = new(ClosedWithoutRecord: true) });
        Assert.Empty(plan.Starts);
        Assert.Contains(plan.Alarms, a => a.Contains("neither an outcome nor a fault"));
    }

    [Fact]
    public void AnAttemptWaitingTooLongForItsOutcomeAlarms()
    {
        var plan = Poll(new[] { Run(finishedAgo: TimeSpan.FromMinutes(11)) }, new() { ["1001-5000-1"] = new(Running: true) });
        Assert.Contains(plan.Alarms, a => a.Contains("no outcome"));
    }

    [Fact]
    public void AnAttemptWithinItsTenMinutesDoesNotAlarm()
        => Assert.Empty(Poll(new[] { Run(finishedAgo: TimeSpan.FromMinutes(9)) }, new() { ["1001-5000-1"] = new(Running: true) }).Alarms);

    [Fact]
    public void EachAttemptOfARunIsItsOwn()
    {
        var plan = Poll(new[] { Run(attempt: 1), Run(attempt: 2) }, new() { ["1001-5000-1"] = new(Outcome: IntakeOutcome.Refused) });
        Assert.Equal("1001-5000-2", Assert.Single(plan.Starts).ExecutionName);
    }
}
