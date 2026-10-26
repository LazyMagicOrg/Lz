using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Test's Verify admits by marker (DecoupledCd.md §12 P5 stage F): an execution names a set marker in its predecessor's
/// evidence store, at a version, and VerifySet refuses it unless the marker parses as the predecessor's, sits at its own id,
/// is undecided here, names test evidence that passed and deploy evidence for every member, and each member's record at the
/// marker's version carries the marker's identity and builtFrom - and no client is newer, by its AipApi, than the Service it
/// runs beside. Only then is each member put through this environment's own Verify, the image first.
/// </summary>
public class VerifySetTests
{
    private const string Predecessor = "dev", Dev = "scu-dev-deploy-evidence-4df6-b9c6", Own = "scu-test-deploy-evidence-d5e0-b27f";
    private const string Records = "scu-build-records-4df6-b9c6", Artifacts = "scu-artifacts-4df6-b9c6";
    private const string ImageDigest = "sha256:224d5d661bfda961eb89d72e76ed960a47d5fbc4d781db5a2a962bc1134085b2";

    private static readonly PromotionSettings Promotion = new(Predecessor, Dev, Own);

    private static readonly VerifySettings Settings = new(
        new[] { "image", "client" }, new[] { "CRITICAL" }, Records, new[] { "scu-4df6-b9c6-aiphost" }, Artifacts,
        new[] { new ClientTarget("Scutara/ScutaraSellerApp", "sellerapp", "scu---webapp-sellerapp-d5e0-b27f", "seller/", new[] { "E1" }) });

    private static BuildRecord ImageRecord(string? serviceVersion = "1.0.421", string digest = ImageDigest) => new(
        1, "image",
        new BuildRecordBuiltFrom("Scutara/ScutaraService", new string('1', 40), "published", new Dictionary<string, string> { ["LazyMagic.Shared"] = "3.0.43-alpha" }, "refs/heads/main"),
        new BuildRecordIdentity("image", Digest: digest),
        "20261025T234220Z", "tmay57", "40245961450",
        new BuildRecordIntake("40245961450", 1, "121000000001", "Scutara/ScutaraService/.github/workflows/build-aiphost.yml@refs/heads/main",
            ImageDigest, new BuildRecordGate1("passed", "gate1/1035592016-40245961450-1.json"), ServiceVersion: serviceVersion,
            BaseImageDigest: "sha256:" + new string('e', 64), Sbom: new BuildRecordObjectRef("sbom/scutara/scutaraservice/20261025T234220Z-40245961450-1.spdx.json", "sbom-v1")));

    private static BuildRecord ClientRecord(string aipApi = "1.0.368")
    {
        var r = new BuildRecord(
            1, "client",
            new BuildRecordBuiltFrom("Scutara/ScutaraSellerApp", new string('2', 40), "published", new Dictionary<string, string> { ["AipApi"] = aipApi }, "refs/heads/main"),
            new BuildRecordIdentity("bundle", Bucket: Artifacts, Key: "placeholder", VersionId: "bundle-v9", Sha256: new string('c', 64)),
            "20261022T133101Z", "tmay57", "39787525465",
            new BuildRecordIntake("39787525465", 1, "121000000002", "Scutara/ScutaraSellerApp/.github/workflows/build-bundle.yml@refs/heads/main",
                "sha256:" + new string('d', 64), new BuildRecordGate1("passed", "gate1/1341231897-39787525465-1.json")));
        return r with { Identity = r.Identity with { Key = BuildRecordFormat.BundleKeyFor(r) } };
    }

    /// <summary>Every object the step can read, by bucket, key and version; and the keys that exist in test's own store.</summary>
    private sealed class Store : IVersionedObjects, IEvidenceProbe
    {
        public Dictionary<(string Bucket, string Key, string Version), string> Objects { get; } = new();
        public HashSet<string> OwnKeys { get; } = new();
        public List<string> Reads { get; } = new();
        public Task<StoredRecord?> ReadVersionAsync(string bucket, string key, string versionId)
        {
            Reads.Add($"{bucket}/{key}@{versionId}");
            return Task.FromResult(Objects.TryGetValue((bucket, key, versionId), out var json) ? new StoredRecord(json, versionId) : null);
        }
        public Task<bool> ExistsAsync(string bucket, string key) => Task.FromResult(bucket == Own && OwnKeys.Contains(key));
        public void Put(string bucket, string key, string version, string json) => Objects[(bucket, key, version)] = json;
    }

