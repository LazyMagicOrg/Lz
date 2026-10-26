using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// MarkPassed's site mode (DecoupledCd.md V-K step 5, S3): after a site execution's Record in an environment with a successor
/// that takes the site, the site it deployed and verified - Record's own evidence, read at its version - written once as a
/// site marker at <c>passed/site/{sha256}.json</c>. The site has no Gate 2 (V-24), so deploying and verifying it is its pass,
/// and the marker names no test evidence. The state is the one a real dev site execution ended with, 2026-10-26.
/// </summary>
public class MarkSitePassedTests
{
    private const string Store = "scu-dev-deploy-evidence-4df6-b9c6", Records = "scu-build-records-4df6-b9c6";
    private const string Execution = "req-20261026T193011Z-40387310346-1-site-1";
    private const string RecordKey = "site/scutara/scutarawebsite/dev/20261026T193011Z-40387310346-1.json";
    private const string EvidenceKey = "deploys/site/scutara/scutarawebsite/req-20261026T193011Z-40387310346-1-site-1.json";
    private const string BundleVersion = "NcCeI67eRMKpQryHfgiszmLVo2RYlPCK";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T19:31:28Z");
    private static readonly MarkPassedSettings Settings = new("dev", Store, Array.Empty<MarkMember>());

    private sealed class Evidence : IRecordStore, IEvidenceWriter
    {
        public Dictionary<string, (string Json, string? Version)> Objects { get; } = new();
        public Dictionary<string, string> Written { get; } = new();
        public bool Exists { get; init; }
        public Task<StoredRecord?> ReadAsync(string bucket, string key)
        { Assert.Equal(Store, bucket); return Task.FromResult(Objects.TryGetValue(key, out var o) ? new StoredRecord(o.Json, o.Version) : null); }
        public Task<bool> PutOnceAsync(string bucket, string key, string body)
        { Assert.Equal(Store, bucket); if (Exists) return Task.FromResult(false); Written[key] = body; return Task.FromResult(true); }
    }

    private static JsonObject Identity(string version = BundleVersion) => new()
    {
        ["bucket"] = "scu-artifacts-4df6-b9c6", ["key"] = "site/scutara/scutarawebsite/dev/20261026T193011Z-40387310346-1.zip",
        ["versionId"] = version, ["sha256"] = "14729756d9d419bc3a7ee34da61190f3a1006c44166987fb2942de740458e0c9", ["bytes"] = 1327115,
    };

    private static JsonObject Verified(string version = BundleVersion, string cls = "site") => new()
    {
        ["class"] = cls,
        ["identity"] = Identity(version),
        ["target"] = new JsonObject { ["repo"] = "Scutara/ScutaraWebsite", ["environment"] = "dev", ["bucket"] = "scu-mp--webapp-website-4df6-b9c6", ["domain"] = "aiproxydev.click", ["keyPrefix"] = "wwwroot/" },
        ["recordVersionId"] = "vJkw2swnCuFUaEskw6dXsecBQnEqYJd5",
        ["builtFrom"] = new JsonObject
        {
            ["repo"] = "Scutara/ScutaraWebsite", ["commit"] = "d33b02b4c90f6eab15192bf4a7d9c9ff398991af", ["lane"] = "published",
            ["packages"] = new JsonObject(), ["ref"] = "refs/heads/main",
        },
        ["builtAt"] = "2026-10-26T19:30:11Z", ["workflowRunId"] = "40387310346",
    };

