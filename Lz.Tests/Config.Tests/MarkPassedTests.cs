using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// MarkPassed (DecoupledCd.md §12 P5 stage D, T-13): after Gate 2 passed, the set it found running - each service's digest
/// and each app's bundle version - traced to this environment's deploy evidence, and written once as a pass marker at
/// <c>passed/sets/{sha256}.json</c> in its own evidence store, for the next environment to read. A member with no deploy
/// evidence reached the environment outside the pipeline, and no marker is written for its set.
/// </summary>
public class MarkPassedTests
{
    private const string Store = "scu-dev-deploy-evidence-4df6-b9c6", Records = "scu-build-records-4df6-b9c6";
    private const string Digest = "sha256:224d5d661bfda961eb89d72e76ed960a47d5fbc4d781db5a2a962bc1134085b2";
    private const string Execution = "req-20261026T100000Z-40300000000-1-mp-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T10:20:00Z");

    private static readonly MarkPassedSettings Settings = new("dev", Store, new[]
    {
        new MarkMember("image", "Scutara/ScutaraService", "scu-dev-cluster/scu-mp-aiphost"),
        new MarkMember("client", "Scutara/ScutaraSellerApp", "sellerapp"),
        new MarkMember("client", "Scutara/ScutaraAdminApp", "adminapp"),
    });

    private sealed class Evidence : IGate2EvidenceReader, IRecordStore, IEvidenceWriter
    {
        public Dictionary<string, (string Json, string Version)> Objects { get; } = new();
        public Dictionary<string, string> Written { get; } = new();
        public bool Exists { get; init; }
        public Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix)
        { Assert.Equal(Store, bucket); return Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList()); }
        public Task<string?> ReadAsync(string bucket, string key) => throw new InvalidOperationException("MarkPassed reads evidence with its version");
        Task<StoredRecord?> IRecordStore.ReadAsync(string bucket, string key)
        { Assert.Equal(Store, bucket); return Task.FromResult(Objects.TryGetValue(key, out var o) ? new StoredRecord(o.Json, o.Version) : null); }
        public Task<bool> PutOnceAsync(string bucket, string key, string body)
        { Assert.Equal(Store, bucket); if (Exists) return Task.FromResult(false); Written[key] = body; return Task.FromResult(true); }

        public void ImageDeploy(string exec, string digest, string outcome = "deployed", string? serviceVersion = "1.0.430") => Objects[$"deploys/image/scutara/scutaraservice/{exec}.json"] = (new JsonObject
        {
            ["schema"] = 1, ["outcome"] = outcome,
            ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = $"image/scutara/scutaraservice/{exec[4..20]}-1.json" },
            ["verified"] = new JsonObject
            {
                ["class"] = "image", ["digest"] = digest, ["recordVersionId"] = "rv-" + exec, ["serviceVersion"] = serviceVersion,
                ["builtFrom"] = BuiltFrom("Scutara/ScutaraService"),
            },
        }.ToJsonString(), "dv-" + exec);

        public void ClientDeploy(string repo, string exec, string version) => Objects[$"deploys/client/scutara/{repo.ToLowerInvariant()}/{exec}.json"] = (new JsonObject
        {
            ["schema"] = 1, ["outcome"] = "deployed",
            ["record"] = new JsonObject { ["bucket"] = Records, ["key"] = $"client/scutara/{repo.ToLowerInvariant()}/{exec[4..20]}-1.json" },
            ["verified"] = new JsonObject
            {
                ["class"] = "client", ["recordVersionId"] = "rv-" + exec,
                ["identity"] = new JsonObject { ["bucket"] = "scu-artifacts-4df6-b9c6", ["key"] = $"client/scutara/{repo.ToLowerInvariant()}/{exec[4..20]}-1.zip", ["versionId"] = version, ["sha256"] = new string('c', 64), ["bytes"] = 1234 },
                ["builtFrom"] = BuiltFrom($"Scutara/{repo}"),
            },
        }.ToJsonString(), "dv-" + exec);
    }

    private static JsonObject BuiltFrom(string repo) => new()
    {
        ["repo"] = repo, ["commit"] = new string('1', 40), ["lane"] = "published",
        ["packages"] = new JsonObject { ["AipApi"] = "1.0.421" }, ["ref"] = "refs/heads/main",
    };

    private static Evidence World()
    {
        var e = new Evidence();
        e.ImageDeploy("req-20261025T090000Z-40200000000-1-mp-1", "sha256:" + new string('0', 64));   // an older image
        e.ImageDeploy(Execution, Digest);
        e.ClientDeploy("ScutaraSellerApp", "req-20261024T080000Z-40100000000-1-sellerapp-1", "bun-sel-7");
        e.ClientDeploy("ScutaraAdminApp", "req-20261023T080000Z-40000000000-1-adminapp-1", "bun-adm-3");
        e.Objects[$"tests/{Execution}.json"] = (new JsonObject { ["schema"] = 1, ["outcome"] = "Passed" }.ToJsonString(), "tv-1");
        return e;
    }

    private static JsonObject State(string outcome = "Passed", JsonObject? before = null) => new()
    {
        ["gate2Start"] = new JsonObject
        {
            ["before"] = before ?? new JsonObject
            {
                ["digests"] = new JsonObject { ["scu-dev-cluster/scu-mp-aiphost"] = new JsonArray(Digest, Digest) },
                ["bundles"] = new JsonObject { ["sellerapp"] = "bun-sel-7", ["adminapp"] = "bun-adm-3" },
            },
        },
        ["gate2"] = new JsonObject { ["outcome"] = outcome, ["evidence"] = $"tests/{Execution}.json" },
    };

    private static Task<JsonObject> Mark(Evidence e, JsonObject? state = null) => MarkPassedStep.RunAsync(state ?? State(), Execution, Settings, e, e, e, Now);

    [Fact]
    public async Task APassedSet_IsWrittenOnce_AsTheMarkerOfWhatRan()
    {
        var e = World();
        var result = await Mark(e);

        var key = result["key"]!.GetValue<string>();
        var marker = PassMarkerFormat.Parse(e.Written[key], "dev");   // what the next environment will read, read as it will
        Assert.Equal(PassMarkerFormat.KeyFor(marker.MarkerId), key);
        Assert.Equal(3, marker.Members.Count);

        var image = marker.Members.Single(m => m.Class == "image");
        Assert.Equal((Digest, "1.0.430", $"rv-{Execution}"), (image.Identity.Digest, image.Version, image.Record.VersionId));
        Assert.Equal(($"deploys/image/scutara/scutaraservice/{Execution}.json", $"dv-{Execution}"), (image.Deploy.Key, image.Deploy.VersionId));
        var seller = marker.Members.Single(m => m.Repo == "Scutara/ScutaraSellerApp");
        Assert.Equal(("bun-sel-7", "bun-sel-7"), (seller.Identity.VersionId, seller.Version));
        Assert.Equal(($"tests/{Execution}.json", "tv-1"), (marker.Evidence.Tests.Key, marker.Evidence.Tests.VersionId));
        Assert.Equal(Execution, marker.Execution);
    }

    [Fact]
    public async Task AnAppServingNothing_IsNoMemberOfTheSet()
    {
        var before = State()["gate2Start"]!["before"]!.DeepClone().AsObject();
        before["bundles"]!["adminapp"] = null;
        var e = World();
        var result = await Mark(e, State(before: before));
        Assert.Equal(2, PassMarkerFormat.Parse(e.Written[result["key"]!.GetValue<string>()], "dev").Members.Count);
    }

    [Fact]
    public async Task AMemberWithNoDeployEvidenceHere_RefusesTheSet_SinceItReachedTheEnvironmentOutsideThePipeline()
    {
        var before = State()["gate2Start"]!["before"]!.DeepClone().AsObject();
        before["digests"]!["scu-dev-cluster/scu-mp-aiphost"] = new JsonArray("sha256:" + new string('9', 64));
        var e = World();
        var ex = await Assert.ThrowsAsync<MarkPassedRefused>(() => Mark(e, State(before: before)));
        Assert.Contains("outside the pipeline", ex.Message);
        Assert.Empty(e.Written);
    }

    [Fact]
    public async Task ADeployThatDidNotLand_IsNotItsEvidence()
    {
        var e = World();
        e.ImageDeploy(Execution, Digest, outcome: "failed");
        await Assert.ThrowsAsync<MarkPassedRefused>(() => Mark(e));
    }

    [Fact]
    public async Task AServiceRunningTwoDigests_RefusesTheSet()
    {
        var before = State()["gate2Start"]!["before"]!.DeepClone().AsObject();
        before["digests"]!["scu-dev-cluster/scu-mp-aiphost"] = new JsonArray(Digest, "sha256:" + new string('0', 64));
        await Assert.ThrowsAsync<MarkPassedRefused>(() => Mark(World(), State(before: before)));
    }

    [Theory]
    [InlineData("TestsSuperseded")]
    [InlineData("TestsIncomplete")]
    [InlineData("PostDeployTestsFailed")]
    public async Task OnlyAPassedGate_IsMarked(string outcome)
    {
        var e = World();
        await Assert.ThrowsAsync<MarkPassedRefused>(() => Mark(e, State(outcome)));
        Assert.Empty(e.Written);
    }

    [Fact]
    public async Task ASecondPassOfTheSameSet_CountsAsAlreadyPassed()
    {
        var e = new Evidence { Exists = true };
        foreach (var o in World().Objects) e.Objects[o.Key] = o.Value;
        var result = await Mark(e);
        Assert.False(result["written"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnImageDeployFromBeforeVerifyRecordedItsServiceVersion_IsNamedByItsDigest()
    {
        var e = World();
        e.ImageDeploy(Execution, Digest, serviceVersion: null);
        var result = await Mark(e);
        Assert.Equal(Digest, PassMarkerFormat.Parse(e.Written[result["key"]!.GetValue<string>()], "dev").Members.Single(m => m.Class == "image").Version);
    }
}
