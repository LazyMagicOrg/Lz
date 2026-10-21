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
    public void ALiveIntake_IsRefused_SinceThisOnePushesNothing()
        => Assert.Contains("dry only", Assert.Throws<InvalidOperationException>(() => IntakeBuildPlanner.Plan(Config("live"), Account)).Message);

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
        Assert.Equal(new[] { DeployerHandlers.IntakeGate1Start }, readers);
        var starters = p.Functions.Where(f => Actions(f.Policy).Contains("codebuild:StartBuild")).Select(f => f.Handler);
        Assert.Equal(new[] { DeployerHandlers.IntakeGate1Start }, starters);
        Assert.Equal(new[] { $"arn:aws:s3:::{p.EvidenceStore}/gate1/*" },
            Grants(Fn(p, DeployerHandlers.IntakeGate1Verdict).Policy).Where(g => g.Action == "s3:PutObject").Select(g => g.Resource));
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
        Assert.DoesNotContain("Category", spec);            // nothing of the repository's own is fixed in it
        Assert.Contains(WithGate1().Gate1!.ReportGroupArn, spec);
        Assert.Equal(("aws/codebuild/standard:8.0", 60), (WithGate1().Gate1!.Image, WithGate1().Gate1!.TimeoutMinutes));
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
        Assert.Equal("MarkTakenIn", s.GetProperty("Gate1Outcome").GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("MarkRefused", s.GetProperty("Gate1Outcome").GetProperty("Default").GetString());
        foreach (var name in new[] { "Gate1Start", "Gate1Verdict" })
            Assert.Equal("MarkFault", s.GetProperty(name).GetProperty("Catch")[0].GetProperty("Next").GetString());
    }

    [Fact]
    public void TheRepositoriesVariable_CarriesHowGate1Runs()
    {
        var repo = IntakeRepositories.Decode(Fn(WithGate1(), DeployerHandlers.IntakeGate1Start).Environment[IntakeEnvironment.Repositories]).Single();
        Assert.Equal(("tests", "Category!=Aws&Category!=E2E", "10.0.301"), (repo.Gate1!.Artifact, repo.Gate1.Filter, repo.Gate1.SdkVersion));
        Assert.Equal("amazon/dynamodb-local:2.6.0", repo.Gate1.Images.Single().As);
    }
}
