using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Aws.Webapp;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// CLASS 3, THE SITE, IN THE DEPLOYER (DecoupledCd.md V-24, §15.7 V-K step 1): Verify admits a site record only into this
/// environment's website bucket and only when it was built for this environment; DeploySite holds the bundle to the
/// environment's domain before it claims the lease, then mirrors assets, then pages, then deletes, with the site's two cache
/// lifetimes and no invalidation, and never creates or re-policies the bucket; VerifySite reads every object back.
/// </summary>
public sealed class SiteDeployTests : IDisposable
{
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string SiteBucket = "scu-mp--webapp-website-4df6-b9c6";
    private const string Repo = "Scutara/ScutaraWebsite";
    private const string BundleVersion = "8XtRS9NSXBeDbRQmd.gaFceR3JvLMali";
    private const string RecordVersion = "4sL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY";
    private const string BuiltAt = "2026-10-23T09:00:00Z";
    private const string Execution = "req-20261023T090000Z-39900000001-1-website-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T09:10:00Z");

    private static readonly SiteTarget Dev = new(Repo, "dev", SiteBucket, "aiproxydev.click");

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-site-deploy-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true); }

    private static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    // ---- fakes -----------------------------------------------------------------------------

    private sealed class Records(string? json) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key) => Task.FromResult(json is null ? null : new StoredRecord(json, RecordVersion));
    }

    private sealed class NotAnImage : IRegistryImages, IServices, ITaskDefinitions
    {
        Task<RegistryImage?> IRegistryImages.DescribeAsync(string repository, string digest) => throw new InvalidOperationException("a site deploy read the registry");
        Task<ServiceSnapshot?> IServices.DescribeAsync(string cluster, string service) => throw new InvalidOperationException("a site deploy read ECS");
        Task<IReadOnlyList<TaskSnapshot>> IServices.RunningTasksAsync(string cluster, string service) => throw new InvalidOperationException("a site deploy read ECS");
        Task<IReadOnlyList<ServiceDeploymentRecord>> IServices.RecentDeploymentsAsync(string cluster, string service) => throw new InvalidOperationException("a site deploy read ECS");
        Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> ITaskDefinitions.DescribeAsync(string taskDefinitionArn) => throw new InvalidOperationException("a site deploy read ECS");
        Task<string> ITaskDefinitions.RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request) => throw new InvalidOperationException("a site deploy wrote ECS");
    }

    private sealed class Artifacts(byte[] zip, bool noSuchVersion = false) : IArtifactObjects
    {
        public List<string> Calls { get; } = new();
        public Task<ArtifactHead?> HeadAsync(string bucket, string key, string versionId)
        {
            Calls.Add($"head {key}@{versionId}");
            return Task.FromResult(noSuchVersion ? null : new ArtifactHead(BundleVersion, Convert.ToBase64String(SHA256.HashData(zip)), "FULL_OBJECT", zip.Length));
        }
        public async Task<(string? VersionId, long Bytes)> DownloadAsync(string bucket, string key, string versionId, string path)
        {
            Calls.Add($"download {key}@{versionId}");
            await File.WriteAllBytesAsync(path, zip);
            return (BundleVersion, zip.Length);
        }
    }

    private sealed class Bucket : IAppBucket
    {
        public Dictionary<string, (byte[] Bytes, StoredObject Head)> Objects { get; } = new(StringComparer.Ordinal);
        public (string Json, string ETag)? Marker { get; set; }
        public List<string> Log { get; } = new();
        private int _etag;

        public Task EnsureAsync(string bucket, string region, string policyJson, bool versioning, int? noncurrentExpirationDays)
            => throw new InvalidOperationException("a site deploy created or re-policied the bucket");

        public Task<MarkerRead?> ReadMarkerAsync(string bucket, string key)
        {
            Log.Add("read-marker");
            return Task.FromResult(Marker is { } m ? new MarkerRead(m.Json, m.ETag) : null);
        }

        public Task<string?> WriteMarkerAsync(string bucket, string key, string json, string? ifMatchETag)
        {
            var kind = JsonNode.Parse(json)!["claim"] is null ? "release" : "claim";
            if (ifMatchETag is null ? Marker != null : ifMatchETag != Marker?.ETag) { Log.Add($"{kind}-refused"); return Task.FromResult<string?>(null); }
            Marker = (json, $"\"e{++_etag}\"");
            Log.Add(kind);
            return Task.FromResult<string?>(Marker.Value.ETag);
        }

        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix)
        {
            Log.Add($"list {prefix}");
            return Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        }

        public Task<StoredObject?> HeadAsync(string bucket, string key)
            => Task.FromResult(Objects.TryGetValue(key, out var o) ? o.Head with
            {
                CacheControl = o.Head.CacheControl is { } cc ? System.Net.Http.Headers.CacheControlHeaderValue.Parse(cc).ToString() : null,
            } : null);

        public async Task PutAsync(string bucket, string key, string filePath, WebappObjectHeaders headers, string sha256Base64)
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), sha256Base64);
            lock (Log)
            {
                Objects[key] = (bytes, new StoredObject(key, Convert.ToHexStringLower(SHA256.HashData(bytes)), headers.CacheControl, headers.ContentType, headers.ContentEncoding));
                Log.Add($"put {key}");
            }
        }

        public Task DeleteAsync(string bucket, IReadOnlyList<string> keys)
        {
            foreach (var key in keys) { Objects.Remove(key); Log.Add($"delete {key}"); }
            return Task.CompletedTask;
        }
    }

    // ---- a site --------------------------------------------------------------------------

    private static Dictionary<string, string> Site(string host = "aiproxydev.click", string build = "1") => new(StringComparer.Ordinal)
    {
        ["index.html"] = $"<html><head><link rel=canonical href=https://{host}/></head><body>build {build}</body></html>",
        ["404.html"] = "<html>missing</html>",
        ["park.html"] = "<html>parked</html>",
        ["css/main.min.abc.css"] = "body{}",
        ["images/hero.webp"] = "webp",
    };

    private static byte[] Zip(IReadOnlyDictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(content);
        return ms.ToArray();
    }

    private static BuildRecord Record(string environment = "dev", byte[]? zip = null)
    {
        var record = new BuildRecord(BuildRecordFormat.CurrentSchema, "site",
            new BuildRecordBuiltFrom(Repo, "0455585a", "published", new Dictionary<string, string>(), "refs/heads/main"),
            new BuildRecordIdentity("bundle"), BuiltAt, "tmay57", "39900000001",
            new BuildRecordIntake("39900000001", 1, "301", $"{Repo}/.github/workflows/publish.yml@refs/heads/main",
                "sha256:" + new string('a', 64), new BuildRecordGate1("passed", "gate1/x.json"), Environment: environment));
        return record with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: ArtifactStore, Key: BuildRecordFormat.BundleKeyFor(record),
                VersionId: BundleVersion, Sha256: Sha256(zip ?? Zip(Site()))),
        };
    }

    private static JsonObject Input(BuildRecord record, string bucket = SiteBucket) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = RecordStore, ["key"] = BuildRecordFormat.KeyFor(record) },
        ["target"] = new JsonObject { ["bucket"] = bucket },
    };

    private static VerifySettings Settings(IReadOnlyList<SiteTarget>? targets = null, string[]? classes = null)
        => new(classes ?? new[] { "image", "client", "site" }, new[] { "CRITICAL" }, RecordStore, new[] { "scu-4df6-b9c6-aiphost" },
            ArtifactStore, Array.Empty<ClientTarget>(), SiteTargets: targets ?? new[] { Dev });

    private static Task<JsonObject> Verify(BuildRecord record, Artifacts artifacts, JsonObject? input = null, VerifySettings? settings = null)
        => VerifyStep.RunAsync(input ?? Input(record), settings ?? Settings(), new Records(BuildRecordFormat.Serialize(record)),
            new NotAnImage(), new NotAnImage(), new NotAnImage(), artifacts);

    // ---- Verify ----------------------------------------------------------------------------

    [Fact]
    public async Task ASiteBuiltForThisEnvironment_IsVerifiedIntoItsWebsiteBucket()
    {
        var zip = Zip(Site());
        var result = await Verify(Record(zip: zip), new Artifacts(zip));

        Assert.Equal("site", result["class"]!.GetValue<string>());
        Assert.Equal(SiteTargets.ToJson(Dev).ToJsonString(), result["target"]!.ToJsonString());
        Assert.Equal(BundleVersion, result["identity"]!["versionId"]!.GetValue<string>());
        Assert.Contains("aiproxydev.click", result["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASiteBuiltForAnotherEnvironment_IsRefused_BeforeItsZipIsAskedAbout()
    {
        var artifacts = new Artifacts(Zip(Site()));
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Record("test"), artifacts));
        Assert.Contains(ex.Refusals, r => r.Check == "intake.environment" && r.Reason.Contains("built for test") && r.Reason.Contains("dev"));
        Assert.Empty(artifacts.Calls);
    }

    [Fact]
    public async Task AnInputNamingAnotherBucket_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Record(), new Artifacts(Zip(Site())), Input(Record(), "scu---webapp-sellerapp-4df6-b9c6")));
        Assert.Contains(ex.Refusals, r => r.Check == "target.bucket" && r.Reason.Contains(SiteBucket));
    }

    [Fact]
    public async Task AnEnvironmentDeployingNoSite_RefusesTheRecord()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Record(), new Artifacts(Zip(Site())), settings: Settings(targets: Array.Empty<SiteTarget>())));
        Assert.Contains(ex.Refusals, r => r.Check == "target.bucket" && r.Reason.Contains("deploys no site here"));
    }

    [Fact]
    public async Task AnEnvironmentWhoseClassesOmitTheSite_RefusesTheRecord()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Record(), new Artifacts(Zip(Site())), settings: Settings(classes: new[] { "image", "client" })));
        Assert.Contains(ex.Refusals, r => r.Check == "class");
    }

    [Fact]
    public async Task AVersionTheStoreDoesNotHold_IsRefused()
        => await Assert.ThrowsAsync<DeployRefused>(() => Verify(Record(), new Artifacts(Zip(Site()), noSuchVersion: true)));

    // ---- DeploySite ------------------------------------------------------------------------

    private static JsonObject Verified(byte[] zip, string builtAt = BuiltAt, SiteTarget? target = null)
        => new()
        {
            ["record"] = new JsonObject { ["bucket"] = RecordStore, ["key"] = BuildRecordFormat.KeyFor(Record(zip: zip)) },
            ["target"] = new JsonObject { ["bucket"] = (target ?? Dev).Bucket },
            ["verified"] = new JsonObject
            {
                ["class"] = "site",
                ["identity"] = new JsonObject { ["bucket"] = ArtifactStore, ["key"] = Record(zip: zip).Identity.Key, ["versionId"] = BundleVersion, ["sha256"] = Sha256(zip), ["bytes"] = zip.Length },
                ["target"] = SiteTargets.ToJson(target ?? Dev),
                ["recordVersionId"] = RecordVersion,
                ["builtFrom"] = new JsonObject { ["repo"] = Repo, ["commit"] = "0455585a", ["lane"] = "published" },
                ["builtAt"] = builtAt,
                ["workflowRunId"] = "39900000001",
            },
        };

    private Task<JsonObject> Deploy(JsonObject state, byte[] zip, Bucket bucket, string execution = Execution)
        => DeploySiteStep.RunAsync(state, execution, _work, new Artifacts(zip), bucket, () => Now);

    [Fact]
    public async Task TheFirstDeploy_ClaimsTheLease_WritesAssetsThenPages_AndReleases_WithNoInvalidation()
    {
        var zip = Zip(Site());
        var bucket = new Bucket();
        var result = await Deploy(Verified(zip), zip, bucket);

        Assert.Equal("read-marker", bucket.Log[0]);
        var claim = bucket.Log.IndexOf("claim");
        var lastAsset = bucket.Log.FindLastIndex(l => l.StartsWith("put ") && !l.EndsWith(".html"));
        var firstPage = bucket.Log.FindIndex(l => l.StartsWith("put ") && l.EndsWith(".html"));
        Assert.True(claim < lastAsset && lastAsset < firstPage && firstPage < bucket.Log.IndexOf("release"), string.Join("\n", bucket.Log));
        Assert.Equal("list wwwroot/", bucket.Log.Single(l => l.StartsWith("list ")));
        Assert.Empty(result["invalidations"]!.AsArray());
        Assert.Equal(5, bucket.Objects.Count);
    }

    [Fact]
    public async Task PagesGetAMinute_AndEverythingElseAnHour()
    {
        var zip = Zip(Site());
        var bucket = new Bucket();
        await Deploy(Verified(zip), zip, bucket);

        Assert.Equal(("public, max-age=60", "text/html"), (bucket.Objects["wwwroot/index.html"].Head.CacheControl, bucket.Objects["wwwroot/index.html"].Head.ContentType));
        Assert.Equal(("public, max-age=3600", "text/css"), (bucket.Objects["wwwroot/css/main.min.abc.css"].Head.CacheControl, bucket.Objects["wwwroot/css/main.min.abc.css"].Head.ContentType));
        Assert.Equal("image/webp", bucket.Objects["wwwroot/images/hero.webp"].Head.ContentType);
    }

    [Fact]
    public async Task ASiteBuiltForAnotherDomain_IsRefused_BeforeTheClaim()
    {
        var zip = Zip(Site(host: "scutara.com"));
        var bucket = new Bucket();
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Deploy(Verified(zip), zip, bucket));
        Assert.Contains("names host scutara.com, and dev serves aiproxydev.click", ex.Message);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
    }

    [Fact]
    public async Task WhatTheSiteLacks_IsDeleted_AfterThePages()
    {
        var bucket = new Bucket();
        var first = Zip(Site());
        await Deploy(Verified(first), first, bucket, execution: "req-a-1");
        var second = Zip(Site(build: "2").Where(kv => kv.Key != "images/hero.webp").ToDictionary(kv => kv.Key, kv => kv.Value));
        bucket.Log.Clear();

        await Deploy(Verified(second, builtAt: "2026-10-23T10:00:00Z"), second, bucket, execution: "req-b-1");

        var delete = bucket.Log.IndexOf("delete wwwroot/images/hero.webp");
        Assert.True(delete > bucket.Log.IndexOf("put wwwroot/index.html") && delete < bucket.Log.IndexOf("release"), string.Join("\n", bucket.Log));
        Assert.DoesNotContain(bucket.Log, l => l == "put wwwroot/css/main.min.abc.css");   // unchanged
    }

    [Fact]
    public async Task AnOlderBuild_IsSuperseded_AndWritesNothing()
    {
        var bucket = new Bucket();
        var newer = Zip(Site(build: "2"));
        await Deploy(Verified(newer, builtAt: "2026-10-23T10:00:00Z"), newer, bucket, execution: "req-b-1");
        var objects = bucket.Objects.ToDictionary(kv => kv.Key, kv => kv.Value.Head);

        var older = Zip(Site());
        await Assert.ThrowsAsync<DeploySuperseded>(() => Deploy(Verified(older), older, bucket, execution: "req-a-1"));
        Assert.Equal(objects, bucket.Objects.ToDictionary(kv => kv.Key, kv => kv.Value.Head));
    }

    // ---- VerifySite ------------------------------------------------------------------------

    private async Task<(JsonObject State, Bucket Bucket)> Deployed()
    {
        var zip = Zip(Site());
        var bucket = new Bucket();
        var state = Verified(zip);
        state["deploy"] = await Deploy(state, zip, bucket);
        return (state, bucket);
    }

    [Fact]
    public async Task ADeployedSite_ReadsBackLanded_WithItsEvidenceUnderSite()
    {
        var (state, bucket) = await Deployed();
        var result = await VerifySiteStep.RunAsync(state, Execution, bucket, Now);

        Assert.Equal("Landed", result["verdict"]!.GetValue<string>());
        Assert.Equal(DeployEvidence.DeployedKey("site", Repo, Execution), result["evidence"]!["key"]!.GetValue<string>());
        Assert.StartsWith("deploys/site/", result["evidence"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnObjectWithTheWrongCacheLifetime_IsNotDeployed()
    {
        var (state, bucket) = await Deployed();
        var (bytes, head) = bucket.Objects["wwwroot/index.html"];
        bucket.Objects["wwwroot/index.html"] = (bytes, head with { CacheControl = "public, max-age=3600" });

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifySiteStep.RunAsync(state, Execution, bucket, Now));
        Assert.Contains("wwwroot/index.html", ex.Message);
    }

    [Fact]
    public async Task AnExtraObject_IsNotDeployed()
    {
        var (state, bucket) = await Deployed();
        bucket.Objects["wwwroot/stray.html"] = (new byte[] { 1 }, new StoredObject("wwwroot/stray.html", Sha256(new byte[] { 1 }), "public, max-age=60", "text/html", null));
        await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifySiteStep.RunAsync(state, Execution, bucket, Now));
    }

    [Fact]
    public async Task AMarkerNamingAnotherExecution_IsNotDeployed()
        => await Assert.ThrowsAsync<BundleNotDeployed>(async () =>
        {
            var (state, bucket) = await Deployed();
            await VerifySiteStep.RunAsync(state, "req-someone-else-1", bucket, Now);
        });

    // ---- targets and rules -----------------------------------------------------------------

    [Fact]
    public void ATargetRoundTrips_AndItsRoutePrefixIsItsEnvironments()
    {
        Assert.Equal(Dev, SiteTargets.Decode(SiteTargets.Encode(new[] { Dev })).Single());
        Assert.Equal("site/scutara/scutarawebsite/dev/", Dev.RecordPrefix);
        Assert.Equal("wwwroot/", Dev.KeyPrefix);
    }

    [Theory]
    [InlineData("{\"repo\":\"a/b\",\"environment\":\"Dev\",\"bucket\":\"b-b-b\",\"domain\":\"x.click\"}")]
    [InlineData("{\"repo\":\"a/b\",\"environment\":\"dev\",\"bucket\":\"B\",\"domain\":\"x.click\"}")]
    [InlineData("{\"repo\":\"a/b\",\"environment\":\"dev\",\"bucket\":\"b-b-b\",\"domain\":\"https://x.click\"}")]
    [InlineData("{\"repo\":\"a/b\",\"environment\":\"dev\",\"bucket\":\"b-b-b\",\"domain\":\"x.click\",\"keyPrefix\":\"other/\"}")]
    public void ATargetThePlannerWouldNotWrite_IsRefused(string json)
        => Assert.Throws<InvalidOperationException>(() => SiteTargets.FromJson(JsonNode.Parse(json)));
}
