using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Pins WHERE <c>lz deploycontainer</c> refuses a workstation image under <c>Pipeline.EnforceSignatures</c>
/// (DecoupledCd.md §8 item 4). The decision's own tests cannot see whether the command asks it, and Lz.Tests does not
/// reference Lz.Cli, so, like <see cref="SignatureHookRemovalCallSiteTests"/>, this reads the source. It fails when the
/// check is dropped, when it moves after the build it exists to prevent, or when the refusal stops failing the command.
/// </summary>
public class WorkstationImageRefusalCallSiteTests
{
    private static string DeployContainerHandler()
    {
        var path = Path.Combine(PackageHandlingScratchBuild.FindLzRepoRoot(), "Lz.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"CLI source not found at {path}");
        var src = File.ReadAllText(path);

        var start = src.IndexOf("private static void RegisterDeployContainerCommand(", StringComparison.Ordinal);
        Assert.True(start >= 0, "the deploycontainer command's registration was not found");
        var end = src.IndexOf("root.AddCommand(cmd);", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of the deploycontainer registration was not found");
        return src[start..end];
    }

    private const string Check = "DeployerPlanner.RefusalForWorkstationImage(config, svcName) is { } tenantRefusal";

    [Fact]
    public void EachTenantContainer_IsCheckedBeforeItIsBuilt()
    {
        var handler = DeployContainerHandler();

        var tenantLoop = handler.IndexOf("foreach (var (tk, tenantConfig) in tenants)", StringComparison.Ordinal);
        var check = handler.IndexOf(Check, StringComparison.Ordinal);
        var build = handler.IndexOf("await deployer.DeployAsync(", tenantLoop, StringComparison.Ordinal);

        Assert.True(tenantLoop >= 0, "the tenant loop this test orders against has moved or been reworded");
        Assert.True(check >= 0, "deploycontainer no longer asks RefusalForWorkstationImage");
        Assert.True(build >= 0, "the tenant build call this test orders against has moved or been reworded");
        Assert.True(tenantLoop < check && check < build,
            "the refusal no longer sits inside the tenant loop, ahead of the build and push it exists to prevent");
    }

    [Fact]
    public void ARefusal_FailsTheCommand_AndSkipsOnlyThatContainer()
    {
        var handler = DeployContainerHandler();
        var block = handler[handler.IndexOf(Check, StringComparison.Ordinal)..];
        block = block[..block.IndexOf("await deployer.DeployAsync(", StringComparison.Ordinal)];

        Assert.Contains("Environment.ExitCode = 1;", block);
        Assert.Contains("continue;", block);
    }

    [Fact]
    public void SystemContainers_AreNeverChecked()
    {
        // The hook is attached to tenant services only, so the one check in the handler is the tenant loop's.
        var handler = DeployContainerHandler();
        var first = handler.IndexOf("RefusalForWorkstationImage(", StringComparison.Ordinal);

        Assert.Equal(handler.LastIndexOf("RefusalForWorkstationImage(", StringComparison.Ordinal), first);
    }
}
