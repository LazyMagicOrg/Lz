using Lz.Tests.Build.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Pins WHERE the trigger is applied and removed (P2 stage D). The plans' tests cannot see whether the bootstrappers act
/// on them, and Lz.Tests has no AWS harness, so — like <c>SignatureHookRemovalCallSiteTests</c> — this reads the source.
/// Crude, and it fails when the off switch is dropped, which is the defect a flag that only creates would be: a trigger
/// switched off in config that keeps deploying every build.
/// </summary>
public class TriggerCallSiteTests
{
    private static string Source(params string[] path)
    {
        var file = Path.Combine(new[] { PackageHandlingScratchBuild.FindLzRepoRoot() }.Concat(path).ToArray());
        Assert.True(File.Exists(file), $"source not found at {file}");
        return File.ReadAllText(file);
    }

    [Fact]
    public void TheBuildAccountBootstrap_ForwardsWhenPlanned_AndOtherwiseRemovesTheRule()
    {
        var src = Source("Lz.Aws", "Pipeline", "PipelineBootstrapper.cs");

        Assert.Contains("plan.RecordForwarding is { } forwarding", src);
        Assert.Contains("TriggerApply.ForwardRecordsAsync(", src);
        Assert.Contains("else if (plan.ForwardRuleToRemove is { } staleRule)", src);
        Assert.Contains("TriggerApply.RemoveRuleAsync(events, eventBusName: null, staleRule,", src);
    }

    [Fact]
    public void TheTargetAccountBootstrap_WiresTheTriggerWhenPlanned_AndOtherwiseRemovesTheStartRule()
    {
        var src = Source("Lz.Aws", "Pipeline", "DeployerBootstrapper.cs");

        var wire = src.IndexOf("TriggerApply.StartOnRecordsAsync(", StringComparison.Ordinal);
        var remove = src.IndexOf("TriggerApply.RemoveRuleAsync(events, DeployerPlanner.TriggerBusName(config), DeployerPlanner.StartRuleName(config),", StringComparison.Ordinal);
        var machine = src.IndexOf("await EnsureStateMachineAsync(sfn, plan, roleArn, accountId, region);", StringComparison.Ordinal);

        Assert.True(wire >= 0, "bootstrapdeployer no longer wires the trigger");
        Assert.True(remove >= 0, "bootstrapdeployer no longer removes the start rule when the trigger is off");
        Assert.True(machine >= 0 && machine < wire, "the trigger is wired before the state machine it starts exists");
    }

    [Fact]
    public void TheStartRule_IsWrittenAfterEverythingItDeliversTo()
    {
        // A rule that exists before the function's invoke permission or its queue would send the first event to a
        // target it may not reach — and a permission failure goes to the dead-letter queue without a retry.
        var src = Source("Lz.Aws", "Pipeline", "TriggerApply.cs");
        var body = src[src.IndexOf("public static async Task StartOnRecordsAsync(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        int At(string call)
        {
            var i = body.IndexOf(call, StringComparison.Ordinal);
            Assert.True(i >= 0, $"StartOnRecordsAsync no longer calls {call}");
            return i;
        }

        var rule = At("await EnsureRuleAsync(");
        Assert.True(At("await EnsureBusPolicyAsync(") < rule);
        Assert.True(At("await EnsureDeadLetterQueueAsync(") < rule);
        Assert.True(At("await EnsureInvokePermissionAsync(") < rule);
        Assert.True(At("await EnsureFailureDestinationAsync(") < rule);
        Assert.True(rule < At("await EnsureOnlyTargetAsync("));
    }
}
