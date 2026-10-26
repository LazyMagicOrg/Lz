using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Test's Verify admits a site by its marker (DecoupledCd.md V-K step 5, S5). The marker names the site dev deployed: dev's
/// record of the run, at a version, and dev's deploy evidence, which landed. Test deploys ITS OWN bundle of the same run -
/// the record beside dev's, under <c>site/{repo}/test/</c> with the same stem - which must be built from what dev's was, and
/// goes through test's own site Verify. Gate 1 held the two bundles to the same bytes apart from their hosts. No revert.
/// </summary>
public class VerifySiteTests
{
    private const string Dev = "scu-dev-deploy-evidence-4df6-b9c6", Own = "scu-test-deploy-evidence-d5e0-b27f";
    private const string Records = "scu-build-records-4df6-b9c6", Artifacts = "scu-artifacts-4df6-b9c6";
    private const string Commit = "d33b02b4c90f6eab15192bf4a7d9c9ff398991af";
    private const string Execution = "ps-0123456789abcdef0123-1";

    private static readonly PromotionSettings Promotion = new("dev", Dev, Own);
    private static readonly SiteTarget TestSite = new("Scutara/ScutaraWebsite", "test", "scu-mp--webapp-website-d5e0-b27f", "scutara-test.click");
    private static readonly VerifySettings Settings = new(
        new[] { "image", "client", "site" }, new[] { "CRITICAL" }, Records, new[] { "scu-4df6-b9c6-aiphost" }, Artifacts,
        SiteTargets: new[] { TestSite });

