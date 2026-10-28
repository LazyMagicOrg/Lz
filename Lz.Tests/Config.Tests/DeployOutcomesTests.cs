using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE OUTCOMES LIST (a chain entry's <c>Outcomes</c>): one message for every deployer execution that ends, whatever its
/// status, read from the execution's evidence; one for every build the intake refuses or cannot decide, in the chain's first
/// environment; and the topic, rule, function and grants that carry them, planned from the one switch.
/// </summary>
public class DeployOutcomesTests
{
    private const string Region = "us-west-2";
    private const string DevAccount = "503947800380";
    private const string BuildAccount = "147440642635";
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private const string Machine = "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer";
    private const string Execution = "req-20261027T050818Z-40453593028-1-site-1";
    private const string SiteRecordKey = "site/scutara/scutarawebsite/dev/20261027T050600Z-40453593028-1.json";
    private const string Commit = "4bee0c4d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b";
    private static readonly string Topic = DeployOutcomes.TopicArn(Region, DevAccount, "scu", "dev");
    private static readonly OutcomeSettings Settings = new("dev", Topic, Evidence, Region);

    private static JsonObject Event(string status, string? recordKey = SiteRecordKey, string name = Execution) => new()
    {
        ["version"] = "0",
        ["detail-type"] = "Step Functions Execution Status Change",
        ["source"] = "aws.states",
        ["detail"] = new JsonObject
        {
            ["executionArn"] = $"arn:aws:states:us-west-2:503947800380:execution:scu-dev-deployer:{name}",
            ["stateMachineArn"] = Machine,
            ["name"] = name,
            ["status"] = status,
            ["startDate"] = 1761541698000L,
            ["stopDate"] = 1761541816000L,
            // As Step Functions sends it: the input as a JSON string.
            ["input"] = recordKey is null ? null : new JsonObject
            {
                ["record"] = new JsonObject { ["bucket"] = "scu-build-records-4df6-b9c6", ["key"] = recordKey },
            }.ToJsonString(),
            // The machine's Fail state: a fixed error that points at the evidence. Never what a message should quote.
            ["error"] = status == "FAILED" ? "DeployFailed" : null,
            ["cause"] = status == "FAILED" ? "see the evidence bucket and this execution's history" : null,
        },
    };

    private static JsonObject Verified(string cls = "site") => new()
    {
        ["class"] = cls,
        ["builtFrom"] = new JsonObject { ["repo"] = "Scutara/ScutaraWebsite", ["commit"] = Commit, ["lane"] = "main", ["ref"] = "refs/heads/main" },
        ["workflowRunId"] = "40453593028",
        ["summary"] = "Deploy site s3://scu-artifacts/site/... into s3://scu-mp--webapp-website/ for aiproxydev.click.",
    };

    private static string DeployedKey => DeployEvidence.DeployedKey("site", "Scutara/ScutaraWebsite", Execution);
    private static string FailedKey => DeployEvidence.FailureKey(Execution);
    private static string TestsKey => $"tests/{Execution}.json";

    private sealed class Store : IRecordStore
    {
        public readonly Dictionary<string, string> Objects = new(StringComparer.Ordinal);
        public Exception? Throws;
        public readonly List<string> Read = new();

        public Task<StoredRecord?> ReadAsync(string bucket, string key)
        {
            Assert.Equal(Evidence, bucket);
            Read.Add(key);
            if (Throws is not null) throw Throws;
            return Task.FromResult(Objects.TryGetValue(key, out var json) ? new StoredRecord(json, "v1") : null);
        }
    }

    private sealed class Publisher : IAlertPublisher
    {
        public readonly List<(string Topic, string Subject, string Message)> Sent = new();
        public Exception? Throws;

        public Task PublishAsync(string topicArn, string subject, string message)
        {
            if (Throws is not null) throw Throws;
            Sent.Add((topicArn, subject, message));
            return Task.CompletedTask;
        }
    }

    private static async Task<(string Subject, string Message)> Notify(JsonObject e, Store store)
    {
        var publisher = new Publisher();
        await DeployNotifyStep.RunAsync(e, Settings, store, publisher);
        var sent = Assert.Single(publisher.Sent);
        Assert.Equal(Topic, sent.Topic);
        return (sent.Subject, sent.Message);
    }

    // ---- the event ------------------------------------------------------------------------

    [Fact]
    public void TheEvent_NamesTheExecution_ItsStatus_AndTheRecordItStartedOn()
    {
        var f = DeployFinished.FromEvent(Event("SUCCEEDED"));
        Assert.Equal(Execution, f.ExecutionName);
        Assert.Equal("SUCCEEDED", f.Status);
        Assert.Equal(SiteRecordKey, f.RecordKey);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761541698000L), f.Started);
    }

    [Fact]
    public void AnEventWithNoInput_StillNamesTheExecution()
        => Assert.Null(DeployFinished.FromEvent(Event("ABORTED", recordKey: null)).RecordKey);

    [Fact]
    public void AnEventThatIsNotAStatusChange_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => DeployFinished.FromEvent(new JsonObject { ["detail"] = new JsonObject() }));

    [Theory]
    [InlineData(SiteRecordKey, "site", "scutara/scutarawebsite", "40453593028")]
    [InlineData("image/Scutara/ScutaraService/20261027T050600Z-40453593001-2.json", "image", "Scutara/ScutaraService", "40453593001")]
    [InlineData("nonsense", null, null, null)]
    [InlineData("image//x/a.json", null, null, null)]
    public void TheRecordKey_SaysTheClassRepositoryAndRun(string key, string? cls, string? repo, string? run)
        => Assert.Equal((cls, repo, run), DeployOutcomes.FromRecordKey(key));

    // ---- the message for a deploy -----------------------------------------------------------

    [Fact]
    public async Task ADeploy_SaysDeployed_WithTheCommitTheRunAndWhatVerifySaid()
    {
        var store = new Store();
        store.Objects[DeployedKey] = new JsonObject { ["outcome"] = "deployed", ["verified"] = Verified() }.ToJsonString();

        var (subject, message) = await Notify(Event("SUCCEEDED"), store);

        Assert.Equal("[dev] deployed: Scutara/ScutaraWebsite 4bee0c4 (site)", subject);
        Assert.Contains($"https://github.com/Scutara/ScutaraWebsite/commit/{Commit}", message);
        Assert.Contains("https://github.com/Scutara/ScutaraWebsite/actions/runs/40453593028", message);
        Assert.Contains("for aiproxydev.click", message);
        Assert.Contains("Finished:    2025-10-27 05:10:16 UTC (1 min 58 s)", message);
        Assert.Contains($"s3://{Evidence}/{DeployedKey}", message);
        Assert.DoesNotContain("Why:", message);
    }

    [Fact]
    public async Task AFailure_QuotesTheEvidencesError_NotTheFailStatesFixedCause()
    {
        var store = new Store();
        store.Objects[FailedKey] = new JsonObject
        {
            ["outcome"] = "failed",
            ["error"] = new JsonObject
            {
                ["type"] = "DeployRefused",
                ["cause"] = "{\"errorType\":\"DeployRefused\",\"errorMessage\":\"the record names commit abc, which main has moved past.\",\"stackTrace\":[]}",
            },
            ["verified"] = Verified(),
        }.ToJsonString();

        var (subject, message) = await Notify(Event("FAILED"), store);

        Assert.Equal("[dev] FAILED: Scutara/ScutaraWebsite 4bee0c4 (site)", subject);
        Assert.Contains("Why: DeployRefused: the record names commit abc, which main has moved past.", message);
        Assert.DoesNotContain("see the evidence bucket", message);
        Assert.DoesNotContain("stackTrace", message);
    }

    [Fact]
    public async Task AFailureBeforeVerify_NamesTheRepositoryAndRunFromTheRecordKey()
    {
        var store = new Store();
        store.Objects[FailedKey] = new JsonObject { ["error"] = new JsonObject { ["type"] = "States.TaskFailed", ["cause"] = "the record could not be read" } }.ToJsonString();

        var (subject, _) = await Notify(Event("FAILED"), store);

        Assert.Equal("[dev] FAILED: scutara/scutarawebsite run 40453593028 (site)", subject);
    }

    [Fact]
    public async Task ADeployThatLanded_ThenFailedGate2_SaysBoth()
    {
        var store = new Store();
        store.Objects[DeployedKey] = new JsonObject { ["verified"] = Verified("image") }.ToJsonString();
        store.Objects[TestsKey] = new JsonObject
        {
            ["outcome"] = "PostDeployTestsFailed",
            ["reasons"] = new JsonArray("e2e: 2 blocking test(s) failed"),
        }.ToJsonString();

        var (subject, message) = await Notify(Event("FAILED"), store);

        Assert.StartsWith("[dev] deployed, then FAILED:", subject);
        Assert.Contains("Why: Gate 2 PostDeployTestsFailed: e2e: 2 blocking test(s) failed", message);
        Assert.Contains("Gate 2: PostDeployTestsFailed", message);
    }

    [Theory]
    [InlineData("TIMED_OUT", "timed out")]
    [InlineData("ABORTED", "stopped")]
    public async Task AnExecutionEndedFromOutside_SaysSo(string status, string said)
    {
        var (subject, message) = await Notify(Event(status), new Store());
        Assert.StartsWith($"[dev] {status.Replace('_', ' ')}:", subject);
        Assert.Contains($"Why: the execution was {said} before it recorded an outcome.", message);
    }

    [Fact]
    public async Task EvidenceThatCannotBeRead_StillSendsTheMessage_NamingTheProblem()
    {
        var store = new Store { Throws = new InvalidOperationException("Access Denied") };

        var (subject, message) = await Notify(Event("SUCCEEDED"), store);

        Assert.StartsWith("[dev] deployed:", subject);
        Assert.Contains($"s3://{Evidence}/{FailedKey} could not be read: InvalidOperationException: Access Denied", message);
    }

    [Fact]
    public async Task AFailedPublish_Throws_SoLambdaRetriesIt()
    {
        var publisher = new Publisher { Throws = new InvalidOperationException("throttled") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeployNotifyStep.RunAsync(Event("SUCCEEDED"), Settings, new Store(), publisher));
    }

    [Fact]
    public async Task OnlyTheThreeEvidenceKeysOfThisExecution_AreRead()
    {
        var store = new Store();
        await Notify(Event("SUCCEEDED"), store);
        Assert.Equal(new[] { DeployedKey, FailedKey, TestsKey }, store.Read);
    }

    [Theory]
    [InlineData("[dev] deployed: a—b\nc", "[dev] deployed: a?b c")]
    [InlineData("  padded  ", "padded")]
    public void ASubject_IsPrintableAsciiOnOneLine(string text, string subject)
        => Assert.Equal(subject, DeployOutcomes.Subject(text));

    [Fact]
    public void ALongSubject_IsCutToWhatSnsAccepts()
    {
        var s = DeployOutcomes.Subject(new string('x', 140));
        Assert.Equal(DeployOutcomes.SubjectLimit, s.Length);
        Assert.EndsWith("...", s);
    }

    // ---- the intake ------------------------------------------------------------------------

    private const long RepoId = 1334483704, RunId = 40453593028;
    private static readonly IntakeOutcomeSettings IntakeSettings = new("dev", Topic, "scu-intake-evidence-4df6-b9c6", Region);

    private sealed class IntakeFakes : IIntakeEvidenceStore, IIntakeAlerts
    {
        public readonly Dictionary<string, string> Evidence = new(StringComparer.Ordinal);
        public readonly List<string> Alerts = new();
        public Task<IReadOnlyList<string>> KeysAsync(string prefix) => Task.FromResult<IReadOnlyList<string>>(Evidence.Keys.ToList());
        public Task<bool> PutOnceAsync(string key, string body) => Task.FromResult(Evidence.TryAdd(key, body));
        public Task PublishAsync(string subject, string message) { Alerts.Add(message); return Task.CompletedTask; }
    }

    private static JsonObject RefusedState() => new()
    {
        ["repo"] = "Scutara/ScutaraWebsite", ["repositoryId"] = RepoId, ["runId"] = RunId, ["runAttempt"] = 1, ["retry"] = 0,
        ["check"] = new JsonObject { ["verdict"] = "passed", ["headSha"] = Commit },
        ["gate1"] = new JsonObject
        {
            ["verdict"] = "failed",
            ["reasons"] = new JsonArray("Gate 1 failed: the site check refused", "site-dev: /about/ links to /team/, which is not in the bundle"),
        },
    };

    [Fact]
    public async Task ARefusal_TellsTheList_WithTheCommitAndEveryReason()
    {
        var f = new IntakeFakes();
        var publisher = new Publisher();

        var result = await IntakeRecordStep.RefusedAsync(RefusedState(), f, f, DateTimeOffset.UtcNow, new IntakeOutcomes(IntakeSettings, publisher));

        var (topic, subject, message) = Assert.Single(publisher.Sent);
        Assert.Equal(Topic, topic);
        Assert.Equal("[dev] not deployed, intake refused: Scutara/ScutaraWebsite 4bee0c4", subject);
        Assert.Contains("  - site-dev: /about/ links to /team/, which is not in the bundle", message);
        Assert.Contains($"https://github.com/Scutara/ScutaraWebsite/actions/runs/{RunId}/attempts/1", message);
        Assert.Equal($"sent to {Topic}", result["outcome"]!.GetValue<string>());
        Assert.Single(f.Alerts);   // the alert is unchanged beside it
    }

    [Fact]
    public async Task ARefusalAlreadyRecorded_IsNotToldTwice()
    {
        var f = new IntakeFakes();
        var publisher = new Publisher();
        var outcomes = new IntakeOutcomes(IntakeSettings, publisher);
        await IntakeRecordStep.RefusedAsync(RefusedState(), f, f, DateTimeOffset.UtcNow, outcomes);
        await IntakeRecordStep.RefusedAsync(RefusedState(), f, f, DateTimeOffset.UtcNow, outcomes);
        Assert.Single(publisher.Sent);
    }

    [Fact]
    public async Task AListThatCannotBeTold_DoesNotTurnARefusalIntoAFault()
    {
        var f = new IntakeFakes();
        var publisher = new Publisher { Throws = new InvalidOperationException("AuthorizationError") };

        var result = await IntakeRecordStep.RefusedAsync(RefusedState(), f, f, DateTimeOffset.UtcNow, new IntakeOutcomes(IntakeSettings, publisher));

        Assert.True(result["written"]!.GetValue<bool>());
        Assert.StartsWith("not sent: InvalidOperationException: AuthorizationError", result["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithNoListConfigured_TheRefusalIsAsBefore()
    {
        var f = new IntakeFakes();
        var result = await IntakeRecordStep.RefusedAsync(RefusedState(), f, f, DateTimeOffset.UtcNow);
        Assert.StartsWith("none:", result["outcome"]!.GetValue<string>());
        Assert.Single(f.Alerts);
    }

    [Fact]
    public async Task AFault_IsToldOnlyOnItsLastRetry()
    {
        var state = new JsonObject
        {
            ["repo"] = "Scutara/ScutaraWebsite", ["repositoryId"] = RepoId, ["runId"] = RunId, ["runAttempt"] = 1, ["retry"] = 0,
            ["error"] = new JsonObject { ["Error"] = "IntakeFault", ["Cause"] = "{\"errorMessage\":\"GitHub answered 502\"}" },
        };
        var f = new IntakeFakes();
        var publisher = new Publisher();
        var outcomes = new IntakeOutcomes(IntakeSettings, publisher);

        await IntakeRecordStep.FaultAsync(state, IntakeNames.For(RepoId, RunId, 1), f, f, DateTimeOffset.UtcNow, outcomes);
        Assert.Empty(publisher.Sent);

        await IntakeRecordStep.FaultAsync(state, IntakeNames.For(RepoId, RunId, 1, IntakeNames.MaxRetries), f, f, DateTimeOffset.UtcNow, outcomes);
        var (_, subject, message) = Assert.Single(publisher.Sent);
        Assert.Equal($"[dev] not deployed, intake could not decide: Scutara/ScutaraWebsite run {RunId}", subject);
        Assert.Contains("  - GitHub answered 502", message);
    }

    // ---- the config ------------------------------------------------------------------------

    private static BuildSideConfig Chain(bool devOutcomes, bool testOutcomes) => new()
    {
        BuildAccountId = BuildAccount,
        Chain = new()
        {
            new() { Environment = "dev", AccountId = DevAccount, EvidenceStore = Evidence, Outcomes = devOutcomes },
            new() { Environment = "test", AccountId = "456687462751", EvidenceStore = "scu-test-deploy-evidence-d5e0-b27f", Outcomes = testOutcomes },
        },
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EachEnvironment_TakesItsOwnEntrysSwitch(bool dev, bool test)
    {
        var d = new PipelineConfig { TargetAccountId = DevAccount };
        var t = new PipelineConfig { TargetAccountId = "456687462751" };
        Chain(dev, test).MergeInto(d, "dev");
        Chain(dev, test).MergeInto(t, "test");
        Assert.Equal((dev, test), (d.Outcomes, t.Outcomes));
    }

    [Fact]
    public void TheIntakesList_IsTheFirstEnvironments_WhicheverEnvironmentIsLoaded()
    {
        var t = new PipelineConfig { TargetAccountId = "456687462751" };
        Chain(true, false).MergeInto(t, "test");
        Assert.Equal(new PipelineNeighbour("dev", DevAccount, Evidence), t.IntakeOutcomesTo);

        var off = new PipelineConfig { TargetAccountId = "456687462751" };
        Chain(false, true).MergeInto(off, "test");
        Assert.Null(off.IntakeOutcomesTo);
    }

    [Fact]
    public void AnEnvironmentConfig_ThatNamesOutcomes_IsToldToMoveIt()
        => Assert.Contains("Outcomes", BuildSideConfig.MovedFieldsNamedIn("Pipeline:\n  Enabled: true\n  Outcomes: true\n"));

    // ---- the plan ----------------------------------------------------------------------------

    private static SystemConfig Dev(bool outcomes, bool firstInChain = true)
    {
        var c = DeployerClientPlanTests.Config();
        c.Pipeline!.DeployOnBuildRecord = firstInChain;
        c.Pipeline.Alerts = true;
        c.Pipeline.Outcomes = outcomes;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config)
        => DeployerPlanner.Plan(config, DeployerClientPlanTests.TargetAccount,
            config.Pipeline!.DeployOnBuildRecord ? new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }) : null,
            DeployerClientPlanTests.Distributions);

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();
    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };

    private static IEnumerable<(string Action, string Resource)> Grants(string policy)
        => Statements(policy).Where(s => s.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(s => Strings(s.GetProperty("Action")).SelectMany(a => Strings(s.GetProperty("Resource")).Select(r => (a, r))));

    [Fact]
    public void WithTheSwitchOff_ThereIsNoListAndNoNotifyFunction()
    {
        var plan = Plan(Dev(outcomes: false));
        Assert.Null(plan.Outcomes);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.DeployNotify);
    }

    [Fact]
    public void WithItOn_EveryEndOfThisDeployer_InvokesTheNotifyFunction()
    {
        var plan = Plan(Dev(outcomes: true));
        var o = plan.Outcomes!;
        var acct = DeployerClientPlanTests.TargetAccount;

        Assert.Equal("scu-dev-deploy-outcomes", o.TopicName);
        Assert.Equal("scu-dev-deployer-outcome", o.RuleName);
        var detail = JsonDocument.Parse(o.Pattern).RootElement.GetProperty("detail");
        Assert.Equal(new[] { "SUCCEEDED", "FAILED", "TIMED_OUT", "ABORTED" }, Strings(detail.GetProperty("status")));
        Assert.Equal(new[] { $"arn:aws:states:us-west-2:{acct}:stateMachine:{plan.StateMachineName}" }, Strings(detail.GetProperty("stateMachineArn")));

        var fn = plan.Functions.Single(f => f.Handler == DeployerHandlers.DeployNotify);
        Assert.Equal(o.FunctionName, fn.Name);
        Assert.False(fn.InvokedByStateMachine);
        Assert.Equal(o.TopicArn, fn.Environment[DeployerEnvironment.OutcomesTopic]);
        Assert.Equal("dev", fn.Environment[DeployerEnvironment.OutcomesEnvironment]);
    }

    [Fact]
    public void TheNotifyFunction_MayTellTheListAndReadThreeEvidencePrefixes_AndNothingElse()
    {
        var plan = Plan(Dev(outcomes: true));
        var fn = plan.Functions.Single(f => f.Handler == DeployerHandlers.DeployNotify);
        var grants = Grants(fn.Policy).Where(g => !g.Action.StartsWith("logs:", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            new[]
            {
                ("s3:GetObject", $"arn:aws:s3:::{plan.EvidenceStore}/deploys/*"),
                ("s3:GetObject", $"arn:aws:s3:::{plan.EvidenceStore}/failures/*"),
                ("s3:GetObject", $"arn:aws:s3:::{plan.EvidenceStore}/tests/*"),
                ("sns:Publish", plan.Outcomes!.TopicArn),
            },
            grants.OrderBy(g => g.Action, StringComparer.Ordinal).ThenBy(g => g.Resource, StringComparer.Ordinal));
    }

    [Fact]
    public void TheFirstEnvironmentsTopic_AdmitsTheIntakesRecordRole_AndNoOtherDoes()
    {
        var first = Plan(Dev(outcomes: true)).Outcomes!;
        var later = Plan(Dev(outcomes: true, firstInChain: false)).Outcomes!;

        var principals = Statements(first.TopicPolicy)
            .Select(s => s.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:PrincipalArn").GetString()).ToList();
        Assert.Contains($"arn:aws:iam::{BuildAccount}:role/scu-intake-record-fn", principals);
        Assert.Contains($"arn:aws:iam::{DeployerClientPlanTests.TargetAccount}:role/scu-dev-deployer-notify-fn", principals);
        Assert.True(first.AdmitsTheIntake);

        Assert.False(later.AdmitsTheIntake);
        Assert.Single(Statements(later.TopicPolicy));
    }

    [Fact]
    public void ThePolicy_AdmitsByAccountAndPrincipalArn_SoItCanBeWrittenBeforeTheRolesExist()
    {
        var policy = CrossAccount.OutcomesTopicPolicy("arn:aws:sns:us-west-2:503947800380:t", DevAccount,
            $"arn:aws:iam::{DevAccount}:role/n", BuildAccount, $"arn:aws:iam::{BuildAccount}:role/r");
        var principals = Statements(policy).Select(s => s.GetProperty("Principal").GetProperty("AWS").GetString()).ToList();
        Assert.Equal(new[] { $"arn:aws:iam::{DevAccount}:root", $"arn:aws:iam::{BuildAccount}:root" }, principals);
        Assert.All(Statements(policy), s => Assert.Equal("sns:Publish", s.GetProperty("Action").GetString()));
        Assert.Throws<InvalidOperationException>(() => CrossAccount.OutcomesTopicPolicy("t", DevAccount, "n", BuildAccount, null));
    }

    [Fact]
    public void TheIntakesRecordFunction_TellsTheFirstEnvironmentsList_WhereItHasOne()
    {
        var on = IntakeConfig(new PipelineNeighbour("dev", DevAccount, Evidence));
        var record = IntakeBuildPlanner.Plan(on, BuildAccount)!.Functions.Single(f => f.Handler == DeployerHandlers.IntakeRecord);
        Assert.Equal(Topic, record.Environment[IntakeEnvironment.OutcomesTopic]);
        Assert.Equal("dev", record.Environment[IntakeEnvironment.OutcomesEnvironment]);
        Assert.Contains(("sns:Publish", Topic), Grants(record.Policy));
        Assert.Equal(IntakeBuildPlanner.RecordRoleName("scu"), record.RoleName);

        var off = IntakeBuildPlanner.Plan(IntakeConfig(null), BuildAccount)!.Functions.Single(f => f.Handler == DeployerHandlers.IntakeRecord);
        Assert.False(off.Environment.ContainsKey(IntakeEnvironment.OutcomesTopic));
        Assert.DoesNotContain(Grants(off.Policy), g => g.Resource == Topic);
    }

    private static SystemConfig IntakeConfig(PipelineNeighbour? outcomesTo) => new()
    {
        SystemKey = "scu", Environment = "dev", Region = Region, SystemSuffix = "4df6-b9c6",
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
            IntakeOutcomesTo = outcomesTo,
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Example/ExampleService", Class = "image", Artifacts = new List<string> { "aiphost" },
                        RepositoryId = 1001, OwnerId = 2002, WorkflowPath = ".github/workflows/build-aiphost.yml", Intake = "live" },
            },
        },
    };
}
