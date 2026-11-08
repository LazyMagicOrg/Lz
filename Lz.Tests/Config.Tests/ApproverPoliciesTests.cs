using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// ProdApproval.md A4-A5: the approver set may answer and read what it answers, and nothing else; the daily set may never answer.
/// </summary>
public class ApproverPoliciesTests
{
    private const string Prod = "982408502448";
    private const string ProdStore = "scu-prod-deploy-evidence-bc79-4fe9", TestStore = "scu-test-deploy-evidence-d5e0-b27f";

    private static SystemConfig ProdConfig(bool approval = true)
    {
        var c = DeployerClientPlanTests.Config(approval: approval);
        c.Environment = "prod";
        c.SystemSuffix = "bc79-4fe9";
        c.Pipeline!.TargetAccountId = Prod;
        c.Pipeline.ChainEvidenceStore = ProdStore;
        c.Pipeline.Predecessor = new PipelineNeighbour("test", "456687462751", TestStore);
        return c;
    }

    private static IEnumerable<JsonObject> Statements(JsonObject policy) => policy["Statement"]!.AsArray().Select(s => s!.AsObject());

    private static IEnumerable<(string Effect, string Action, string Resource)> Grants(JsonObject policy) =>
        Statements(policy).SelectMany(s => s["Action"]!.AsArray().Select(a =>
            (s["Effect"]!.GetValue<string>(), a!.GetValue<string>(), s["Resource"]!.GetValue<string>())));

    [Fact]
    public void TheApproverSet_AnswersAndReadsWhatItAnswers_AndNothingElse()
    {
        var region = ProdConfig().Region;
        var grants = Grants(ApproverPolicies.For(ProdConfig()).Approver).ToHashSet();
        var expected = new HashSet<(string, string, string)>
        {
            ("Allow", "states:SendTaskSuccess", "*"),
            ("Allow", "states:SendTaskFailure", "*"),
            ("Allow", "states:ListExecutions", $"arn:aws:states:{region}:{Prod}:stateMachine:scu-prod-deployer"),
            ("Allow", "states:DescribeExecution", $"arn:aws:states:{region}:{Prod}:execution:scu-prod-deployer:*"),
            ("Allow", "states:GetExecutionHistory", $"arn:aws:states:{region}:{Prod}:execution:scu-prod-deployer:*"),
            ("Allow", "s3:GetObject", $"arn:aws:s3:::{ProdStore}/plans/*"),
            ("Allow", "s3:GetObjectVersion", $"arn:aws:s3:::{ProdStore}/plans/*"),
            ("Allow", "lambda:InvokeFunction", $"arn:aws:lambda:{region}:{Prod}:function:scu-prod-deployer-revert-start"),
            ("Allow", "s3:GetObject", $"arn:aws:s3:::{TestStore}/passed/*"),
            ("Allow", "s3:GetObjectVersion", $"arn:aws:s3:::{TestStore}/passed/*"),
        };
        Assert.Equal(expected, grants);
    }

    [Fact]
    public void TheDailySet_DeniesEveryAnswer_AndMaySetTheHold()
    {
        var daily = ApproverPolicies.For(ProdConfig()).DailyInline;
        var denied = Grants(daily).Where(g => g.Effect == "Deny").Select(g => (g.Action, g.Resource)).ToHashSet();
        Assert.Equal(PipelineBootstrapPlanner.SelfRewriteDenied.Where(a => a.StartsWith("states:SendTask")).Select(a => (a, "*")).ToHashSet(), denied);
        Assert.Equal(3, denied.Count);
        Assert.Contains(("Allow", "ssm:PutParameter", $"arn:aws:ssm:{ProdConfig().Region}:{Prod}:parameter/scu/prod/pipeline/hold"), Grants(daily));
        Assert.Equal(4, Grants(daily).Count());   // three denies and the hold
    }

    [Fact]
    public void AnEnvironmentThePipelineDoesNotKnow_OrThatRequiresNoApproval_IsRefused()
    {
        var noBlock = ProdConfig();
        noBlock.Pipeline = null;
        Assert.Contains("PB-23", Assert.Throws<InvalidOperationException>(() => ApproverPolicies.For(noBlock)).Message);
        Assert.Throws<InvalidOperationException>(() => ApproverPolicies.For(ProdConfig(approval: false)));
        var noStore = ProdConfig();
        noStore.Pipeline!.ChainEvidenceStore = null;
        Assert.Throws<InvalidOperationException>(() => ApproverPolicies.For(noStore));
    }
}
