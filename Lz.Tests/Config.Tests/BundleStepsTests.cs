using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Aws.Webapp;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Class 2's steps (DecoupledCd.md P4 stage C), driven by fakes: Verify for a client record, DeployBundle and VerifyBundle.
///
/// <para>The decisions have their own tests. These pin the ORDER around them — nothing read from the artifact store before
/// the record's right to the bucket is established, nothing written before the lease is held, no manifest before the
/// assets it names, no delete before the manifests — which is where a deploy that works in a test and breaks a live app
/// would hide.</para>
/// </summary>
public sealed class BundleStepsTests : IDisposable
{
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string SellerBucket = "scu---webapp-sellerapp-4df6-b9c6";
    private const string AdminBucket = "scu---webapp-adminapp-4df6-b9c6";
    private const string RecordKey = "client/scutara/scutarasellerapp/20260914T173258Z-34875215076.json";
    private const string BundleKey = "client/scutara/scutarasellerapp/20260914T173258Z-34875215076.zip";
    private const string BundleVersion = "7WtRS9NSXBeDbRQmd.gaFceR3JvLMali";
    private const string BundleSha256 = "2d49aec564ad3d3be1320a697f290bbd3a58c2bde1beb7848a4babf4e63a5109";
    private const string RecordVersion = "3sL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY";
    private const string BuiltAt = "2026-09-14T17:32:58Z";
    private const string Execution = "req-20260914T173258Z-34875215076-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T18:00:00Z");

    private static readonly ClientTarget Seller = new("Scutara/ScutaraSellerApp", "sellerapp", SellerBucket, "seller/", new[] { "E31GJ01SW2CEEA" });
    private static readonly ClientTarget Admin = new("Scutara/ScutaraAdminApp", "adminapp", AdminBucket, "admin/", new[] { "E31GJ01SW2CEEA" });

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-bundle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    // ---------------------------------------------------------------------------------------
    //  Fakes
    // ---------------------------------------------------------------------------------------

    private sealed class Records(string? json) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key)
            => Task.FromResult(json is null ? null : new StoredRecord(json, RecordVersion));
    }

    /// <summary>The registry and ECS, which a bundle deploy must never read: any call fails the test.</summary>
    private sealed class NotAnImage : IRegistryImages, IServices, ITaskDefinitions
    {
        Task<RegistryImage?> IRegistryImages.DescribeAsync(string repository, string digest) => throw new InvalidOperationException("a bundle deploy read the registry");
        Task<ServiceSnapshot?> IServices.DescribeAsync(string cluster, string service) => throw new InvalidOperationException("a bundle deploy read ECS");
        Task<IReadOnlyList<TaskSnapshot>> IServices.RunningTasksAsync(string cluster, string service) => throw new InvalidOperationException("a bundle deploy read ECS");
        Task<IReadOnlyList<ServiceDeploymentRecord>> IServices.RecentDeploymentsAsync(string cluster, string service) => throw new InvalidOperationException("a bundle deploy read ECS");
        Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> ITaskDefinitions.DescribeAsync(string taskDefinitionArn) => throw new InvalidOperationException("a bundle deploy read ECS");
        Task<string> ITaskDefinitions.RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request) => throw new InvalidOperationException("a bundle deploy wrote ECS");
    }

    /// <summary>The artifact store: serves one zip at one version, and records every call.</summary>
    private sealed class Artifacts(byte[] zip, string version, string? sha256Hex = null) : IArtifactObjects
    {
        public List<string> Calls { get; } = new();
        public string? ChecksumType { get; init; } = "FULL_OBJECT";
        public bool NoSuchVersion { get; init; }
        public byte[]? ServedBytes { get; init; }
        public string? ServedVersion { get; init; }

        public Task<ArtifactHead?> HeadAsync(string bucket, string key, string versionId)
        {
            Calls.Add($"head {bucket}/{key}@{versionId}");
            return Task.FromResult(NoSuchVersion
                ? null
                : new ArtifactHead(version, Convert.ToBase64String(Convert.FromHexString(sha256Hex ?? Sha256(zip))), ChecksumType, zip.Length));
        }

        public async Task<(string? VersionId, long Bytes)> DownloadAsync(string bucket, string key, string versionId, string path)
        {
            Calls.Add($"download {bucket}/{key}@{versionId}");
            var bytes = ServedBytes ?? zip;
            await File.WriteAllBytesAsync(path, bytes);
            return (ServedVersion ?? version, bytes.Length);
        }
    }

    /// <summary>
    /// A web app's bucket as S3 behaves for the calls the step makes: conditional marker writes, a checksum a put is refused
    /// on, and a log of every operation in order.
    /// </summary>
    private sealed class Bucket : IAppBucket
    {
        public Dictionary<string, (byte[] Bytes, StoredObject Head)> Objects { get; } = new(StringComparer.Ordinal);
        public (string Json, string ETag)? Marker { get; set; }
        public List<string> Log { get; } = new();

        /// <summary>Runs before a marker write is judged: another execution's write, landing at that moment.</summary>
        public Action<Bucket, string>? BeforeMarkerWrite { get; set; }

        /// <summary>Runs before each marker read is answered.</summary>
        public Action<Bucket>? OnMarkerRead { get; set; }

        private int _etag;

        public string NextETag() => $"\"e{++_etag}\"";

        public Task EnsureAsync(string bucket, string region, string policyJson, bool versioning, int? noncurrentExpirationDays)
        {
            Log.Add($"ensure {bucket} {region} versioning={versioning} days={noncurrentExpirationDays}");
            return Task.CompletedTask;
        }

        public Task<MarkerRead?> ReadMarkerAsync(string bucket, string key)
        {
            Assert.Equal(BundleMarker.Key, key);
            OnMarkerRead?.Invoke(this);
            Log.Add("read-marker");
            return Task.FromResult(Marker is { } m ? new MarkerRead(m.Json, m.ETag) : null);
        }

        public Task<string?> WriteMarkerAsync(string bucket, string key, string json, string? ifMatchETag)
        {
            var kind = JsonNode.Parse(json)!["claim"] is null ? "release" : "claim";
            BeforeMarkerWrite?.Invoke(this, kind);

            var current = Marker?.ETag;
            if (ifMatchETag is null ? current != null : ifMatchETag != current)
            {
                Log.Add($"{kind}-refused");
                return Task.FromResult<string?>(null);
            }

            Marker = (json, NextETag());
            Log.Add(kind);
            return Task.FromResult<string?>(Marker.Value.ETag);
        }

        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix)
        {
            Log.Add($"list {prefix}");
            return Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        }

        public Task<StoredObject?> HeadAsync(string bucket, string key)
            => Task.FromResult(Objects.TryGetValue(key, out var o) ? o.Head : null);

        public async Task PutAsync(string bucket, string key, string filePath, WebappObjectHeaders headers, string sha256Base64)
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            // S3 refuses bytes that do not match the checksum sent with them.
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), sha256Base64);

            lock (Log)
            {
                Objects[key] = (bytes, new StoredObject(key, Sha256(bytes), headers.CacheControl, headers.ContentType, headers.ContentEncoding));
                Log.Add($"put {key}");
            }
        }

        public Task DeleteAsync(string bucket, IReadOnlyList<string> keys)
        {
            foreach (var key in keys)
            {
                Objects.Remove(key);
                Log.Add($"delete {key}");
            }
            return Task.CompletedTask;
        }

        /// <summary>Plant what an earlier deploy left, with the headers the rules give it.</summary>
        public void Holds(IReadOnlyDictionary<string, string> files, string basePath = "seller/", string? onlyPath = null)
        {
            var rules = new WebappHeaderRules(basePath, files.Keys);
            foreach (var (path, content) in files.Where(f => onlyPath is null || f.Key == onlyPath))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var h = rules.For(path);
                Objects[WebappSyncRules.StoragePrefix + path] =
                    (bytes, new StoredObject(WebappSyncRules.StoragePrefix + path, Sha256(bytes), h.CacheControl, h.ContentType, h.ContentEncoding));
            }
        }
    }

    private sealed class Invalidations : IInvalidations
    {
        public List<(string Distribution, string Path, string CallerReference)> Created { get; } = new();
        public string Status { get; set; } = "Completed";
        public List<string> Log { get; } = new();

        public Task<string> CreateAsync(string distributionId, string path, string callerReference)
        {
            // CloudFront's idempotency: the same caller reference returns the invalidation it already made.
            var existing = Created.FindIndex(c => c.Distribution == distributionId && c.CallerReference == callerReference);
            if (existing < 0)
            {
                Created.Add((distributionId, path, callerReference));
                existing = Created.Count - 1;
            }
            Log.Add($"invalidate {distributionId} {path}");
            return Task.FromResult($"I{existing + 1}");
        }

        public Task<string?> StatusAsync(string distributionId, string invalidationId)
        {
            Log.Add($"status {invalidationId}");
            return Task.FromResult<string?>(Status);
        }
    }

    // ---------------------------------------------------------------------------------------
    //  Bundles and state
    // ---------------------------------------------------------------------------------------

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly IReadOnlyDictionary<string, string> Build1 = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["seller/index.html"] = "<html>build 1</html>",
        ["seller/_framework/dotnet.js"] = "loader 1",
        ["seller/_framework/App.aaa111.wasm"] = "wasm 1",
        ["seller/_framework/App.aaa111.wasm.br"] = "wasm 1 br",
        ["seller/_content/Lib/appConfig.js"] = "config",
        ["seller/_content/Lib/site.css"] = "css",
    };

    private static readonly IReadOnlyDictionary<string, string> Build2 = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["seller/index.html"] = "<html>build 2</html>",
        ["seller/_framework/dotnet.js"] = "loader 2",
        ["seller/_framework/App.bbb222.wasm"] = "wasm 2",
        ["seller/_framework/App.bbb222.wasm.br"] = "wasm 2 br",
        ["seller/_content/Lib/appConfig.js"] = "config",
        ["seller/_content/Lib/site.css"] = "css",
    };

    private static byte[] Zip(IReadOnlyDictionary<string, string> files, params string[] extraNames)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            foreach (var name in extraNames)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("x");
            }
        }
        return ms.ToArray();
    }

    /// <summary>The state Verify leaves for a client bundle.</summary>
    private static JsonObject Verified(byte[] zip, string builtAt = BuiltAt, ClientTarget? target = null, string version = BundleVersion)
        => new()
        {
            ["record"] = new JsonObject { ["bucket"] = RecordStore, ["key"] = RecordKey },
            ["target"] = new JsonObject { ["bucket"] = (target ?? Seller).Bucket },
            ["verified"] = new JsonObject
            {
                ["class"] = "client",
                ["identity"] = new JsonObject
                {
                    ["bucket"] = ArtifactStore, ["key"] = BundleKey, ["versionId"] = version, ["sha256"] = Sha256(zip), ["bytes"] = zip.Length,
                },
                ["target"] = ClientTargets.ToJson(target ?? Seller),
                ["recordVersionId"] = RecordVersion,
                ["builtFrom"] = new JsonObject { ["repo"] = "Scutara/ScutaraSellerApp", ["commit"] = "8f0e", ["lane"] = "published" },
                ["builtAt"] = builtAt,
                ["workflowRunId"] = "34875215076",
            },
        };

    private DeployBundleSettings Settings() => new("us-west-2", "503947800380", true, 30, _work);

    private Task<JsonObject> Deploy(JsonObject state, Artifacts artifacts, Bucket bucket, Invalidations? invalidations = null, string execution = Execution)
        => DeployBundleStep.RunAsync(state, execution, Settings(), artifacts, bucket, invalidations ?? new Invalidations(), () => Now);

    private static int IndexOf(List<string> log, Func<string, bool> match) => log.FindIndex(l => match(l));
    private static int LastIndexOf(List<string> log, Func<string, bool> match) => log.FindLastIndex(l => match(l));

    // ---------------------------------------------------------------------------------------
    //  Verify, for a client record
    // ---------------------------------------------------------------------------------------

    private static VerifySettings ClientSettings(string? artifactStore = ArtifactStore, IReadOnlyList<ClientTarget>? targets = null)
        => new(new[] { "image", "client" }, new[] { "CRITICAL" }, RecordStore, new[] { "scu-4df6-b9c6-aiphost" },
            artifactStore, targets ?? new[] { Seller, Admin });

    private static JsonObject ClientInput(string bucket = SellerBucket) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = RecordStore, ["key"] = RecordKey },
        ["target"] = new JsonObject { ["bucket"] = bucket },
    };

    private static Task<JsonObject> VerifyClient(Artifacts artifacts, JsonObject? input = null, VerifySettings? settings = null,
        string? record = BuildRecordFormatTests.BundleWorkflowEmitted)
        => VerifyStep.RunAsync(input ?? ClientInput(), settings ?? ClientSettings(), new Records(record),
            new NotAnImage(), new NotAnImage(), new NotAnImage(), artifacts);

    /// <summary>The artifact store as it really holds the first SellerApp bundle: that version, that checksum.</summary>
    private static Artifacts TheRealBundle(string? checksumType = "FULL_OBJECT", bool noSuchVersion = false)
        => new(new byte[1], BundleVersion, BundleSha256) { ChecksumType = checksumType, NoSuchVersion = noSuchVersion };

    [Fact]
    public async Task TheFirstBundleRecord_AtItsRealKey_Verifies_ByAHeadOfItsVersion()
    {
        var artifacts = TheRealBundle();

        var result = await VerifyClient(artifacts);

        // One HEAD, of the record's key at the record's version, and no download.
        Assert.Equal(new[] { $"head {ArtifactStore}/{BundleKey}@{BundleVersion}" }, artifacts.Calls);
        Assert.Equal("client", result["class"]!.GetValue<string>());
        Assert.Equal(BundleVersion, result["identity"]!["versionId"]!.GetValue<string>());
        Assert.Equal(BundleSha256, result["identity"]!["sha256"]!.GetValue<string>());
        Assert.Equal(SellerBucket, result["target"]!["bucket"]!.GetValue<string>());
        Assert.Equal("/seller*", result["target"]!["invalidationPath"]!.GetValue<string>());
        Assert.Equal(RecordVersion, result["recordVersionId"]!.GetValue<string>());
        Assert.Contains(BundleVersion, result["summary"]!.GetValue<string>());
        Assert.Contains("/seller*", result["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASellerAppRecord_AimedAtAdminAppsBucket_IsRefused_BeforeItsBundleIsLookedAt()
    {
        var artifacts = TheRealBundle();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(artifacts, ClientInput(AdminBucket)));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal("target.bucket", refusal.Check);
        Assert.Contains("deploy as web app 'sellerapp'", refusal.Reason);
        Assert.Empty(artifacts.Calls);
    }

    [Fact]
    public async Task SellerAppsRecord_StoredUnderAdminAppsPrefix_IsRefused_ByWhereItIs()
    {
        // Written by AdminApp's role, claiming to be SellerApp's: the claim that role was never entitled to make, into the
        // bucket that claim would reach. The location decides, before the bucket is even considered.
        var input = ClientInput();
        input["record"]!["key"] = RecordKey.Replace("scutarasellerapp", "scutaraadminapp");
        var artifacts = TheRealBundle();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(artifacts, input));

        Assert.Contains(ex.Refusals, r => r.Check == "record.key" && r.Reason.Contains("different repository's role"));
        Assert.Empty(artifacts.Calls);
    }

    [Fact]
    public async Task ARepositoryWithNoClientTarget_IsRefused()
    {
        var artifacts = TheRealBundle();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(artifacts, settings: ClientSettings(targets: new[] { Admin })));

        Assert.Contains(ex.Refusals, r => r.Check == "target.bucket" && r.Reason.Contains("deploys into no web app here"));
        Assert.Empty(artifacts.Calls);
    }

    [Fact]
    public async Task ABundleWhoseChecksumIsNotTheRecords_IsRefused()
    {
        var artifacts = new Artifacts(new byte[1], BundleVersion, new string('0', 64));

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(artifacts));

        Assert.Contains(ex.Refusals, r => r.Check == "identity.sha256" && r.Reason.Contains("not the record's"));
    }

    [Fact]
    public async Task AVersionTheStoreDoesNotHave_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(TheRealBundle(noSuchVersion: true)));

        Assert.Contains(ex.Refusals, r => r.Check == "identity.versionId");
    }

    [Theory]
    [InlineData("COMPOSITE", "not the whole object's")]
    [InlineData(null, "not the whole object's")]
    public async Task AChecksumThatIsNotTheWholeObjects_IsRefused_AsWhatItIs(string? type, string reason)
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(TheRealBundle(checksumType: type)));

        Assert.Contains(ex.Refusals, r => r.Check == "identity.sha256" && r.Reason.Contains(reason));
    }

    [Fact]
    public async Task WithoutTheArtifactStore_ABundleRecordDoesNotEvenParse()
    {
        // What every Verify deployed before client targets existed does, and still does where none is configured.
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(TheRealBundle(), settings: ClientSettings(artifactStore: null, targets: Array.Empty<ClientTarget>())));

        Assert.Equal("record", ex.Refusals.Single().Check);
    }

    [Fact]
    public async Task AClientInput_WithNoBucket_IsRefused()
    {
        var input = ClientInput();
        input["target"] = new JsonObject { ["cluster"] = "scu-dev-cluster" };

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(TheRealBundle(), input));

        Assert.Contains("target.bucket", ex.Message);
    }

    [Fact]
    public async Task AClassWithNoBranch_IsRefusedByName()
    {
        // A well-formed site record, where the environment accepts the class: Verify has no branch for it yet.
        var site = BuildRecordFormatTests.BundleWorkflowEmitted.Replace("\"client\"", "\"site\"").Replace("client/scutara/", "site/scutara/");
        var input = ClientInput();
        input["record"]!["key"] = RecordKey.Replace("client/", "site/");
        var settings = ClientSettings() with { Classes = new[] { "image", "client", "site" } };

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyClient(TheRealBundle(), input, settings, site));

        Assert.Contains(ex.Refusals, r => r.Check == "class" && r.Reason.Contains("'site'"));
    }

    // ---------------------------------------------------------------------------------------
    //  DeployBundle
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstDeploy_ClaimsTheLease_WritesAssetsThenManifests_InvalidatesTheAppsPath_AndReleases()
    {
        var zip = Zip(Build1);
        var bucket = new Bucket();
        var invalidations = new Invalidations();

        var result = await Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket, invalidations);

        var log = bucket.Log;
        Assert.Equal("read-marker", log[0]);
        var ensure = IndexOf(log, l => l.StartsWith("ensure"));
        var claim = log.IndexOf("claim");
        var firstPut = IndexOf(log, l => l.StartsWith("put"));
        var release = log.IndexOf("release");
        Assert.True(ensure > 0 && ensure < claim, string.Join("\n", log));
        Assert.True(claim < firstPut, "a write came before the lease was held");
        Assert.True(LastIndexOf(log, l => l.StartsWith("put")) < release);
        Assert.Equal("ensure scu---webapp-sellerapp-4df6-b9c6 us-west-2 versioning=True days=30", log[ensure]);

        // Every manifest after every asset: index.html, dotnet.js and appConfig.js are the no-cache files here.
        var manifests = new[] { "seller/index.html", "seller/_framework/dotnet.js", "seller/_content/Lib/appConfig.js" }
            .Select(p => log.IndexOf($"put wwwroot/{p}")).ToList();
        var assets = new[] { "seller/_framework/App.aaa111.wasm", "seller/_framework/App.aaa111.wasm.br", "seller/_content/Lib/site.css" }
            .Select(p => log.IndexOf($"put wwwroot/{p}")).ToList();
        Assert.All(manifests.Concat(assets), i => Assert.True(i >= 0));
        Assert.True(assets.Max() < manifests.Min(), string.Join("\n", log));

        Assert.Equal(new[] { ("E31GJ01SW2CEEA", "/seller*", Execution) }, invalidations.Created);

        var marker = BundleMarker.Parse(bucket.Marker!.Value.Json);
        Assert.Null(marker.Claim);
        Assert.Equal(Execution, marker.Deployed!.Execution);
        Assert.Equal(BuiltAt, marker.Deployed.BuiltAt);
        Assert.Equal(BundleVersion, marker.Deployed.BundleVersionId);
        Assert.Equal(result["manifestSha256"]!.GetValue<string>(), marker.Deployed.ManifestSha256);

        Assert.Equal(6, result["files"]!.GetValue<int>());
        Assert.Equal(6, result["put"]!["absent"]!.GetValue<int>());
        Assert.Equal(0, result["deleted"]!.GetValue<int>());
        Assert.False(Directory.Exists(Path.Combine(_work, Execution)), "the expanded bundle was left in /tmp");
    }

    [Fact]
    public async Task ANewerBuild_WritesOnlyWhatChanged_AndDeletesTheOldNames_AfterItsManifests()
    {
        var bucket = new Bucket();
        var zip1 = Zip(Build1);
        await Deploy(Verified(zip1, version: "v1"), new Artifacts(zip1, "v1"), bucket, execution: "req-1-1");
        bucket.Log.Clear();

        var zip2 = Zip(Build2);
        var result = await Deploy(Verified(zip2, builtAt: "2026-09-15T09:00:00Z", version: "v2"), new Artifacts(zip2, "v2"), bucket, execution: "req-2-1");

        var log = bucket.Log;
        var puts = log.Where(l => l.StartsWith("put")).ToList();
        Assert.Equal(new[] { "put wwwroot/seller/_framework/App.bbb222.wasm", "put wwwroot/seller/_framework/App.bbb222.wasm.br" },
            puts.Take(2).Order(StringComparer.Ordinal));
        Assert.Equal(4, puts.Count);                     // two new assets, index.html and dotnet.js; appConfig.js and site.css unchanged
        Assert.Equal(2, result["unchanged"]!.GetValue<int>());

        var deletes = log.Where(l => l.StartsWith("delete")).ToList();
        Assert.Equal(new[] { "delete wwwroot/seller/_framework/App.aaa111.wasm", "delete wwwroot/seller/_framework/App.aaa111.wasm.br" }, deletes);
        Assert.True(LastIndexOf(log, l => l.StartsWith("put")) < IndexOf(log, l => l.StartsWith("delete")),
            "a file was deleted while a manifest naming it could still be served");
        Assert.Equal(Build2.Count, bucket.Objects.Count);
    }

    [Fact]
    public async Task AnOlderBundle_IsRefused_BeforeAnythingIsReadOrWritten()
    {
        var bucket = new Bucket();
        var newer = Zip(Build2);
        await Deploy(Verified(newer, builtAt: "2026-09-15T09:00:00Z", version: "v2"), new Artifacts(newer, "v2"), bucket, execution: "req-2-1");
        bucket.Log.Clear();
        var before = bucket.Objects.ToDictionary(o => o.Key, o => o.Value.Head);

        var older = Zip(Build1);
        var artifacts = new Artifacts(older, BundleVersion);
        var ex = await Assert.ThrowsAsync<DeploySuperseded>(() => Deploy(Verified(older), artifacts, bucket));

        Assert.Contains("already serves a newer build (2026-09-15T09:00:00Z", ex.Message);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
        Assert.Empty(artifacts.Calls);
        Assert.Equal(before, bucket.Objects.ToDictionary(o => o.Key, o => o.Value.Head));
    }

    [Fact]
    public async Task AnOlderBundle_IsDeployedOnPurpose_WithAllowOlderBuild()
    {
        var bucket = new Bucket();
        var newer = Zip(Build2);
        await Deploy(Verified(newer, builtAt: "2026-09-15T09:00:00Z", version: "v2"), new Artifacts(newer, "v2"), bucket, execution: "req-2-1");

        var older = Zip(Build1);
        var state = Verified(older);
        state["allowOlderBuild"] = true;
        var result = await Deploy(state, new Artifacts(older, BundleVersion), bucket);

        Assert.Contains("allowOlderBuild", result["ordering"]!.GetValue<string>());
        Assert.Equal("<html>build 1</html>", Encoding.UTF8.GetString(bucket.Objects["wwwroot/seller/index.html"].Bytes));
    }

    [Fact]
    public async Task AnotherExecutionsLiveLease_IsWaitedOn_WithNothingDownloadedOrWritten()
    {
        var bucket = new Bucket();
        bucket.Marker = (BundleMarker.Serialize(BundleMarker.Claimed(null, "2026-09-14T10:00:00Z", "req-other-1", Now)), bucket.NextETag());
        var zip = Zip(Build1);
        var artifacts = new Artifacts(zip, BundleVersion);

        var ex = await Assert.ThrowsAsync<BundleDeployInProgress>(() => Deploy(Verified(zip), artifacts, bucket));

        Assert.Contains("req-other-1", ex.Message);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
        Assert.Empty(artifacts.Calls);
    }

    [Fact]
    public async Task ALeaseTakenBetweenTheReadAndTheClaim_IsWaitedOn_BeforeAnyObjectIsWritten()
    {
        var bucket = new Bucket
        {
            BeforeMarkerWrite = (b, kind) =>
            {
                if (kind == "claim" && b.Marker is null)
                    b.Marker = (BundleMarker.Serialize(BundleMarker.Claimed(null, BuiltAt, "req-other-1", Now)), b.NextETag());
            },
        };
        var zip = Zip(Build1);

        await Assert.ThrowsAsync<BundleDeployInProgress>(() => Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket));

        Assert.Contains("claim-refused", bucket.Log);
        Assert.DoesNotContain(bucket.Log, l => l.StartsWith("put") || l.StartsWith("delete") || l.StartsWith("list"));
    }

    [Fact]
    public async Task AReleaseThatFindsItsLeaseTaken_IsARace_NotASuccess()
    {
        var bucket = new Bucket
        {
            BeforeMarkerWrite = (b, kind) =>
            {
                if (kind == "release")
                    b.Marker = (BundleMarker.Serialize(BundleMarker.Claimed(null, BuiltAt, "req-other-1", Now.AddMinutes(20))), b.NextETag());
            },
        };
        var zip = Zip(Build1);

        await Assert.ThrowsAsync<BundleDeployRaced>(() => Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket));

        Assert.Contains("release-refused", bucket.Log);
    }

    [Fact]
    public async Task BytesThatDoNotHashToWhatVerifyChecked_AreRefused_BeforeTheBucketIsTouched()
    {
        var zip = Zip(Build1);
        var artifacts = new Artifacts(zip, BundleVersion) { ServedBytes = Zip(Build2) };
        var bucket = new Bucket();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Deploy(Verified(zip), artifacts, bucket));

        Assert.Equal("identity.sha256", ex.Refusals.Single().Check);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
    }

    [Fact]
    public async Task AnotherVersionServed_IsRefused_BeforeTheBucketIsTouched()
    {
        var zip = Zip(Build1);
        var bucket = new Bucket();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Deploy(Verified(zip), new Artifacts(zip, BundleVersion) { ServedVersion = "other" }, bucket));

        Assert.Equal("identity.versionId", ex.Refusals.Single().Check);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
    }

    [Fact]
    public async Task ABundleBuiltForAnotherApp_IsRefused_BeforeItHoldsALease()
    {
        // SellerApp's bundle, deployed as AdminApp.
        var zip = Zip(Build1);
        var bucket = new Bucket();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Deploy(Verified(zip, target: Admin), new Artifacts(zip, BundleVersion), bucket));

        Assert.Contains("built for base path 'seller/'", ex.Message);
        Assert.Equal(new[] { "read-marker" }, bucket.Log);
    }

    [Theory]
    [InlineData("seller/../../escape.txt")]
    [InlineData("seller/INDEX.html")]
    public async Task ABundleWhoseEntriesCouldEscapeOrCollide_IsRefused_BeforeTheBucketIsTouched(string entry)
    {
        var files = new SortedDictionary<string, string>(Build1.ToDictionary(f => f.Key, f => f.Value), StringComparer.Ordinal);
        var zip = Zip(files, entry);
        var bucket = new Bucket();

        await Assert.ThrowsAsync<DeployRefused>(() => Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket));

        Assert.Equal(new[] { "read-marker" }, bucket.Log);
        Assert.False(File.Exists(Path.Combine(_work, "escape.txt")));
    }

    [Fact]
    public async Task TheSameExecutionAgain_WritesNothing_AndGetsTheSameInvalidation()
    {
        // What a Step Functions retry of DeployBundle does after a fault past the release.
        var zip = Zip(Build1);
        var bucket = new Bucket();
        var invalidations = new Invalidations();
        await Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket, invalidations);
        bucket.Log.Clear();

        var again = await Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket, invalidations);

        Assert.DoesNotContain(bucket.Log, l => l.StartsWith("put") || l.StartsWith("delete"));
        Assert.Single(invalidations.Created);
        Assert.Equal("I1", again["invalidations"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(6, again["unchanged"]!.GetValue<int>());
    }

    [Fact]
    public async Task ARetryHoldingItsOwnLease_Continues()
    {
        // The first attempt claimed and died; the retry meets its own live claim.
        var zip = Zip(Build1);
        var bucket = new Bucket();
        bucket.Marker = (BundleMarker.Serialize(BundleMarker.Claimed(null, BuiltAt, Execution, Now)), bucket.NextETag());

        await Deploy(Verified(zip), new Artifacts(zip, BundleVersion), bucket);

        Assert.Null(BundleMarker.Parse(bucket.Marker!.Value.Json).Claim);
    }

    // ---------------------------------------------------------------------------------------
    //  VerifyBundle, and Record after it
    // ---------------------------------------------------------------------------------------

    private async Task<(JsonObject State, Bucket Bucket, Invalidations Invalidations)> Deployed()
    {
        var zip = Zip(Build1);
        var bucket = new Bucket();
        var invalidations = new Invalidations();
        var state = Verified(zip);
        state["deploy"] = await Deploy(state, new Artifacts(zip, BundleVersion), bucket, invalidations);
        bucket.Log.Clear();
        return (state, bucket, invalidations);
    }

    [Fact]
    public async Task ADeployedBundle_ReadsBack_AndItsEvidenceNamesTheBundleVersion_WhereRecordWritesIt()
    {
        var (state, bucket, invalidations) = await Deployed();

        var result = await VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now);

        Assert.Equal("Landed", result["verdict"]!.GetValue<string>());
        Assert.Equal(6, result["objects"]!.GetValue<int>());
        Assert.Equal("deploys/client/scutara/scutarasellerapp/" + Execution + ".json", result["evidence"]!["key"]!.GetValue<string>());

        var body = JsonNode.Parse(result["evidence"]!["body"]!.GetValue<string>())!;
        Assert.Equal("deployed", body["outcome"]!.GetValue<string>());
        Assert.Equal(BundleVersion, body["verified"]!["identity"]!["versionId"]!.GetValue<string>());
        Assert.Equal(SellerBucket, body["target"]!["bucket"]!.GetValue<string>());
        Assert.Equal(state["deploy"]!["manifestSha256"]!.GetValue<string>(), body["verification"]!["manifestSha256"]!.GetValue<string>());

        // Record takes it from there, unchanged.
        state["rollout"] = result;
        var writer = new EvidenceWriter();
        var recorded = await RecordStep.RunAsync(state, Execution, "scu-dev-deploy-evidence-4df6-b9c6", writer);
        Assert.Equal("deploys/client/scutara/scutarasellerapp/" + Execution + ".json", recorded["key"]!.GetValue<string>());
        Assert.Single(writer.Written);
    }

    private sealed class EvidenceWriter : IEvidenceWriter
    {
        public List<(string Bucket, string Key)> Written { get; } = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body)
        {
            Written.Add((bucket, key));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task AnInvalidationStillInProgress_IsWaitedOn_BeforeTheAppIsRead()
    {
        var (state, bucket, invalidations) = await Deployed();
        invalidations.Status = "InProgress";

        await Assert.ThrowsAsync<BundleInvalidationInProgress>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Empty(bucket.Log);
    }

    [Fact]
    public async Task AnObjectWhoseBytesChanged_IsNotTheBundle()
    {
        var (state, bucket, invalidations) = await Deployed();
        var key = "wwwroot/seller/_content/Lib/site.css";
        var o = bucket.Objects[key];
        bucket.Objects[key] = (o.Bytes, o.Head with { Sha256Hex = new string('9', 64) });

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("hash to manifest", ex.Message);
    }

    [Fact]
    public async Task AnExtraObject_IsNotTheBundle()
    {
        var (state, bucket, invalidations) = await Deployed();
        bucket.Holds(new Dictionary<string, string> { ["seller/stray.js"] = "stray" });

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("7 object(s)", ex.Message);
    }

    [Fact]
    public async Task AnObjectWithTheWrongHeaders_IsNotTheBundle()
    {
        var (state, bucket, invalidations) = await Deployed();
        var key = "wwwroot/seller/index.html";
        var o = bucket.Objects[key];
        bucket.Objects[key] = (o.Bytes, o.Head with { CacheControl = WebappSyncRules.Baseline });

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("index.html is 'public, max-age=3600'", ex.Message);
    }

    [Fact]
    public async Task AnObjectWithNoChecksumOfItsOwn_IsNotEvidence()
    {
        var (state, bucket, invalidations) = await Deployed();
        var key = "wwwroot/seller/_content/Lib/site.css";
        var o = bucket.Objects[key];
        bucket.Objects[key] = (o.Bytes, o.Head with { Sha256Hex = null });

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("carry no SHA-256", ex.Message);
    }

    [Fact]
    public async Task ADeploySupersededBeforeItWasVerified_IsNotReportedAsLanded()
    {
        var (state, bucket, invalidations) = await Deployed();
        var later = Zip(Build2);
        await Deploy(Verified(later, builtAt: "2026-09-15T09:00:00Z", version: "v2"), new Artifacts(later, "v2"), bucket, invalidations, "req-2-1");

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("req-2-1", ex.Message);
    }

    [Fact]
    public async Task AMarkerThatChangesWhileTheAppIsReadBack_MakesTheReadUntrustworthy()
    {
        var (state, bucket, invalidations) = await Deployed();
        // The second read VerifyBundle makes is the one after the objects: another deploy's write lands between the two.
        var reads = 0;
        bucket.OnMarkerRead = b =>
        {
            if (++reads == 2) b.Marker = (b.Marker!.Value.Json, b.NextETag());
        };

        var ex = await Assert.ThrowsAsync<BundleNotDeployed>(() => VerifyBundleStep.RunAsync(state, Execution, bucket, invalidations, Now));

        Assert.Contains("changed while its objects were read back", ex.Message);
    }
}
