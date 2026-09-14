using Lz.Aws.Webapp;

namespace Lz.Tests.Storage.Tests;

/// <summary>
/// The mirror plan (DecoupledCd.md P4 stage C): what a deploy of a bundle writes, in what order, and what it deletes.
/// </summary>
public class WebappMirrorPlannerTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222222222222222222222222222";

    private static BundleFile File_(string path, string hash = Hash1) => new(path, hash, 10);

    private static StoredObject Stored(string path, string? hash, WebappObjectHeaders headers)
        => new(WebappSyncRules.StoragePrefix + path, hash, headers.CacheControl, headers.ContentType, headers.ContentEncoding);

    private static readonly BundleFile[] Bundle =
    {
        File_("seller/index.html"),
        File_("seller/_framework/dotnet.js"),
        File_("seller/_framework/App.a1.wasm"),
        File_("seller/_content/Lib/site.css"),
    };

    private static WebappObjectHeaders Rules(string path) => new WebappHeaderRules("seller/", Bundle.Select(b => b.Path)).For(path);

    // ---------------------------------------------------------------------------------------
    //  What is written, and why
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyBucket_GetsEverything_AssetsApartFromManifests()
    {
        var plan = WebappMirrorPlanner.Plan(Bundle, Array.Empty<StoredObject>(), "seller/");

        Assert.Equal("wwwroot/seller/", plan.KeyPrefix);
        Assert.Equal(new[] { "wwwroot/seller/_content/Lib/site.css", "wwwroot/seller/_framework/App.a1.wasm" },
            plan.Assets.Select(p => p.Key));
        Assert.Equal(new[] { "wwwroot/seller/_framework/dotnet.js", "wwwroot/seller/index.html" },
            plan.Manifests.Select(p => p.Key));
        Assert.All(plan.Assets.Concat(plan.Manifests), p => Assert.Equal("absent", p.Reason));
        Assert.Empty(plan.Deletes);
        Assert.Equal(0, plan.Unchanged);
    }

    [Fact]
    public void AnObjectWithTheSameBytesAndHeaders_IsLeftAlone()
    {
        var stored = Bundle.Select(b => Stored(b.Path, b.Sha256Hex, Rules(b.Path))).ToList();

        var plan = WebappMirrorPlanner.Plan(Bundle, stored, "seller/");

        Assert.Empty(plan.Assets);
        Assert.Empty(plan.Manifests);
        Assert.Empty(plan.Deletes);
        Assert.Equal(4, plan.Unchanged);
    }

    [Fact]
    public void AManifestReadBackWithItsDirectivesReordered_IsLeftAlone()
    {
        // The AWS .NET SDK reads no-cache, must-revalidate back as must-revalidate, no-cache. Taken as a change, every deploy
        // would rewrite every manifest, and VerifyBundle would refuse every deploy (2026-09-14, the first client deploy).
        var stored = Bundle.Select(b =>
        {
            var h = Rules(b.Path);
            return Stored(b.Path, b.Sha256Hex, h with
            {
                CacheControl = System.Net.Http.Headers.CacheControlHeaderValue.Parse(h.CacheControl).ToString(),
            });
        }).ToList();

        var plan = WebappMirrorPlanner.Plan(Bundle, stored, "seller/");

        Assert.Empty(plan.Manifests);
        Assert.Empty(plan.Assets);
        Assert.Equal(4, plan.Unchanged);
    }

    [Fact]
    public void DifferentBytes_NoRecordedBytes_AndDifferentHeaders_AreEachWrittenAgain()
    {
        var stored = new[]
        {
            Stored("seller/index.html", Hash2, Rules("seller/index.html")),                                // content changed
            // Written by deploywebapp's sync, which records no SHA-256: the bytes cannot be compared, so they are rewritten.
            Stored("seller/_framework/dotnet.js", null, Rules("seller/_framework/dotnet.js")),
            Stored("seller/_content/Lib/site.css", Hash1, new WebappObjectHeaders(WebappSyncRules.Baseline, "text/plain", null)),
            Stored("seller/_framework/App.a1.wasm", Hash1, Rules("seller/_framework/App.a1.wasm") with { ContentEncoding = "br" }),
        };

        var plan = WebappMirrorPlanner.Plan(Bundle, stored, "seller/");
        var reasons = plan.Assets.Concat(plan.Manifests).ToDictionary(p => p.Key, p => p.Reason);

        Assert.Equal("content", reasons["wwwroot/seller/index.html"]);
        Assert.Equal("content", reasons["wwwroot/seller/_framework/dotnet.js"]);
        Assert.Equal("headers", reasons["wwwroot/seller/_content/Lib/site.css"]);
        Assert.Equal("headers", reasons["wwwroot/seller/_framework/App.a1.wasm"]);
    }

    [Fact]
    public void WhatTheBundleLacks_UnderTheAppsPrefix_IsDeleted()
    {
        var stored = Bundle.Select(b => Stored(b.Path, b.Sha256Hex, Rules(b.Path)))
            .Append(Stored("seller/_framework/App.OLD.wasm", Hash2, Rules("seller/_framework/App.a1.wasm")))
            .ToList();

        var plan = WebappMirrorPlanner.Plan(Bundle, stored, "seller/");

        Assert.Equal(new[] { "wwwroot/seller/_framework/App.OLD.wasm" }, plan.Deletes);
    }

    [Fact]
    public void TheFirstPipelineDeployOfSellerApp_RewritesAllOfIt_AndDeletesTheWorkstationBuildsNames()
    {
        // The real inputs: the bundle CI built, and the bucket deploywebapp left — whose objects carry no SHA-256.
        var dir = Path.Combine(AppContext.BaseDirectory, "Storage.Tests", "testdata", "bundles");
        var bundle = File.ReadAllLines(Path.Combine(dir, "sellerapp-bundle-34875215076.manifest.txt"))
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
            .Select(l => new BundleFile(l[66..], l[..64], 1)).ToList();
        var stored = File.ReadAllLines(Path.Combine(dir, "sellerapp-live-headers-2026-09-14.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.TrimEnd('\r').Split('\t'))
            .Select(f => new StoredObject(f[0], null, f[1], f[2], f[3] == "-" ? null : f[3])).ToList();

        var plan = WebappMirrorPlanner.Plan(bundle, stored, "seller/");
        var puts = plan.Assets.Concat(plan.Manifests).ToList();

        Assert.Equal(573, puts.Count);
        Assert.Equal(318, puts.Count(p => p.Reason == "absent"));   // the published pins changed 318 hashed names
        Assert.Equal(255, puts.Count(p => p.Reason == "content"));  // the rest: same name, no recorded SHA-256
        Assert.Equal(318, plan.Deletes.Count);
        Assert.Equal("6b1dc9ddc8326dc75d805c651452233229167269c20ecd71982133bf660ad100", plan.ManifestSha256);
    }

    // ---------------------------------------------------------------------------------------
    //  What does not belong
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ABundleBuiltForAnotherPath_IsRefused()
    {
        // SellerApp's bundle deployed as AdminApp would name /seller/ files from /admin/.
        var ex = Assert.Throws<InvalidOperationException>(() => WebappMirrorPlanner.Plan(Bundle, Array.Empty<StoredObject>(), "admin/"));

        Assert.Contains("built for base path 'seller/'", ex.Message);
    }

    [Fact]
    public void AFileOutsideTheBasePath_IsRefused()
    {
        var bundle = Bundle.Append(File_("robots.txt")).ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => WebappMirrorPlanner.Plan(bundle, Array.Empty<StoredObject>(), "seller/"));

        Assert.Contains("outside its base path", ex.Message);
        Assert.Contains("robots.txt", ex.Message);
    }

    [Fact]
    public void AListingOutsideThePrefix_IsRefused_RatherThanDeletedFrom()
    {
        var stored = new[] { new StoredObject("wwwroot/admin/index.html", null, null, null, null) };

        var ex = Assert.Throws<InvalidOperationException>(() => WebappMirrorPlanner.Plan(Bundle, stored, "seller/"));

        Assert.Contains("outside 'wwwroot/seller/'", ex.Message);
    }

    [Fact]
    public void AnEmptyBundle_IsRefused_RatherThanDeletingTheApp()
    {
        var stored = Bundle.Select(b => Stored(b.Path, b.Sha256Hex, Rules(b.Path))).ToList();

        Assert.Throws<InvalidOperationException>(() => WebappMirrorPlanner.Plan(Array.Empty<BundleFile>(), stored, "seller/"));
    }

    [Theory]
    [InlineData("seller/../admin/index.html", "'..'")]
    [InlineData("seller/./index.html", "'.'")]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData("seller\\index.html", "backslash")]
    [InlineData("C:/seller/index.html", "colon")]
    [InlineData("seller//index.html", "empty")]
    [InlineData("seller/in\u0001dex.html", "control")]
    public void AnEntryThatCouldLeaveTheDirectoryOrThePrefix_IsRefused(string name, string why)
    {
        var refusals = WebappMirrorPlanner.RefusalsForEntryNames(new[] { "seller/index.html", name });

        Assert.Contains(refusals, r => r.Contains(why));
    }

    [Fact]
    public void DirectoryEntries_AreNotFiles_AndDuplicatesAreRefused()
    {
        Assert.Empty(WebappMirrorPlanner.RefusalsForEntryNames(new[] { "seller/", "seller/index.html" }));
        Assert.Contains(WebappMirrorPlanner.RefusalsForEntryNames(new[] { "seller/a.js", "seller/a.js" }), r => r.Contains("twice"));
        Assert.Contains(WebappMirrorPlanner.RefusalsForEntryNames(new[] { "seller/" }), r => r.Contains("no files"));
    }
}