    /// <summary>This environment's own Verify of one member, faked: it answers with the record version it was told to.</summary>
    private sealed class MemberVerify
    {
        public List<JsonObject> Asked { get; } = new();
        public Func<JsonObject, JsonObject>? Answer { get; set; }
        public Task<JsonObject> RunAsync(JsonObject memberState)
        {
            Asked.Add(memberState);
            var key = memberState["record"]!["key"]!.GetValue<string>();
            return Task.FromResult(Answer?.Invoke(memberState) ?? new JsonObject
            {
                ["class"] = key.StartsWith("image/", StringComparison.Ordinal) ? "image" : "client",
                ["recordVersionId"] = key.StartsWith("image/", StringComparison.Ordinal) ? "rv-img" : "rv-sel",
            });
        }
    }

    private sealed record Fixture(Store Store, PassMarker Marker, string Key, string Version);

    private static Fixture Set(BuildRecord? image = null, BuildRecord? client = null, string testsOutcome = "Passed",
        string deployOutcome = "deployed", bool withClient = true, Action<Store, BuildRecord, BuildRecord>? tamper = null)
    {
        var store = new Store();
        var img = image ?? ImageRecord();
        var cli = client ?? ClientRecord();
        store.Put(Records, BuildRecordFormat.KeyFor(img), "rv-img", BuildRecordFormat.Serialize(img));
        store.Put(Records, BuildRecordFormat.KeyFor(cli), "rv-sel", BuildRecordFormat.Serialize(cli));

        PassMarkerMember Member(BuildRecord r, string version, string deployKey) => new(
            r.Class, r.BuiltFrom.Repo, new PassMarkerRecordReference(Records, BuildRecordFormat.KeyFor(r), version), r.Identity,
            r.Class == "image" ? r.Intake?.ServiceVersion ?? "none" : r.Identity.VersionId!, r.BuiltFrom, new PassMarkerReference(deployKey, "dv-" + version));

        var members = new List<PassMarkerMember> { Member(img, "rv-img", "deploys/image/scutara/scutaraservice/req-img.json") };
        if (withClient) members.Add(Member(cli, "rv-sel", "deploys/client/scutara/scutarasellerapp/req-sel.json"));
        foreach (var m in members)
            store.Put(Dev, m.Deploy.Key, m.Deploy.VersionId, new JsonObject { ["schema"] = 1, ["outcome"] = deployOutcome }.ToJsonString());
        store.Put(Dev, "tests/req-img.json", "tv-1", new JsonObject { ["schema"] = 1, ["outcome"] = testsOutcome }.ToJsonString());

        var marker = new PassMarker(1, PassMarkerFormat.MarkerIdFor(members), Predecessor, members,
            new PassMarkerEvidence(new PassMarkerReference("tests/req-img.json", "tv-1")), "req-img", "2026-10-26T00:10:00Z");
        var key = PassMarkerFormat.KeyFor(marker.MarkerId);
        store.Put(Dev, key, "mv-1", PassMarkerFormat.Write(marker));
        tamper?.Invoke(store, img, cli);
        return new Fixture(store, marker, key, "mv-1");
    }

    private static JsonObject Input(string? key, string? version) => new()
    {
        ["marker"] = key is null ? null : new JsonObject { ["key"] = key, ["versionId"] = version },
        ["target"] = new JsonObject { ["cluster"] = "scu-test-cluster", ["service"] = "scu-mp-aiphost", ["container"] = "aiphost", ["repository"] = "scu-4df6-b9c6-aiphost" },
    };

    private static Task<JsonObject> Run(Fixture f, JsonObject? input = null, MemberVerify? verify = null)
        => VerifySetStep.RunAsync(input ?? Input(f.Key, f.Version), Promotion, Settings, f.Store, f.Store, (verify ?? new MemberVerify()).RunAsync);

