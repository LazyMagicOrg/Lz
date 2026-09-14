using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The sweep for images no build record names (P2 stage D3): what it counts as an image, which records it reads, what it
/// alerts on, and that it alerts once.
/// </summary>
public class DeployerAlertsTests
{
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private const string Topic = "arn:aws:sns:us-west-2:503947800380:scu-dev-pipeline-alerts";
    private const string Repository = "scu-4df6-b9c6-aiphost";
    private const string Prefix = "image/scutara/scutaraservice/";

    private const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string DockerImage = "application/vnd.docker.container.image.v1+json";
    private const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    private const string NotarySignature = "application/vnd.cncf.notary.signature";

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly CorroborateSettings Settings =
        new(Store, Evidence, Topic, new[] { new CorroborateSource(Repository, Prefix) });

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private static RepositoryImage Image(char c, TimeSpan age, string artifact = DockerImage, string manifest = DockerManifest)
        => new(Digest(c), manifest, artifact, Now - age, new[] { "a108a57c725e0c85c96ed1a6c0cdbbf5dad14b4f" });

    private static string RecordNaming(string digest) => BuildRecordFormat.Serialize(new BuildRecord(
        BuildRecordFormat.CurrentSchema, "image",
        new BuildRecordBuiltFrom("Scutara/ScutaraService", "a108a57c725e0c85c96ed1a6c0cdbbf5dad14b4f", "published",
            new Dictionary<string, string>(), "refs/heads/main"),
        new BuildRecordIdentity("image", Digest: digest),
        "2026-09-14T11:00:00Z", "github-actions", "34790041339"));

    // ---------------------------------------------------------------------------------------
    //  Fakes
    // ---------------------------------------------------------------------------------------

    private sealed class Images(params RepositoryImage[] images) : IRepositoryImages
    {
        public List<string> Listed { get; } = new();
        public Task<IReadOnlyList<RepositoryImage>> ListAsync(string repository)
        {
            Listed.Add(repository);
            return Task.FromResult<IReadOnlyList<RepositoryImage>>(images);
        }
    }