    private static Evidence World(string outcome = "deployed", string evidenceVersion = BundleVersion, string? readVersion = "ev-1")
    {
        var e = new Evidence();
        e.Objects[EvidenceKey] = (new JsonObject
        {
            ["schema"] = 1, ["outcome"] = outcome, ["execution"] = Execution,
            ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = RecordKey },
            ["verified"] = Verified(evidenceVersion),
        }.ToJsonString(), readVersion);
        return e;
    }

    private static JsonObject State(string cls = "site", bool recorded = true) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = RecordKey },
        ["verified"] = Verified(cls: cls),
        ["recorded"] = recorded ? new JsonObject { ["key"] = EvidenceKey, ["written"] = true } : null,
    };

    private static Task<JsonObject> Mark(Evidence e, JsonObject? state = null) => MarkPassedStep.RunSiteAsync(state ?? State(), Execution, Settings, e, e, Now);

    private static async Task<string> Refused(Evidence e, JsonObject? state = null)
        => (await Assert.ThrowsAsync<MarkPassedRefused>(() => Mark(e, state))).Message;

    [Fact]
    public async Task TheSiteItDeployed_IsWrittenOnce_AsASiteMarker_FromRecordsEvidence()
    {
        var e = World();
        var result = await Mark(e);

        var (key, body) = Assert.Single(e.Written);
        Assert.Matches("^passed/site/[0-9a-f]{64}\\.json$", key);
        Assert.Equal(key, result["key"]!.GetValue<string>());
        Assert.True(result["written"]!.GetValue<bool>());

        var marker = PassMarkerFormat.Parse(body, "dev");
        Assert.True(PassMarkerFormat.IsSite(marker));
        Assert.Null(marker.Evidence.Tests);
        Assert.Equal((Execution, "2026-10-26T19:31:28.0000000+00:00"), (marker.Execution, marker.WrittenAt));
        var site = Assert.Single(marker.Members);
        Assert.Equal(("site", "Scutara/ScutaraWebsite", BundleVersion), (site.Class, site.Repo, site.Version));
        Assert.Equal(new PassMarkerRecordReference(Records, RecordKey, "vJkw2swnCuFUaEskw6dXsecBQnEqYJd5"), site.Record);
        Assert.Equal((EvidenceKey, "ev-1"), (site.Deploy.Key, site.Deploy.VersionId));
        Assert.Equal(("bundle", BundleVersion), (site.Identity.Kind, site.Identity.VersionId));
        Assert.Equal("d33b02b4c90f6eab15192bf4a7d9c9ff398991af", site.BuiltFrom.Commit);
    }

    [Fact]
    public async Task ASecondPass_OfTheSameSite_CountsAsAlreadyPassed()
    {
        var result = await Mark(new Evidence { Exists = true, Objects = { [EvidenceKey] = World().Objects[EvidenceKey] } });
        Assert.False(result["written"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EvidenceThatDidNotLand_IsNoPass()
        => Assert.Contains("deployed", await Refused(World(outcome: "failed")));

    [Fact]
    public async Task EvidenceNamingAnotherBundleVersion_IsNotThisSite()
        => Assert.Contains(BundleVersion, await Refused(World(evidenceVersion: "another-version")));

    [Fact]
    public async Task EvidenceNotReadAtAVersion_IsRefused()
        => Assert.Contains("version", await Refused(World(readVersion: null)));

    [Fact]
    public async Task AStateWithoutRecordsEvidence_IsRefused()
        => Assert.Contains("recorded", await Refused(World(), State(recorded: false)));

    // ---- the function's choice of mode ----

    [Fact]
    public async Task AVerifiedSite_IsMarkedInSiteMode_ByTheOneFunction()
    {
        var e = World();
        var result = await MarkPassedStep.RunForStateAsync(State(), Execution, Settings, new NoGate2Reads(), e, e, Now);
        Assert.StartsWith("passed/site/", result["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnythingElse_IsMarkedAsASet()
        // A client execution has no Gate 2 result in this state, so the set mode refuses it - by its own words, not the site's.
        => Assert.Contains("Gate 2", (await Assert.ThrowsAsync<MarkPassedRefused>(() =>
            MarkPassedStep.RunForStateAsync(State(cls: "client"), Execution, Settings, new NoGate2Reads(), World(), World(), Now))).Message);

    private sealed class NoGate2Reads : IGate2EvidenceReader
    {
        public Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix) => throw new InvalidOperationException("site mode lists nothing");
        public Task<string?> ReadAsync(string bucket, string key) => throw new InvalidOperationException("site mode reads nothing unversioned");
    }

    [Fact]
    public async Task AnExecutionThatDeployedNoSite_IsRefused()
        => Assert.Contains("site", await Refused(World(), State(cls: "client")));
}
