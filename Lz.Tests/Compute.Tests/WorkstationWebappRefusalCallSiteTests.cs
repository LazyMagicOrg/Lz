using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Pins WHERE <c>lz deploywebapp</c> refuses an app the pipeline deploys (DecoupledCd.md P-8). The decision has its own tests;
/// these read the CLI source, as <see cref="EdgeAndConfigRefusalCallSiteTests"/> does, because Lz.Tests does not reference
/// Lz.Cli. They fail when the check is dropped, when it moves after the deploy it exists to prevent, when a refusal stops
/// failing the command, and when the name the refusal judges and the name the bucket is built from become two names.
/// </summary>
public class WorkstationWebappRefusalCallSiteTests
{
    private static string Handler()
    {
        var path = Path.Combine(PackageHandlingScratchBuild.FindLzRepoRoot(), "Lz.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"CLI source not found at {path}");
        var src = File.ReadAllText(path);

        var start = src.IndexOf("private static void RegisterDeployWebappCommand(", StringComparison.Ordinal);
        Assert.True(start >= 0, "the deploywebapp command's registration was not found");
        var end = src.IndexOf("root.AddCommand(cmd);", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of the deploywebapp registration was not found");
        return src[start..end];
    }

    private const string ConfigLoop = "foreach (var config in configs)";
    private const string TenantLoop = "foreach (var (tk, tenantConfig) in tenants)";
    private const string Check = "DeployerPlanner.RefusalForWorkstationWebapp(config, webappName) is { } webappRefusal";
    private const string Name = "var webappName = Path.GetFileName(webappFolder).ToLowerInvariant();";
    private const string Bucket = "WebappSyncRules.SystemBucketName(config.SystemKey, webappName, config.SystemSuffix)";

    [Fact]
    public void TheRefusal_IsAskedInTheConfigLoop_BeforeAnyTenantAndBeforeEitherDeploy()
    {
        var handler = Handler();

        var configLoop = handler.IndexOf(ConfigLoop, StringComparison.Ordinal);
        var asked = handler.IndexOf(Check, StringComparison.Ordinal);
        var tenantLoop = handler.IndexOf(TenantLoop, StringComparison.Ordinal);
        var staticDeploy = handler.IndexOf("await deployer.DeployStaticAsync(", StringComparison.Ordinal);
        var blazorDeploy = handler.IndexOf("await deployer.DeployAsync(", StringComparison.Ordinal);

        Assert.True(configLoop >= 0, "the config loop this test orders against has moved or been reworded");
        Assert.True(asked >= 0, "deploywebapp no longer asks RefusalForWorkstationWebapp");
        Assert.True(tenantLoop >= 0, "the tenant loop this test orders against has moved or been reworded");
        Assert.True(staticDeploy >= 0 && blazorDeploy >= 0, "the deploy calls this test orders against have moved or been reworded");
        Assert.True(configLoop < asked && asked < tenantLoop && tenantLoop < staticDeploy && tenantLoop < blazorDeploy,
            "the refusal no longer sits in the config loop, ahead of every tenant and of the deploys it exists to prevent");
    }

    [Fact]
    public void ARefusal_FailsTheCommand_AndDeploysNothingForThatConfig()
    {
        var handler = Handler();
        var asked = handler.IndexOf(Check, StringComparison.Ordinal);
        var block = handler[asked..handler.IndexOf(TenantLoop, asked, StringComparison.Ordinal)];
        block = block[..(block.IndexOf("continue;", StringComparison.Ordinal) + "continue;".Length)];

        Assert.Contains("REFUSED:", block);
        Assert.Contains("Environment.ExitCode = 1;", block);
        Assert.EndsWith("continue;", block);
    }

    [Fact]
    public void TheRefusal_JudgesTheNameTheBucketIsBuiltFrom()
    {
        // One variable, declared once before the check, and the only name the system bucket is built from — so the app the
        // refusal lets through is the bucket the deploy writes.
        var handler = Handler();

        var declared = handler.IndexOf(Name, StringComparison.Ordinal);
        Assert.True(declared >= 0, "the web app's name is no longer declared as this test expects");
        Assert.Equal(declared, handler.LastIndexOf("var webappName", StringComparison.Ordinal));
        Assert.True(declared < handler.IndexOf(Check, StringComparison.Ordinal), "the name is declared after the refusal that judges it");

        var bucket = handler.IndexOf(Bucket, StringComparison.Ordinal);
        Assert.True(bucket >= 0, "the system bucket is no longer built from webappName");
        Assert.Equal(bucket, handler.LastIndexOf("WebappSyncRules.SystemBucketName(", StringComparison.Ordinal));
    }
}