    private static BuildRecord SiteRecord(string environment, string commit = Commit)
    {
        var record = new BuildRecord(
            BuildRecordFormat.CurrentSchema, "site",
            new BuildRecordBuiltFrom("Scutara/ScutaraWebsite", commit, "published", new Dictionary<string, string>(), "refs/heads/main"),
            new BuildRecordIdentity("bundle"), "2026-10-26T19:30:11Z", "tmay57", "40387310346",
            new BuildRecordIntake("40387310346", 1, "1334483704", "Scutara/ScutaraWebsite/.github/workflows/build-site.yml@refs/heads/main",
                "sha256:" + new string('a', 64), new BuildRecordGate1("passed", "gate1/1334483704-40387310346-1.json"), Environment: environment));
        return record with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: Artifacts, Key: BuildRecordFormat.BundleKeyFor(record),
                VersionId: "bv-" + environment, Sha256: new string(environment == "dev" ? 'd' : 'e', 64)),
        };
    }

    private sealed class Store : IVersionedObjects, IEvidenceProbe
    {
        public Dictionary<(string Bucket, string Key, string Version), string> Objects { get; } = new();
        public HashSet<string> OwnKeys { get; } = new();
        public Task<StoredRecord?> ReadVersionAsync(string bucket, string key, string versionId)
            => Task.FromResult(Objects.TryGetValue((bucket, key, versionId), out var json) ? new StoredRecord(json, versionId) : null);
        public Task<bool> ExistsAsync(string bucket, string key) => Task.FromResult(bucket == Own && OwnKeys.Contains(key));
        public void Put(string bucket, string key, string version, string json) => Objects[(bucket, key, version)] = json;
    }

    /// <summary>Test's own site Verify, faked: it answers for the record it was given, built from <c>commit</c>.</summary>
    private sealed class SiteVerify(string commit = Commit)
    {
        public List<JsonObject> Asked { get; } = new();
        public Task<JsonObject> RunAsync(JsonObject memberState)
        {
            Asked.Add(memberState);
            return Task.FromResult(new JsonObject
            {
                ["class"] = "site", ["recordVersionId"] = "rv-test",
                ["builtFrom"] = new JsonObject { ["repo"] = "Scutara/ScutaraWebsite", ["commit"] = commit, ["lane"] = "published", ["packages"] = new JsonObject(), ["ref"] = "refs/heads/main" },
            });
        }
    }

    private sealed record Fixture(Store Store, PassMarker Marker, string Key);

    private static Fixture Site(string deployOutcome = "deployed", BuildRecord? markerRecord = null)
    {
        var store = new Store();
        var dev = SiteRecord("dev");
        store.Put(Records, BuildRecordFormat.KeyFor(dev), "rv-dev", BuildRecordFormat.Serialize(dev));
        var named = markerRecord ?? dev;
        var member = new PassMarkerMember("site", "Scutara/ScutaraWebsite", new PassMarkerRecordReference(Records, BuildRecordFormat.KeyFor(dev), "rv-dev"),
            named.Identity, named.Identity.VersionId!, named.BuiltFrom,
            new PassMarkerReference("deploys/site/scutara/scutarawebsite/req-20261026T193011Z-40387310346-1-site-1.json", "dv-1"));
        store.Put(Dev, member.Deploy.Key, "dv-1", new JsonObject { ["schema"] = 1, ["outcome"] = deployOutcome }.ToJsonString());
        var marker = new PassMarker(1, PassMarkerFormat.MarkerIdFor(new[] { member }), "dev", new[] { member }, new PassMarkerEvidence(null),
            "req-20261026T193011Z-40387310346-1-site-1", "2026-10-26T19:31:28Z");
        var key = PassMarkerFormat.KeyFor(marker.MarkerId);
        store.Put(Dev, key, "mv-1", PassMarkerFormat.Write(marker));
        return new Fixture(store, marker, key);
    }

    private static JsonObject Input(string key, string field = "marker") => new() { [field] = new JsonObject { ["key"] = key, ["versionId"] = "mv-1" } };

    private static Task<JsonObject> Run(Fixture f, JsonObject? input = null, SiteVerify? verify = null, VerifySettings? settings = null, string? decidedHere = null)
        => VerifySetStep.RunAsync(input ?? Input(f.Key), Execution, Promotion, settings ?? Settings, f.Store, f.Store, new RevertReads(decidedHere), (verify ?? new SiteVerify()).RunAsync);

    private sealed class RevertReads(string? decided) : IRevertReads
    {
        public Task<string?> DecisionOutcomeAsync(string markerId) => Task.FromResult(decided);
        public Task<IReadOnlyList<string>> OtherRunningAsync(string ownExecution) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> NewestDeployedAsync() => Task.FromResult<string?>(null);
    }

    private static async Task<string> Refused(Fixture f, string check, JsonObject? input = null, SiteVerify? verify = null, VerifySettings? settings = null, string? decidedHere = null)
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Run(f, input, verify, settings, decidedHere));
        Assert.Contains(ex.Refusals, r => r.Check == check);
        return ex.Message;
    }

    [Fact]
    public async Task ASiteDevPassed_IsAdmitted_AsThisEnvironmentsBundleOfTheSameRun()
    {
        var f = Site();
        var verify = new SiteVerify();
        var set = await Run(f, verify: verify);

        var asked = Assert.Single(verify.Asked);
        Assert.Equal("site/scutara/scutarawebsite/test/20261026T193011Z-40387310346-1.json", asked["record"]!["key"]!.GetValue<string>());
        Assert.Equal(Records, asked["record"]!["bucket"]!.GetValue<string>());
        Assert.Equal(TestSite.Bucket, asked["target"]!["bucket"]!.GetValue<string>());

        Assert.Equal(f.Marker.MarkerId, set["markerId"]!.GetValue<string>());
        Assert.False(set["revert"]!.GetValue<bool>());
        Assert.Null(set["image"]);
        Assert.Equal("site", set["site"]!["verified"]!["class"]!.GetValue<string>());
        Assert.Equal(asked["record"]!.ToJsonString(), set["site"]!["record"]!.ToJsonString());
    }

    [Fact]
    public async Task ASiteDecidedHere_IsNotAdmittedAgain()
    {
        var f = Site();
        f.Store.OwnKeys.Add(PassMarkerFormat.DecisionKeyFor(f.Marker.MarkerId));
        await Refused(f, "decision");
    }

    [Fact]
    public async Task ASiteThatDidNotLandInDev_IsRefused()
        => await Refused(Site(deployOutcome: "failed"), "members[0].deploy");

    [Fact]
    public async Task DevsRecord_MustSayWhatTheMarkerSays()
        => await Refused(Site(markerRecord: SiteRecord("dev", commit: new string('9', 40))), "members[0].record");

    [Fact]
    public async Task ThisEnvironmentsBundle_BuiltFromAnythingElse_IsRefused()
        => Assert.Contains(Commit, await Refused(Site(), "site.builtFrom", verify: new SiteVerify(commit: new string('7', 40))));

    [Fact]
    public async Task AnEnvironmentThatDeploysNoSite_RefusesASiteMarker()
        => await Refused(Site(), "site.target", settings: Settings with { SiteTargets = Array.Empty<SiteTarget>() });

    [Fact]
    public async Task ASiteIsNeverReverted()
    {
        // Even one deployed here, which a set's revert would go back to.
        var f = Site();
        Assert.Contains("never reverted", await Refused(f, "revertTo", Input(f.Key, "revertTo"), decidedHere: "deployed"));
    }

    [Fact]
    public async Task AKeyThatIsNeitherASetNorASite_IsStillRefusedBeforeAnyRead()
        => await Refused(Site(), "marker.key", Input("passed/other/abc.json"));
}
