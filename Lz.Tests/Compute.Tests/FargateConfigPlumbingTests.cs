using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Guards the FIX, not the decision.
///
/// <para><see cref="DeploymentAlarmPolicyTests"/> pins the merger's
/// <c>tenant ?? system ?? default</c> precedence, and every one of those tests calls the merger
/// directly — so all of them stayed green while the bug was live. The bug was never in the merger:
/// <c>AwsFargateTenantServiceComponent</c> handed it <c>new AwsSystemConfig()</c>, deleting the
/// system step at the call site. A test that cannot see the call site cannot see the bug.</para>
///
/// <para>Lz.Tests has no Pulumi mock harness, so the component's <c>Deploy</c> cannot be executed to
/// observe what it passes. Reading the source is the honest second-best: crude, but it fails when
/// the defect returns, which is the entire job. The same repo already tests build files by reading
/// them (<c>CommonPackageHandlingTargetsTests</c>), so this is not a new kind of test here.</para>
///
/// <para>If a Pulumi test harness is ever added, replace this with a real one and delete it.</para>
/// </summary>
public class FargateConfigPlumbingTests
{
    private static string ComponentSource()
    {
        var path = Path.Combine(
            PackageHandlingScratchBuild.FindLzRepoRoot(),
            "Lz.Aws", "Compute", "Fargate", "AwsFargateTenantServiceComponent.cs");
        Assert.True(File.Exists(path), $"component source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheComponentDoesNotHandTheMergerAnEmptySystemConfig()
    {
        // The exact shape of the bug: `GetEffectiveFargateConfig(new AwsSystemConfig(), tenantConfig)`.
        // It silently collapsed `tenant ?? system ?? default` to `tenant ?? default`, so every
        // system-level Fargate value was read by nothing and nothing said so.
        var src = ComponentSource();

        // Matched as the CALL, not as the bare type name: the comment a few lines above the fixed
        // call site quotes `new AwsSystemConfig()` while explaining the bug, and a guard that trips
        // on its own documentation is a guard nobody keeps.
        Assert.DoesNotContain("GetEffectiveFargateConfig(new AwsSystemConfig()", src);
    }

    [Fact]
    public void TheComponentPassesTheRealSystemConfigToTheMerger()
    {
        var src = ComponentSource();

        Assert.Contains("GetEffectiveFargateConfig(systemConfig, tenantConfig)", src);
    }

    [Fact]
    public void DesiredCountComesFromConfigRatherThanBeingHardCoded()
    {
        // `DesiredCount = 1` made FargateConfig.DesiredCount a knob nothing read. Every config on
        // this machine sets 1, so no deployment changed — which is precisely why nothing caught it.
        var src = ComponentSource();

        Assert.Contains("DesiredCount = fargate.DesiredCount", src);
        Assert.DoesNotContain("DesiredCount = 1,", src);
    }
}
