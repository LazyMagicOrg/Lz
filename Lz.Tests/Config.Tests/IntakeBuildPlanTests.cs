using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>The intake's resources in the build account (DecoupledCd.md §15.4, §3; V-B, dry).</summary>
public class IntakeBuildPlanTests
{
    private const string Account = "147440642635";

    private static SystemConfig Config(string? intake = "dry", List<string>? artifacts = null) => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Example/ExampleService", Class = "image", Artifacts = artifacts ?? new List<string> { "aiphost" },
                        RepositoryId = 1001, OwnerId = 2002, WorkflowPath = ".github/workflows/build-aiphost.yml", Intake = intake },
                new() { Repo = "Example/ExampleApp", Class = "client", Artifacts = new List<string> { "app" } },   // not in the intake
            },
        },
    };

    private static IntakeBuildPlan Plan() => IntakeBuildPlanner.Plan(Config(), Account)!;
    private static DeployerFunction Fn(IntakeBuildPlan p, string handler) => p.Functions.Single(f => f.Handler == handler);
    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();
    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };
    private static IEnumerable<string> Actions(string policy) => Statements(policy).SelectMany(s => Strings(s.GetProperty("Action")));

    private static IEnumerable<(string Action, string Resource)> Grants(string policy)
        => Statements(policy).Where(s => s.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(s => Strings(s.GetProperty("Action")).SelectMany(a => Strings(s.GetProperty("Resource")).Select(r => (a, r))));

    [Fact]
    public void ASystemWithNoIntakeRepository_GetsNoIntake()
        => Assert.Null(IntakeBuildPlanner.Plan(Config(intake: null), Account));

    [Fact]
    public void ASystemWithNoPipelineBlock_GetsNoIntake_AndReadsNothingElse()
        => Assert.Null(IntakeBuildPlanner.Plan(new SystemConfig { SystemKey = "med", Environment = "dev", Region = "us-west-2", SystemSuffix = "x" }, Account));

    [Fact]
    public void ALiveIntake_IsPlanned_AndAnUnknownModeIsRefused()
    {
        Assert.Equal("live", Assert.Single(IntakeBuildPlanner.Plan(Config("live"), Account)!.Repositories).Mode);
        Assert.Contains("not dry or live", Assert.Throws<InvalidOperationException>(() => IntakeBuildPlanner.Plan(Config("wet"), Account)).Message);
    }

    [Fact]
    public void ARepositoryWithNoArtifacts_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => IntakeBuildPlanner.Plan(Config(artifacts: new List<string>()), Account));

    [Fact]
    public void OnlyTheIntakesRepositories_AreCarried_WithTheirIdsAndClass()
    {
        var repo = Assert.Single(Plan().Repositories);
        Assert.Equal(("Example/ExampleService", 1001L, 2002L, "image", "dry"), (repo.Repo, repo.RepositoryId, repo.OwnerId, repo.Class, repo.Mode));
        Assert.Equal(IntakeRepositories.Encode(Plan().Repositories), Fn(Plan(), DeployerHandlers.IntakePoller).Environment[IntakeEnvironment.Repositories]);
    }

    [Fact]
    public void TheNames_AreTheSystemsAndTheSecretIsOnePath()
    {
        var p = Plan();
        Assert.Equal("scu-intake-evidence-4df6-b9c6", p.EvidenceStore);
        Assert.Equal("scu-intake-scratch-4df6-b9c6", p.ScratchBucket);
        Assert.Equal("scu/cicd/intake-app", p.AppSecretName);
        Assert.Equal("scu-intake", p.StateMachineName);
        Assert.Equal("rate(1 minute)", p.ScheduleExpression);
        Assert.Equal(1, p.ScratchExpirationDays);
    }

    [Fact]
    public void OnlyTheFetchFunction_WritesTheScratchBucket_AndThePolicyRefusesEveryoneElse()
    {
        var p = Plan();
        var writers = p.Functions.Where(f => Grants(f.Policy).Any(g => g.Action == "s3:PutObject" && g.Resource.Contains(p.ScratchBucket))).Select(f => f.Handler);
        Assert.Equal(new[] { DeployerHandlers.IntakeFetch }, writers);

        var deny = Statements(p.ScratchBucketPolicy).Single(s => s.GetProperty("Sid").GetString() == "OnlyTheFetchFunctionWrites");
        Assert.Equal("Deny", deny.GetProperty("Effect").GetString());
        Assert.Equal($"arn:aws:iam::{Account}:role/scu-intake-fetch-fn",
            deny.GetProperty("Condition").GetProperty("ArnNotEquals").GetProperty("aws:PrincipalArn").GetString());
    }

    [Fact]
    public void EachFunction_WritesTheEvidenceOnlyUnderItsOwnPrefixes()
    {
        var p = Plan();
        IEnumerable<string> Writes(string handler) => Grants(Fn(p, handler).Policy).Where(g => g.Action == "s3:PutObject" && g.Resource.Contains(p.EvidenceStore)).Select(g => g.Resource);
        string Under(string prefix) => $"arn:aws:s3:::{p.EvidenceStore}/{prefix}*";

        Assert.Equal(new[] { Under("alarms/") }, Writes(DeployerHandlers.IntakePoller));
        Assert.Equal(new[] { Under("staged/") }, Writes(DeployerHandlers.IntakeFetch));
        Assert.Equal(new[] { Under("taken-in/"), Under("refused/"), Under("faults/") }, Writes(DeployerHandlers.IntakeRecord));
        Assert.Empty(Writes(DeployerHandlers.IntakeCheckRun));
    }

    [Fact]
    public void TheAppKey_IsReadOnlyByTheFunctionsThatCallGitHub()
    {
        var p = Plan();
        var readers = p.Functions.Where(f => Actions(f.Policy).Contains("secretsmanager:GetSecretValue")).Select(f => f.Handler).OrderBy(h => h);
        Assert.Equal(new[] { DeployerHandlers.IntakePoller, DeployerHandlers.IntakeCheckRun, DeployerHandlers.IntakeFetch }.OrderBy(h => h), readers);
        Assert.All(p.Functions.SelectMany(f => Grants(f.Policy)).Where(g => g.Action == "secretsmanager:GetSecretValue"),
            g => Assert.Equal($"arn:aws:secretsmanager:us-west-2:{Account}:secret:scu/cicd/intake-app-*", g.Resource));
    }

    [Fact]
    public void NothingDeletes_PassesARole_PushesAnImage_OrWritesARecord()
    {
        var p = Plan();
        var all = p.Functions.SelectMany(f => Actions(f.Policy)).Concat(Actions(p.StateMachineRolePolicy)).ToList();
        Assert.DoesNotContain(all, a => a.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("iam:PassRole", all);
        Assert.DoesNotContain(all, a => a.StartsWith("ecr:"));
        Assert.DoesNotContain(p.Functions.SelectMany(f => Grants(f.Policy)), g => g.Resource.Contains("build-records") || g.Resource.Contains("artifacts"));
    }

    [Fact]
    public void OnlyThePoller_StartsTheIntake_AndTheMachineInvokesOnlyItsThreeSteps()
    {
        var p = Plan();
        var starters = p.Functions.Where(f => Actions(f.Policy).Contains("states:StartExecution")).Select(f => f.Handler);
        Assert.Equal(new[] { DeployerHandlers.IntakePoller }, starters);

        var invoked = Grants(p.StateMachineRolePolicy).Where(g => g.Action == "lambda:InvokeFunction").Select(g => g.Resource).OrderBy(r => r);
        Assert.Equal(new[] { "check-run", "fetch", "record" }.Select(n => $"arn:aws:lambda:us-west-2:{Account}:function:scu-intake-{n}").OrderBy(r => r), invoked);
    }

    [Fact]
    public void ThePoller_RunsOneAtATime_AndATickIsNeverQueuedOrRetried()
    {
        var p = Plan();
        Assert.Equal(1, p.PollerReservedConcurrency);
        Assert.Equal(60, p.PollerMaximumEventAgeSeconds);
        Assert.Equal("scu-intake-poller", p.PollerFunctionName);
    }

    [Fact]
    public void ASilentPoller_Alarms_AsAFailingOneDoes()
    {
        var silent = Plan().Alarms.Single(a => a.Name == "scu-intake-poller-silent");
        Assert.Equal(("Invocations", "LessThanThreshold", "breaching"), (silent.Metric, silent.Comparison, silent.TreatMissingData));
        Assert.Contains(Plan().Alarms, a => a.Name == "scu-intake-poller-errors");
    }

    // ---- the machine ----

    private static JsonElement States() => JsonDocument.Parse(Plan().Definition).RootElement.GetProperty("States");

    [Fact]
    public void TheMachine_HasNoGate1PushOrRecordState()
    {
        var names = States().EnumerateObject().Select(s => s.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Gate") || n.Contains("Push") || n.Contains("WriteRecord") || n.Contains("Zip"));
        Assert.Equal("CheckRun", JsonDocument.Parse(Plan().Definition).RootElement.GetProperty("StartAt").GetString());
    }

    [Fact]
    public void EveryWorkingStep_CatchesIntoTheFaultRecord_AndAFailedFaultRecordFailsOnItsOwn()
    {
        var s = States();
        foreach (var name in new[] { "CheckRun", "FetchAndStage", "RecordTakenIn", "RecordRefused" })
            Assert.Equal("MarkFault", s.GetProperty(name).GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal("FaultNotRecorded", s.GetProperty("RecordFault").GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal("Faulted", s.GetProperty("RecordFault").GetProperty("Next").GetString());
        Assert.Equal("Fail", s.GetProperty("Faulted").GetProperty("Type").GetString());
    }

    [Fact]
    public void OnlyAPassedRun_IsFetched_AndOnlyAStagedFetch_IsTakenIn()
    {
        var s = States();
        Assert.Equal("FetchAndStage", s.GetProperty("RunVerdict").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("passed", s.GetProperty("RunVerdict").GetProperty("Choices")[0].GetProperty("StringEquals").GetString());
        Assert.Equal("MarkRefused", s.GetProperty("RunVerdict").GetProperty("Default").GetString());
        Assert.Equal("staged", s.GetProperty("FetchVerdict").GetProperty("Choices")[0].GetProperty("StringEquals").GetString());
        Assert.Equal("MarkRefused", s.GetProperty("FetchVerdict").GetProperty("Default").GetString());
        Assert.Equal("taken-in", s.GetProperty("MarkTakenIn").GetProperty("Result").GetString());
        Assert.Equal("fault", s.GetProperty("MarkFault").GetProperty("Result").GetString());
    }

    [Fact]
    public void TheFetch_HasRoomForAnImage()
    {
        var fetch = Fn(Plan(), DeployerHandlers.IntakeFetch);
        Assert.Equal(900, fetch.TimeoutSeconds);
        Assert.True(IntakeBuildPlanner.FetchEphemeralStorageMb >= 4096);
    }

    // ---- Gate 1 (V-C) ----

    private static IntakeBuildPlan WithGate1()
    {
        var config = Config();
        var repo = config.Pipeline!.Repositories![0];
        repo.TestBundle = "tests";
        repo.Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", Filter = "Category!=Aws&Category!=E2E", SdkVersion = "10.0.301",
            SdkSha512 = new string('a', 128),
            Images = new() { new() { From = "public.ecr.aws/x/y@sha256:" + new string('b', 64), As = "amazon/dynamodb-local:2.6.0" } },
        };
        return IntakeBuildPlanner.Plan(config, Account)!;
    }

    [Fact]
    public void WithoutGate1_ThePlanHasNone_AndAStagedFetchIsTakenInAsBefore()
    {
        Assert.Null(Plan().Gate1);
        Assert.DoesNotContain(Plan().Functions, f => f.Handler is var h && (h == DeployerHandlers.IntakeGate1Start || h == DeployerHandlers.IntakeGate1Verdict));
        Assert.Equal("MarkTakenIn", States().GetProperty("FetchVerdict").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.False(States().TryGetProperty("Gate1Start", out _));
    }

    [Fact]
    public void Gate1sRole_HoldsItsLogsAndReportsAndNothingElse()
    {
        var g = WithGate1().Gate1!;
        var actions = Actions(g.RolePolicy).ToList();
        Assert.All(actions, a => Assert.True(a.StartsWith("logs:") || a.StartsWith("codebuild:"), a));
        Assert.All(Grants(g.RolePolicy).Where(x => x.Action.StartsWith("codebuild:")), x => Assert.Equal(g.ReportGroupArn, x.Resource));
        Assert.All(Grants(g.RolePolicy).Where(x => x.Action.StartsWith("logs:")), x => Assert.Contains("/aws/codebuild/scu-intake-gate1", x.Resource));
        Assert.Contains("codebuild.amazonaws.com", g.TrustPolicy);
    }

    [Fact]
    public void OnlyTheStartFunction_ReadsTheScratchBucketOrStartsABuild()
    {
        var p = WithGate1();
        var readers = p.Functions.Where(f => Grants(f.Policy).Any(g => g.Action == "s3:GetObject" && g.Resource.Contains(p.ScratchBucket))).Select(f => f.Handler);
        Assert.Equal(new[] { DeployerHandlers.IntakeGate1Start, DeployerHandlers.IntakePublish }, readers.OrderBy(h => h, StringComparer.Ordinal));   // the publish reads what it pushes
        var starters = p.Functions.Where(f => Actions(f.Policy).Contains("codebuild:StartBuild")).Select(f => f.Handler);
        Assert.Equal(new[] { DeployerHandlers.IntakeGate1Start }, starters);
        Assert.Equal(new[] { $"arn:aws:s3:::{p.EvidenceStore}/gate1/*" },
            Grants(Fn(p, DeployerHandlers.IntakeGate1Verdict).Policy).Where(g => g.Action == "s3:PutObject").Select(g => g.Resource));
        // The verdict reads Gate 2's lists out of Gate 1's own log, and no other.
        var logReads = Grants(Fn(p, DeployerHandlers.IntakeGate1Verdict).Policy).Where(g => g.Action == "logs:GetLogEvents").Select(g => g.Resource).ToList();
        Assert.Single(logReads);
        Assert.EndsWith($":log-group:{p.Gate1!.LogGroupName}:log-stream:*", logReads[0]);
    }

    [Fact]
    public void TheBuildspec_ChecksBothHashes_AndTakesWhatItRunsFromTheBuildsVariables()
    {
        var spec = WithGate1().Gate1!.Buildspec;
        Assert.Contains("sha512sum -c -", spec);
        Assert.Contains("sha256sum -c -", spec);
        Assert.True(spec.IndexOf("sha256sum -c -", StringComparison.Ordinal) < spec.IndexOf("unzip", StringComparison.Ordinal), "the bundle is checked before it is unzipped");
        Assert.Contains("unzip -q /tmp/bundle.zip -d \"$GATE1_ROOT\"", spec);
        Assert.Contains("--filter \"$GATE1_FILTER\"", spec);
        Assert.Contains("--list-tests --filter \"$filter\"", spec);
        Assert.True(spec.IndexOf("--list-tests", StringComparison.Ordinal) < spec.IndexOf("--filter \"$GATE1_FILTER\"", StringComparison.Ordinal), "Gate 2's lists are printed before a test runs");
        Assert.DoesNotContain("Category", spec);            // nothing of the repository's own is fixed in it
        Assert.Contains(WithGate1().Gate1!.ReportGroupArn, spec);
        Assert.Equal(("aws/codebuild/standard:8.0", 60), (WithGate1().Gate1!.Image, WithGate1().Gate1!.TimeoutMinutes));
    }

    [Fact]
    public void TheBuildspec_PrintsWhatItTests_BeforeAnythingElse()
    {
        var first = FirstCommand(WithGate1().Gate1!.Buildspec);
        Assert.StartsWith("printf 'GATE1 ", first);
        foreach (var name in new[] { "INTAKE_REPO", "INTAKE_RUN_ID", "INTAKE_RUN_ATTEMPT", "INTAKE_COMMIT", "INTAKE_WORKFLOW_REF", "INTAKE_EXECUTION", "INTAKE_TESTS", "BUNDLE_SHA256", "INTAKE_ARTIFACTS" })
            Assert.Contains($"\"${name}\"", first);
        Assert.DoesNotContain(": ", first);   // a plain YAML scalar, as the buildspec's other commands are
    }

    [Fact]
    public void ACollectionCheck_InstallsNoSdk_ListsTheSpecsWithForbidOnly_AndPrintsItsCount()
    {
        var spec = WithGate1().Gate1!.Buildspec;
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(spec);
        var phases = (Dictionary<object, object>)yaml["phases"];
        List<string> Commands(string phase) => ((List<object>)((Dictionary<object, object>)phases[phase])["commands"]).Select(c => (string)c).ToList();

        // The SDK and the images are a .NET test run's only: a collection check, and a vitest run, skip them.
        Assert.All(Commands("install").Where(c => c.Contains("sdk.tgz") || c.Contains("docker pull")),
            c => Assert.StartsWith("test \"$GATE1_KIND\" != tests ||", c));
        var collect = Commands("build").Single(c => c.Contains("--forbid-only"));
        Assert.StartsWith("if [ \"$GATE1_KIND\" = collection ]", collect);
        Assert.Contains("npx playwright test --list --forbid-only", collect);
        Assert.Contains("npx playwright test --list --forbid-only --grep @gate", collect);
        Assert.Contains("GATE1-LIST-COLLECTED", collect);
        Assert.StartsWith(Gate2Lists.Prefix, "GATE1-LIST-COLLECTED");   // the verdict reads only lines with this prefix
    }

    [Theory]
    [InlineData(0, "GATE1-LIST-COLLECTED total 53 gate 12", 0)]
    [InlineData(1, "GATE1-LIST-COLLECTED failed", 1)]   // playwright refused: a spec that does not load, or a .only
    public void TheCollectionCommand_PrintsTheLineTheJudgeReads_UnderBash(int playwrightExit, string expected, int exit)
    {
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(WithGate1().Gate1!.Buildspec);
        var build = (Dictionary<object, object>)((Dictionary<object, object>)yaml["phases"])["build"];
        var collect = ((List<object>)build["commands"]).Select(c => (string)c).Single(c => c.Contains("--forbid-only"));
        // A stand-in npx printing Playwright's own summary line (1.62.1, measured 2026-10-23): 53 in all, 12 tagged @gate.
        var script = $$"""
            export GATE1_KIND=collection GATE1_ROOT=/tmp
            npx() { if [ {{playwrightExit}} -ne 0 ]; then echo "Error: focused item found in the --forbid-only mode"; return {{playwrightExit}}; fi
                    case "$*" in *@gate*) echo "Total: 12 tests in 5 files";; *) echo "Total: 53 tests in 18 files";; esac; }
            {{collect}}
            """;
        var psi = new System.Diagnostics.ProcessStartInfo("bash", "-s") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var bash = System.Diagnostics.Process.Start(psi)!;
        bash.StandardInput.Write(script.Replace("\r", "") + "\n");
        bash.StandardInput.Close();
        var lines = bash.StandardOutput.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bash.WaitForExit();

        Assert.Equal(exit, bash.ExitCode);
        Assert.Equal(expected, lines.Last());
        if (exit == 0)
            Assert.Equal((Gate1Outcome.Passed, (int?)12), (Gate1CollectionJudge.Judge(new("SUCCEEDED", null, Array.Empty<string>(), null, null), lines).Outcome,
                Gate1CollectionJudge.Judge(new("SUCCEEDED", null, Array.Empty<string>(), null, null), lines).GateCount));
    }

    [Fact]
    public void AVitestRun_InstallsWithNpmCi_AndRunsVitestWithAJsonReport()
    {
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(WithGate1().Gate1!.Buildspec);
        var phases = (Dictionary<object, object>)yaml["phases"];
        List<string> Commands(string phase) => ((List<object>)((Dictionary<object, object>)phases[phase])["commands"]).Select(c => (string)c).ToList();

        Assert.Contains(Commands("pre_build"), c => c.Contains("[ \"$GATE1_KIND\" = vitest ]") && c.Contains("npm ci"));
        var run = Commands("build").Single(c => c.Contains("vitest run"));
        Assert.StartsWith("if [ \"$GATE1_KIND\" = vitest ]", run);
        Assert.Contains("--reporter=json --outputFile=/tmp/gate1-vitest.json", run);
        // The .NET run's two steps are skipped for it, as for a collection check.
        Assert.All(Commands("build").Where(c => c.Contains("dotnet test")), c => Assert.StartsWith("test \"$GATE1_KIND\" != tests ||", c));
    }

    [Theory]
    [InlineData(0, 16, 16, 0, 0, "GATE1-LIST-VITEST total 16 passed 16 failed 0 suites-failed 0", Gate1Outcome.Passed)]
    [InlineData(1, 16, 14, 2, 1, "GATE1-LIST-VITEST total 16 passed 14 failed 2 suites-failed 1", Gate1Outcome.Failed)]
    public void TheVitestCommand_PrintsTheLineTheJudgeReads_AndEndsAsVitestDid_UnderBash(int exit, int total, int passed, int failed, int suites, string expected, Gate1Outcome outcome)
    {
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(WithGate1().Gate1!.Buildspec);
        var build = (Dictionary<object, object>)((Dictionary<object, object>)yaml["phases"])["build"];
        var run = ((List<object>)build["commands"]).Select(c => (string)c).Single(c => c.Contains("vitest run"));
        // A stand-in npx writing vitest's JSON report (its numTotalTests, numPassedTests, numFailedTests, numFailedTestSuites)
        // where the command told it to, and exiting as vitest does; the command's own node reads it.
        var script = $$"""
            export GATE1_KIND=vitest GATE1_ROOT=/tmp
            npx() { printf '{"numTotalTests":{{total}},"numPassedTests":{{passed}},"numFailedTests":{{failed}},"numFailedTestSuites":{{suites}}}' > /tmp/gate1-vitest.json; return {{exit}}; }
            {{run}}
            """;
        var psi = new System.Diagnostics.ProcessStartInfo("bash", "-s") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var bash = System.Diagnostics.Process.Start(psi)!;
        bash.StandardInput.Write(script.Replace("\r", "") + "\n");
        bash.StandardInput.Close();
        var lines = bash.StandardOutput.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bash.WaitForExit();

        Assert.Equal(exit, bash.ExitCode);
        Assert.Equal(expected, lines.Last());
        // The judge is given what the verdict's log reader keeps, as in the intake: a marker it drops is a count never read
        // (the first vitest run, 2026-10-30, printed GATE1-VITEST, which the reader dropped, and faulted).
        Assert.Equal(outcome, Gate1VitestJudge.Judge(new(exit == 0 ? "SUCCEEDED" : "FAILED", exit == 0 ? null : "BUILD: COMMAND_EXECUTION_ERROR",
            Array.Empty<string>(), null, null), lines.Where(Gate2Lists.Kept).ToList()).Outcome);
    }

    [Fact]
    public void TheHeader_PrintsTheRunAndEveryStagedSha_UnderBash()
    {
        var first = FirstCommand(WithGate1().Gate1!.Buildspec);
        // The script goes in on stdin, its variables exported in it: WSL's bash, first on a Windows PATH, drops the process's
        // environment and expands a -c argument's variables before the script runs.
        var variables = new Dictionary<string, string>
        {
            ["INTAKE_REPO"] = "Example/App", ["INTAKE_RUN_ID"] = "9001", ["INTAKE_RUN_ATTEMPT"] = "2", ["INTAKE_COMMIT"] = "abc123",
            ["INTAKE_WORKFLOW_REF"] = "Example/App/.github/workflows/b.yml@refs/heads/main", ["INTAKE_EXECUTION"] = "1-9001-2-r1",
            ["INTAKE_TESTS"] = "tests-2", ["BUNDLE_SHA256"] = "2222", ["INTAKE_ARTIFACTS"] = "app-2 1111\ntests-2 2222",
        };
        var psi = new System.Diagnostics.ProcessStartInfo("bash", "-s") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var bash = System.Diagnostics.Process.Start(psi)!;
        bash.StandardInput.Write(string.Concat(variables.Select(kv => $"export {kv.Key}='{kv.Value}'\n")) + first + "\n");
        bash.StandardInput.Close();
        var lines = bash.StandardOutput.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
        bash.WaitForExit();

        Assert.Equal(0, bash.ExitCode);
        Assert.Equal(new[]
        {
            "GATE1 Example/App run 9001 attempt 2, commit abc123 (Example/App/.github/workflows/b.yml@refs/heads/main)",
            "GATE1 intake execution 1-9001-2-r1",
            "GATE1 tests tests-2 2222 (the bundle under test)",
            "GATE1 staged app-2 1111",
            "GATE1 staged tests-2 2222",
        }, lines);
    }

    /// <summary>The buildspec's first command: the first "- " line after the first "commands:".</summary>
    private static string FirstCommand(string spec)
    {
        var lines = spec.Replace("\r", "").Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim() == "commands:");
        return lines.Skip(at + 1).First(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).TrimStart()[2..];
    }

    [Fact]
    public void TheMachine_RunsGate1OnlyWhereABundleWasStaged_AndOnlyAPassIsTakenIn()
    {
        var s = JsonDocument.Parse(WithGate1().Definition).RootElement.GetProperty("States");
        Assert.Equal("HasGate1", s.GetProperty("FetchVerdict").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("$.fetch.tests", s.GetProperty("HasGate1").GetProperty("Choices")[0].GetProperty("Variable").GetString());
        Assert.Equal("Gate1Start", s.GetProperty("HasGate1").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("Gate1Verdict", s.GetProperty("Gate1Start").GetProperty("Next").GetString());
        var wait = s.GetProperty("Gate1Verdict").GetProperty("Retry")[0];
        Assert.Equal("Gate1StillRunning", wait.GetProperty("ErrorEquals")[0].GetString());
        Assert.True(wait.GetProperty("IntervalSeconds").GetInt32() * wait.GetProperty("MaxAttempts").GetInt32() > 60 * 60, "the wait outlasts the build's timeout");
        Assert.Equal("passed", s.GetProperty("Gate1Outcome").GetProperty("Choices")[0].GetProperty("StringEquals").GetString());
        Assert.Equal("Publish", s.GetProperty("Gate1Outcome").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("MarkRefused", s.GetProperty("Gate1Outcome").GetProperty("Default").GetString());
        // The publish follows only a pass, waits while the image is unsigned, and then takes the run in.
        Assert.Equal("MarkTakenIn", s.GetProperty("Publish").GetProperty("Next").GetString());
        Assert.Equal("ImageNotYetSigned", s.GetProperty("Publish").GetProperty("Retry")[0].GetProperty("ErrorEquals")[0].GetString());
        foreach (var name in new[] { "Gate1Start", "Gate1Verdict", "Publish" })
            Assert.Equal("MarkFault", s.GetProperty(name).GetProperty("Catch")[0].GetProperty("Next").GetString());
    }

    [Fact]
    public void ThePublish_PushesOnlyToItsPipelineRepository_SignsUnderItsProfile_AndWritesOnlyItsOwnPrefixes()
    {
        var p = WithGate1();
        var publish = Fn(p, DeployerHandlers.IntakePublish);
        var grants = Statements(publish.Policy).ToList();
        JsonElement Sid(string sid) => grants.Single(s => s.GetProperty("Sid").GetString() == sid);

        Assert.Equal(new[] { "arn:aws:ecr:us-west-2:147440642635:repository/scu-4df6-b9c6-aiphost" }, Strings(Sid("PushToItsPipelineRepositories").GetProperty("Resource")));
        Assert.Equal(new[] { "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_exampleservice" }, Strings(Sid("SignItsPushes").GetProperty("Resource")));
        Assert.Equal(new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/image/example/exampleservice/*", "arn:aws:s3:::scu-build-records-4df6-b9c6/tests/example/exampleservice/*" },
            Strings(Sid("ReadAndWriteItsRecordsOnce").GetProperty("Resource")));
        Assert.Equal(new[] { "arn:aws:s3:::scu-artifacts-4df6-b9c6/image/example/exampleservice/*", "arn:aws:s3:::scu-artifacts-4df6-b9c6/tests/example/exampleservice/*" },
            Strings(Sid("WriteItsArtifactsOnce").GetProperty("Resource")));
        Assert.DoesNotContain(Actions(publish.Policy), a => a.Contains("Delete") || a == "iam:PassRole" || a == "s3:*" || a == "ecr:*");

        // Existence is read by listing the exact key (measured: a HEAD of a missing key without a list grant is a 403), so
        // each store lists, and lists only, this repository's two prefixes.
        foreach (var (sid, bucket) in new[] { ("ListItsRecords", "scu-build-records-4df6-b9c6"), ("ListItsArtifacts", "scu-artifacts-4df6-b9c6") })
        {
            Assert.Equal(new[] { $"arn:aws:s3:::{bucket}" }, Strings(Sid(sid).GetProperty("Resource")));
            Assert.Equal(new[] { "image/example/exampleservice/*", "tests/example/exampleservice/*" },
                Strings(Sid(sid).GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix")));
        }

        var target = Assert.Single(PublishTargets.Decode(publish.Environment[IntakeEnvironment.PublishTargets]));
        Assert.Equal(("Example/ExampleService", "aiphost", "scu-4df6-b9c6-aiphost"), (target.Repo, target.Artifact, target.Repository));
    }

    [Fact]
    public void TheRepositoriesVariable_CarriesHowGate1Runs()
    {
        var repo = IntakeRepositories.Decode(Fn(WithGate1(), DeployerHandlers.IntakeGate1Start).Environment[IntakeEnvironment.Repositories]).Single();
        Assert.Equal(("tests", "Category!=Aws&Category!=E2E", "10.0.301"), (repo.Gate1!.Artifact, repo.Gate1.Filter, repo.Gate1.SdkVersion));
        Assert.Equal("amazon/dynamodb-local:2.6.0", repo.Gate1.Images.Single().As);
    }
}
