using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// What Gate 2 runs (DecoupledCd.md §15.5): the e2e tier from the tests record of the image member that runs — for an image's
/// gate, the same run attempt's; for a client's, the running image's, found through this environment's deploy evidence — and
/// the browser tier from the newest ScutaraE2ETests tests record. Each by version, presigned, with Gate 1's lists.
/// </summary>
public sealed class Gate2BundlesTests
{
    private const string Records = "scu-build-records-4df6-b9c6", Artifacts = "scu-artifacts-4df6-b9c6", Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private const string Stem = "20261023T204651Z-40021850117-1";
    private const string Digest = "sha256:d5de";
    private const string BrowserPrefix = "tests/scutara/scutarae2etests/";

    /// <summary>A tests record as the intake writes one; its stem is {builtAt}-{run}-{attempt}, which the parser holds its key to.</summary>
    private static string TestsRecord(string repo, string stem, BuildRecordGate1Lists? lists)
    {
        var parts = stem.Split('-');
        var t = parts[0];
        var builtAt = $"{t[..4]}-{t[4..6]}-{t[6..8]}T{t[9..11]}:{t[11..13]}:{t[13..15]}Z";
        return BuildRecordFormat.Serialize(new BuildRecord(
            BuildRecordFormat.CurrentSchema, "tests",
            new BuildRecordBuiltFrom($"Scutara/{repo}", new string('a', 40), "published", new Dictionary<string, string>(), "refs/heads/main"),
            new BuildRecordIdentity("bundle", Bucket: Artifacts, Key: $"tests/scutara/{repo.ToLowerInvariant()}/{stem}.zip", VersionId: $"v-{stem}", Sha256: new string('b', 64)),
            builtAt, "tmay57", parts[1],
            new BuildRecordIntake(parts[1], int.Parse(parts[2]), "1", "w", "sha256:" + new string('c', 64), new BuildRecordGate1("passed", "gate1/x.json", lists))));
    }

