using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Gate 2 in the deployer's plan (DecoupledCd.md §15.5, V-E), under <c>Pipeline.Gate2</c>: off, nothing of it is planned and
/// the machine ends at Record; on, Record goes on to Gate2Start, the build, Gate2Result and the outcome's own end.
/// </summary>
public class DeployerGate2PlanTests
{
    private static SystemConfig Config(string gate2)
    {
        var config = DeployerClientPlanTests.Config();
        config.Pipeline!.DeployOnBuildRecord = true;
        config.Pipeline.Alerts = true;
        config.Pipeline.Gate2 = gate2;
        config.Pipeline.Gate2BrowserTests = "Scutara/ScutaraE2ETests";
        config.Pipeline.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        return config;
    }

    private static PipelineDeployer Plan(string gate2) => DeployerPlanner.Plan(Config(gate2), DeployerClientPlanTests.TargetAccount,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        gate2Inputs: new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc", "subnet-08e81426ad2fcbdba" }));

    private static JsonElement States(PipelineDeployer plan) => JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };

    // ---- off ----

    [Fact]
    public void Off_NothingOfGate2IsPlanned_AndTheMachineEndsAtRecord()
    {
        var plan = Plan("off");

        Assert.Null(plan.Gate2);
        Assert.DoesNotContain(plan.Functions, f => f.Name.Contains("gate2"));
        Assert.True(States(plan).GetProperty("Record").GetProperty("End").GetBoolean());
        Assert.DoesNotContain(States(plan).EnumerateObject(), s => s.Name.StartsWith("Gate2", StringComparison.Ordinal));
        Assert.DoesNotContain("codebuild", plan.RolePolicy);
    }

    // ---- the build account's grants to Gate2Start ----

    private static IEnumerable<System.Text.Json.Nodes.JsonObject> BuildGrants(string gate2)
    {
        var build = PipelineBootstrapPlanner.Plan(Config(gate2), "147440642635");
        return (build.BuildRecordReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>())
            .Concat(build.ArtifactReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>());
    }

