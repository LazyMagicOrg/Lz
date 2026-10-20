using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The intake's order and its retry-safe writes (DecoupledCd.md §15.4, V-12; §15.7 V-A: "a plan in which any push, zip
/// or record state is reachable without a passing verdict for the same hashes fails its test"). The default plan keeps
/// every rule; each rule has a plan, one edge away from the default, that breaks it and must be named.
/// </summary>
public class IntakePlanTests
{
    private static IReadOnlyList<IntakeEdge> With(params IntakeEdge[] extra) => IntakePlan.Default().Concat(extra).ToList();

    [Fact]
    public void TheDefaultPlanKeepsEveryRule() => Assert.Empty(IntakePlan.Violations(IntakePlan.Default()));

    public static TheoryData<string, IntakeEdge> Breaks => new()
    {
        { "PushImage is reachable without Gate 1's passed verdict", new(IntakeStep.Gate1, IntakeStep.PushChoice) },
        { "PushImage is reachable without Gate 1's passed verdict", new(IntakeStep.FetchAndStage, IntakeStep.PushImage, IntakeGuard.Passed) },
        { "WriteZips is reachable without Gate 1's passed verdict", new(IntakeStep.CheckCommits, IntakeStep.WriteZips, IntakeGuard.Passed) },
        { "WriteDeployRecords is reachable without Gate 1's passed verdict", new(IntakeStep.FetchAndStage, IntakeStep.WriteDeployRecords, IntakeGuard.Passed) },
        { "bytes are fetched without CheckRun", new(IntakeStep.Start, IntakeStep.FetchAndStage) },
        { "bytes are fetched without CheckCommits", new(IntakeStep.CheckRun, IntakeStep.FetchAndStage, IntakeGuard.Passed) },
        { "Gate 1's verdict is reachable without Gate 1", new(IntakeStep.FetchAndStage, IntakeStep.Gate1Verdict, IntakeGuard.Passed) },
        { "Gate 1 runs on bytes that were not staged", new(IntakeStep.CheckCommits, IntakeStep.Gate1, IntakeGuard.Passed) },
        { "WriteTestsRecord is reachable from the push without waiting for the signature", new(IntakeStep.PushImage, IntakeStep.WriteZips) },
        { "a deployable record is written before the tests record", new(IntakeStep.WriteZips, IntakeStep.WriteDeployRecords) },
        { "the intake can close having recorded neither", new(IntakeStep.WriteDeployRecords, IntakeStep.End) },
        { "a verdict that did not pass leads to a push or a write", new(IntakeStep.Gate1Verdict, IntakeStep.WriteZips, IntakeGuard.NotPassed) },
        { "a passed verdict leads straight to a refusal or a fault", new(IntakeStep.Gate1Verdict, IntakeStep.RecordFault, IntakeGuard.Passed) },
    };

    [Theory]
    [MemberData(nameof(Breaks))]
    public void APlanBreakingARuleIsNamed(string violation, IntakeEdge extra)
        => Assert.Contains(IntakePlan.Violations(With(extra)), v => v.StartsWith(violation, StringComparison.Ordinal));

    [Fact]
    public void APlanThatNeverRecordsAnOutcomeIsNamed()
    {
        var plan = IntakePlan.Default().Where(e => e.To != IntakeStep.RecordOutcome).ToList();
        Assert.Contains("RecordOutcome is never reached", IntakePlan.Violations(plan));
    }

    [Fact]
    public void AClientOnlyRunSkipsThePushButNotTheOrder()
    {
        // NoImage goes straight to the zips; the tests record still precedes the deployable ones.
        var plan = IntakePlan.Default();
        Assert.Contains(plan, e => e.From == IntakeStep.PushChoice && e.To == IntakeStep.WriteZips && e.Guard == IntakeGuard.NoImage);
        Assert.Empty(IntakePlan.Violations(plan));
    }

    // ---- the writes ----------------------------------------------------------------------------

    private const string D1 = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string D2 = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void ThePushReadsTheTagFirst()
    {
        Assert.Equal(WriteDecision.Write, IntakeWrites.Push(null, D1));
        Assert.Equal(WriteDecision.AlreadyDone, IntakeWrites.Push(D1, D1));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Push(D2, D1));
    }

    [Fact]
    public void AZipIsWrittenOnceAndOtherBytesAreRefused()
    {
        Assert.Equal(WriteDecision.Write, IntakeWrites.Zip(null, "abc"));
        Assert.Equal(WriteDecision.AlreadyDone, IntakeWrites.Zip("ABC", "abc"));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Zip("abd", "abc"));
    }

    private static BuildRecord Rec(int attempt, string digest = D1) => new(
        1, "image", new BuildRecordBuiltFrom("Scutara/ScutaraService", "abc", "published", new Dictionary<string, string>()),
        new BuildRecordIdentity("image", Digest: digest), "2026-10-20T15:04:05Z", "intake", "500",
        new BuildRecordIntake("500", attempt, "1", "w", D1, new BuildRecordGate1("passed", "e")));

    [Fact]
    public void ARecordIsWrittenOnceForItsAttempt()
    {
        Assert.Equal(WriteDecision.Write, IntakeWrites.Record(null, Rec(2)));
        Assert.Equal(WriteDecision.AlreadyDone, IntakeWrites.Record(Rec(2), Rec(2)));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Record(Rec(1), Rec(2)));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Record(Rec(2, D2), Rec(2)));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Record(Rec(2) with { Intake = null }, Rec(2)));
        Assert.Equal(WriteDecision.Refuse, IntakeWrites.Record(Rec(2) with { Class = "tooling" }, Rec(2)));
    }

    private static readonly Dictionary<string, string> Staged = new() { ["image"] = "aa", ["tests"] = "bb" };

    [Fact]
    public void AHeldVerdictIsReusedOnlyForTheSameBytes()
    {
        Assert.Equal(Gate1ReuseDecision.Run, IntakeWrites.Gate1Reuse(null, D1, Staged));
        Assert.Equal(Gate1ReuseDecision.Reuse, IntakeWrites.Gate1Reuse(new Gate1Verdict(true, D1, new Dictionary<string, string> { ["image"] = "AA", ["tests"] = "bb" }), D1, Staged));
        Assert.Equal(Gate1ReuseDecision.Refuse, IntakeWrites.Gate1Reuse(new Gate1Verdict(true, D2, Staged), D1, Staged));
        Assert.Equal(Gate1ReuseDecision.Refuse, IntakeWrites.Gate1Reuse(new Gate1Verdict(true, D1, new Dictionary<string, string> { ["image"] = "aa", ["tests"] = "cc" }), D1, Staged));
        Assert.Equal(Gate1ReuseDecision.Refuse, IntakeWrites.Gate1Reuse(new Gate1Verdict(true, D1, new Dictionary<string, string> { ["image"] = "aa" }), D1, Staged));
        Assert.Equal(Gate1ReuseDecision.Refuse, IntakeWrites.Gate1Reuse(new Gate1Verdict(true, D1, new Dictionary<string, string> { ["image"] = "aa", ["other"] = "bb" }), D1, Staged));
    }
}
