using Lz.Tests.Build.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Pins WHERE the alerts (P2 stage D3) are applied and removed. The plans' tests cannot see whether the bootstrappers act on
/// them, and Lz.Tests has no AWS harness, so — like <see cref="TriggerCallSiteTests"/> — this reads the source. It fails when
/// an off switch is dropped, when something publishes before the topic exists, and when a written alarm or policy is no
/// longer read back.
/// </summary>
public class AlertsCallSiteTests
{
    private static string Source(params string[] path)
    {
        var file = Path.Combine(new[] { PackageHandlingScratchBuild.FindLzRepoRoot() }.Concat(path).ToArray());
        Assert.True(File.Exists(file), $"source not found at {file}");
        return File.ReadAllText(file);
    }

    private static int At(string src, string call, string where)
    {
        var i = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{where} no longer contains {call}");
        return i;
    }

    /// <summary>The body of the method that starts at <paramref name="signature"/>, up to the next method's doc comment.</summary>
    private static string Body(string src, string signature)
    {
        var start = At(src, signature, "the source");
        var end = src.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? src[start..] : src[start..end];
    }

    [Fact]
    public void TheDeployerBootstrap_CreatesTheTopicBeforeAnythingThatPublishesToIt()
    {
        const string file = "DeployerBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var topic = At(src, "await AlertsApply.EnsureTopicAsync(sns, alertsTopic);", file);
        Assert.True(topic < At(src, "foreach (var fn in plan.Functions)", file), "the topic is created after the sweep that publishes to it");
        Assert.True(topic < At(src, "await TriggerApply.StartOnRecordsAsync(", file), "the topic is created after the queue alarm that notifies it");
        Assert.True(topic < At(src, "await AlertsApply.WireAsync(events, lambda, cloudWatch, alerts);", file), "the topic is created after the rule that targets it");
    }

    [Fact]
    public void TheDeployerBootstrap_WiresTheAlertsAfterTheMachine_AndOtherwiseRemovesThem()
    {
        const string file = "DeployerBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var machine = At(src, "await EnsureStateMachineAsync(sfn, plan, roleArn, accountId, region);", file);
        var wire = At(src, "await AlertsApply.WireAsync(events, lambda, cloudWatch, alerts);", file);
        var remove = At(src, "await AlertsApply.RemoveAsync(events, cloudWatch, DeployerPlanner.FailedDeployRuleName(config),", file);

        Assert.True(machine < wire, "the failed-deploy rule is wired before the machine whose failures it reports exists");
        Assert.True(wire < remove, "the removal is no longer the else of the wiring");
        Assert.Contains("DeployerPlanner.CorroborateScheduleRuleName(config), DeployerPlanner.CorroborateErrorsAlarmName(config));", src[remove..]);
    }

    [Fact]
    public void TheSchedule_IsWrittenAfterThePermissionItNeeds()
    {
        const string file = "AlertsApply.cs";
        var body = Body(Source("Lz.Aws", "Pipeline", file), "public static async Task WireAsync(");

        var permission = At(body, "await TriggerApply.EnsureInvokePermissionAsync(", file);
        var schedule = At(body, "await EnsureScheduleRuleAsync(events, plan);", file);
        var target = At(body, "await TriggerApply.EnsureOnlyTargetAsync(events, eventBusName: null, plan.ScheduleRuleName,", file);

        Assert.True(permission < schedule && schedule < target, "the schedule could fire into a function EventBridge may not invoke");
        At(body, "await TriggerApply.EnsureOnlyTargetAsync(events, eventBusName: null, plan.FailedDeployRuleName,", file);
        At(body, "await EnsureErrorsAlarmAsync(cloudWatch, plan);", file);
    }

    [Fact]
    public void TheTopicsPolicy_AndEveryAlarmsActions_AreReadBack()
    {
        var alerts = Source("Lz.Aws", "Pipeline", "AlertsApply.cs");
        var topic = Body(alerts, "public static async Task EnsureTopicAsync(");
        var set = At(topic, "await sns.SetTopicAttributesAsync(", "EnsureTopicAsync");
        var readBack = At(topic, "if (!CrossAccount.SamePolicy(plan.TopicPolicy, after))", "EnsureTopicAsync");
        Assert.True(set < readBack);

        At(alerts, "await TriggerApply.RequireAlarmActionsAsync(cloudWatch, plan.CorroborateErrorsAlarmName, actions);", "AlertsApply.cs");

        var trigger = Source("Lz.Aws", "Pipeline", "TriggerApply.cs");
        At(trigger, "AlarmActions = alarmActions.ToList(),", "TriggerApply.cs");
        At(trigger, "await RequireAlarmActionsAsync(cloudWatch, alarm, alarmActions);", "TriggerApply.cs");

        // BOTH QUEUE ALARMS CARRY THEIR PLAN'S ACTIONS — including the empty list that turns alerts off.
        var forward = Body(trigger, "public static async Task ForwardRecordsAsync(");
        var start = Body(trigger, "public static async Task StartOnRecordsAsync(");
        Assert.Contains("plan.AlarmActions);", forward);
        Assert.Contains("plan.AlarmActions);", start);
    }

    [Fact]
    public void ARecordedAnomaly_IsFoundByListingItsKey_NotByReadingIt()
    {
        // A read of a missing key answers 404 only to a principal holding s3:ListBucket, and the sweep's list grant is
        // conditioned on a prefix a read request does not carry: the read could answer 403 for "not recorded yet", on the
        // one path — an image without a record — that the sweep exists for.
        var adapters = Source("Lz.Aws.Deployer", "Adapters.cs");
        var probe = adapters[At(adapters, "internal sealed class S3EvidenceProbe", "Adapters.cs")..];
        probe = probe[..probe.IndexOf("\n}", StringComparison.Ordinal)];

        Assert.Contains("ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = key, MaxKeys = 1 })", probe);
        Assert.DoesNotContain("GetObjectMetadata", probe);
        Assert.DoesNotContain("GetObjectAsync", probe);
    }

    [Fact]
    public void TheSweep_RunsTheStep_WithTheWriterThatWritesOnce()
    {
        var functions = Source("Lz.Aws.Deployer", "Functions.cs");
        var handler = functions[At(functions, "public sealed class CorroborateFunction", "Functions.cs")..];
        handler = handler[..handler.IndexOf("\n}", StringComparison.Ordinal)];

        Assert.Contains("CorroborateStep.RunAsync(", handler);
        Assert.Contains("new S3EvidenceWriter(Clients.S3.Value),", handler);
        Assert.Contains("DateTimeOffset.UtcNow", handler);
    }
}
