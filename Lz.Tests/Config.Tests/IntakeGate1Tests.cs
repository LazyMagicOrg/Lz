using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>Gate 1 in the dry intake (DecoupledCd.md §15.4, V-C).</summary>
public sealed class IntakeGate1Tests : IDisposable
{
    private const long RepoId = 1001, RunId = 9001;
    private static readonly IntakeGate1 Gate1 = new("tests", "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", "Category!=Aws&Category!=E2E",
        "10.0.301", new string('a', 128), new[] { new IntakeImage("public.ecr.aws/x/y@sha256:" + new string('b', 64), "amazon/dynamodb-local:2.6.0") });
    private static readonly IntakeRepository Service = new("Example/ExampleService", RepoId, 2002, ".github/workflows/build-aiphost.yml", "dry", "image", new[] { "aiphost" }, Gate1);
    private static readonly IReadOnlyList<IntakeRepository> Repos = new[] { Service };
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-gate1-" + Guid.NewGuid().ToString("N")[..8]);
    private static string Exec => IntakeNames.For(RepoId, RunId, 1);
    private static Gate1Header Header => Gate1Header.From(State(), Exec + "-r1");

    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private static JsonObject State() => new()
    {
        ["repo"] = Service.Repo, ["repositoryId"] = RepoId, ["runId"] = RunId, ["runAttempt"] = 1, ["retry"] = 0,
        ["check"] = new JsonObject
        {
            ["verdict"] = "passed", ["headSha"] = new string('d', 40),
            ["workflowRef"] = $"{Service.Repo}/.github/workflows/build-aiphost.yml@refs/heads/main",
        },
        ["fetch"] = new JsonObject
        {
            ["verdict"] = "staged",
            ["manifestDigest"] = "sha256:" + new string('c', 64),
            ["staged"] = new JsonArray(
                new JsonObject { ["artifact"] = "aiphost-1", ["key"] = $"{Exec}/aiphost-1.zip", ["sha256"] = new string('1', 64) },
                new JsonObject { ["artifact"] = "tests-1", ["key"] = $"{Exec}/tests-1.zip", ["sha256"] = new string('2', 64) }),
            ["tests"] = new JsonObject { ["artifact"] = "tests-1", ["key"] = $"{Exec}/tests-1.zip", ["sha256"] = new string('2', 64) },
        },
    };

    private sealed class Fakes : IGate1Builds, IScratchPresigner, IIntakeEvidenceStore
    {
        public (string Project, IReadOnlyDictionary<string, string> Variables)? Started;
        public (string Key, TimeSpan Lifetime)? Presigned;
        public Gate1Build Build = new("SUCCEEDED", null, new[] { "arn:report" }, Now.AddMinutes(-5), Now);
        public Gate1Report? Report = new(7332, new Dictionary<string, int> { ["SUCCEEDED"] = 7331, ["SKIPPED"] = 1 });
        public IReadOnlyList<string> Failed = new[] { "A.Test1", "B.Test2" };
        public IReadOnlyList<string> LogLines = ListBlocks(("precondition", new[] { "E.Pre" }), ("blocking", new[] { "E.A", "E.B(x: \"1\")", "E.B(x: \"2\")" }), ("advisory", Array.Empty<string>()));
        public int ReportReads;
        public readonly Dictionary<string, string> Evidence = new(StringComparer.Ordinal);

        public Task<string> StartAsync(string project, IReadOnlyDictionary<string, string> variables) { Started = (project, variables); return Task.FromResult("build:1"); }
        public Task<Gate1Build> DescribeAsync(string buildId) => Task.FromResult(Build);
        public Task<Gate1Report?> ReportAsync(IReadOnlyList<string> reportArns) { ReportReads++; return Task.FromResult(Report); }
        public Task<IReadOnlyList<string>> FailedCasesAsync(IReadOnlyList<string> reportArns, int max) => Task.FromResult<IReadOnlyList<string>>(Failed.Take(max).ToList());
        public Task<IReadOnlyList<string>> ListLinesAsync(Gate1Build build) => Task.FromResult(LogLines);
        public string GetUrl(string key, TimeSpan lifetime) { Presigned = (key, lifetime); return "https://presigned/one-object"; }
        public Task<IReadOnlyList<string>> KeysAsync(string prefix) => Task.FromResult<IReadOnlyList<string>>(Evidence.Keys.ToList());
        public Task<bool> PutOnceAsync(string key, string body) => Task.FromResult(Evidence.TryAdd(key, body));
    }