    private static readonly BuildRecordGate1Lists E2eLists = new(new[] { "P.Pre" }, new[] { "B.One", "B.Two" }, new[] { "A.Adv" });
    private static readonly BuildRecordGate1Lists BrowserLists = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), GateCount: 12);

    private sealed class Store : IRecordStore, IGate2EvidenceReader, IGate2RecordList, IGate2Presigner
    {
        public readonly Dictionary<(string, string), string> Objects = new();
        public Task<StoredRecord?> ReadAsync(string bucket, string key)
            => Task.FromResult(Objects.TryGetValue((bucket, key), out var v) ? new StoredRecord(v, "rv1") : null);
        Task<string?> IGate2EvidenceReader.ReadAsync(string bucket, string key) => Task.FromResult(Objects.GetValueOrDefault((bucket, key)));
        public Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix)
            => Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.Item1 == bucket && k.Item2.StartsWith(prefix, StringComparison.Ordinal)).Select(k => k.Item2).ToList());
        public string GetUrl(string bucket, string key, string versionId, TimeSpan lifetime) => $"https://{bucket}/{key}?versionId={versionId}&ttl={lifetime.TotalMinutes}";
    }

    private static Store Filled(BuildRecordGate1Lists? e2e = null, bool browser = true)
    {
        var s = new Store();
        s.Objects[(Records, $"tests/scutara/scutaraservice/{Stem}.json")] = TestsRecord("ScutaraService", Stem, e2e ?? E2eLists);
        if (browser)
        {
            s.Objects[(Records, $"{BrowserPrefix}20261022T100000Z-1-1.json")] = TestsRecord("ScutaraE2ETests", "20261022T100000Z-1-1", BrowserLists with { GateCount = 11 });
            s.Objects[(Records, $"{BrowserPrefix}20261023T100000Z-2-1.json")] = TestsRecord("ScutaraE2ETests", "20261023T100000Z-2-1", BrowserLists);
        }
        return s;
    }

    private static Gate2RecordBundles Bundles(Store s) => new(s, s, s, s, Records, Artifacts, Evidence, BrowserPrefix);

    private static JsonObject ImageState() => new()
    {
        ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = $"image/scutara/scutaraservice/{Stem}.json" },
        ["verified"] = new JsonObject { ["class"] = "image", ["digest"] = Digest },
    };

    private static JsonObject ClientState() => new()
    {
        ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = "client/scutara/scutaraadminapp/x-1.json" },
        ["verified"] = new JsonObject { ["class"] = "client" },
    };

    private static Gate2Running Running(params string?[] digests)
        => new(new Dictionary<string, IReadOnlyList<string?>> { ["scu-dev-cluster/scu-mp-aiphost"] = digests }, new Dictionary<string, string?>());

    [Fact]
    public async Task AnImagesGate_RunsTheSameAttemptsTests_ByVersion_WithGate1sLists()
    {
        var bundles = await Bundles(Filled()).ResolveAsync(ImageState(), Running(Digest));

        Assert.Equal($"https://{Artifacts}/tests/scutara/scutaraservice/{Stem}.zip?versionId=v-{Stem}&ttl=15", bundles.Variables["GATE2_E2E_BUNDLE_URL"]);
        Assert.Equal(new string('b', 64), bundles.Variables["GATE2_E2E_BUNDLE_SHA256"]);
        Assert.Equal(new[] { "B.One", "B.Two" }, bundles.Recorded.Blocking);
        Assert.Equal(new[] { "P.Pre" }, bundles.Recorded.Precondition);
    }

    [Fact]
    public async Task TheBrowserTier_IsTheNewestScutaraE2ETestsRecord_WithItsGateCount()
    {
        var bundles = await Bundles(Filled()).ResolveAsync(ImageState(), Running(Digest));

        Assert.Contains("tests/scutara/scutarae2etests/20261023T100000Z-2-1.zip", bundles.Variables["GATE2_BROWSER_BUNDLE_URL"]);
        Assert.Equal(12, bundles.Recorded.GateCount);
    }

    [Fact]
    public async Task AClientsGate_RunsTheRunningImagesTests_FoundThroughThisEnvironmentsDeployEvidence()
    {
        var s = Filled();
        s.Objects[(Evidence, "deploys/image/scutara/scutaraservice/req-old-mp-1.json")] = new JsonObject
        {
            ["record"] = new JsonObject { ["key"] = "image/scutara/scutaraservice/20261001T000000Z-1-1.json" }, ["verified"] = new JsonObject { ["digest"] = "sha256:old" },
        }.ToJsonString();
        s.Objects[(Evidence, "deploys/image/scutara/scutaraservice/req-new-mp-1.json")] = new JsonObject
        {
            ["record"] = new JsonObject { ["key"] = $"image/scutara/scutaraservice/{Stem}.json" }, ["verified"] = new JsonObject { ["digest"] = Digest },
        }.ToJsonString();

        var bundles = await Bundles(s).ResolveAsync(ClientState(), Running(Digest, Digest));

        Assert.Contains($"tests/scutara/scutaraservice/{Stem}.zip", bundles.Variables["GATE2_E2E_BUNDLE_URL"]);
    }

    [Fact]
    public async Task AClientsGate_WhileTheServiceRunsTwoImages_CannotNameItsImageMember()
    {
        var ex = await Assert.ThrowsAsync<Gate2NotResolvable>(() => Bundles(Filled()).ResolveAsync(ClientState(), Running(Digest, "sha256:other")));
        Assert.Contains("more than one image", ex.Message);
    }

    [Fact]
    public async Task ATestsRecordWrittenBeforeGate1sListsWere_CannotBeGated()
    {
        var s = Filled();
        s.Objects[(Records, $"tests/scutara/scutaraservice/{Stem}.json")] = TestsRecord("ScutaraService", Stem, lists: null);
        var ex = await Assert.ThrowsAsync<Gate2NotResolvable>(() => Bundles(s).ResolveAsync(ImageState(), Running(Digest)));
        Assert.Contains("lists", ex.Message);
    }

    [Fact]
    public async Task NoTestsRecord_OrNoBrowserRecord_CannotBeGated()
    {
        var noTests = Filled();
        noTests.Objects.Remove((Records, $"tests/scutara/scutaraservice/{Stem}.json"));
        await Assert.ThrowsAsync<Gate2NotResolvable>(() => Bundles(noTests).ResolveAsync(ImageState(), Running(Digest)));
        await Assert.ThrowsAsync<Gate2NotResolvable>(() => Bundles(Filled(browser: false)).ResolveAsync(ImageState(), Running(Digest)));
    }
}
