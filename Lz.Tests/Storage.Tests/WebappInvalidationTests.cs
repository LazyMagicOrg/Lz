using Lz.Aws.Webapp;
using Lz.Tests.Build.Tests;

namespace Lz.Tests.Storage.Tests;

/// <summary>
/// What <c>lz deploywebapp</c> and <c>lz deploystaticsite</c> clear from CloudFront (DecoupledCd.md P-9): the path the
/// bundle was synced under, in every environment. Both skipped dev as having "no caching", which is true of the
/// Keycloak topology's CDN component and false of the KVS component, whose cache policy applies in every environment —
/// so a dev deploy could serve hour-old stylesheets and scripts beside a fresh index.html. Outside dev both cleared
/// "/*", every app and host on the distribution.
/// </summary>
public class WebappInvalidationTests
{
    private static string LzRoot() => PackageHandlingScratchBuild.FindLzRepoRoot();

    [Theory]
    [InlineData("seller/", "/seller*")]
    [InlineData("seller", "/seller*")]
    [InlineData("/explore/", "/explore*")]
    [InlineData("apps/admin/", "/apps/admin*")]
    public void AnAppUnderAPath_ClearsOnlyThatPath(string synced, string expected)
    {
        // "/seller*" rather than "/seller/*": CFRequest serves "/seller" too, rewritten to the app's index.html.
        Assert.Equal(expected, WebappDeployer.InvalidationPath(synced));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("  ")]
    public void AnAppAtTheRoot_OwnsEveryPath(string synced)
    {
        Assert.Equal("/*", WebappDeployer.InvalidationPath(synced));
    }

    [Fact]
    public void TheBasePath_IsWhereThePublishedFrameworkSits()
    {
        var root = Path.Combine(Path.GetTempPath(), "lz-webapp-base-" + Guid.NewGuid().ToString("N"));
        try
        {
            var underPath = Path.Combine(root, "under");
            Directory.CreateDirectory(Path.Combine(underPath, "seller", "_framework"));
            File.WriteAllText(Path.Combine(underPath, "seller", "_framework", "dotnet.js"), "");

            var atRoot = Path.Combine(root, "root");
            Directory.CreateDirectory(Path.Combine(atRoot, "_framework"));

            var noFramework = Path.Combine(root, "static");
            Directory.CreateDirectory(noFramework);
            File.WriteAllText(Path.Combine(noFramework, "index.html"), "");

            Assert.Equal("seller/", WebappDeployer.BundleBasePath(underPath));
            Assert.Equal("", WebappDeployer.BundleBasePath(atRoot));
            Assert.Equal("", WebappDeployer.BundleBasePath(noFramework));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AppConfig_IsFetchedFresh_WhereverItSits()
    {
        // The fixed list named "appConfig.js" at the bundle root. The apps keep it in their UI library, under
        // _content/BlazorUI/, so it took the one-hour baseline like any stylesheet.
        var files = WebappDeployer.NoCacheFiles(new[]
        {
            "index.html",
            "_content/BlazorUI/appConfig.js",
            "_content/BlazorUI/appConfig.js.br",
            "_content/BlazorUI/appConfig.js.gz",
            "_content/BaseApp.BlazorUI/baseapp.indexbody.js",
            "_framework/dotnet.js",
            "WASMApp.styles.css",
        });

        Assert.Contains(files, f => f.Path == "_content/BlazorUI/appConfig.js" && f.ContentType == "application/javascript");
        Assert.Contains(files, f => f.Path == "index.html");
        Assert.Contains(files, f => f.Path == "_framework/dotnet.js");

        // The compressed siblings are the caller's variants of one entry, and nothing else is swept in.
        Assert.DoesNotContain(files, f => f.Path.EndsWith(".br", StringComparison.Ordinal) || f.Path.EndsWith(".gz", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Path == "WASMApp.styles.css" || f.Path.EndsWith("baseapp.indexbody.js", StringComparison.Ordinal));
        Assert.Equal(files.Count, files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheDeployer_NeverAsksWhichEnvironmentItIsIn()
    {
        var src = File.ReadAllText(Path.Combine(LzRoot(), "Lz.Aws", "Webapp", "WebappDeployer.cs"));

        // The skip must not come back in any form that names dev.
        Assert.DoesNotContain("\"dev\"", src);

        // Both deploy paths clear the cache, and by the path they synced.
        var blazor = Body(src, "public async Task DeployAsync(", "public async Task DeployStaticAsync(");
        var staticSite = Body(src, "public async Task DeployStaticAsync(", "// Private helpers");
        Assert.Contains("await InvalidateAsync(distributionId, InvalidationPath(", blazor);
        Assert.Contains("await InvalidateAsync(distributionId, InvalidationPath(", staticSite);
    }

    [Fact]
    public void BothCommands_LookUpTheDistribution_InEveryEnvironment()
    {
        var cli = File.ReadAllText(Path.Combine(LzRoot(), "Lz.Cli", "Program.cs"));

        foreach (var registration in new[] { "private static void RegisterDeployWebappCommand(", "private static void RegisterDeployStaticSiteCommand(" })
        {
            var start = cli.IndexOf(registration, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{registration}' was not found");
            var handler = cli[start..cli.IndexOf("root.AddCommand(cmd);", start, StringComparison.Ordinal)];

            Assert.Contains("WebappDeployer.FindDistributionIdAsync(", handler);
            Assert.DoesNotContain("Equals(\"dev\"", handler);
        }
    }

    private static string Body(string src, string from, string to)
    {
        var start = src.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' was not found");
        var end = src.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' was not found after '{from}'");
        return src[start..end];
    }
}
