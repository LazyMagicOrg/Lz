using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Pins WHERE <c>lz updateedge</c> and <c>lz updateconfig</c> refuse. Each decision has its own tests; these read the CLI
/// source, as <see cref="WorkstationImageRefusalCallSiteTests"/> does, because Lz.Tests does not reference Lz.Cli. They
/// fail when a check is dropped, when it moves after the work it exists to prevent, when a refusal stops failing the
/// command, or when a dry run is let through: under the refusal the command is closed, not merely careful.
/// </summary>
public class EdgeAndConfigRefusalCallSiteTests
{
    private static string LzRoot() => PackageHandlingScratchBuild.FindLzRepoRoot();

    private static string Handler(string registration)
    {
        var path = Path.Combine(LzRoot(), "Lz.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"CLI source not found at {path}");
        var src = File.ReadAllText(path);

        var start = src.IndexOf(registration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{registration}' was not found");
        var end = src.IndexOf("root.AddCommand(cmd);", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the end of '{registration}' was not found");
        return src[start..end];
    }

    private const string ConfigLoop = "foreach (var config in configs)";
    private const string TenantLoop = "foreach (var (tk, tenantConfig) in tenants)";

    private const string UpdateEdge = "private static void RegisterUpdateEdgeCommand(";
    private const string EdgeCheck = "DeployerPlanner.RefusalForWorkstationEdge(config) is { } edgeRefusal";

    private const string UpdateConfig = "private static void RegisterUpdateConfigCommand(";
    private const string ConfigCheck = "AwsTenantConfigPublisher.RefusalFor(config, ";

    private static void AssertAskedBeforeTheWork(string handler, string check, string work, string command)
    {
        var configLoop = handler.IndexOf(ConfigLoop, StringComparison.Ordinal);
        var asked = handler.IndexOf(check, StringComparison.Ordinal);
        var tenantLoop = handler.IndexOf(TenantLoop, StringComparison.Ordinal);
        var worked = handler.IndexOf(work, StringComparison.Ordinal);

        Assert.True(configLoop >= 0, $"{command}: the config loop this test orders against has moved or been reworded");
        Assert.True(asked >= 0, $"{command} no longer asks its refusal");
        Assert.True(tenantLoop >= 0, $"{command}: the tenant loop this test orders against has moved or been reworded");
        Assert.True(worked >= 0, $"{command}: the call this test orders against has moved or been reworded");
        Assert.True(configLoop < asked && asked < tenantLoop && tenantLoop < worked,
            $"{command}'s refusal no longer sits in the config loop, ahead of every tenant and of the work it exists to prevent");
    }

    private static void AssertARefusalFailsTheCommand(string handler, string check, string command)
    {
        var configLoop = handler.IndexOf(ConfigLoop, StringComparison.Ordinal);
        var asked = handler.IndexOf(check, StringComparison.Ordinal);
        var block = handler[asked..handler.IndexOf(TenantLoop, asked, StringComparison.Ordinal)];

        Assert.Contains("anyFailure = true;", block);
        Assert.Contains("continue;", block);
        // A dry run is refused too: nothing between the loop and the check may consult it.
        Assert.DoesNotContain("dryRun", handler[configLoop..asked]);
        Assert.DoesNotContain("dryRun", block);
        _ = command;
    }

    [Fact]
    public void UpdateEdge_AsksBeforeAnyTenant_AndBeforeTheUpdater() =>
        AssertAskedBeforeTheWork(Handler(UpdateEdge), EdgeCheck, "new AwsEdgeUpdater(", "updateedge");

    [Fact]
    public void UpdateEdge_ARefusal_FailsTheCommand_DryRunIncluded() =>
        AssertARefusalFailsTheCommand(Handler(UpdateEdge), EdgeCheck, "updateedge");

    [Fact]
    public void UpdateConfig_AsksBeforeAnyTenant_AndBeforeThePublish() =>
        AssertAskedBeforeTheWork(Handler(UpdateConfig), ConfigCheck, "AwsTenantConfigPublisher.PublishAsync(", "updateconfig");

    [Fact]
    public void UpdateConfig_ARefusal_FailsTheCommand_DryRunIncluded() =>
        AssertARefusalFailsTheCommand(Handler(UpdateConfig), ConfigCheck, "updateconfig");

    [Fact]
    public void UpdateConfig_ClaimsNoPickupItCannotSee()
    {
        // The help text and the success line said a running AppHost picks the value up within ~60 s. That came from a
        // design spike; on most topologies nothing reads the parameter at all.
        var handler = Handler(UpdateConfig);
        var publisher = File.ReadAllText(Path.Combine(LzRoot(), "Lz.Aws", "Ops", "AwsTenantConfigPublisher.cs"));

        foreach (var text in new[] { handler, publisher })
        {
            Assert.DoesNotContain("poll interval", text);
            Assert.DoesNotContain("~60s", text);
        }
    }
}