    private static string[] ListBlocks(params (string Name, string[] Tests)[] lists)
        => lists.SelectMany(l => new[] { $"GATE1-LIST-BEGIN {l.Name}" }.Concat(l.Tests.Select(t => $"GATE1-LIST {l.Name} {t}")).Append($"GATE1-LIST-END {l.Name}")).ToArray();

    private static Dictionary<string, int> Counts(int ok, int failed = 0, int error = 0)
        => new() { ["SUCCEEDED"] = ok, ["FAILED"] = failed, ["ERROR"] = error };

    // ---- the judgement ----

    [Fact]
    public void ARunningBuild_IsWaitedFor()
        => Assert.Equal(Gate1Outcome.Running, Gate1Judge.Judge(new("IN_PROGRESS", null, Array.Empty<string>(), null, null), null).Outcome);

    [Theory]
    [InlineData("FAILED")]
    [InlineData("SUCCEEDED")]   // a build that reports a failed test but succeeded is still a failed Gate 1
    public void AFailedTest_FailsGate1_WhateverTheBuildSays(string status)
    {
        var (outcome, reason) = Gate1Judge.Judge(new(status, "BUILD: exit 1", new[] { "r" }, null, null), new(10, Counts(9, failed: 1)));
        Assert.Equal(Gate1Outcome.Failed, outcome);
        Assert.Contains("1 of 10", reason);
    }

    [Fact]
    public void AnErroredTest_FailsGate1_Too()
        => Assert.Equal(Gate1Outcome.Failed, Gate1Judge.Judge(new("FAILED", null, new[] { "r" }, null, null), new(10, Counts(9, error: 1))).Outcome);

    [Fact]
    public void EveryTestPassingInASucceededBuild_Passes()
        => Assert.Equal(Gate1Outcome.Passed, Gate1Judge.Judge(new("SUCCEEDED", null, new[] { "r" }, null, null), new(10, Counts(10))).Outcome);

    [Theory]
    [InlineData("FAILED", "PRE_BUILD: sha256sum: WARNING: 1 computed checksum did NOT match")]
    [InlineData("TIMED_OUT", null)]
    [InlineData("FAULT", "INSTALL: curl failed")]
    [InlineData("STOPPED", null)]
    public void ABuildThatEndedWithoutAFailedTest_IsAFault_RetriedNotDecided(string status, string? phase)
    {
        var (outcome, reason) = Gate1Judge.Judge(new(status, phase, Array.Empty<string>(), null, null), null);
        Assert.Equal(Gate1Outcome.Fault, outcome);
        Assert.Contains(status, reason);
    }

    [Fact]
    public void ASucceededBuildThatRanNothing_IsAFault()
    {
        Assert.Equal(Gate1Outcome.Fault, Gate1Judge.Judge(new("SUCCEEDED", null, Array.Empty<string>(), null, null), null).Outcome);
        Assert.Equal(Gate1Outcome.Fault, Gate1Judge.Judge(new("SUCCEEDED", null, new[] { "r" }, null, null), new(0, Counts(0))).Outcome);
    }

    // ---- start ----

