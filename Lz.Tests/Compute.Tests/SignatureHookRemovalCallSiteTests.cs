using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Pins WHERE the signature hook is taken off, since the decision's own tests cannot see whether anything calls it.
///
/// <para>Measured 2026-09-13 against dev: with the hook attached and <c>Pipeline.EnforceSignatures</c> deleted,
/// <c>lz deploytenant</c> applied <c>update Service [deploymentConfiguration]</c> and the hook stayed — the Pulumi
/// provider sends ECS no <c>lifecycleHooks</c> at all for an empty list (CloudTrail). The tenant post-deploy step
/// removes it through the SDK instead. Lz.Tests has no Pulumi or AWS harness for that step, so, like
/// <see cref="FargateConfigPlumbingTests"/>, this reads the source: crude, and it fails when the call is dropped
/// or moved behind the digest-pinned <c>continue</c> that every deploy of a pinned service takes.</para>
/// </summary>
public class SignatureHookRemovalCallSiteTests
{
    private static string PostDeploySource()
    {
        var path = Path.Combine(
            PackageHandlingScratchBuild.FindLzRepoRoot(),
            "Lz.Aws", "Topologies", "AwsEcsFargateCognitoDynamodbPostDeployAction.cs");
        Assert.True(File.Exists(path), $"post-deploy source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheTenantPostDeployStep_RemovesAnUndeclaredHook_BeforeThePinnedSkip()
    {
        var src = PostDeploySource();

        var call = src.IndexOf("await RemoveUndeclaredSignatureHookAsync(clusterName, prefix, serviceName);", StringComparison.Ordinal);
        var pinnedSkip = src.IndexOf("task definition is digest-pinned", StringComparison.Ordinal);

        Assert.True(call >= 0, "the post-deploy loop no longer calls RemoveUndeclaredSignatureHookAsync");
        Assert.True(pinnedSkip >= 0, "the digest-pinned skip this test orders against has moved or been reworded");
        Assert.True(call < pinnedSkip, "the hook removal sits after the digest-pinned `continue`, which every pinned deploy takes");
    }

    [Fact]
    public void TheRemovalUsesThePlannersDecision_AndSendsTheKeptHooksExplicitly()
    {
        var src = PostDeploySource();

        Assert.Contains("DeployerPlanner.HooksToKeepAfterRemoval(", src);
        Assert.Contains("LifecycleHooks = keep.Select(i => attached[i]).ToList()", src);
    }
}
