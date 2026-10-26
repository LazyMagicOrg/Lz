using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer planned for an environment promoted into (DecoupledCd.md §12 P5 stage F): one whose build-side chain names a
/// predecessor. Its machine deploys a SET - Verify admits it by the predecessor's marker, the image member goes through
/// Prepare, Deploy, VerifyRollout and Record as dev's does, then each bundle, one at a time, and the set is decided - and
/// its Verify reads the predecessor's markers and evidence across the account boundary. The first environment's machine
/// is planned as it was, byte for byte.
/// </summary>
public class DeployerPromotedPlanTests
{
    private const string TestAccount = "456687462751";
    private const string DevStore = "scu-dev-deploy-evidence-4df6-b9c6", OwnStore = "scu-test-deploy-evidence-d5e0-b27f";

    private static SystemConfig Test(bool approval = false)
    {
        var c = DeployerClientPlanTests.Config(approval: approval);
        c.Environment = "test";
        c.SystemSuffix = "d5e0-b27f";
        c.Pipeline!.TargetAccountId = TestAccount;
        c.Pipeline.BuildSuffix = "4df6-b9c6";
        c.Pipeline.ChainEvidenceStore = OwnStore;
        c.Pipeline.Predecessor = new PipelineNeighbour("dev", "503947800380", DevStore);
        c.Pipeline.Alerts = true;
        return c;
    }

    private static readonly DeployerTriggerInputs Cluster = new("scu-test-cluster", new[] { "mp" });

    private static PipelineDeployer Plan(SystemConfig? c = null, DeployerTriggerInputs? cluster = null)
        => DeployerPlanner.Plan(c ?? Test(), TestAccount, triggerInputs: cluster ?? Cluster, clientInputs: DeployerClientPlanTests.Distributions);

