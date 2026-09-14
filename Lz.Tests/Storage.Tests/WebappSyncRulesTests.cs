using System.Text.Json;
using Lz.Aws.Storage;
using Lz.Aws.Webapp;

namespace Lz.Tests.Storage.Tests;

/// <summary>
/// The rules a web-app bundle is mirrored by (DecoupledCd.md P-2, P4 stage C), held against the real artifacts: the headers
/// <c>lz deploywebapp</c> left on the SellerApp bucket, and the manifest the bundle workflow printed for the first bundle.
/// </summary>
public class WebappSyncRulesTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Storage.Tests", "testdata", "bundles", name);

    /// <summary>key → headers, as read back from the bucket.</summary>
    private static IReadOnlyList<(string Key, WebappObjectHeaders Headers)> LiveHeaders()
        => File.ReadAllLines(Fixture("sellerapp-live-headers-2026-09-14.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Select(f => (f[0], new WebappObjectHeaders(f[1], f[2], f[3] == "-" ? null : f[3])))
            .ToList();

    /// <summary>(path, sha256) pairs, in the order the workflow printed them.</summary>
    private static IReadOnlyList<(string Path, string Sha256Hex)> CiManifest()
        => File.ReadAllLines(Fixture("sellerapp-bundle-34875215076.manifest.txt"))
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Select(l => (l[66..], l[..64]))
            .ToList();

    // ---------------------------------------------------------------------------------------
    //  Against the real artifacts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRules_GiveEveryLiveObjectTheHeadersDeploywebappLeft_ButTheThreeDd82106Changed()
    {
        var live = LiveHeaders();
        Assert.Equal(573, live.Count);

        var paths = live.Select(o => o.Key[WebappSyncRules.StoragePrefix.Length..]).ToList();
        var rules = new WebappHeaderRules(WebappSyncRules.BasePathOf(paths), paths);

        var differing = live
            .Where(o => rules.For(o.Key[WebappSyncRules.StoragePrefix.Length..]) != o.Headers)
            .Select(o => o.Key)
            .ToList();

        // appConfig.js became a no-cache manifest after the deploy that left these headers ran (Lz dd82106): the rules give
        // it no-cache, the bucket still has the baseline. Every other object — 570 of them, across all five passes — agrees.
        Assert.Equal(new[]
        {
            "wwwroot/seller/_content/BlazorUI/appConfig.js",
            "wwwroot/seller/_content/BlazorUI/appConfig.js.br",
            "wwwroot/seller/_content/BlazorUI/appConfig.js.gz",
        }, differing.OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal(new WebappObjectHeaders(WebappSyncRules.NoCache, "application/javascript", "br"),
            rules.For("seller/_content/BlazorUI/appConfig.js.br"));
    }

    [Fact]
    public void TheManifest_IsTheOneTheBundleWorkflowPrinted_ByteForByte()
    {
        // `manifest:  sha256 6b1dc9dd…` in run 34875215076's log: sha256sum of manifest.txt, lines sorted by LC_ALL=C.
        var pairs = CiManifest();
        Assert.Equal(573, pairs.Count);

        // Shuffled first, so the order is the function's and not the fixture's.
        var shuffled = pairs.OrderBy(p => p.Sha256Hex, StringComparer.Ordinal).ToList();

        Assert.Equal("6b1dc9ddc8326dc75d805c651452233229167269c20ecd71982133bf660ad100", WebappSyncRules.ManifestSha256(shuffled));
    }

    [Fact]
    public void TheManifestOrder_IsUtf8ByteOrder_NotCultureOrder()
    {
        // C's sort puts uppercase before '_' before lowercase; a culture-aware sort does not.
        var text = WebappSyncRules.ManifestText(new[] { ("seller/_x", "b"), ("seller/Zebra", "a"), ("seller/abc", "c") });

        Assert.Equal("a  seller/Zebra\nb  seller/_x\nc  seller/abc\n", text);
    }

    [Fact]
    public void TheBundlesBasePath_IsItsFrameworkFolder_AndTheLiveBundleIsSeller()
    {
        Assert.Equal("seller/", WebappSyncRules.BasePathOf(CiManifest().Select(p => p.Path)));
        Assert.Equal("", WebappSyncRules.BasePathOf(new[] { "_framework/dotnet.js", "index.html" }));
        Assert.Equal("", WebappSyncRules.BasePathOf(new[] { "index.html", "css/site.css" }));
    }

    [Fact]
    public void ABundleWithTwoFrameworkFolders_HasNoBasePath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WebappSyncRules.BasePathOf(new[] { "seller/_framework/a.wasm", "admin/_framework/b.wasm" }));

        Assert.Contains("2 _framework folders", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The passes, one at a time
    // ---------------------------------------------------------------------------------------

    private static readonly string[] Bundle =
    {
        "seller/index.html", "seller/index.html.br",
        "seller/_framework/dotnet.js", "seller/_framework/dotnet.js.br",
        "seller/_framework/blazor.webassembly.js",
        "seller/_framework/dotnet.runtime.abc123.js", "seller/_framework/dotnet.runtime.abc123.js.gz",
        "seller/_framework/App.xyz.wasm", "seller/_framework/App.xyz.wasm.br",
        "seller/_framework/icudt.q1.dat", "seller/_framework/icudt.q1.dat.gz",
        "seller/_framework/App.xyz.pdb",
        "seller/_content/Lib/lib.css", "seller/_content/Lib/lib.css.br",
        "seller/_content/Lib/appConfig.js",
        "seller/service-worker-assets.js",
        "seller/fonts/icons.woff2",
        "seller/data.unknownext",
    };

    [Theory]
    // Pass 1: the baseline, and a type by extension — a compressed sibling outside _framework keeps its underlying type
    // and gets no encoding, which CFRequest never serves (it rewrites _framework/*.wasm|js|dat only).
    [InlineData("seller/_content/Lib/lib.css", WebappSyncRules.Baseline, "text/css", null)]
    [InlineData("seller/_content/Lib/lib.css.br", WebappSyncRules.Baseline, "text/css", null)]
    [InlineData("seller/fonts/icons.woff2", WebappSyncRules.Baseline, "font/woff2", null)]
    [InlineData("seller/data.unknownext", WebappSyncRules.Baseline, WebappSyncRules.UnknownContentType, null)]
    // Pass 2: immutable under _framework, octet-stream unless wasm or js.
    [InlineData("seller/_framework/App.xyz.pdb", WebappSyncRules.Immutable, "application/octet-stream", null)]
    [InlineData("seller/_framework/App.xyz.wasm", WebappSyncRules.Immutable, "application/wasm", null)]
    [InlineData("seller/_framework/icudt.q1.dat", WebappSyncRules.Immutable, "application/octet-stream", null)]
    [InlineData("seller/_framework/dotnet.runtime.abc123.js", WebappSyncRules.Immutable, "application/javascript", null)]
    // Pass 3: the manifests, anywhere appConfig.js is, and their siblings with an encoding.
    [InlineData("seller/index.html", WebappSyncRules.NoCache, "text/html", null)]
    [InlineData("seller/index.html.br", WebappSyncRules.NoCache, "text/html", "br")]
    [InlineData("seller/_framework/blazor.webassembly.js", WebappSyncRules.NoCache, "application/javascript", null)]
    [InlineData("seller/_framework/dotnet.js", WebappSyncRules.NoCache, "application/javascript", null)]
    [InlineData("seller/_content/Lib/appConfig.js", WebappSyncRules.NoCache, "application/javascript", null)]
    [InlineData("seller/service-worker-assets.js", WebappSyncRules.NoCache, "application/javascript", null)]
    // Pass 4: the framework's compressed siblings, immutable with their encoding and underlying type.
    [InlineData("seller/_framework/App.xyz.wasm.br", WebappSyncRules.Immutable, "application/wasm", "br")]
    [InlineData("seller/_framework/dotnet.runtime.abc123.js.gz", WebappSyncRules.Immutable, "application/javascript", "gzip")]
    [InlineData("seller/_framework/icudt.q1.dat.gz", WebappSyncRules.Immutable, "application/octet-stream", "gzip")]
    // Pass 5: a manifest's compressed sibling stays no-cache — pass 4 must leave it alone.
    [InlineData("seller/_framework/dotnet.js.br", WebappSyncRules.NoCache, "application/javascript", "br")]
    public void EachPass_LeavesTheHeadersDeploywebappsPassesLeave(string path, string cacheControl, string contentType, string? encoding)
    {
        var rules = new WebappHeaderRules("seller/", Bundle);

        Assert.Equal(new WebappObjectHeaders(cacheControl, contentType, encoding), rules.For(path));
    }

    [Fact]
    public void AnAppAtTheRoot_GetsTheSameRules()
    {
        var rules = new WebappHeaderRules("", new[] { "index.html", "_framework/dotnet.js", "_framework/a.wasm.br" });

        Assert.Equal(WebappSyncRules.NoCache, rules.For("index.html").CacheControl);
        Assert.Equal(WebappSyncRules.NoCache, rules.For("_framework/dotnet.js").CacheControl);
        Assert.Equal(new WebappObjectHeaders(WebappSyncRules.Immutable, "application/wasm", "br"), rules.For("_framework/a.wasm.br"));
    }

    // ---------------------------------------------------------------------------------------
    //  Reading headers back
    // ---------------------------------------------------------------------------------------

    [Theory]
    // MEASURED: what the first client deploy sent, and what the AWS .NET SDK read back from S3 (2026-09-14).
    [InlineData("no-cache, must-revalidate", "must-revalidate, no-cache", true)]
    [InlineData("public, max-age=31536000, immutable", "immutable,public,  max-age=31536000", true)]
    [InlineData("public, max-age=3600", "PUBLIC, Max-Age=3600", true)]
    [InlineData("public, max-age=3600", "public, max-age=31536000", false)]
    [InlineData("no-cache, must-revalidate", "no-cache", false)]
    [InlineData("no-cache, must-revalidate", null, false)]
    [InlineData(null, "", true)]
    public void CacheControl_IsItsDirectives_InAnyOrder(string? sent, string? readBack, bool same)
        => Assert.Equal(same, WebappSyncRules.SameCacheControl(sent, readBack));

    [Fact]
    public void WhatDotNetReadsBack_CarriesTheHeadersTheRulesGive()
    {
        // The SDK reads Cache-Control through CacheControlHeaderValue: this is exactly the string it hands back.
        var rules = new WebappHeaderRules("seller/", new[] { "seller/index.html" });
        var expected = rules.For("seller/index.html");
        var readBack = System.Net.Http.Headers.CacheControlHeaderValue.Parse(expected.CacheControl).ToString();

        Assert.NotEqual(expected.CacheControl, readBack);   // the reordering this exists for
        Assert.True(WebappSyncRules.Carries(expected, readBack, "text/html", null));
        Assert.False(WebappSyncRules.Carries(expected, readBack, "text/plain", null));
        Assert.False(WebappSyncRules.Carries(expected, readBack, "text/html", "br"));
    }

    // ---------------------------------------------------------------------------------------
    //  Names shared with deploywebapp
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/seller/,/seller", "seller/")]
    [InlineData("/admin/,/admin,", "admin/")]
    [InlineData("/", "")]
    [InlineData("", "")]
    public void AConfiguredWebAppPath_IsTheBasePathItsBundleIsBuiltFor(string behaviorPath, string basePath)
        => Assert.Equal(basePath, WebappSyncRules.BasePathFromBehaviorPath(behaviorPath));

    [Fact]
    public void TheSystemBucket_IsTheOneDeploywebappDeploysInto()
        => Assert.Equal("scu---webapp-sellerapp-4df6-b9c6", WebappSyncRules.SystemBucketName("scu", "SellerApp", "4df6-b9c6"));

    [Fact]
    public void TheBucketPolicy_LetsOnlyThisAccountsCloudFrontRead()
    {
        using var doc = JsonDocument.Parse(WebappSyncRules.CloudFrontReadPolicy("scu---webapp-sellerapp-4df6-b9c6", "503947800380"));
        var statement = Assert.Single(doc.RootElement.GetProperty("Statement").EnumerateArray());

        Assert.Equal("AllowCloudFrontRead", statement.GetProperty("Sid").GetString());
        Assert.Equal("cloudfront.amazonaws.com", statement.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal("s3:GetObject", statement.GetProperty("Action").GetString());
        Assert.Equal("arn:aws:s3:::scu---webapp-sellerapp-4df6-b9c6/*", statement.GetProperty("Resource").GetString());
        Assert.Equal("503947800380", statement.GetProperty("Condition").GetProperty("StringEquals").GetProperty("AWS:SourceAccount").GetString());
    }

    [Fact]
    public void WebappDeployer_UsesTheSharedDefinitions()
    {
        // The CLI's names delegate, so the two deployers cannot drift (P-2).
        Assert.Equal(WebappSyncRules.InvalidationPath("seller/"), WebappDeployer.InvalidationPath("seller/"));
        Assert.Equal(WebappSyncRules.NoCacheFiles(new[] { "x/appConfig.js" }), WebappDeployer.NoCacheFiles(new[] { "x/appConfig.js" }));

        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Lz.Aws", "Webapp", "WebappDeployer.cs"));
        Assert.Contains("WebappSyncRules.CloudFrontReadPolicy(bucketName, accountId)", src);
        var cli = File.ReadAllText(Path.Combine(RepoRoot(), "Lz.Cli", "Program.cs"));
        Assert.Contains("WebappSyncRules.SystemBucketName(config.SystemKey, webappName, config.SystemSuffix)", cli);
    }

    [Fact]
    public void TheBundleDeploysLifecycleRule_IsTheOneDeploywebappWrites()
        => Assert.Equal(BucketDurabilityEnsurer.LifecycleRuleId, Lz.Aws.Pipeline.DeployBundleSettings.NoncurrentExpiryRuleId);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lz.slnx not found above the test output.");
    }
}
