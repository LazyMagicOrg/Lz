using Lz.Aws.Compute.Fargate;
using Lz.Aws.Config;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// The pure decision behind alarm-backed deployment rollback. No AWS and no Pulumi — the Fargate
/// component only translates this.
///
/// <para>What is worth pinning here is not that the happy path works, but the two ways this feature
/// could exist and silently do nothing: an alarm created with dimensions that match no metric (which
/// under <c>notBreaching</c> can never fire), and a misconfigured threshold quietly coerced to
/// "off". Both are refusals, and both are asserted below.</para>
/// </summary>
public class DeploymentAlarmPolicyTests
{
    [Fact]
    public void NullFargateConfig_IsNone_SoAnUnoptedSystemsPlanIsByteIdentical()
    {
        var decision = DeploymentAlarmPolicy.ForTenantService(null);

        Assert.Equal(DeploymentAlarmDecision.None, decision);
        Assert.False(decision.Enabled);
    }

    [Fact]
    public void KnobUnset_IsNone_WhichIsTheDefaultEveryExistingSystemIsOn()
    {
        // The config object exists and carries the usual sizing; only the new knob is absent.
        // This is the shape every sibling system deploys today, so it must decide "do nothing".
        var decision = DeploymentAlarmPolicy.ForTenantService(
            new FargateConfig { Cpu = 1024, Memory = 2048, DesiredCount = 1 });

        Assert.Equal(DeploymentAlarmDecision.None, decision);
    }

    [Fact]
    public void APositiveThreshold_EnablesTheGateAndCarriesTheNumberThrough()
    {
        var decision = DeploymentAlarmPolicy.ForTenantService(
            new FargateConfig { RollbackOnTarget5xxPerMinute = 5 });

        Assert.True(decision.Enabled);
        Assert.Equal(5, decision.Target5xxPerMinute);
    }

    [Fact]
    public void ZeroIsAValidThreshold_MeaningRollBackOnAnyFiveHundred()
    {
        // Zero must NOT be treated as "off". The alarm compares with GreaterThanThreshold, so a
        // threshold of zero is the strictest useful setting, and reading it as absent would
        // silently disable exactly the strictest thing anyone could ask for.
        var decision = DeploymentAlarmPolicy.ForTenantService(
            new FargateConfig { RollbackOnTarget5xxPerMinute = 0 });

        Assert.True(decision.Enabled);
        Assert.Equal(0, decision.Target5xxPerMinute);
    }

    [Fact]
    public void ANegativeThreshold_IsRefused_NotSilentlyTreatedAsOff()
    {
        // Coercing this to None would disable a rollback someone explicitly asked for, and nothing
        // downstream would ever say so.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => DeploymentAlarmPolicy.ForTenantService(
                new FargateConfig { RollbackOnTarget5xxPerMinute = -1 }));

        Assert.Contains("RollbackOnTarget5xxPerMinute", ex.Message);
    }

    [Theory]
    [InlineData(
        "arn:aws:elasticloadbalancing:us-west-2:503947800380:loadbalancer/app/scu-dev-alb-fccd87d/1a2b3c4d5e6f",
        "app/scu-dev-alb-fccd87d/1a2b3c4d5e6f")]
    [InlineData(
        "arn:aws:elasticloadbalancing:eu-west-1:1:loadbalancer/app/x/y",
        "app/x/y")]
    public void TheLoadBalancerDimensionIsTheArnSuffix(string arn, string expected)
        => Assert.Equal(expected, DeploymentAlarmPolicy.LoadBalancerDimension(arn));

    [Theory]
    [InlineData("")]
    [InlineData("not-an-arn")]
    [InlineData("arn:aws:elasticloadbalancing:us-west-2:1:targetgroup/tg/abc")]   // right service, wrong resource
    [InlineData("arn:aws:elasticloadbalancing:us-west-2:1:loadbalancer/")]        // marker present, nothing after it
    public void AnUnparseableAlbArn_Throws_RatherThanYieldingAnAlarmThatCanNeverFire(string arn)
    {
        // THE point of this class. An empty or wrong LoadBalancer dimension produces an alarm that
        // matches no metric; it then sits in INSUFFICIENT_DATA, and because missing data is treated
        // as not-breaching it never fires. The service would reference a guard that cannot act, and
        // nothing would report it. Failing the deploy is the only outcome that stays honest.
        Assert.Throws<FormatException>(() => DeploymentAlarmPolicy.LoadBalancerDimension(arn));
    }

    [Fact]
    public void ANullAlbArn_Throws_ForTheSameReason()
        => Assert.Throws<FormatException>(() => DeploymentAlarmPolicy.LoadBalancerDimension(null!));

    // ---------------------------------------------------------------------------------------
    // Where the knob is READ from. An adversarial review found that the tenant service component
    // handed the merger an EMPTY system config, so `tenant ?? system ?? default` collapsed to
    // `tenant ?? default` and every system-level Fargate value was read by nothing. That is fixed
    // (the real SystemConfig is now threaded through ITenantServiceComponent.Deploy); these pin the
    // precedence so it cannot silently collapse again.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ASystemLevelValueIsHonoured_TheBugThatMadeThisKnobInert()
    {
        var system = new AwsSystemConfig { Fargate = new FargateConfig { RollbackOnTarget5xxPerMinute = 5 } };
        var tenant = new AwsTenantConfig();   // no Fargate block, as Scutara's tenant config has none

        var effective = AwsConfigMerger.GetEffectiveFargateConfig(system, tenant);

        Assert.True(DeploymentAlarmPolicy.ForTenantService(effective).Enabled);
        Assert.Equal(5, effective.RollbackOnTarget5xxPerMinute);
    }

    [Fact]
    public void ATenantLevelValueOverridesTheSystemOne()
    {
        var system = new AwsSystemConfig { Fargate = new FargateConfig { RollbackOnTarget5xxPerMinute = 5 } };
        var tenant = new AwsTenantConfig { Fargate = new FargateConfig { RollbackOnTarget5xxPerMinute = 50 } };

        var effective = AwsConfigMerger.GetEffectiveFargateConfig(system, tenant);

        Assert.Equal(50, effective.Target5xxOrThrow());
    }

    [Fact]
    public void AnEmptySystemHalfStillFallsBackToDefaults_WhichIsWhatMadeTheBugInvisible()
    {
        // The whole reason this went unnoticed: with both halves empty the merger yields
        // FargateConfig defaults, and on every workspace on this machine the system block's values
        // happen to EQUAL those defaults - so the block was indistinguishable from its absence.
        var effective = AwsConfigMerger.GetEffectiveFargateConfig(new AwsSystemConfig(), new AwsTenantConfig());

        Assert.Equal(new FargateConfig().DesiredCount, effective.DesiredCount);
        Assert.Equal(new FargateConfig().LogRetentionDays, effective.LogRetentionDays);
        Assert.Null(effective.RollbackOnTarget5xxPerMinute);
    }
}

internal static class FargateConfigTestExtensions
{
    /// <summary>Reads the knob, failing loudly rather than returning a nullable into an assertion.</summary>
    internal static int Target5xxOrThrow(this FargateConfig c)
        => c.RollbackOnTarget5xxPerMinute ?? throw new InvalidOperationException("knob not set");
}