    private static JsonElement States(PipelineDeployer p) => JsonDocument.Parse(p.Definition).RootElement.GetProperty("States");

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };

    private static bool Grants(string policy, string action, string resource)
        => Statements(policy).Any(s => s.GetProperty("Effect").GetString() == "Allow"
            && Strings(s.GetProperty("Action")).Contains(action) && Strings(s.GetProperty("Resource")).Contains(resource));

    // ---- the machine ----

    [Fact]
    public void Verify_AdmitsTheSet_ThenTheImageMemberGoesThroughDevsImagePath()
    {
        var s = States(Plan());
        Assert.Equal("Verify", JsonDocument.Parse(Plan().Definition).RootElement.GetProperty("StartAt").GetString());
        Assert.Equal(("$.set", "IsRevert"), (s.GetProperty("Verify").GetProperty("ResultPath").GetString(), s.GetProperty("Verify").GetProperty("Next").GetString()));
        Assert.Equal("ImageMember", s.GetProperty("IsRevert").GetProperty("Default").GetString());

        var image = s.GetProperty("ImageMember");
        Assert.Equal("Pass", image.GetProperty("Type").GetString());
        var p = image.GetProperty("Parameters");
        Assert.Equal(("$.set.image.record", "$.set.image.target", "$.set.image.verified"),
            (p.GetProperty("record.$").GetString(), p.GetProperty("target.$").GetString(), p.GetProperty("verified.$").GetString()));
        Assert.Equal("Prepare", image.GetProperty("Next").GetString());
        // A promotion keeps Prepare's and DeployBundle's refusal of an older build, whatever its input says.
        Assert.False(p.GetProperty("allowOlderBuild").GetBoolean());
        Assert.Equal(("Deploy", "VerifyRollout", "Record"), (s.GetProperty("Prepare").GetProperty("Next").GetString(),
            s.GetProperty("Deploy").GetProperty("Next").GetString(), s.GetProperty("VerifyRollout").GetProperty("Next").GetString()));
    }

    [Fact]
    public void TheImagesRecord_GoesOnToTheBundles_OneAtATime_ThenTheSetIsDecided()
    {
        var s = States(Plan());
        Assert.Equal("Bundles", s.GetProperty("Record").GetProperty("Next").GetString());

        var map = s.GetProperty("Bundles");
        Assert.Equal(("Map", "$.set.bundles", 1), (map.GetProperty("Type").GetString(), map.GetProperty("ItemsPath").GetString(), map.GetProperty("MaxConcurrency").GetInt32()));
        Assert.Equal("$$.Map.Item.Value.record", map.GetProperty("ItemSelector").GetProperty("record.$").GetString());
        Assert.Equal("AfterSet", map.GetProperty("Next").GetString());   // a revert is recorded there instead of decided (stage J)
        Assert.Equal("$.allowOlderBuild", map.GetProperty("ItemSelector").GetProperty("allowOlderBuild.$").GetString());
        Assert.Equal("RecordFailure", map.GetProperty("Catch")[0].GetProperty("Next").GetString());

        var inner = map.GetProperty("ItemProcessor");
        Assert.Equal("DeployBundle", inner.GetProperty("StartAt").GetString());
        var st = inner.GetProperty("States");
        Assert.Equal(("VerifyBundle", "RecordBundle"), (st.GetProperty("DeployBundle").GetProperty("Next").GetString(), st.GetProperty("VerifyBundle").GetProperty("Next").GetString()));
        Assert.True(st.GetProperty("RecordBundle").GetProperty("End").GetBoolean());
        // A Catch cannot leave a Map's processor: a bundle's failure is the Map's, caught there.
        Assert.All(st.EnumerateObject(), x => Assert.False(x.Value.TryGetProperty("Catch", out _), x.Name));

        Assert.Equal("Decide", s.GetProperty("AfterSet").GetProperty("Default").GetString());
        Assert.Equal(("Task", "Decided"), (s.GetProperty("Decide").GetProperty("Type").GetString(), s.GetProperty("Decide").GetProperty("Next").GetString()));
        Assert.Equal("Succeed", s.GetProperty("Decided").GetProperty("Type").GetString());
    }

    [Fact]
    public void AFailure_IsRecordedThenDecided_AndADecisionFaultStillEndsFailed()
    {
        var s = States(Plan());
        Assert.Equal("DecideFailure", s.GetProperty("RecordFailure").GetProperty("Next").GetString());
        Assert.Equal("Failed", s.GetProperty("DecideFailure").GetProperty("Next").GetString());
        Assert.Equal("Failed", s.GetProperty("DecideFailure").GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal(s.GetProperty("Decide").GetProperty("Resource").GetString(), s.GetProperty("DecideFailure").GetProperty("Resource").GetString());
    }

    [Fact]
    public void ThereIsNoBranchOnAClass_NorAnyBundleStateOutsideTheSetsLoop()
    {
        var s = States(Plan());
        foreach (var name in new[] { "ByClass", "NoBranchForClass", "DeployBundle", "VerifyBundle", "DeploySite", "VerifySite" })
            Assert.False(s.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void ThePromotedMachine_HasNoContractOrDenyGaps()
    {
        var plan = Plan();
        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
        Assert.Empty(DeployerPlanner.DenyGaps(plan.RolePolicy, plan.DenyPolicy));
        foreach (var f in plan.Functions) Assert.Empty(DeployerPlanner.DenyGaps(f.Policy, plan.DenyPolicy));
    }

    [Fact]
    public void ProdsApprovalOrAGate2AfterAPredecessor_IsRefused_UntilP6()
        => Assert.Contains("P6", Assert.Throws<InvalidOperationException>(() => Plan(Test(approval: true))).Message);

    // ---- what its functions read and write ----

    [Fact]
    public void Verify_KnowsItsPredecessor_AndReadsItsMarkersAndEvidenceThereByVersion()
    {
        var verify = Plan().Functions.Single(f => f.Handler == DeployerHandlers.Verify);
        Assert.Equal(("dev", DevStore, OwnStore), (verify.Environment[DeployerEnvironment.Predecessor],
            verify.Environment[DeployerEnvironment.PredecessorEvidenceStore], verify.Environment[DeployerEnvironment.EvidenceStore]));
        foreach (var prefix in new[] { "passed/", "deploys/", "tests/" })
            foreach (var action in new[] { "s3:GetObject", "s3:GetObjectVersion" })
                Assert.True(Grants(verify.Policy, action, $"arn:aws:s3:::{DevStore}/{prefix}*"), $"{action} {prefix}");
        Assert.True(Grants(verify.Policy, "s3:GetObject", $"arn:aws:s3:::{OwnStore}/decisions/*"));
        Assert.False(Grants(verify.Policy, "s3:GetObject", $"arn:aws:s3:::{DevStore}/decisions/*"));
    }

    [Fact]
    public void TheDecisionFunction_MayWriteOnlyDecisions_InItsOwnStore()
    {
        var decide = Plan().Functions.Single(f => f.Handler == DeployerHandlers.Decide);
        Assert.Equal("scu-test-deployer-decide", decide.Name);
        var writes = Statements(decide.Policy).Where(s => Strings(s.GetProperty("Action")).Contains("s3:PutObject")).SelectMany(s => Strings(s.GetProperty("Resource"))).ToList();
        Assert.Equal(new[] { $"arn:aws:s3:::{OwnStore}/decisions/*" }, writes);
        Assert.Equal(OwnStore, decide.Environment[DeployerEnvironment.EvidenceStore]);
    }

    // ---- the revert (stage J) ----

    [Fact]
    public void ARevert_SetsTheHoldBeforePrepare_AndItsImageMemberMayBeOlderThanWhatRuns()
    {
        var s = States(Plan());
        var choice = s.GetProperty("IsRevert").GetProperty("Choices")[0];
        Assert.Equal(("$.set.revert", true, "SetHold"), (choice.GetProperty("Variable").GetString(), choice.GetProperty("BooleanEquals").GetBoolean(), choice.GetProperty("Next").GetString()));
        Assert.Equal(("Task", "RevertImageMember"), (s.GetProperty("SetHold").GetProperty("Type").GetString(), s.GetProperty("SetHold").GetProperty("Next").GetString()));
        Assert.Equal("RecordFailure", s.GetProperty("SetHold").GetProperty("Catch")[0].GetProperty("Next").GetString());

        var p = s.GetProperty("RevertImageMember").GetProperty("Parameters");
        Assert.True(p.GetProperty("allowOlderBuild").GetBoolean());
        Assert.Equal("$.revertTo", p.GetProperty("revertTo.$").GetString());
        Assert.Equal("$.set.image.verified", p.GetProperty("verified.$").GetString());
        Assert.Equal("Prepare", s.GetProperty("RevertImageMember").GetProperty("Next").GetString());
    }

    [Fact]
    public void AfterItsSet_ARevertIsRecordedUnderReverts_AndDecidesNothing()
    {
        var s = States(Plan());
        var choice = s.GetProperty("AfterSet").GetProperty("Choices")[0];
        Assert.Equal(("$.set.revert", true, "RecordRevert"), (choice.GetProperty("Variable").GetString(), choice.GetProperty("BooleanEquals").GetBoolean(), choice.GetProperty("Next").GetString()));
        Assert.Equal("Reverted", s.GetProperty("RecordRevert").GetProperty("Next").GetString());
        Assert.Equal("Succeed", s.GetProperty("Reverted").GetProperty("Type").GetString());
    }

    [Fact]
    public void TheRevertsFunctions_MayDoTheirOneThing()
    {
        var plan = Plan();
        var machine = $"arn:aws:states:us-west-2:{TestAccount}:stateMachine:{plan.StateMachineName}";
        var hold = $"arn:aws:ssm:us-west-2:{TestAccount}:parameter/scu/test/pipeline/hold";

        var setHold = plan.Functions.Single(f => f.Handler == DeployerHandlers.SetHold);
        Assert.Equal(new[] { hold }, Statements(setHold.Policy).Where(s => Strings(s.GetProperty("Action")).Contains("ssm:PutParameter")).SelectMany(s => Strings(s.GetProperty("Resource"))));
        Assert.Equal("/scu/test/pipeline/hold", setHold.Environment[DeployerEnvironment.HoldParameter]);

        var record = plan.Functions.Single(f => f.Handler == DeployerHandlers.RecordRevert);
        Assert.Equal(new[] { $"arn:aws:s3:::{OwnStore}/reverts/*" },
            Statements(record.Policy).Where(s => Strings(s.GetProperty("Action")).Contains("s3:PutObject")).SelectMany(s => Strings(s.GetProperty("Resource"))));

        var start = plan.Functions.Single(f => f.Handler == DeployerHandlers.RevertStart);
        Assert.False(start.InvokedByStateMachine);   // a person invokes it, through the operator set
        Assert.True(Grants(start.Policy, "states:StartExecution", machine));
        Assert.True(Grants(start.Policy, "states:ListExecutions", machine));
        Assert.Equal(Poller(plan).Environment[DeployerEnvironment.PromotionTarget], start.Environment[DeployerEnvironment.PromotionTarget]);
        Assert.DoesNotContain(Statements(start.Policy), s => Strings(s.GetProperty("Action")).Any(a => a.StartsWith("ssm:", StringComparison.Ordinal)));

        var verify = plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify);
        Assert.Equal(machine, verify.Environment[DeployerEnvironment.StateMachine]);
        Assert.True(Grants(verify.Policy, "states:ListExecutions", machine));

        foreach (var f in new[] { setHold, record, start, verify }) Assert.Empty(DeployerPlanner.DenyGaps(f.Policy, plan.DenyPolicy));
        Assert.DoesNotContain(plan.RolePolicy, "revert-start");   // the machine never starts a revert
    }

    // ---- the promotion poller (stage I) ----

    private static DeployerFunction Poller(PipelineDeployer p) => p.Functions.Single(f => f.Handler == DeployerHandlers.PromotionPoller);

    [Fact]
    public void ThePoller_RunsEveryMinute_OneAtATime_OnTicksAMinuteOld()
    {
        var plan = Plan();
        var poll = plan.Promotion!;
        Assert.Equal(("scu-test-deployer-poller", "scu-test-deployer-poller-schedule", "rate(1 minute)", 1, 60),
            (poll.PollerFunctionName, poll.ScheduleRuleName, poll.ScheduleExpression, poll.ReservedConcurrency, poll.MaximumEventAgeSeconds));
        Assert.False(Poller(plan).InvokedByStateMachine);
        Assert.Equal("/scu/test/pipeline/hold", poll.HoldParameter);
    }

    [Fact]
    public void ThePoller_AlarmsOnItsErrorsAndItsSilence_AndTheTopicAdmitsBoth()
    {
        var plan = Plan();
        Assert.Equal(new[] { "scu-test-deployer-poller-errors", "scu-test-deployer-poller-silent" }, plan.Promotion!.Alarms.Select(a => a.Name));
        Assert.Equal("breaching", plan.Promotion.Alarms.Single(a => a.Name.EndsWith("-silent")).TreatMissingData);
        foreach (var alarm in plan.Promotion.Alarms)
            Assert.Contains($"arn:aws:cloudwatch:us-west-2:{TestAccount}:alarm:{alarm.Name}", plan.Alerts!.TopicPolicy);
    }

    [Fact]
    public void ThePoller_StartsTheImageTarget_ItsServiceInItsCluster()
    {
        var target = System.Text.Json.Nodes.JsonNode.Parse(Poller(Plan()).Environment[DeployerEnvironment.PromotionTarget])!;
        Assert.Equal(("scu-test-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost"),
            (target["cluster"]!.GetValue<string>(), target["service"]!.GetValue<string>(), target["container"]!.GetValue<string>(), target["repository"]!.GetValue<string>()));
    }

    [Fact]
    public void ThePoller_ReadsWhatItDecidesOn_AndMayStartOnlyItsOwnDeployer()
    {
        var plan = Plan();
        var f = Poller(plan);
        var machine = $"arn:aws:states:us-west-2:{TestAccount}:stateMachine:{plan.StateMachineName}";
        Assert.True(Grants(f.Policy, "s3:GetObject", $"arn:aws:s3:::{DevStore}/passed/*"));
        Assert.True(Grants(f.Policy, "s3:ListBucket", $"arn:aws:s3:::{DevStore}"));
        Assert.True(Grants(f.Policy, "s3:GetObject", $"arn:aws:s3:::{OwnStore}/decisions/*"));
        Assert.True(Grants(f.Policy, "s3:PutObject", $"arn:aws:s3:::{OwnStore}/decisions/*"));
        Assert.True(Grants(f.Policy, "states:StartExecution", machine));
        Assert.True(Grants(f.Policy, "states:ListExecutions", machine));
        Assert.True(Grants(f.Policy, "ssm:GetParameter", $"arn:aws:ssm:us-west-2:{TestAccount}:parameter/scu/test/pipeline/hold"));
        Assert.True(Grants(f.Policy, "sns:Publish", plan.Alerts!.TopicArn));
        // Nothing of the predecessor's but its markers; nothing written but decisions.
        Assert.False(Grants(f.Policy, "s3:GetObject", $"arn:aws:s3:::{DevStore}/deploys/*"));
        Assert.DoesNotContain(Statements(f.Policy), s => Strings(s.GetProperty("Action")).Contains("s3:PutObject")
            && Strings(s.GetProperty("Resource")).Any(r => r != $"arn:aws:s3:::{OwnStore}/decisions/*"));
        Assert.Empty(DeployerPlanner.DenyGaps(f.Policy, plan.DenyPolicy));
    }

    [Fact]
    public void ThePollersListing_IsLimitedToSetMarkers_AndItsOwnDecisions()
    {
        var f = Poller(Plan());
        var lists = Statements(f.Policy).Where(s => Strings(s.GetProperty("Action")).Contains("s3:ListBucket")).ToList();
        Assert.All(lists, s => Assert.True(s.TryGetProperty("Condition", out _), "an unconditioned ListBucket"));
        Assert.Contains(lists, s => s.GetRawText().Contains("passed/sets/*"));
        Assert.Contains(lists, s => s.GetRawText().Contains("decisions/*"));
    }

    [Fact]
    public void APromotedEnvironmentWithoutAlerts_OrWithoutItsCluster_OrWithTwoTenants_IsRefused()
    {
        var quiet = Test();
        quiet.Pipeline!.Alerts = false;
        Assert.Contains("Alerts", Assert.Throws<InvalidOperationException>(() => Plan(quiet)).Message);
        Assert.Contains("cluster", Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(Test(), TestAccount, clientInputs: DeployerClientPlanTests.Distributions)).Message);
        Assert.Contains("tenant", Assert.Throws<InvalidOperationException>(() => Plan(cluster: new DeployerTriggerInputs("scu-test-cluster", new[] { "mp", "xx" }))).Message);
    }

    [Fact]
    public void ThePoller_IsPlannedOnlyWhereThereIsAPredecessor()
    {
        var dev = DeployerClientPlanTests.Config();
        var plan = DeployerPlanner.Plan(dev, DeployerClientPlanTests.TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);
        Assert.Null(plan.Promotion);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.PromotionPoller);
    }

    // ---- the first environment is as it was ----

    [Fact]
    public void AnEnvironmentWithoutAPredecessor_GetsNoneOfThePromotedMachine()
    {
        // Dev heads the chain: it is fed by build records, and gains MarkPassed for its successor in stage D, never a set's loop.
        var dev = DeployerClientPlanTests.Config();
        dev.Pipeline!.Successor = new PipelineNeighbour("test", TestAccount, OwnStore);
        var plan = DeployerPlanner.Plan(dev, DeployerClientPlanTests.TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);
        var s = States(plan);

        Assert.Equal("$.verified", s.GetProperty("Verify").GetProperty("ResultPath").GetString());
        foreach (var name in new[] { "ImageMember", "Bundles", "Decide", "Decided", "DecideFailure" })
            Assert.False(s.TryGetProperty(name, out _), name);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.Decide);
        Assert.False(plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify).Environment.ContainsKey(DeployerEnvironment.Predecessor));
    }
}