    private static async Task<string> Refused(Fixture f, string check, JsonObject? input = null, MemberVerify? verify = null)
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Run(f, input, verify));
        Assert.Contains(ex.Refusals, r => r.Check == check);
        return ex.Message;
    }

    // ---- what it admits ----

    [Fact]
    public async Task AValidMarker_VerifiesEveryMemberHere_TheImageFirst()
    {
        var f = Set();
        var verify = new MemberVerify();
        var result = await Run(f, verify: verify);

        Assert.Equal(f.Marker.MarkerId, result["markerId"]!.GetValue<string>());
        Assert.Equal((f.Key, "mv-1"), (result["marker"]!["key"]!.GetValue<string>(), result["marker"]!["versionId"]!.GetValue<string>()));
        Assert.Equal(new[] { "image", "client" }, verify.Asked.Select(a => a["record"]!["key"]!.GetValue<string>().Split('/')[0]));
        Assert.Equal("rv-img", result["image"]!["verified"]!["recordVersionId"]!.GetValue<string>());
        Assert.Single(result["bundles"]!.AsArray());
        // The image member keeps the execution's target; a client member's is its repository's web app here.
        Assert.Equal("scu-test-cluster", verify.Asked[0]["target"]!["cluster"]!.GetValue<string>());
        Assert.Equal("scu---webapp-sellerapp-d5e0-b27f", verify.Asked[1]["target"]!["bucket"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASetOfTheImageAlone_HasNoBundles()
    {
        var result = await Run(Set(withClient: false));
        Assert.Empty(result["bundles"]!.AsArray());
    }

    // ---- the marker ----

    [Fact]
    public async Task AnInputWithoutAMarker_IsRefused_SinceAPromotedEnvironmentDeploysOnlyByMarker()
        => Assert.Contains("marker", await Refused(Set(), "marker", Input(null, null)));

    [Fact]
    public async Task AMarkerKeyOutsidePassedSets_IsRefused_BeforeAnythingIsRead()
    {
        var f = Set();
        await Refused(f, "marker.key", Input("deploys/image/scutara/scutaraservice/req-img.json", "dv-rv-img"));
        Assert.Empty(f.Store.Reads);
    }

    [Fact]
    public async Task AMarkerVersionThePredecessorDoesNotServe_IsRefused()
        => await Refused(Set(), "marker", Input(Set().Key, "mv-2"));

    [Fact]
    public async Task AMarkerAtAKeyThatIsNotItsOwnId_IsRefused()
    {
        var f = Set();
        var elsewhere = PassMarkerFormat.KeyFor("sets/" + new string('e', 64));
        f.Store.Put(Dev, elsewhere, "mv-1", PassMarkerFormat.Write(f.Marker));
        await Refused(f, "marker.key", Input(elsewhere, "mv-1"));
    }

    [Fact]
    public async Task AMarkerAnotherEnvironmentWrote_IsRefused()
    {
        var f = Set();
        f.Store.Put(Dev, f.Key, "mv-1", PassMarkerFormat.Write(f.Marker with { Environment = "test" }));
        Assert.Contains("'test'", await Refused(f, "marker"));
    }

    [Fact]
    public async Task AMarkerAlreadyDecidedHere_IsRefused()
    {
        var f = Set();
        f.Store.OwnKeys.Add(PassMarkerFormat.DecisionKeyFor(f.Marker.MarkerId));
        await Refused(f, "decision");
    }

    // ---- the predecessor's evidence ----

    [Theory]
    [InlineData("TestsIncomplete")]
    [InlineData("PostDeployTestsFailed")]
    [InlineData("TestsSuperseded")]
    public async Task TestEvidenceThatDidNotPass_IsRefused(string outcome)
        => Assert.Contains(outcome, await Refused(Set(testsOutcome: outcome), "evidence.tests"));

    [Fact]
    public async Task TestEvidenceThePredecessorDoesNotServe_IsRefused()
        => await Refused(Set(tamper: (s, _, _) => s.Objects.Remove((Dev, "tests/req-img.json", "tv-1"))), "evidence.tests");

    [Fact]
    public async Task AMemberWhoseDeployDidNotLand_IsRefused()
        => await Refused(Set(deployOutcome: "failed"), "members[0].deploy");

    [Fact]
    public async Task AMemberWithoutDeployEvidenceThere_IsRefused()
        => await Refused(Set(tamper: (s, _, _) => s.Objects.Remove((Dev, "deploys/client/scutara/scutarasellerapp/req-sel.json", "dv-rv-sel"))), "members[1].deploy");

    // ---- each member's record ----

    [Fact]
    public async Task AMemberRecordAtTheMarkersVersionThatIsNotThere_IsRefused()
        => await Refused(Set(tamper: (s, img, _) => s.Objects.Remove((Records, BuildRecordFormat.KeyFor(img), "rv-img"))), "members[0].record");

    [Fact]
    public async Task AMemberRecordWhoseIdentityIsNotTheMarkers_IsRefused()
        => await Refused(Set(tamper: (s, img, _) => s.Put(Records, BuildRecordFormat.KeyFor(img), "rv-img",
            BuildRecordFormat.Serialize(ImageRecord(digest: "sha256:" + new string('9', 64))))), "members[0].record");

    [Fact]
    public async Task AMemberRecordWhoseBuiltFromIsNotTheMarkers_IsRefused()
        => await Refused(Set(tamper: (s, _, cli) => s.Put(Records, BuildRecordFormat.KeyFor(cli), "rv-sel",
            BuildRecordFormat.Serialize(cli with { BuiltFrom = cli.BuiltFrom with { Commit = new string('3', 40) } }))), "members[1].record");

    [Fact]
    public async Task AMemberRecordOutsideTheBuildRecordStore_IsRefused()
    {
        var f = Set();
        var moved = f.Marker.Members.Select((m, i) => i == 0 ? m with { Record = m.Record with { Bucket = Dev } } : m).ToList();
        var marker = f.Marker with { Members = moved, MarkerId = PassMarkerFormat.MarkerIdFor(moved) };
        var key = PassMarkerFormat.KeyFor(marker.MarkerId);
        f.Store.Put(Dev, key, "mv-1", PassMarkerFormat.Write(marker));
        // The record IS there, so only the bucket can refuse it - and it is never read from there.
        f.Store.Put(Dev, moved[0].Record.Key, "rv-img", BuildRecordFormat.Serialize(ImageRecord()));
        Assert.Contains("not the build-record store", await Refused(f, "members[0].record", Input(key, "mv-1")));
        Assert.DoesNotContain($"{Dev}/{moved[0].Record.Key}@rv-img", f.Store.Reads);
    }

    [Fact]
    public async Task ASetWithoutAnImageMember_IsRefused()
    {
        // The set is everything the predecessor runs beside the Service (T-13); clients alone would land onto whatever
        // Service this environment happens to run.
        var f = Set();
        var clients = f.Marker.Members.Where(m => m.Class == "client").ToList();
        var marker = f.Marker with { Members = clients, MarkerId = PassMarkerFormat.MarkerIdFor(clients) };
        var key = PassMarkerFormat.KeyFor(marker.MarkerId);
        f.Store.Put(Dev, key, "mv-1", PassMarkerFormat.Write(marker));
        Assert.Contains("0 image members", await Refused(f, "members", Input(key, "mv-1")));
    }

    [Fact]
    public async Task WhatThisEnvironmentsVerifyReadMustBeTheMarkersRecordVersion()
    {
        var verify = new MemberVerify { Answer = s => new JsonObject { ["class"] = "image", ["recordVersionId"] = "rv-other" } };
        await Refused(Set(), "members[0].record", verify: verify);
    }

    [Fact]
    public async Task AMemberThisEnvironmentsVerifyRefuses_IsRefused()
    {
        var verify = new MemberVerify { Answer = _ => throw new DeployRefused("scan", "a CRITICAL finding") };
        await Refused(Set(), "scan", verify: verify);
    }

    // ---- the AipApi check (T-13) ----

    [Fact]
    public async Task AClientWhoseAipApiIsNewerThanTheServiceItRunsBeside_IsRefused()
    {
        var message = await Refused(Set(client: ClientRecord(aipApi: "1.0.430")), "members[1].aipApi");
        Assert.Contains("1.0.430", message);
        Assert.Contains("1.0.421", message);
    }

    [Theory]
    [InlineData("1.0.421")]
    [InlineData("1.0.9")]
    public async Task AClientWhoseAipApiIsTheServicesOrOlder_IsAdmitted(string aipApi)
        => Assert.Single((await Run(Set(client: ClientRecord(aipApi: aipApi))))["bundles"]!.AsArray());

    [Fact]
    public async Task AnImageMemberWhoseRecordNamesNoServiceVersion_IsRefused()
        // The record parser requires it of an image record; refused there, by the member's record.
        => Assert.Contains("serviceVersion", await Refused(Set(image: ImageRecord(serviceVersion: null)), "members[0].record"));

    [Fact]
    public async Task AnImageMemberWhoseServiceVersionCannotBeCompared_IsRefused()
        => await Refused(Set(image: ImageRecord(serviceVersion: "1.0.421-rc1")), "members[0].serviceVersion");

    [Fact]
    public async Task AClientWithoutAnAipApiPackage_IsRefused()
    {
        var bare = ClientRecord();
        bare = bare with { BuiltFrom = bare.BuiltFrom with { Packages = new Dictionary<string, string>() } };
        await Refused(Set(client: bare), "members[1].aipApi");
    }
}