    [Fact]
    public void Provisioned_TheBuildAccountGrantsGate2StartTheTestsRecordsTheBrowserListingAndTheBundlesByVersion_AndNoOneElse()
    {
        var gate2 = BuildGrants("provisioned").Where(g => g["Sid"]!.GetValue<string>().StartsWith("Gate2", StringComparison.Ordinal)).ToList();
        string Of(string sid, string field) => gate2.Single(g => g["Sid"]!.GetValue<string>().StartsWith(sid, StringComparison.Ordinal))[field]!.ToJsonString();

        Assert.Equal(3, gate2.Count);
        Assert.Contains("scu-build-records-4df6-b9c6/tests/*", Of("Gate2ReadsTestsRecords", "Resource"));
        Assert.Contains("tests/scutara/scutarae2etests/*", Of("Gate2ListsBrowserTestsRecords", "Condition"));
        Assert.Contains("s3:GetObjectVersion", Of("Gate2ReadsTestBundlesByVersion", "Action"));
        Assert.Contains("scu-artifacts-4df6-b9c6/tests/*", Of("Gate2ReadsTestBundlesByVersion", "Resource"));
        Assert.All(gate2, g => Assert.Equal($"arn:aws:iam::503947800380:role/{DeployerPlanner.Gate2StartRoleName(Config("provisioned"))}",
            g["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>()));
    }

    [Fact]
    public void Off_TheBuildAccountGrantsGate2Nothing()
        => Assert.DoesNotContain(BuildGrants("off"), g => g["Sid"]!.GetValue<string>().StartsWith("Gate2", StringComparison.Ordinal));

    // ---- provisioned: the resources, not the states ----

    [Fact]
    public void Provisioned_PlansGate2sResources_AndLeavesTheMachineAsItIs()
    {
        var plan = Plan("provisioned");

        Assert.NotNull(plan.Gate2);
        Assert.Equal("scu-dev-gate2", plan.Gate2!.ProjectName);
        Assert.True(States(plan).GetProperty("Record").GetProperty("End").GetBoolean());
        Assert.DoesNotContain(States(plan).EnumerateObject(), s => s.Name.StartsWith("Gate2", StringComparison.Ordinal));
        Assert.DoesNotContain("codebuild", plan.RolePolicy);
        Assert.Equal(Plan("off").Definition, plan.Definition);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("maybe")]
    public void AGate2ValueThatIsNotOffProvisionedOrOn_IsRefused(string value)
    {
        var config = Config(value);
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config, "systemconfig.scu.dev.yaml"));
        Assert.Contains($"Pipeline.Gate2 is '{value}'", ex.Message);
    }

    // ---- on: the states ----

    [Fact]
    public void On_RecordGoesOnToGate2_StartThenTheBuildThenTheResult()
    {
        var s = States(Plan("on"));

        Assert.Equal("Gate2Start", s.GetProperty("Record").GetProperty("Next").GetString());
        Assert.False(s.GetProperty("Record").TryGetProperty("End", out _));
        Assert.Equal("Gate2Ours", s.GetProperty("Gate2Start").GetProperty("Next").GetString());
        var ours = s.GetProperty("Gate2Ours");
        Assert.Equal(("$.gate2Start.verdict", "ours", "Gate2Run"),
            (ours.GetProperty("Choices")[0].GetProperty("Variable").GetString(), ours.GetProperty("Choices")[0].GetProperty("StringEquals").GetString(),
             ours.GetProperty("Choices")[0].GetProperty("Next").GetString()));
        Assert.Equal("Gate2Result", ours.GetProperty("Default").GetString());   // superseded: no build, the result records it

        var run = s.GetProperty("Gate2Run");
        Assert.Equal("arn:aws:states:::codebuild:startBuild.sync", run.GetProperty("Resource").GetString());
        Assert.Equal("scu-dev-gate2", run.GetProperty("Parameters").GetProperty("ProjectName").GetString());
        Assert.Equal("$.gate2Start.variables", run.GetProperty("Parameters").GetProperty("EnvironmentVariablesOverride.$").GetString());
        Assert.Equal(DeployerPlanner.Gate2BuildTimeoutSeconds, run.GetProperty("TimeoutSeconds").GetInt32());
        // A failing test fails the build: its failure goes to the result state, which reads the files.
        Assert.Equal("Gate2Result", run.GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal("Gate2Outcome", s.GetProperty("Gate2Result").GetProperty("Next").GetString());
    }

    [Theory]
    [InlineData("Passed", "Gate2Passed", "Succeed")]
    [InlineData("TestsSuperseded", "Gate2Superseded", "Succeed")]
    [InlineData("PostDeployTestsFailed", "PostDeployTestsFailed", "Fail")]
    public void EachOutcome_EndsInItsOwnState(string outcome, string next, string type)
    {
        var s = States(Plan("on"));
        var choice = s.GetProperty("Gate2Outcome").GetProperty("Choices").EnumerateArray().Single(c => c.GetProperty("StringEquals").GetString() == outcome);

        Assert.Equal(next, choice.GetProperty("Next").GetString());
        Assert.Equal(type, s.GetProperty(next).GetProperty("Type").GetString());
    }

    [Fact]
    public void AnythingElse_IsTestsIncomplete_AFailThatTheFailedExecutionRuleAlerts()
    {
        var s = States(Plan("on"));
        Assert.Equal("TestsIncomplete", s.GetProperty("Gate2Outcome").GetProperty("Default").GetString());
        Assert.Equal(("Fail", "TestsIncomplete"), (s.GetProperty("TestsIncomplete").GetProperty("Type").GetString(),
            s.GetProperty("TestsIncomplete").GetProperty("Error").GetString()));
    }

    [Fact]
    public void AGate2Fault_EndsInGate2Fault_NotRecordFailure_SinceTheDeployIsRecorded()
    {
        var s = States(Plan("on"));
        foreach (var state in new[] { "Gate2Start", "Gate2Result" })
            Assert.Equal("Gate2Fault", s.GetProperty(state).GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal("Fail", s.GetProperty("Gate2Fault").GetProperty("Type").GetString());
    }

    [Fact]
    public void AHeldGate2Lease_IsWaitedOut_PastTheLeasesLife()
    {
        var retry = States(Plan("on")).GetProperty("Gate2Start").GetProperty("Retry").EnumerateArray()
            .Single(r => r.GetProperty("ErrorEquals")[0].GetString() == nameof(Gate2LeaseHeld));
        Assert.True(retry.GetProperty("IntervalSeconds").GetInt32() * retry.GetProperty("MaxAttempts").GetInt32() > Lz.Aws.Ops.ServiceLease.Duration.TotalSeconds);
    }

    [Fact]
    public void ThePlannedMachine_HasNoContractGaps()
    {
        var plan = Plan("on");
        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    // ---- on: what it reads, and who may do what ----

    [Fact]
    public void TheSet_IsWhatTheTriggerRollsAndEveryClientApp()
    {
        var set = Plan("on").Gate2!.Set;

        Assert.Equal(new[] { "scu-dev-cluster" }, set.Services.Select(s => s.Cluster).Distinct());
        Assert.NotEmpty(set.Services);
        Assert.Equal(new[] { "adminapp", "sellerapp" }, set.Apps.Select(a => a.App));
        Assert.Equal(Gate2Set.Decode(Function(Plan("on"), DeployerHandlers.Gate2Start).Environment[DeployerEnvironment.Gate2Set]).Encode(), set.Encode());
    }

    [Fact]
    public void OnlyGate2Result_WritesUnderTests_AndNothingElseThereDoes()
    {
        var plan = Plan("on");
        var writers = plan.Functions.Where(f => Statements(f.Policy).Any(st =>
            Strings(st.GetProperty("Action")).Contains("s3:PutObject") && Strings(st.GetProperty("Resource")).Any(r => r.EndsWith("/tests/*", StringComparison.Ordinal))));
        Assert.Equal(new[] { DeployerHandlers.Gate2Result }, writers.Select(f => f.Handler));
    }

    [Fact]
    public void TheGate2Lease_IsReachedByItsKeysAlone_AndTheServiceLeaseByNeither()
    {
        var plan = Plan("on");
        foreach (var handler in new[] { DeployerHandlers.Gate2Start, DeployerHandlers.Gate2Result })
        {
            var leading = Statements(Function(plan, handler).Policy)
                .Where(st => st.TryGetProperty("Condition", out _))
                .SelectMany(st => st.GetProperty("Condition").EnumerateObject())
                .Where(c => c.Name == "ForAllValues:StringLike")
                .SelectMany(c => Strings(c.Value.GetProperty("dynamodb:LeadingKeys")));
            Assert.Equal(new[] { $"{DeployerPlanner.Gate2LeasePrefix}*" }, leading);
        }
        Assert.Equal("lease#gate2#dev", plan.Gate2!.LeaseKey);
    }

    [Fact]
    public void Gate2Start_ReadsTestsRecordsAndTheirZipsByVersion_ListsOnlyTheBrowserTests_AndReadsTheImagesDeployedHere()
    {
        var start = Function(Plan("on"), DeployerHandlers.Gate2Start);
        var statements = Statements(start.Policy).ToList();
        string[] Resources(string sid) => Strings(statements.Single(s => s.GetProperty("Sid").GetString() == sid).GetProperty("Resource")).ToArray();

        Assert.Equal(new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/tests/*" }, Resources("ReadTestsRecords"));
        Assert.Equal(new[] { "arn:aws:s3:::scu-artifacts-4df6-b9c6/tests/*" }, Resources("SignTheTestBundlesByVersion"));
        Assert.Equal(new[] { "arn:aws:s3:::scu-dev-deploy-evidence-4df6-b9c6/deploys/image/*" }, Resources("ReadTheImagesDeployed"));
        var list = statements.Single(s => s.GetProperty("Sid").GetString() == "ListTheBrowserTestsRecords");
        Assert.Equal(new[] { "tests/scutara/scutarae2etests/*" }, Strings(list.GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix")));
        Assert.Equal("tests/scutara/scutarae2etests/", start.Environment[DeployerEnvironment.Gate2BrowserPrefix]);
        // Gate2Start writes nowhere in S3.
        Assert.DoesNotContain(statements.SelectMany(s => Strings(s.GetProperty("Action"))), a => a == "s3:PutObject");
    }

    [Fact]
    public void OnlyGate2Start_MayClaimTheGate2Lease()
    {
        var plan = Plan("on");
        bool Claims(string handler) => Statements(Function(plan, handler).Policy).Any(st => Strings(st.GetProperty("Action")).Contains("dynamodb:PutItem"));
        Assert.True(Claims(DeployerHandlers.Gate2Start));
        Assert.False(Claims(DeployerHandlers.Gate2Result));
    }

    [Fact]
    public void TheMachine_MayRunOnlyGate2sProject()
    {
        var plan = Plan("on");
        var run = Statements(plan.RolePolicy).Single(st => st.GetProperty("Sid").GetString() == "RunGate2sBuild");
        Assert.Equal(new[] { "arn:aws:codebuild:us-west-2:503947800380:project/scu-dev-gate2" }, Strings(run.GetProperty("Resource")));
        Assert.Contains("lambda:InvokeFunction", plan.RolePolicy);
        Assert.Contains("scu-dev-deployer-gate2-start", plan.RolePolicy);
        Assert.Contains("scu-dev-deployer-gate2-result", plan.RolePolicy);
    }

    [Fact]
    public void NeitherGate2Function_MayDeleteAnything()
    {
        var plan = Plan("on");
        foreach (var handler in new[] { DeployerHandlers.Gate2Start, DeployerHandlers.Gate2Result })
            Assert.DoesNotContain(Statements(Function(plan, handler).Policy).SelectMany(st => Strings(st.GetProperty("Action"))),
                a => a.Contains("Delete", StringComparison.Ordinal));
    }

    // ---- the switch's requirements ----


    [Fact]
    public void Gate2WithoutTheTriggerOrAlerts_IsRefusedByTheValidator()
    {
        var config = Config("on");
        config.Pipeline!.Alerts = false;
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config, "systemconfig.scu.dev.yaml"));
        Assert.Contains("Pipeline.Gate2", ex.Message);
    }
}