    private sealed class Records : IRecordKeys, IRecordStore
    {
        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);
        public List<(string Bucket, string Prefix, string StartAfter)> Listings { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, string startAfter)
        {
            Listings.Add((bucket, prefix, startAfter));
            return Task.FromResult<IReadOnlyList<string>>(Bodies.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && string.CompareOrdinal(k, startAfter) > 0)
                .OrderBy(k => k, StringComparer.Ordinal).ToList());
        }

        public Task<StoredRecord?> ReadAsync(string bucket, string key)
            => Task.FromResult(Bodies.TryGetValue(key, out var body) ? new StoredRecord(body, "v1") : null);
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

    private static Task<JsonObject> Sweep(Images images, Records records, Evidences evidence, Alerts alerts)
        => CorroborateStep.RunAsync(Settings, images, records, records, evidence, evidence, alerts, Now);

    private static string[] Digests(JsonObject result, string field)
        => result[field]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    // ---------------------------------------------------------------------------------------
    //  What counts as an image
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnImageIsSelectedByWhatItIs_AndASignatureIsNotOne()
    {
        // As dev's registry lists them (2026-09-13).
        Assert.True(DeployerAlerts.IsRunnableImage(DockerManifest, DockerImage));
        Assert.False(DeployerAlerts.IsRunnableImage(OciManifest, NotarySignature));

        Assert.True(DeployerAlerts.IsRunnableImage(OciManifest, "application/vnd.oci.image.config.v1+json"));
        Assert.True(DeployerAlerts.IsRunnableImage("application/vnd.oci.image.index.v1+json", null));
        Assert.True(DeployerAlerts.IsRunnableImage("application/vnd.docker.distribution.manifest.list.v2+json", ""));

        // An artifact of any other kind is not deployable, so it needs no record.
        Assert.False(DeployerAlerts.IsRunnableImage(OciManifest, "application/spdx+json"));
        Assert.False(DeployerAlerts.IsRunnableImage(OciManifest, null));
    }

    // ---------------------------------------------------------------------------------------
    //  The sweep
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnImageARecordNames_IsCorroborated_AndNothingIsSentOrWritten()
    {
        var records = new Records();
        records.Bodies[$"{Prefix}20260914T110000Z-34790041339.json"] = RecordNaming(Digest('a'));
        var evidence = new Evidences();
        var alerts = new Alerts();

        var result = await Sweep(new Images(Image('a', TimeSpan.FromHours(1))), records, evidence, alerts);

        Assert.Equal(new[] { Digest('a') }, Digests(result, "corroborated"));
        Assert.Empty(Digests(result, "anomalies"));
        Assert.Empty(alerts.Sent);
        Assert.Empty(evidence.Objects);
    }

    [Fact]
    public async Task AnImageNoRecordNames_IsAlertedOnce_ThenRecorded()
    {
        var records = new Records();
        records.Bodies[$"{Prefix}20260914T110000Z-34790041339.json"] = RecordNaming(Digest('a'));
        var evidence = new Evidences();
        var alerts = new Alerts();

        var result = await Sweep(new Images(Image('a', TimeSpan.FromHours(1)), Image('b', TimeSpan.FromHours(2))), records, evidence, alerts);

        Assert.Equal(new[] { Digest('b') }, Digests(result, "anomalies"));

        var (topic, subject, message) = Assert.Single(alerts.Sent);
        Assert.Equal(Topic, topic);
        Assert.Equal($"lz pipeline: an image in {Repository} has no build record", subject);
        Assert.True(subject.Length <= 100, "SNS refuses a subject over 100 characters");
        Assert.Contains(Digest('b'), message);
        Assert.Contains($"s3://{Store}/{Prefix}", message);
        Assert.Contains($"s3://{Evidence}/anomalies/{Repository}/sha256-{new string('b', 64)}.json", message);

        var recorded = JsonNode.Parse(Assert.Single(evidence.Objects,
            o => o.Key == $"{Evidence}/anomalies/{Repository}/sha256-{new string('b', 64)}.json").Value)!;
        Assert.Equal("anomaly", recorded["outcome"]!.GetValue<string>());
        Assert.Equal(Digest('b'), recorded["digest"]!.GetValue<string>());
        Assert.Equal(Repository, recorded["repository"]!.GetValue<string>());

        // The next run finds it recorded, and says nothing.
        var again = new Alerts();
        var second = await Sweep(new Images(Image('a', TimeSpan.FromHours(1)), Image('b', TimeSpan.FromHours(2))), records, evidence, again);
        Assert.Empty(again.Sent);
        Assert.Equal(new[] { Digest('b') }, Digests(second, "alreadyRecorded"));
        Assert.Empty(Digests(second, "anomalies"));
    }

    [Fact]
    public async Task AnAlertThatCannotBeSent_IsNotRecorded_SoTheNextRunSendsIt()
    {
        var evidence = new Evidences();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep(new Images(Image('b', TimeSpan.FromHours(2))), new Records(), evidence, new Alerts(new InvalidOperationException("throttled"))));

        Assert.Empty(evidence.Objects);
    }

    [Fact]
    public async Task ASignature_IsNeverJudged_ThoughNoRecordNamesIt()
    {
        var alerts = new Alerts();
        var result = await Sweep(
            new Images(Image('s', TimeSpan.FromHours(2), NotarySignature, OciManifest)), new Records(), new Evidences(), alerts);

        Assert.Empty(alerts.Sent);
        Assert.Empty(Digests(result, "anomalies"));
        Assert.Empty(Digests(result, "corroborated"));
    }

    [Fact]
    public async Task AnImageYoungerThanTheGrace_IsNotJudged_AndOneOlderThanTheWindowIsNotSeen()
    {
        var records = new Records();
        var alerts = new Alerts();

        var result = await Sweep(
            new Images(
                Image('n', DeployerAlerts.Grace - TimeSpan.FromMinutes(1)),
                Image('o', DeployerAlerts.Window + TimeSpan.FromMinutes(1))),
            records, new Evidences(), alerts);

        Assert.Equal(new[] { Digest('n') }, Digests(result, "tooNew"));
        Assert.Empty(alerts.Sent);
        // Nothing was due, so no record was listed.
        Assert.Empty(records.Listings);
    }

    [Fact]
    public async Task OnlyTheRecordsThatCouldNameTheImages_AreListed()
    {
        var records = new Records();
        var oldest = TimeSpan.FromHours(3);

        await Sweep(new Images(Image('a', TimeSpan.FromHours(1)), Image('b', oldest)), records, new Evidences(), new Alerts());

        var (bucket, prefix, startAfter) = Assert.Single(records.Listings);
        Assert.Equal(Store, bucket);
        Assert.Equal(Prefix, prefix);
        Assert.Equal(Prefix + DeployerAlerts.StampAt(Now - oldest - DeployerAlerts.RecordLead), startAfter);
        Assert.Equal("image/scutara/scutaraservice/20260914T030000Z", startAfter);
    }

    [Fact]
    public async Task ARecordUnderAnotherRepositorysPrefix_DoesNotCorroborate()
    {
        // Provenance, as Verify judges it: SellerApp's role writes under its own prefix, and a record there naming the
        // service's image says nothing about how that image was built.
        var records = new Records();
        records.Bodies["image/scutara/scutarasellerapp/20260914T110000Z-1.json"] = RecordNaming(Digest('b'));
        var alerts = new Alerts();

        var result = await Sweep(new Images(Image('b', TimeSpan.FromHours(2))), records, new Evidences(), alerts);

        Assert.Equal(new[] { Digest('b') }, Digests(result, "anomalies"));
        Assert.Single(alerts.Sent);
    }

    [Fact]
    public async Task AnUnreadableRecord_IsCountedAndSaid_NotThrown()
    {
        var records = new Records();
        records.Bodies[$"{Prefix}20260914T110000Z-1.json"] = """{ "schema": 2 }""";
        var alerts = new Alerts();

        var result = await Sweep(new Images(Image('b', TimeSpan.FromHours(2))), records, new Evidences(), alerts);

        Assert.Equal(new[] { Digest('b') }, Digests(result, "anomalies"));
        Assert.Contains("1 record(s) in that window could not be read or parsed", Assert.Single(alerts.Sent).Message);
    }

    // ---------------------------------------------------------------------------------------
    //  Configuration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheSources_RoundTrip_AndABadOneIsRefused()
    {
        var sources = new[]
        {
            new CorroborateSource(Repository, Prefix),
            new CorroborateSource("scu-4df6-b9c6-worker", "image/scutara/scutaraworkers/"),
        };

        Assert.Equal(sources, DeployerAlerts.DecodeSources(DeployerAlerts.EncodeSources(sources)));

        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeSources(""));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeSources("scu-4df6-b9c6-aiphost"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeSources("scu-4df6-b9c6-aiphost=client/scutara/scutarasellerapp/"));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.DecodeSources("scu-4df6-b9c6-aiphost=image/scutara/scutaraservice"));
    }

    [Fact]
    public void TheSettings_RefuseAMissingVariable()
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.EvidenceStore] = Evidence,
            [DeployerEnvironment.AlertsTopic] = Topic,
            [DeployerEnvironment.CorroborateSources] = DeployerAlerts.EncodeSources(Settings.Sources),
        };

        var read = CorroborateSettings.Read(n => env.GetValueOrDefault(n));
        Assert.Equal(Settings.Sources, read.Sources);
        Assert.Equal(Topic, read.AlertsTopicArn);

        foreach (var name in env.Keys.ToList())
        {
            var without = new Dictionary<string, string>(env);
            without.Remove(name);
            Assert.Throws<InvalidOperationException>(() => CorroborateSettings.Read(n => without.GetValueOrDefault(n)));
        }
    }

    [Fact]
    public void AStamp_IsARecordKeysStamp()
    {
        // KeyFor removes the dashes and colons from builtAt; a stamp built from a time must sort against those keys.
        var record = BuildRecordFormat.Parse(RecordNaming(Digest('a')));
        Assert.Equal(
            BuildRecordFormat.KeyFor(record),
            $"{Prefix}{DeployerAlerts.StampAt(new DateTimeOffset(2026, 9, 14, 11, 0, 0, TimeSpan.Zero))}-34790041339.json");
    }
}
