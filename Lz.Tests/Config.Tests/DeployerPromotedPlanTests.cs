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

    // ---- P3 I2: a promoted environment plans its set's config before the image moves ----

    /// <summary>Test as P3 I2 plans it: config and tooling accepted, Infrastructure: plan (or as given).</summary>
    private static SystemConfig TestPlanningConfig(string infrastructure = PipelineConfig.InfrastructurePlan)
    {
        var c = Test();
        c.Pipeline!.Classes = new List<string> { "image", "client", "config", "tooling" };
        c.Pipeline.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "config", Artifacts = new List<string> { "config" } });
        c.Pipeline.Repositories.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        c.State = new StateConfig
        {
            Backend = "s3://scu-test-pulumi-state-d5e0-b27f?region=us-west-2",
            SecretsProvider = "awskms://alias/scu-test-pulumi-key-d5e0-b27f",
        };
        c.Pipeline.Infrastructure = infrastructure;
        return c;
    }

    private static PipelineDeployer PlanningPlan(SystemConfig? c = null)
        => DeployerPlanner.Plan(c ?? TestPlanningConfig(), TestAccount, triggerInputs: Cluster, clientInputs: DeployerClientPlanTests.Distributions,
            infraInputs: new DeployerInfraInputs("subnet-0test", "sg-0test"));

    [Fact]
    public void UnderInfrastructurePlan_ASetsConfigIsPlannedHere_ThenTheImage_AndNothingIsApplied()
    {
        var states = States(PlanningPlan());
        Assert.Equal("HasConfig", states.GetProperty("IsRevert").GetProperty("Default").GetString());
        var has = states.GetProperty("HasConfig");
        Assert.Equal("ConfigMember", has.GetProperty("Choices")[0].GetProperty("Next").GetString());
        Assert.Equal("$.set.config", has.GetProperty("Choices")[0].GetProperty("Variable").GetString());
        Assert.Equal("ImageMember", has.GetProperty("Default").GetString());
        var member = states.GetProperty("ConfigMember");
        Assert.Equal("VerifyTooling", member.GetProperty("Next").GetString());
        Assert.Equal("$.set.config.verified", member.GetProperty("Parameters").GetProperty("verified.$").GetString());
        Assert.Equal("$.marker", member.GetProperty("Parameters").GetProperty("marker.$").GetString());   // ImageMember reads it next
        Assert.Equal("Plan", states.GetProperty("VerifyTooling").GetProperty("Next").GetString());
        Assert.Contains("'--env', 'test'", states.GetProperty("Plan").GetProperty("Parameters").GetRawText());
        Assert.Equal("ImageMember", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.False(states.TryGetProperty("Planned", out _));
        Assert.False(states.TryGetProperty("DeployInfrastructure", out _));
        Assert.Equal("RevertImageMember", states.GetProperty("SetHold").GetProperty("Next").GetString());   // a revert skips the config
    }

    [Fact]
    public void UnderInfrastructurePlan_VerifyReadsAConfigRecordByVersion()
    {
        var verify = PlanningPlan().Functions.Single(f => f.Handler == DeployerHandlers.Verify);
        Assert.True(Grants(verify.Policy, "s3:GetObjectVersion", "arn:aws:s3:::scu-build-records-4df6-b9c6/config/*"));
        Assert.False(Grants(Plan().Functions.Single(f => f.Handler == DeployerHandlers.Verify).Policy,
            "s3:GetObjectVersion", "arn:aws:s3:::scu-build-records-4df6-b9c6/config/*"));
    }

    // ---- P3 I4: a promoted environment's image through Pulumi ----

    private static SystemConfig TestThroughPulumi(string infrastructure = PipelineConfig.InfrastructureDeploy)
    {
        var c = TestPlanningConfig(infrastructure);
        c.Pipeline!.ImageDeploy = PipelineConfig.ImageDeployPulumi;
        c.Pipeline.EnforceSignatures = true;
        c.Rollback = new RollbackConfig { PinImageDigest = true };
        return c;
    }

    [Fact]
    public void UnderImageDeployPulumi_ThePromotedImageMember_GoesThroughChooseConfigAndUpdateimage_AfterTheConfig()
    {
        var states = States(PlanningPlan(TestThroughPulumi()));
        Assert.Equal("ImageMember", states.GetProperty("RecordInfrastructure").GetProperty("Next").GetString());   // the config first
        Assert.Equal("Prepare", states.GetProperty("ImageMember").GetProperty("Next").GetString());
        Assert.Equal("ChooseConfig", states.GetProperty("Prepare").GetProperty("Next").GetString());
        Assert.Equal("VerifyImageTooling", states.GetProperty("ChooseConfig").GetProperty("Next").GetString());
        Assert.Contains("'updateimage', '--env', 'test'", states.GetProperty("Deploy").GetProperty("Parameters").GetRawText());
        Assert.Equal("Prepare", states.GetProperty("RevertImageMember").GetProperty("Next").GetString());   // a revert rides the same path
    }

    [Fact]
    public void ImageDeployPulumi_WhenPromotedInto_NeedsTheConfigDeployedHere_NotATrigger()
    {
        var ok = TestThroughPulumi();
        ConfigValidator.Validate(ok, "test.yaml");   // no throw: no DeployOnBuildRecord, and none needed

        var planOnly = TestThroughPulumi(PipelineConfig.InfrastructurePlan);
        Assert.Contains("Infrastructure: deploy", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(planOnly, "test.yaml")).Message);
    }

    // ---- P6, PB-13: a promoted environment's own Gate 2 ----

    private static readonly DeployerGate2Inputs TestGate2Hosts =
        new("match.scutara-test.click", "scutara-test.click", "vpc-0test", new[] { "subnet-0testprivate" });

    private static SystemConfig TestWithGate2()
    {
        var c = TestThroughPulumi();
        c.Pipeline!.Gate2 = PipelineConfig.Gate2On;
        c.Pipeline.Gate2BrowserTests = "Scutara/ScutaraE2ETests";
        c.Pipeline.Repositories!.First(r => r.Class == "image").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        return c;
    }

    private static PipelineDeployer GatedPlan(SystemConfig c)
        => DeployerPlanner.Plan(c, TestAccount, triggerInputs: Cluster, clientInputs: DeployerClientPlanTests.Distributions,
            infraInputs: new DeployerInfraInputs("subnet-0test", "sg-0test"), gate2Inputs: TestGate2Hosts);

    [Fact]
    public void UnderGate2On_ADecidedSet_GoesOnToThisEnvironmentsGate2_WithTheImageMemberBackInPlace()
    {
        var plan = GatedPlan(TestWithGate2());
        var states = States(plan);
        Assert.Equal("GateTheSet", states.GetProperty("Decide").GetProperty("Next").GetString());
        var gate = states.GetProperty("GateTheSet");
        Assert.Equal("$.set.site", gate.GetProperty("Choices")[0].GetProperty("Variable").GetString());
        Assert.Equal("Decided", gate.GetProperty("Choices")[0].GetProperty("Next").GetString());   // a site is never gated
        Assert.Equal("Gate2Image", gate.GetProperty("Default").GetString());
        var image = states.GetProperty("Gate2Image");
        Assert.Equal("Gate2Start", image.GetProperty("Next").GetString());
        Assert.Equal("$.set.image.verified", image.GetProperty("Parameters").GetProperty("verified.$").GetString());
        Assert.Equal("$.set.image.target", image.GetProperty("Parameters").GetProperty("target.$").GetString());
        Assert.False(states.TryGetProperty("GatesThisClass", out _));   // dev's entry, from Record, has no place in a promoted machine
        Assert.Equal("Reverted", states.GetProperty("RecordRevert").GetProperty("Next").GetString());   // a revert is never gated

        // What it reads: the service a promoted image member rolls, not a trigger's routes.
        var start = plan.Functions.Single(f => f.Handler == DeployerHandlers.Gate2Start);
        Assert.Contains("scu-test-cluster", start.Environment[DeployerEnvironment.Gate2Set]);
        Assert.Contains("scu-mp-aiphost", start.Environment[DeployerEnvironment.Gate2Set]);
    }

    [Fact]
    public void Gate2_WhenPromotedInto_IsValid_WithoutATrigger()
    {
        var c = TestWithGate2();
        ConfigValidator.Validate(c, "test.yaml");   // no throw: DeployOnBuildRecord is a promoted environment's poller's to replace
        c.Pipeline!.Predecessor = null;
        Assert.Contains("Pipeline.Gate2 is on", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(c, "test.yaml")).Message);
    }

    [Fact]
    public void WithoutInfrastructure_ThePromotedMachineHasNoConfigStep()
    {
        var states = States(Plan());
        Assert.Equal("ImageMember", states.GetProperty("IsRevert").GetProperty("Default").GetString());
        Assert.False(states.TryGetProperty("HasConfig", out _));
        Assert.False(states.TryGetProperty("VerifyTooling", out _));
    }

    [Fact]
    public void UnderInfrastructureDeploy_ASetsConfigIsAppliedHere_ThenTheImage()
    {
        // P3 I3: plan, approve (a pass-through: test requires no approval), apply, the empty second preview, the evidence - and only
        // then the image member. The config's branch never ends the execution: the set does, at Decide.
        var states = States(PlanningPlan(TestPlanningConfig(PipelineConfig.InfrastructureDeploy)));
        Assert.Equal("ApproveInfrastructure", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.Equal("DeployInfrastructure", states.GetProperty("ApproveInfrastructure").GetProperty("Next").GetString());
        Assert.Contains("'infradeploy', '--env', 'test'", states.GetProperty("DeployInfrastructure").GetProperty("Parameters").GetRawText());
        Assert.Equal("VerifyInfrastructure", states.GetProperty("DeployInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("RecordInfrastructure", states.GetProperty("VerifyInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("ImageMember", states.GetProperty("RecordInfrastructure").GetProperty("Next").GetString());
        Assert.False(states.TryGetProperty("InfrastructureDeployed", out _));
        Assert.False(states.TryGetProperty("Planned", out _));
        Assert.Equal("RevertImageMember", states.GetProperty("SetHold").GetProperty("Next").GetString());
    }

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
    public void UnderApproval_APromotedSetWithNoConfig_WaitsOnceAtApprove_ThenItsAnswerIsChecked()
    {
        var states = States(Plan(Test(approval: true)));
        Assert.Equal("Approve", states.GetProperty("IsRevert").GetProperty("Default").GetString());
        Assert.Equal("RevertImageMember", states.GetProperty("SetHold").GetProperty("Next").GetString());   // a revert is never approved here

        var approve = states.GetProperty("Approve");
        Assert.Equal("arn:aws:states:::sns:publish.waitForTaskToken", approve.GetProperty("Resource").GetString());
        Assert.Equal(86400, approve.GetProperty("HeartbeatSeconds").GetInt32());
        Assert.Equal("$.approval", approve.GetProperty("ResultPath").GetString());
        Assert.Equal("CheckApproval", approve.GetProperty("Next").GetString());
        var parameters = approve.GetProperty("Parameters");
        Assert.Equal("arn:aws:sns:us-west-2:1:deploys", parameters.GetProperty("TopicArn").GetString());
        Assert.DoesNotContain("Task.Token", parameters.GetProperty("Message.$").GetString());   // an email is no place for a credential
        Assert.Contains("lz approve", parameters.GetProperty("Message.$").GetString());
        Assert.Equal("$$.Task.Token", parameters.GetProperty("MessageAttributes").GetProperty(ApprovalAnswer.TokenAttribute).GetProperty("StringValue.$").GetString());

        var catches = approve.GetProperty("Catch").EnumerateArray().ToList();
        Assert.Equal((ApprovalAnswer.RejectedError, "RecordFailure", "$.error"),
            (catches[0].GetProperty("ErrorEquals")[0].GetString(), catches[0].GetProperty("Next").GetString(), catches[0].GetProperty("ResultPath").GetString()));
        Assert.Equal(("States.Timeout", "ApprovalTimedOut"), (catches[1].GetProperty("ErrorEquals")[0].GetString(), catches[1].GetProperty("Next").GetString()));
        Assert.Equal(ApprovalAnswer.TimedOutError, states.GetProperty("ApprovalTimedOut").GetProperty("Result").GetProperty("Error").GetString());
        Assert.Equal("$.error", states.GetProperty("ApprovalTimedOut").GetProperty("ResultPath").GetString());

        var check = states.GetProperty("CheckApproval");
        Assert.Equal("ApprovalDiffers", check.GetProperty("Default").GetString());
        var plain = check.GetProperty("Choices").EnumerateArray().Single(c => c.GetProperty("Next").GetString() == "ImageMember").Compact();
        Assert.Contains("\"StringEqualsPath\":\"$.set.markerId\"", plain);
        Assert.Contains("\"StringEqualsPath\":\"$.marker.versionId\"", plain);
        Assert.DoesNotContain("planVersionId", plain);
        Assert.Equal("RecordFailure", states.GetProperty("ApprovalDiffers").GetProperty("Next").GetString());
        Assert.Equal("$.approval", states.GetProperty("ImageMember").GetProperty("Parameters").GetProperty("approval.$").GetString());
    }

    [Fact]
    public void WithoutApproval_APromotedMachine_HasNoApproveState()
    {
        var states = States(Plan());
        Assert.False(states.TryGetProperty("Approve", out _));
        Assert.False(states.TryGetProperty("CheckApproval", out _));
        Assert.Equal("ImageMember", states.GetProperty("IsRevert").GetProperty("Default").GetString());
    }

    [Fact]
    public void UnderApproval_AConfigsPlanIsWhatIsApproved_BeforeAnythingIsApplied()
    {
        var c = TestThroughPulumi();
        c.Pipeline!.Approval = new PipelineApprovalConfig { Required = true, NotifyTopicArn = "arn:aws:sns:us-west-2:1:deploys" };
        ConfigValidator.Validate(c, "test.yaml");   // admitted after a predecessor

        var states = States(PlanningPlan(c));
        Assert.Equal("Approve", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.Equal("Approve", states.GetProperty("HasConfig").GetProperty("Default").GetString());
        Assert.False(states.TryGetProperty("ApproveInfrastructure", out _));
        var config = states.GetProperty("CheckApproval").GetProperty("Choices").EnumerateArray()
            .Single(ch => ch.GetProperty("Next").GetString() == "DeployInfrastructure").Compact();
        Assert.Contains("\"StringEqualsPath\":\"$.planned.versionId\"", config);
        Assert.Contains("\"StringEqualsPath\":\"$.set.markerId\"", config);
    }

    [Fact]
    public void UnderApproval_AFirstEnvironment_StillRefusesAnInfrastructureDeploy()
    {
        var c = TestThroughPulumi();
        c.Pipeline!.Predecessor = null;
        c.Pipeline.Approval = new PipelineApprovalConfig { Required = true, NotifyTopicArn = "arn:aws:sns:us-west-2:1:deploys" };
        Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(c, "dev.yaml"));
    }

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
    public void Verify_ReadsEachMembersRecordAtTheMarkersVersion_OnBothSidesOfTheAccountBoundary()
    {
        // VerifySet reads a record by its version (GetObject with a VersionId), which S3 authorises as s3:GetObjectVersion.
        var verify = Plan().Functions.Single(f => f.Handler == DeployerHandlers.Verify);
        foreach (var cls in new[] { "image", "client" })
            Assert.True(Grants(verify.Policy, "s3:GetObjectVersion", $"arn:aws:s3:::scu-build-records-4df6-b9c6/{cls}/*"), cls);

        var build = PipelineBootstrapPlanner.Plan(Test(), "147440642635");
        foreach (var cls in new[] { "image", "client" })
            Assert.Contains(build.BuildRecordReadGrant!, st => st["Resource"]!.ToString() == $"arn:aws:s3:::scu-build-records-4df6-b9c6/{cls}/*"
                && st["Action"] is System.Text.Json.Nodes.JsonArray a && a.Select(x => x!.GetValue<string>()).Contains("s3:GetObjectVersion"));
    }

    [Fact]
    public void TheFirstEnvironmentsRecordGrants_AreAsTheyWere()
    {
        var build = PipelineBootstrapPlanner.Plan(DeployerClientPlanTests.Config(), "147440642635");
        Assert.All(build.BuildRecordReadGrant!.Where(st => st["Resource"]!.ToString().EndsWith("/*")),
            st => Assert.Equal("s3:GetObject", st["Action"]!.GetValue<string>()));
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
    public void ThePromotionsImageTarget_IsThePollersTarget_SoBootstrapSeedsTheInputItRolls()
    {
        // P3 J4: a promoted environment has no trigger, so its image input is seeded from this target.
        var plan = Plan();
        var target = plan.Promotion!.ImageTarget!;
        var polled = System.Text.Json.Nodes.JsonNode.Parse(Poller(plan).Environment[DeployerEnvironment.PromotionTarget])!;
        Assert.Equal(
            (polled["cluster"]!.GetValue<string>(), polled["service"]!.GetValue<string>(), polled["container"]!.GetValue<string>(), polled["repository"]!.GetValue<string>()),
            (target.Cluster, target.Service, target.Container, target.Repository));
    }

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
        // Dev heads the chain: it is fed by build records, and gains MarkPassed for its successor (stage D, which needs Gate 2
        // on - DeployerGate2PlanTests), never a set's loop.
        var dev = DeployerClientPlanTests.Config();
        var plan = DeployerPlanner.Plan(dev, DeployerClientPlanTests.TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);
        var s = States(plan);

        Assert.Equal("$.verified", s.GetProperty("Verify").GetProperty("ResultPath").GetString());
        foreach (var name in new[] { "ImageMember", "Bundles", "Decide", "Decided", "DecideFailure" })
            Assert.False(s.TryGetProperty(name, out _), name);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.Decide);
        Assert.False(plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify).Environment.ContainsKey(DeployerEnvironment.Predecessor));
    }
}
