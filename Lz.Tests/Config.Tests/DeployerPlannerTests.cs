using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The in-account deployer's plan (DecoupledCd.md §5, punchlist P2 stage A).
///
/// <para>Every assertion here is about a DOCUMENT — an IAM policy or a state-machine definition —
/// and both fail in ways no applier test would catch: an unscoped PassRole is an escalation nobody
/// notices, and a missing Catch is an execution that dies leaving a request pending forever.</para>
/// </summary>
public class DeployerPlannerTests
{
    private static SystemConfig Config(bool approvalRequired) => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
        Rollback = new RollbackConfig { PinImageDigest = true },
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            Classes = new List<string> { "image" },
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Scutara/ScutaraService", Class = "image",
                        Artifacts = new List<string> { "aiphost" } },
            },
            Approval = approvalRequired
                ? new PipelineApprovalConfig
                  { Required = true, NotifyTopicArn = "arn:aws:sns:us-west-2:1:deploys" }
                : new PipelineApprovalConfig { Required = false },
        },
    };

    private static JsonElement Definition(bool approvalRequired)
        => JsonDocument.Parse(DeployerPlanner.Plan(Config(approvalRequired), "503947800380").Definition)
            .RootElement;

    // ---------------------------------------------------------------------------------------
    //  Execution naming — the idempotency key
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheExecutionNameIsRequestIdAndAttempt()
    {
        Assert.Equal("req-abc123-1", DeployerPlanner.ExecutionName("abc123", 1));
    }

    [Theory]
    [InlineData("sha256:b330844a")]   // the obvious mistake: a raw digest
    [InlineData("a/b")]
    [InlineData("with space")]
    [InlineData("q?x")]
    public void AnIdStepFunctionsWouldRejectIsRefused_NotSanitised(string requestId)
    {
        // Sanitising would be worse than failing: two ids that cleaned up to one name would collapse
        // into a single execution, and the second deploy would silently never run. The name IS the
        // idempotency key, so mangling it defeats the mechanism it exists for.
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeployerPlanner.ExecutionName(requestId, 1));

        Assert.Contains("Refusing rather than sanitising", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AttemptMustStartAtOne(int attempt)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.ExecutionName("abc", attempt));
    }

    [Fact]
    public void ARetryGetsADifferentName_SoItIsNotSwallowedAsADuplicate()
    {
        Assert.NotEqual(
            DeployerPlanner.ExecutionName("abc123", 1),
            DeployerPlanner.ExecutionName("abc123", 2));
    }

    [Fact]
    public void AnOverlongNameIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeployerPlanner.ExecutionName(new string('x', 90), 1));

        Assert.Contains("80", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The role — where an unscoped grant would be an escalation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PassRoleIsScopedToTheServicesOwnRoles()
    {
        // THE ONE THAT MATTERS. A principal that may pass ANY role can register a task definition
        // running as any role in the account — privilege escalation dressed as a deploy.
        var plan = DeployerPlanner.Plan(Config(false), "503947800380");
        using var doc = JsonDocument.Parse(plan.RolePolicy);

        var pass = doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Single(s => s.GetProperty("Action").EnumerateArray().Any(a => a.GetString() == "iam:PassRole"));

        var resources = pass.GetProperty("Resource").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.All(resources, r => Assert.DoesNotContain("role/*", r!));
        Assert.Contains(resources, r => r!.EndsWith("-task"));
        Assert.Contains(resources, r => r!.EndsWith("-execution"));

        // And only to ECS, so it cannot be passed to something else that assumes it.
        Assert.Equal("ecs-tasks.amazonaws.com",
            pass.GetProperty("Condition").GetProperty("StringEquals")
                .GetProperty("iam:PassedToService").GetString());
    }

    [Fact]
    public void TheDeployerCannotDeleteAnything()
    {
        var plan = DeployerPlanner.Plan(Config(false), "503947800380");

        Assert.DoesNotContain("Delete", plan.RolePolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ecs:DeleteService", plan.RolePolicy);
    }

    [Fact]
    public void TheSelfRewriteDenyIsAnExplicitDeny_AndSeparate()
    {
        // Separate from the grant so it is visible rather than buried, and a Deny because an
        // absence of grant can be undone by a later Allow that nobody connects to this decision.
        var plan = DeployerPlanner.Plan(Config(false), "503947800380");
        using var doc = JsonDocument.Parse(plan.DenyPolicy);

        var stmt = doc.RootElement.GetProperty("Statement")[0];
        Assert.Equal("Deny", stmt.GetProperty("Effect").GetString());

        var actions = stmt.GetProperty("Action").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(PipelineBootstrapPlanner.SelfRewriteDenied, actions);
    }

    // ---------------------------------------------------------------------------------------
    //  The state machine
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ItStartsAtVerify_SoNothingDeploysUnverified()
    {
        Assert.Equal("Verify", Definition(false).GetProperty("StartAt").GetString());
    }

    [Fact]
    public void EveryStateThatCanFail_Catches()
    {
        // A state without a Catch dies silently and leaves the request looking pending forever —
        // and the reconciler would keep finding it and starting it again.
        var states = Definition(true).GetProperty("States");

        foreach (var name in new[] { "Verify", "Approve", "Deploy", "VerifyRollout" })
        {
            var s = states.GetProperty(name);
            Assert.True(s.TryGetProperty("Catch", out var c), $"{name} has no Catch");
            Assert.Equal("RecordFailure", c[0].GetProperty("Next").GetString());
        }
    }

    [Fact]
    public void WithoutApproval_ThereIsNoApprovalState_NotADeadOne()
    {
        // A machine carrying a skipped human gate reads as though approval might happen. Dev's
        // machine says what it does: Verify goes straight to Deploy.
        var states = Definition(false).GetProperty("States");

        Assert.False(states.TryGetProperty("Approve", out _));
        Assert.Equal("Deploy", states.GetProperty("Verify").GetProperty("Next").GetString());
    }

    [Fact]
    public void WithApproval_TheGateIsWaitForTaskToken_AndCannotBeBypassed()
    {
        var states = Definition(true).GetProperty("States");
        var approve = states.GetProperty("Approve");

        Assert.Equal("Verify", Definition(true).GetProperty("StartAt").GetString());
        Assert.Equal("Approve", states.GetProperty("Verify").GetProperty("Next").GetString());
        Assert.Contains("waitForTaskToken", approve.GetProperty("Resource").GetString());
        Assert.Equal("Deploy", approve.GetProperty("Next").GetString());
    }

    [Fact]
    public void TheApprovalHeartbeatComesFromConfig()
    {
        var c = Config(true);
        c.Pipeline!.Approval!.HeartbeatSeconds = 3600;

        using var doc = JsonDocument.Parse(DeployerPlanner.Plan(c, "503947800380").Definition);
        Assert.Equal(3600, doc.RootElement.GetProperty("States").GetProperty("Approve")
            .GetProperty("HeartbeatSeconds").GetInt32());
    }

    [Fact]
    public void ThereIsNoPlanState_BecauseClassOneHasNothingToPreview()
    {
        // Plan is the Pulumi preview classes 5-7 need. An empty one here would imply a check that
        // is not happening.
        Assert.False(Definition(false).GetProperty("States").TryGetProperty("Plan", out _));
    }

    [Fact]
    public void DeployNamesATaskDefinition_NeverATag()
    {
        var deploy = Definition(false).GetProperty("States").GetProperty("Deploy");
        var parameters = deploy.GetProperty("Parameters");

        // Everything comes from execution state, so nothing about what is deployed is baked into
        // the machine — and the task definition is what carries the digest.
        Assert.True(parameters.TryGetProperty("TaskDefinition.$", out _));
        Assert.DoesNotContain(":latest", deploy.ToString());
    }

    [Fact]
    public void RecordWritesEvidenceUnderAConditionalWrite()
    {
        var record = Definition(false).GetProperty("States").GetProperty("Record");

        Assert.Equal("*", record.GetProperty("Parameters").GetProperty("IfNoneMatch").GetString());
        Assert.True(record.GetProperty("End").GetBoolean());
    }

    [Fact]
    public void TheFailurePathEndsInAFailState_NotASuccess()
    {
        // An execution that recorded a failure and then succeeded would report green for a deploy
        // that did not happen.
        var states = Definition(false).GetProperty("States");

        Assert.Equal("Failed", states.GetProperty("RecordFailure").GetProperty("Next").GetString());
        Assert.Equal("Fail", states.GetProperty("Failed").GetProperty("Type").GetString());
    }

    [Fact]
    public void EveryFunctionTheDefinitionReferencesIsDeclared()
    {
        // The applier has to create these before the machine can run; a definition naming a
        // function nobody planned is a machine that fails on its first execution.
        var plan = DeployerPlanner.Plan(Config(true), "503947800380");

        foreach (var fn in plan.Functions)
            Assert.Contains($":function:{fn}", plan.Definition);
    }

    [Fact]
    public void TheEvidenceStoreIsPerEnvironment()
    {
        // Unlike the three build-account stores, evidence records what happened in THIS account.
        var dev = DeployerPlanner.Plan(Config(false), "503947800380").EvidenceStore;

        var prodConfig = Config(false);
        prodConfig.Environment = "prod";
        var prod = DeployerPlanner.Plan(prodConfig, "982408502448").EvidenceStore;

        Assert.NotEqual(dev, prod);
        Assert.Contains("dev", dev);
    }
}
