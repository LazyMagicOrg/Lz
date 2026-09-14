using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The sweep's second half (DecoupledCd.md P4 stage D): a client bundle in the artifact store that no build record names. A
/// bundle's record is found by its key — the zip's own stem with <c>.json</c> (<see cref="BuildRecordFormat.BundleKeyFor"/>) —
/// and it names the bundle only when it names that key AND that version, since the version is the identity.
///
/// <para>THE RECORD IS SELLERAPP'S FIRST, <see cref="BuildRecordFormatTests.BundleWorkflowEmitted"/>, and the version its zip was
/// given, so a corroborated bundle here is the one dev deployed from.</para>
/// </summary>
public class DeployerBundleSweepTests
{
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private const string Topic = "arn:aws:sns:us-west-2:503947800380:scu-dev-pipeline-alerts";
    private const string SellerPrefix = "client/scutara/scutarasellerapp/";
    private const string AdminPrefix = "client/scutara/scutaraadminapp/";
    private const string BundleKey = "client/scutara/scutarasellerapp/20260914T173258Z-34875215076.zip";
    private const string RecordKey = "client/scutara/scutarasellerapp/20260914T173258Z-34875215076.json";
    private const string Version = "7WtRS9NSXBeDbRQmd.gaFceR3JvLMali";

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 19, 0, 0, TimeSpan.Zero);

    private static CorroborateSettings Settings(params string[] prefixes) => new(
        RecordStore, Evidence, Topic, new[] { new CorroborateSource("scu-4df6-b9c6-aiphost", "image/scutara/scutaraservice/") },
        ArtifactStore, prefixes.Length == 0 ? new[] { SellerPrefix } : prefixes);

    private static string Name(string key, string version) => $"{key}?versionId={version}";

    // ---------------------------------------------------------------------------------------
    //  Fakes
    // ---------------------------------------------------------------------------------------

    private sealed class Versions(params StoredVersion[] versions) : IArtifactVersions
    {
        public List<(string Bucket, string Prefix)> Listed { get; } = new();

        public Task<IReadOnlyList<StoredVersion>> ListAsync(string bucket, string prefix)
        {
            Listed.Add((bucket, prefix));
            return Task.FromResult<IReadOnlyList<StoredVersion>>(
                versions.Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList());
        }
    }

    /// <summary>No images: the bundle half is what these tests are about.</summary>
    private sealed class NoImages : IRepositoryImages
    {
        public Task<IReadOnlyList<RepositoryImage>> ListAsync(string repository) => Task.FromResult<IReadOnlyList<RepositoryImage>>(Array.Empty<RepositoryImage>());
    }

    private sealed class Records : IRecordKeys, IRecordStore
    {
        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);
        public List<(string Bucket, string Key)> Reads { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, string startAfter)
            => throw new InvalidOperationException("a bundle's record is found by its key, never by listing");

        public Task<StoredRecord?> ReadAsync(string bucket, string key)
        {
            Reads.Add((bucket, key));
            return Task.FromResult(bucket == RecordStore && Bodies.TryGetValue(key, out var body) ? new StoredRecord(body, "v1") : null);
        }
    }

    private sealed class Evidences : IEvidenceProbe, IEvidenceWriter
    {
        public Dictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);
        public Task<bool> ExistsAsync(string bucket, string key) => Task.FromResult(Objects.ContainsKey($"{bucket}/{key}"));
        public Task<bool> PutOnceAsync(string bucket, string key, string body) => Task.FromResult(Objects.TryAdd($"{bucket}/{key}", body));
    }

    private sealed class Alerts(Exception? fail = null) : IAlertPublisher
    {
        public List<(string Topic, string Subject, string Message)> Sent { get; } = new();
        public Task PublishAsync(string topicArn, string subject, string message)
        {
            if (fail != null) throw fail;
            Sent.Add((topicArn, subject, message));
            return Task.CompletedTask;
        }
    }

    private static Task<JsonObject> Sweep(Versions versions, Records records, Evidences evidence, Alerts alerts, CorroborateSettings? settings = null)
        => CorroborateStep.RunAsync(settings ?? Settings(), new NoImages(), versions, records, records, evidence, evidence, alerts, Now);

    private static string[] Field(JsonObject result, string field) => result[field]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static StoredVersion Zip(TimeSpan age, string key = BundleKey, string version = Version) => new(key, version, Now - age);

    private static Records WithTheRecord(string? body = null)
    {
        var records = new Records();
        records.Bodies[RecordKey] = body ?? BuildRecordFormatTests.BundleWorkflowEmitted;
        return records;
    }

    // ---------------------------------------------------------------------------------------
    //  The sweep
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABundleItsRecordNames_IsCorroborated_AndNothingIsSentOrWritten()
    {
        var versions = new Versions(Zip(TimeSpan.FromHours(1)));
        var records = WithTheRecord();
        var evidence = new Evidences();
        var alerts = new Alerts();

        var result = await Sweep(versions, records, evidence, alerts);

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "corroborated"));
        Assert.Empty(Field(result, "anomalies"));
        Assert.Empty(alerts.Sent);
        Assert.Empty(evidence.Objects);
        Assert.Equal((ArtifactStore, SellerPrefix), Assert.Single(versions.Listed));
        Assert.Equal((RecordStore, RecordKey), Assert.Single(records.Reads));
    }

    [Fact]
    public async Task ABundleWithNoRecord_IsAlertedOnce_ThenRecorded()
    {
        var evidence = new Evidences();
        var alerts = new Alerts();

        var result = await Sweep(new Versions(Zip(TimeSpan.FromHours(2))), new Records(), evidence, alerts);

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "anomalies"));

        var (topic, subject, message) = Assert.Single(alerts.Sent);
        Assert.Equal(Topic, topic);
        Assert.Equal($"lz pipeline: a bundle in {ArtifactStore} has no build record", subject);
        Assert.True(subject.Length <= 100, "SNS refuses a subject over 100 characters");
        Assert.Contains($"s3://{ArtifactStore}/{BundleKey}", message);
        Assert.Contains(Version, message);
        Assert.Contains($"there is no build record at s3://{RecordStore}/{RecordKey}", message);

        var anomalyKey = $"anomalies/{BundleKey}/{Version}.json";
        Assert.Contains($"s3://{Evidence}/{anomalyKey}", message);
        var recorded = JsonNode.Parse(Assert.Single(evidence.Objects, o => o.Key == $"{Evidence}/{anomalyKey}").Value)!;
        Assert.Equal("anomaly", recorded["outcome"]!.GetValue<string>());
        Assert.Equal("bundle-without-record", recorded["kind"]!.GetValue<string>());
        Assert.Equal(ArtifactStore, recorded["store"]!.GetValue<string>());
        Assert.Equal(BundleKey, recorded["key"]!.GetValue<string>());
        Assert.Equal(Version, recorded["versionId"]!.GetValue<string>());
        Assert.Equal(RecordKey, recorded["recordSearched"]!["key"]!.GetValue<string>());

        // The next run finds it recorded, and says nothing.
        var again = new Alerts();
        var second = await Sweep(new Versions(Zip(TimeSpan.FromHours(2))), new Records(), evidence, again);
        Assert.Empty(again.Sent);
        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(second, "alreadyRecorded"));
        Assert.Empty(Field(second, "anomalies"));
    }

    [Fact]
    public async Task AVersionTheRecordDoesNotName_IsAnAnomaly_ThoughItSharesTheKey()
    {
        // Write-once refuses a second write to a key while the first stands, so a second version means something deleted
        // the first and wrote again. The record names the version that was built; the other is no build's.
        const string rewritten = "Kq3mX0vZ9pL2sT8wYb1nR4cF6hJ5dG7e";
        var alerts = new Alerts();

        var result = await Sweep(
            new Versions(Zip(TimeSpan.FromHours(3)), Zip(TimeSpan.FromHours(1), version: rewritten)), WithTheRecord(), new Evidences(), alerts);

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "corroborated"));
        Assert.Equal(new[] { Name(BundleKey, rewritten) }, Field(result, "anomalies"));
        var message = Assert.Single(alerts.Sent).Message;
        Assert.Contains(rewritten, message);
        Assert.Contains($"names s3://{ArtifactStore}/{BundleKey} version {Version}", message);
    }

    [Theory]
    [InlineData("{ \"schema\": 2 }", "cannot be read")]
    [InlineData("not json", "cannot be read")]
    public async Task AnUnreadableRecord_IsAnAnomaly_ThatSaysSo(string body, string said)
    {
        var alerts = new Alerts();

        var result = await Sweep(new Versions(Zip(TimeSpan.FromHours(2))), WithTheRecord(body), new Evidences(), alerts);

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "anomalies"));
        Assert.Contains(said, Assert.Single(alerts.Sent).Message);
    }

    [Fact]
    public async Task ARecordNamingAnotherStore_DoesNotCorroborate()
    {
        var elsewhere = BuildRecordFormatTests.BundleWorkflowEmitted.Replace(
            "\"bucket\": \"scu-artifacts-4df6-b9c6\"", "\"bucket\": \"someone-elses-artifacts\"");
        Assert.NotEqual(BuildRecordFormatTests.BundleWorkflowEmitted, elsewhere);

        var result = await Sweep(new Versions(Zip(TimeSpan.FromHours(2))), WithTheRecord(elsewhere), new Evidences(), new Alerts());

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "anomalies"));
    }

    [Fact]
    public async Task AnImageRecordAtTheBundlesRecordKey_DoesNotCorroborate()
    {
        var image = BuildRecordFormat.Serialize(new BuildRecord(
            BuildRecordFormat.CurrentSchema, "image",
            new BuildRecordBuiltFrom("Scutara/ScutaraSellerApp", "80646ba000000000000000000000000000000000", "published",
                new Dictionary<string, string>(), "refs/heads/main"),
            new BuildRecordIdentity("image", Digest: "sha256:" + new string('a', 64)),
            "2026-09-14T17:32:58Z", "tmay57", "34875215076"));

        var result = await Sweep(new Versions(Zip(TimeSpan.FromHours(2))), WithTheRecord(image), new Evidences(), new Alerts());

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "anomalies"));
    }

    [Fact]
    public async Task AnObjectThatIsNoBundlesName_IsAnAnomaly_AndNoRecordIsRead()
    {
        const string stray = "client/scutara/scutarasellerapp/notes.txt";
        var records = WithTheRecord();
        var alerts = new Alerts();

        var result = await Sweep(new Versions(Zip(TimeSpan.FromHours(2), key: stray)), records, new Evidences(), alerts);

        Assert.Equal(new[] { Name(stray, Version) }, Field(result, "anomalies"));
        Assert.Contains(".zip", Assert.Single(alerts.Sent).Message);
        Assert.Empty(records.Reads);
    }

    [Fact]
    public async Task ABundleYoungerThanTheGrace_IsNotJudged_AndOneOlderThanTheWindowIsNotSeen()
    {
        var records = new Records();
        var alerts = new Alerts();

        var result = await Sweep(
            new Versions(
                Zip(DeployerAlerts.Grace - TimeSpan.FromMinutes(1)),
                Zip(DeployerAlerts.Window + TimeSpan.FromMinutes(1), key: "client/scutara/scutarasellerapp/20260901T000000Z-1.zip")),
            records, new Evidences(), alerts);

        Assert.Equal(new[] { Name(BundleKey, Version) }, Field(result, "tooNew"));
        Assert.Empty(Field(result, "anomalies"));
        Assert.Empty(alerts.Sent);
        Assert.Empty(records.Reads);
    }

    [Fact]
    public async Task EachBundleSource_IsListedUnderItsOwnPrefix_InTheArtifactStore()
    {
        var versions = new Versions();

        await Sweep(versions, new Records(), new Evidences(), new Alerts(), Settings(SellerPrefix, AdminPrefix));

        Assert.Equal(new[] { (ArtifactStore, SellerPrefix), (ArtifactStore, AdminPrefix) }, versions.Listed);
    }

    [Fact]
    public async Task AnAlertThatCannotBeSent_IsNotRecorded_SoTheNextRunSendsIt()
    {
        var evidence = new Evidences();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep(new Versions(Zip(TimeSpan.FromHours(2))), new Records(), evidence, new Alerts(new InvalidOperationException("throttled"))));

        Assert.Empty(evidence.Objects);
    }

    // ---------------------------------------------------------------------------------------
    //  Configuration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBundleSources_RoundTrip_AndABadOneIsRefused()
    {
        var prefixes = new[] { SellerPrefix, AdminPrefix };

        Assert.Equal(prefixes, DeployerAlerts.DecodeBundleSources(DeployerAlerts.EncodeBundleSources(prefixes)));

        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeBundleSources(""));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeBundleSources("image/scutara/scutaraservice/"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeBundleSources("client/scutara/scutarasellerapp"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeBundleSources("client/scutara/"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeBundleSources($"scu-4df6-b9c6-aiphost={SellerPrefix}"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.EncodeBundleSources(new[] { "client/scutara/" }));
    }

    private static Dictionary<string, string> Variables(bool bundleSources, bool artifactStore)
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.BuildRecordStore] = RecordStore,
            [DeployerEnvironment.EvidenceStore] = Evidence,
            [DeployerEnvironment.AlertsTopic] = Topic,
            [DeployerEnvironment.CorroborateSources] = "scu-4df6-b9c6-aiphost=image/scutara/scutaraservice/",
        };
        if (bundleSources) env[DeployerEnvironment.CorroborateBundleSources] = DeployerAlerts.EncodeBundleSources(new[] { SellerPrefix, AdminPrefix });
        if (artifactStore) env[DeployerEnvironment.ArtifactStore] = ArtifactStore;
        return env;
    }

    [Fact]
    public void TheSettings_ReadBundleSources_WithTheArtifactStore()
    {
        var env = Variables(bundleSources: true, artifactStore: true);

        var settings = CorroborateSettings.Read(n => env.GetValueOrDefault(n));

        Assert.Equal(new[] { SellerPrefix, AdminPrefix }, settings.BundlePrefixes);
        Assert.Equal(ArtifactStore, settings.ArtifactStore);
    }

    [Fact]
    public void TheSettings_RefuseBundleSourcesWithoutTheArtifactStore()
    {
        var env = Variables(bundleSources: true, artifactStore: false);

        var ex = Assert.Throws<InvalidOperationException>(() => CorroborateSettings.Read(n => env.GetValueOrDefault(n)));

        Assert.Contains(DeployerEnvironment.ArtifactStore, ex.Message);
    }

    [Fact]
    public void TheSettings_WithoutBundleSources_AreTheImageSweepsSettings()
    {
        var env = Variables(bundleSources: false, artifactStore: false);

        var settings = CorroborateSettings.Read(n => env.GetValueOrDefault(n));

        Assert.Empty(settings.BundlePrefixes ?? Array.Empty<string>());
        Assert.Null(settings.ArtifactStore);
    }

    [Fact]
    public void TheAnomalyKey_IsTheBundlesKeyAndVersion_WithAVersionThatCouldLeaveItsFolderEscaped()
    {
        Assert.Equal($"anomalies/{BundleKey}/{Version}.json", DeployerAlerts.BundleAnomalyKey(BundleKey, Version));
        Assert.Equal($"anomalies/{BundleKey}/a%2F..%2Fb.json", DeployerAlerts.BundleAnomalyKey(BundleKey, "a/../b"));
    }
}
