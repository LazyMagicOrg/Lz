using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The writer pin and the read grant on an environment's evidence store (DecoupledCd.md §12 P5 stage D): in a chain of more
/// than one, only each prefix's writer may put under it - a function, never a principal that runs repository code - and no
/// one deletes; the store grants its successor's poller and Verify its markers and the evidence they name; and no deployer
/// role may change the store's policy or lifecycle. An environment alone in the chain keeps the one write-once statement.
/// </summary>
public class EvidencePinPlanTests
{
    private const string DevStore = "scu-dev-deploy-evidence-4df6-b9c6", TestStore = "scu-test-deploy-evidence-d5e0-b27f";
    private const string Dev = "503947800380", Test = "456687462751";

    private static SystemConfig DevConfig(bool successor = true)
    {
        var c = DeployerClientPlanTests.Config();
        c.Pipeline!.DeployOnBuildRecord = true;
        c.Pipeline.Alerts = true;
        c.Pipeline.Gate2 = "on";
        c.Pipeline.Gate2BrowserTests = "Scutara/ScutaraE2ETests";
        c.Pipeline.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        if (successor) c.Pipeline.Successor = new PipelineNeighbour("test", Test, TestStore);
        return c;
    }

    private static PipelineDeployer PlanDev(bool successor = true) => DeployerPlanner.Plan(DevConfig(successor), Dev,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        gate2Inputs: new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc" }));

    private static PipelineDeployer PlanTest()
    {
        var c = DeployerClientPlanTests.Config();
        c.Environment = "test";
        c.SystemSuffix = "d5e0-b27f";
        c.Pipeline!.TargetAccountId = Test;
        c.Pipeline.BuildSuffix = "4df6-b9c6";
        c.Pipeline.Alerts = true;
        c.Pipeline.Predecessor = new PipelineNeighbour("dev", Dev, DevStore);
        return DeployerPlanner.Plan(c, Test, triggerInputs: new DeployerTriggerInputs("scu-test-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions);
    }

    private static JsonObject Statement(PipelineDeployer p, string sid) => p.EvidenceStorePolicy.Single(s => s["Sid"]!.GetValue<string>() == sid);

    /// <summary>The roles a prefix's pin admits; empty when it admits none.</summary>
    private static IReadOnlyList<string> Writers(PipelineDeployer p, string prefix)
    {
        var s = p.EvidenceStorePolicy.Single(st => st["Resource"]?.ToString() == $"arn:aws:s3:::{p.EvidenceStore}/{prefix}*" && st["Effect"]!.GetValue<string>() == "Deny");
        Assert.Equal("s3:PutObject", s["Action"]!.GetValue<string>());
        Assert.Equal("*", s["Principal"]!.GetValue<string>());
        return s["Condition"]?["ArnNotLike"]?["aws:PrincipalArn"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : Array.Empty<string>();
    }

    private static string Role(string account, string name) => $"arn:aws:iam::{account}:role/{name}";

    [Fact]
    public void Dev_PinsEachPrefixToItsWriter()
    {
        var p = PlanDev();
        Assert.Equal(new[] { Role(Dev, "scu-dev-deployer-record-fn"), Role(Dev, "scu-dev-deployer-record-failure-fn") }, Writers(p, "deploys/"));
        Assert.Equal(Writers(p, "deploys/"), Writers(p, "failures/"));
        Assert.Equal(new[] { Role(Dev, "scu-dev-deployer-gate2-result-fn") }, Writers(p, "tests/"));
        Assert.Equal(new[] { Role(Dev, "scu-dev-deployer-mark-passed-fn") }, Writers(p, "passed/"));
        Assert.Equal(new[] { Role(Dev, DeployerPlanner.CorroborateFunctionRoleName(DevConfig())) }, Writers(p, "anomalies/"));
        foreach (var unwritten in new[] { "decisions/", "reverts/", "datasteps/" })
            Assert.Empty(Writers(p, unwritten));
    }

    [Fact]
    public void Test_PinsDecisionsToTheDecisionAndThePoller_AndRevertsToTheirRecord()
    {
        var p = PlanTest();
        Assert.Equal(new[] { Role(Test, "scu-test-deployer-decide-fn"), Role(Test, "scu-test-deployer-poller-fn") }, Writers(p, "decisions/"));
        Assert.Equal(new[] { Role(Test, "scu-test-deployer-record-revert-fn") }, Writers(p, "reverts/"));
        Assert.Empty(Writers(p, "passed/"));   // test writes no marker until P6
        Assert.Empty(Writers(p, "tests/"));    // and runs no Gate 2
    }

    [Fact]
    public void NoOneDeletesEvidence()
    {
        foreach (var p in new[] { PlanDev(), PlanTest() })
        {
            var s = Statement(p, "LzNoOneDeletesEvidence");
            Assert.Equal(("Deny", "*"), (s["Effect"]!.GetValue<string>(), s["Principal"]!.GetValue<string>()));
            Assert.Equal(new[] { "s3:DeleteObject", "s3:DeleteObjectVersion" }, s["Action"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.Null(s["Condition"]);
        }
    }

    [Fact]
    public void DevGrantsItsSuccessorsPollerAndVerify_ItsMarkersAndTheEvidenceTheyName()
    {
        var p = PlanDev();
        var readers = new[] { Role(Test, "scu-test-deployer-verify-fn"), Role(Test, "scu-test-deployer-poller-fn") };
        var list = Statement(p, "LzTheSuccessorListsMarkers");
        Assert.Equal(readers, list["Principal"]!["AWS"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(("s3:ListBucket", $"arn:aws:s3:::{DevStore}", "passed/*"),
            (list["Action"]!.GetValue<string>(), list["Resource"]!.GetValue<string>(), list["Condition"]!["StringLike"]!["s3:prefix"]!.GetValue<string>()));
        var read = Statement(p, "LzTheSuccessorReadsMarkersAndEvidence");
        Assert.Equal(readers, read["Principal"]!["AWS"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(new[] { $"arn:aws:s3:::{DevStore}/passed/*", $"arn:aws:s3:::{DevStore}/deploys/*", $"arn:aws:s3:::{DevStore}/tests/*" },
            read["Resource"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(new[] { "s3:GetObject", "s3:GetObjectVersion" }, read["Action"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void NoDeployerRole_MayChangeAPinnedStoresPolicyOrLifecycle()
    {
        foreach (var p in new[] { PlanDev(), PlanTest() })
            foreach (var action in new[] { "s3:PutBucketPolicy", "s3:DeleteBucketPolicy", "s3:PutLifecycleConfiguration" })
                Assert.True(DeployerPlanner.Denies(p.DenyPolicy, action, $"arn:aws:s3:::{p.EvidenceStore}"), action);
    }

    [Fact]
    public void AnEnvironmentAloneInTheChain_KeepsTheOneWriteOnceStatement_AndItsDeny()
    {
        var p = PlanDev(successor: false);
        Assert.Equal(new[] { WriteOnceStore.Sid }, p.EvidenceStorePolicy.Select(s => s["Sid"]!.GetValue<string>()));
        Assert.False(DeployerPlanner.Denies(p.DenyPolicy, "s3:PutBucketPolicy", $"arn:aws:s3:::{p.EvidenceStore}"));
    }

    [Fact]
    public void ThePinnedPlans_StillHaveNoDenyGaps()
    {
        foreach (var p in new[] { PlanDev(), PlanTest() })
        {
            Assert.Empty(DeployerPlanner.DenyGaps(p.RolePolicy, p.DenyPolicy));
            foreach (var f in p.Functions) Assert.Empty(DeployerPlanner.DenyGaps(f.Policy, p.DenyPolicy));
        }
    }
}