    [Fact]
    public async Task TheBuild_GetsAOneObjectUrl_ItsSha_AndHowToRunFromConfig()
    {
        var f = new Fakes();

        var result = await IntakeGate1StartStep.RunAsync(State(), Exec, Repos, f, f, "scu-intake-gate1");

        Assert.Equal(($"{Exec}/tests-1.zip", TimeSpan.FromMinutes(15)), f.Presigned);
        var (project, v) = f.Started!.Value;
        Assert.Equal("scu-intake-gate1", project);
        Assert.Equal("https://presigned/one-object", v["BUNDLE_URL"]);
        Assert.Equal(new string('2', 64), v["BUNDLE_SHA256"]);
        Assert.Equal(Gate1.Run, v["GATE1_RUN"]);
        Assert.Equal("/tmp/bundle", v["GATE1_ROOT"]);
        Assert.Equal("/home/runner/work/R/R", IntakeGate1StartStep.VariablesFor(Gate1 with { BuildRoot = "/home/runner/work/R/R" }, "u", "s", Header)["GATE1_ROOT"]);
        Assert.Equal(Gate1.Filter, v["GATE1_FILTER"]);
        Assert.Equal((Gate1.SdkVersion, Gate1.SdkSha512), (v["SDK_VERSION"], v["SDK_SHA512"]));
        Assert.Equal($"{Gate1.Images[0].From} {Gate1.Images[0].As}", v["GATE1_IMAGES"]);
        Assert.Equal("build:1", result["buildId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheBuild_IsToldWhatItTests_ForItsLog_TheRunTheCommitTheExecutionAndEveryStagedSha()
    {
        var f = new Fakes();

        await IntakeGate1StartStep.RunAsync(State(), Exec + "-r1", Repos, f, f, "scu-intake-gate1");

        var v = f.Started!.Value.Variables;
        Assert.Equal(Service.Repo, v["INTAKE_REPO"]);
        Assert.Equal(("9001", "1"), (v["INTAKE_RUN_ID"], v["INTAKE_RUN_ATTEMPT"]));
        Assert.Equal(new string('d', 40), v["INTAKE_COMMIT"]);
        Assert.Equal($"{Service.Repo}/.github/workflows/build-aiphost.yml@refs/heads/main", v["INTAKE_WORKFLOW_REF"]);
        Assert.Equal(Exec + "-r1", v["INTAKE_EXECUTION"]);   // the execution's own name, a retry's suffix included
        Assert.Equal("tests-1", v["INTAKE_TESTS"]);
        Assert.Equal($"aiphost-1 {new string('1', 64)}\ntests-1 {new string('2', 64)}", v["INTAKE_ARTIFACTS"]);
    }

    [Fact]
    public void TheHeader_ReadsWhatTheStateHolds_AndSaysSoWhereItHoldsNothing()
    {
        var bare = State();
        bare.Remove("check");
        var v = Gate1Header.From(bare, Exec).Variables();
        Assert.Equal(("(not in the state)", "(not in the state)"), (v["INTAKE_COMMIT"], v["INTAKE_WORKFLOW_REF"]));
        Assert.Equal(Service.Repo, v["INTAKE_REPO"]);
    }

    [Fact]
    public void TheHeadersVariables_CannotBeReplacedByConfig()
    {
        var forged = Gate1 with { Environment = new Dictionary<string, string> { ["INTAKE_COMMIT"] = "forged", ["INTAKE_ARTIFACTS"] = "x y" } };
        var v = IntakeGate1StartStep.VariablesFor(forged, "u", "s", Header);
        Assert.Equal(new string('d', 40), v["INTAKE_COMMIT"]);
        Assert.StartsWith("aiphost-1 ", v["INTAKE_ARTIFACTS"]);
    }

    [Fact]
    public void AHeaderLine_IsNeverReadAsAGate2ListLine()
    {
        var lists = ListBlocks(("precondition", new[] { "P" }), ("blocking", new[] { "A" }), ("advisory", Array.Empty<string>()));
        var withHeader = new[] { "GATE1 Example/ExampleService run 9001 attempt 1, commit d (ref)", "GATE1 staged tests-1 2222" }.Concat(lists).ToList();
        Assert.Equal(Gate2Lists.Parse(lists)!["blocking"], Gate2Lists.Parse(withHeader)!["blocking"]);
    }

    [Fact]
    public void ConfigsVariables_GoWithTheBuild_AndCannotReplaceGate1sOwn()
    {
        var withEnv = Gate1 with { Environment = new Dictionary<string, string> { ["TESTCONTAINERS_HOST_OVERRIDE"] = "localhost", ["BUNDLE_URL"] = "https://elsewhere" } };
        var v = IntakeGate1StartStep.VariablesFor(withEnv, "https://presigned/one-object", new string('2', 64), Header);
        Assert.Equal("localhost", v["TESTCONTAINERS_HOST_OVERRIDE"]);
        Assert.Equal("https://presigned/one-object", v["BUNDLE_URL"]);

        var lists = IntakeGate1StartStep.VariablesFor(Gate1 with { Environment = new Dictionary<string, string> { ["GATE1_LISTS"] = "blocking Category=Nothing" } }, "u", "s", Header);
        Assert.Equal(Gate2Lists.Variable(), lists["GATE1_LISTS"]);   // Gate 2's filters are fixed, never config's
    }

    // ---- Gate 2's lists ----

    [Fact]
    public void TheLists_AreGate2sFixedFilters_OnePerLine()
        => Assert.Equal("precondition Category=E2E&Gate2=Precondition\nblocking Category=E2E&Gate2=Blocking\nadvisory Category=E2E&Gate2=Advisory", Gate2Lists.Variable());

    [Fact]
    public void EachList_IsItsFirstCompleteBlock_AndATestPrintingOneLaterReplacesNothing()
    {
        var lines = ListBlocks(("precondition", new[] { "P" }), ("blocking", new[] { "A", "B with spaces" }), ("advisory", Array.Empty<string>()))
            .Concat(ListBlocks(("blocking", new[] { "Forged" })))
            .Prepend("GATE1-LIST blocking Stray").ToList();

        var lists = Gate2Lists.Parse(lines)!;

        Assert.Equal(new[] { "P" }, lists["precondition"]);
        Assert.Equal(new[] { "A", "B with spaces" }, lists["blocking"]);
        Assert.Empty(lists["advisory"]);
    }

    [Fact]
    public void ATheory_IsListedOnce_ByItsMethod_WhetherDiscoveryExpandedItsCasesOrNot()
    {
        // Measured: with no live environment an e2e theory is skipped and listed once (Gate 1); with one, once per case.
        static IReadOnlyList<string> Blocking(params string[] tests)
            => Gate2Lists.Parse(ListBlocks(("precondition", Array.Empty<string>()), ("blocking", tests), ("advisory", Array.Empty<string>())))!["blocking"];

        var skipped = Blocking("N.T.Fact", "N.T.Rebuild");
        var expanded = Blocking("N.T.Fact", "N.T.Rebuild(licence: \"E2E-SOLAR-1\")", "N.T.Rebuild(licence: \"E2E-SOLAR-2\")");
        Assert.Equal(new[] { "N.T.Fact", "N.T.Rebuild" }, skipped);
        Assert.Equal(skipped, expanded);
    }

    [Theory]
    [InlineData("blocking")]
    [InlineData("advisory")]
    public void AListWithNoEnd_OrNoneAtAll_MeansNoLists(string missing)
    {
        var lines = ListBlocks(Gate2Lists.Filters.Where(f => f.Name != missing).Select(f => (f.Name, new[] { "T" })).ToArray()).ToList();
        Assert.Null(Gate2Lists.Parse(lines));
        Assert.Null(Gate2Lists.Parse(lines.Append($"GATE1-LIST-BEGIN {missing}").Append($"GATE1-LIST {missing} T")));
    }

    [Fact]
    public async Task NoGate1Configured_OrNoBundleStaged_IsAFault()
    {
        var bare = new[] { Service with { Gate1 = null } };
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeGate1StartStep.RunAsync(State(), Exec, bare, new Fakes(), new Fakes(), "p"));
        var noTests = State();
        noTests["fetch"]!.AsObject().Remove("tests");
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeGate1StartStep.RunAsync(noTests, Exec, Repos, new Fakes(), new Fakes(), "p"));
    }

    // ---- the browser tests' collection check (§15.4) ----

    private static readonly IntakeGate1 Collection = new("tests", "", "", "", "", Array.Empty<IntakeImage>(), Kind: "collection");

    [Fact]
    public void TheBuild_IsToldItsKind_ATestRunsOrACollectionCheck()
    {
        Assert.Equal("tests", IntakeGate1StartStep.VariablesFor(Gate1, "u", "s", Header)["GATE1_KIND"]);
        Assert.Equal("collection", IntakeGate1StartStep.VariablesFor(Collection, "u", "s", Header)["GATE1_KIND"]);
    }

    [Fact]
    public async Task TheStart_RecordsItsKind_ForTheVerdict()
    {
        var f = new Fakes();
        var collection = new[] { Service with { Gate1 = Collection } };
        Assert.Equal("collection", (await IntakeGate1StartStep.RunAsync(State(), Exec, collection, f, f, "p"))["kind"]!.GetValue<string>());
        Assert.Equal("tests", (await IntakeGate1StartStep.RunAsync(State(), Exec, Repos, f, f, "p"))["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("SUCCEEDED", null, "GATE1-LIST-COLLECTED total 53 gate 12", Gate1Outcome.Passed, 12)]
    [InlineData("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", "GATE1-LIST-COLLECTED failed", Gate1Outcome.Failed, null)]   // a spec that does not load, or a .only
    [InlineData("FAILED", "PRE_BUILD: npm ci failed", null, Gate1Outcome.Fault, null)]                                      // not the specs' own failure
    [InlineData("SUCCEEDED", null, null, Gate1Outcome.Fault, null)]                                                          // no count: it decided nothing
    [InlineData("SUCCEEDED", null, "GATE1-LIST-COLLECTED total 0 gate 0", Gate1Outcome.Fault, null)]                       // collected nothing
    [InlineData("SUCCEEDED", null, "GATE1-LIST-COLLECTED total 53 gate 0", Gate1Outcome.Fault, null)]                      // no @gate test: Gate 2 would run none
    public void ACollectionCheck_IsJudgedFromItsLog(string status, string? phase, string? line, Gate1Outcome outcome, int? gateCount)
    {
        var (o, _, count) = Gate1CollectionJudge.Judge(new(status, phase, Array.Empty<string>(), null, null), line is null ? Array.Empty<string>() : new[] { line });
        Assert.Equal((outcome, gateCount), (o, count));
    }

    [Fact]
    public async Task ACollectionChecksPass_IsWritten_WithTheGateCount_AndReadsNoReport()
    {
        var f = new Fakes { Build = new("SUCCEEDED", null, Array.Empty<string>(), Now.AddMinutes(-1), Now), LogLines = new[] { "GATE1-LIST-COLLECTED total 53 gate 12" } };
        var state = State();
        state["gate1Start"] = new JsonObject { ["buildId"] = "build:1", ["kind"] = "collection" };

        var verdict = await IntakeGate1VerdictStep.RunAsync(state, Exec, f, f, Now);

        Assert.Equal("passed", verdict["verdict"]!.GetValue<string>());
        Assert.Equal(12, verdict["gateCount"]!.GetValue<int>());
        Assert.Equal(0, f.ReportReads);
    }

    // ---- a Node repository's vitest run (BuyerAppReactPunchlist BR-P3) ----

    [Theory]
    [InlineData("SUCCEEDED", null, "GATE1-LIST-VITEST total 16 passed 16 failed 0 suites-failed 0", Gate1Outcome.Passed)]
    [InlineData("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", "GATE1-LIST-VITEST total 16 passed 15 failed 1 suites-failed 1", Gate1Outcome.Failed)]
    [InlineData("SUCCEEDED", null, "GATE1-LIST-VITEST total 16 passed 15 failed 1 suites-failed 1", Gate1Outcome.Failed)]   // a failed test decides, whatever the build says
    [InlineData("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", "GATE1-LIST-VITEST total 4 passed 4 failed 0 suites-failed 1", Gate1Outcome.Failed)]   // a file that did not load
    [InlineData("FAILED", "PRE_BUILD: npm ci failed", null, Gate1Outcome.Fault)]                                      // not the tests' own failure
    [InlineData("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", "GATE1-LIST-VITEST unreadable", Gate1Outcome.Fault)]         // no count it could read
    [InlineData("SUCCEEDED", null, null, Gate1Outcome.Fault)]                                                          // no count: it decided nothing
    [InlineData("SUCCEEDED", null, "GATE1-LIST-VITEST total 0 passed 0 failed 0 suites-failed 0", Gate1Outcome.Fault)]    // ran no test
    [InlineData("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", "GATE1-LIST-VITEST total 16 passed 16 failed 0 suites-failed 0", Gate1Outcome.Fault)]   // all passed, yet the build failed
    public void AVitestRun_IsJudgedFromItsLog(string status, string? phase, string? line, Gate1Outcome outcome)
    {
        var (o, _, _) = Gate1VitestJudge.Judge(new(status, phase, Array.Empty<string>(), null, null), line is null ? Array.Empty<string>() : new[] { line });
        Assert.Equal(outcome, o);
    }

    [Fact]
    public async Task AVitestRunsPass_IsWritten_WithItsCounts_AndNeedsNoGate2Lists_AndReadsNoReport()
    {
        var f = new Fakes { Build = new("SUCCEEDED", null, Array.Empty<string>(), Now.AddMinutes(-1), Now), LogLines = new[] { "GATE1-LIST-VITEST total 16 passed 16 failed 0 suites-failed 0" } };
        var state = State();
        state["gate1Start"] = new JsonObject { ["buildId"] = "build:1", ["kind"] = "vitest" };

        var verdict = await IntakeGate1VerdictStep.RunAsync(state, Exec, f, f, Now);

        Assert.Equal("passed", verdict["verdict"]!.GetValue<string>());
        Assert.Equal(16, verdict["counts"]!["SUCCEEDED"]!.GetValue<int>());
        Assert.Null(verdict["gate2Lists"]);
        Assert.Equal(0, f.ReportReads);
    }

    [Fact]
    public async Task AVitestRunsFailure_IsDecided_AndAsksTheReportNothing()
    {
        var f = new Fakes { Build = new("FAILED", "BUILD: COMMAND_EXECUTION_ERROR", Array.Empty<string>(), Now.AddMinutes(-1), Now), LogLines = new[] { "GATE1-LIST-VITEST total 16 passed 14 failed 2 suites-failed 1" } };
        var state = State();
        state["gate1Start"] = new JsonObject { ["buildId"] = "build:1", ["kind"] = "vitest" };

        var verdict = await IntakeGate1VerdictStep.RunAsync(state, Exec, f, f, Now);

        Assert.Equal("failed", verdict["verdict"]!.GetValue<string>());
        Assert.Contains("2 of 16 tests failed", verdict["summary"]!.GetValue<string>());
        Assert.Equal(0, f.ReportReads);
    }

    // ---- the verdict ----

    private static JsonObject Started() { var s = State(); s["gate1Start"] = new JsonObject { ["buildId"] = "build:1" }; return s; }

    [Fact]
    public async Task WhileTheBuildRuns_TheVerdictWaits_ReadingNoReport()
    {
        var f = new Fakes { Build = new("IN_PROGRESS", null, Array.Empty<string>(), Now, null) };
        await Assert.ThrowsAsync<Gate1StillRunning>(() => IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now));
        Assert.Equal(0, f.ReportReads);
        Assert.Empty(f.Evidence);
    }

    [Fact]
    public async Task APass_IsWrittenOnce_BindingTheManifestAndEveryStagedSha()
    {
        var f = new Fakes();

        var verdict = await IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now);

        Assert.Equal("passed", verdict["verdict"]!.GetValue<string>());
        Assert.Equal("sha256:" + new string('c', 64), verdict["manifestDigest"]!.GetValue<string>());
        Assert.Equal(new string('1', 64), verdict["artifactSha256"]!["aiphost-1"]!.GetValue<string>());
        Assert.Equal(new string('2', 64), verdict["artifactSha256"]!["tests-1"]!.GetValue<string>());
        Assert.Equal(300, verdict["seconds"]!.GetValue<int>());
        Assert.Equal(new[] { "E.A", "E.B" }, verdict["gate2Lists"]!["blocking"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(verdict["gate2Lists"]!["advisory"]!.AsArray());
        Assert.True(f.Evidence.ContainsKey(IntakeEvidence.Gate1(Exec)));
    }

    [Fact]
    public async Task APassWithoutGate2sLists_IsAFault_SinceGate2CouldNotCheckItsResults()
    {
        var f = new Fakes { LogLines = ListBlocks(("precondition", new[] { "P" })) };
        var fault = await Assert.ThrowsAsync<IntakeFault>(() => IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now));
        Assert.Contains("no complete Gate 2 lists", fault.Message);
        Assert.Empty(f.Evidence);
    }

    [Fact]
    public async Task AFailureWithoutTheLists_IsStillDecided()
    {
        var f = new Fakes { Build = new("FAILED", null, new[] { "r" }, null, null), Report = new(3, Counts(2, failed: 1)), LogLines = Array.Empty<string>() };
        var verdict = await IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now);
        Assert.Equal("failed", verdict["verdict"]!.GetValue<string>());
        Assert.Null(verdict["gate2Lists"]);
    }

    [Fact]
    public async Task AFailure_IsDecided_NamingTheFailedTests()
    {
        var f = new Fakes { Build = new("FAILED", "BUILD: exit 1", new[] { "r" }, Now.AddMinutes(-5), Now), Report = new(10, Counts(8, failed: 2)) };

        var verdict = await IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now);

        Assert.Equal("failed", verdict["verdict"]!.GetValue<string>());
        var reasons = verdict["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.StartsWith("Gate 1 failed: 2 of 10", reasons[0]);
        Assert.Contains("A.Test1", reasons);
    }

    [Fact]
    public async Task AFaultedBuild_Throws_SoTheMachineRecordsAFault()
    {
        var f = new Fakes { Build = new("FAILED", "INSTALL: curl failed", Array.Empty<string>(), null, null), Report = null };
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeGate1VerdictStep.RunAsync(Started(), Exec, f, f, Now));
        Assert.Empty(f.Evidence);
    }

    [Fact]
    public async Task AGate1Refusal_IsWhatTheRefusalRecords()
    {
        var f = new Fakes { Build = new("FAILED", null, new[] { "r" }, null, null), Report = new(3, Counts(2, failed: 1)) };
        var state = Started();
        state["gate1"] = await IntakeGate1VerdictStep.RunAsync(state, Exec, f, f, Now);
        state["check"] = new JsonObject { ["verdict"] = "passed" };

        var alerts = new Alerts();
        await IntakeRecordStep.RefusedAsync(state, f, alerts, Now);

        Assert.Contains("Gate 1 failed: 1 of 3", f.Evidence[IntakeEvidence.Refused(Exec)]);
        Assert.Contains("Gate 1 failed", alerts.Sent.Single());
    }

    private sealed class Alerts : IIntakeAlerts
    {
        public readonly List<string> Sent = new();
        public Task PublishAsync(string subject, string message) { Sent.Add(message); return Task.CompletedTask; }
    }

    // ---- the fetch stages the bundle, and does not read it as an image ----

    private sealed class GitHub : IIntakeGitHub, IIntakeScratch
    {
        public readonly Dictionary<long, byte[]> Bytes = new();
        public readonly List<GitHubArtifact> Listed = new();
        public readonly Dictionary<string, long> Staged = new();
        public Task<GitHubRun?> RunAttemptAsync(string repo, long runId, int attempt) => Task.FromResult<GitHubRun?>(null);
        public Task<IReadOnlyList<GitHubJob>> JobsAsync(string repo, long runId, int attempt) => Task.FromResult<IReadOnlyList<GitHubJob>>(Array.Empty<GitHubJob>());
        public Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string repo, long runId) => Task.FromResult<IReadOnlyList<GitHubArtifact>>(Listed);
        public Task DownloadAsync(string repo, long artifactId, string path) => File.WriteAllBytesAsync(path, Bytes[artifactId]);
        public Task PutFileAsync(string key, string path) { Staged[key] = new FileInfo(path).Length; return Task.CompletedTask; }
        public void Add(long id, string name, byte[] zip)
        {
            Bytes[id] = zip;
            Listed.Add(new GitHubArtifact(id, name, RunId, false, "sha256:" + Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), zip.Length));
        }
    }

    private static byte[] Zip(string entry, byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) using (var s = z.CreateEntry(entry).Open()) s.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public async Task TheFetch_StagesTheTestBundle_WithoutReadingItAsAnImage()
    {
        var gh = new GitHub();
        gh.Add(31, "aiphost-1", Zip("aiphost.oci.tar", new IntakeArtifactsTests.Layout().Tar().ToArray()));
        gh.Add(32, "tests-1", Zip("ServiceTest/bin/Debug/net10.0/ServiceTest.dll", new byte[] { 1, 2, 3 }));   // no .tar in it
        var f = new Fakes();
        var state = State(); state.Remove("fetch");

        var result = await IntakeFetchStep.RunAsync(state, Exec, Repos, gh, gh, f, _work);

        Assert.Equal("staged", result["verdict"]!.GetValue<string>());
        Assert.Equal($"{Exec}/tests-1.zip", result["tests"]!["key"]!.GetValue<string>());
        Assert.Equal(2, gh.Staged.Count);
    }

    [Fact]
    public async Task AMissingTestBundle_IsRefused_LikeAnyMissingArtifact()
    {
        var gh = new GitHub();
        gh.Add(31, "aiphost-1", Zip("aiphost.oci.tar", new IntakeArtifactsTests.Layout().Tar().ToArray()));
        var state = State(); state.Remove("fetch");

        var result = await IntakeFetchStep.RunAsync(state, Exec, Repos, gh, gh, new Fakes(), _work);

        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("tests-1"));
    }
}
