using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The hold in the plan (DecoupledCd.md §12 P5 stage J, §5.3 rule 5): <c>/{sk}/{env}/pipeline/hold</c>, read before every
/// automated start - by dev's record-event start function and by a promoted environment's poller - and never inside the
/// machine; every change to it alerted, through a rule the alerts topic admits.
/// </summary>
public class HoldPlanTests
{
    private static readonly DeployerTriggerInputs Mp = new("scu-dev-cluster", new[] { "mp" });

    private static SystemConfig Dev(bool trigger = true)
    {
        var c = DeployerClientPlanTests.Config();
        c.Pipeline!.DeployOnBuildRecord = trigger;
        c.Pipeline.Alerts = true;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig c, bool inputs = true)
        => DeployerPlanner.Plan(c, DeployerClientPlanTests.TargetAccount, triggerInputs: inputs ? Mp : null, clientInputs: DeployerClientPlanTests.Distributions);

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };

    [Fact]
    public void DevsStartFunction_ReadsTheHoldBeforeEveryStart()
    {
        var plan = Plan(Dev());
        Assert.Equal("/scu/dev/pipeline/hold", plan.HoldParameter);
        var start = plan.Functions.Single(f => f.Handler == DeployerHandlers.Start);
        Assert.Equal("/scu/dev/pipeline/hold", start.Environment[DeployerEnvironment.HoldParameter]);
        Assert.Contains(Statements(start.Policy), s => Strings(s.GetProperty("Action")).Contains("ssm:GetParameter")
            && Strings(s.GetProperty("Resource")).Contains("arn:aws:ssm:us-west-2:503947800380:parameter/scu/dev/pipeline/hold"));
        Assert.Empty(DeployerPlanner.DenyGaps(start.Policy, plan.DenyPolicy));
    }

    [Fact]
    public void EveryChangeToTheHold_IsAlerted_ByARuleTheTopicAdmits()
    {
        var alerts = Plan(Dev()).Alerts!;
        Assert.Equal("scu-dev-pipeline-hold-changed", alerts.HoldChangedRuleName);
        var pattern = JsonDocument.Parse(alerts.HoldChangedPattern!).RootElement;
        Assert.Equal(("aws.ssm", "Parameter Store Change", "/scu/dev/pipeline/hold"),
            (pattern.GetProperty("source")[0].GetString(), pattern.GetProperty("detail-type")[0].GetString(), pattern.GetProperty("detail").GetProperty("name")[0].GetString()));

        var events = Statements(alerts.TopicPolicy).Single(s => s.GetProperty("Sid").GetString() == CrossAccount.AlertsFailedDeploySid);
        Assert.Equal(new[] { alerts.FailedDeployRuleArn, alerts.HoldChangedRuleArn! },
            Strings(events.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn")));
    }

    [Fact]
    public void APromotedEnvironment_HasAHold_TooForItsPoller()
    {
        var test = DeployerClientPlanTests.Config();
        test.Environment = "test";
        test.SystemSuffix = "d5e0-b27f";
        test.Pipeline!.TargetAccountId = "456687462751";
        test.Pipeline.BuildSuffix = "4df6-b9c6";
        test.Pipeline.Alerts = true;
        test.Pipeline.Predecessor = new PipelineNeighbour("dev", "503947800380", "scu-dev-deploy-evidence-4df6-b9c6");
        var plan = DeployerPlanner.Plan(test, "456687462751", triggerInputs: new DeployerTriggerInputs("scu-test-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions);
        Assert.Equal("/scu/test/pipeline/hold", plan.HoldParameter);
        Assert.Equal(plan.HoldParameter, plan.Promotion!.HoldParameter);
        Assert.NotNull(plan.Alerts!.HoldChangedRuleName);
    }

    [Fact]
    public void WithNoAutomatedStart_ThereIsNoHold_AndTheTopicPolicyIsAsBefore()
    {
        var plan = Plan(Dev(trigger: false), inputs: false);
        Assert.Null(plan.HoldParameter);
        Assert.Null(plan.Alerts!.HoldChangedRuleName);
        var events = Statements(plan.Alerts.TopicPolicy).Single(s => s.GetProperty("Sid").GetString() == CrossAccount.AlertsFailedDeploySid);
        Assert.Equal(JsonValueKind.String, events.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").ValueKind);
    }
}
