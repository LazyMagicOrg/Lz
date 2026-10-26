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
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig? c = null)
        => DeployerPlanner.Plan(c ?? Test(), TestAccount, clientInputs: DeployerClientPlanTests.Distributions);

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
        Assert.Equal(("$.set", "ImageMember"), (s.GetProperty("Verify").GetProperty("ResultPath").GetString(), s.GetProperty("Verify").GetProperty("Next").GetString()));

        var image = s.GetProperty("ImageMember");
        Assert.Equal("Pass", image.GetProperty("Type").GetString());
        var p = image.GetProperty("Parameters");
        Assert.Equal(("$.set.image.record", "$.set.image.target", "$.set.image.verified"),
            (p.GetProperty("record.$").GetString(), p.GetProperty("target.$").GetString(), p.GetProperty("verified.$").GetString()));
        Assert.Equal("Prepare", image.GetProperty("Next").GetString());
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
        Assert.Equal("Decide", map.GetProperty("Next").GetString());
        Assert.Equal("RecordFailure", map.GetProperty("Catch")[0].GetProperty("Next").GetString());

        var inner = map.GetProperty("ItemProcessor");
        Assert.Equal("DeployBundle", inner.GetProperty("StartAt").GetString());
        var st = inner.GetProperty("States");
        Assert.Equal(("VerifyBundle", "RecordBundle"), (st.GetProperty("DeployBundle").GetProperty("Next").GetString(), st.GetProperty("VerifyBundle").GetProperty("Next").GetString()));
        Assert.True(st.GetProperty("RecordBundle").GetProperty("End").GetBoolean());
        // A Catch cannot leave a Map's processor: a bundle's failure is the Map's, caught there.
        Assert.All(st.EnumerateObject(), x => Assert.False(x.Value.TryGetProperty("Catch", out _), x.Name));

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
