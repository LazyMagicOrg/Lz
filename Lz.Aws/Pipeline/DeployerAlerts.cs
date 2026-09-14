using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  THE SWEEP (P2 stage D3): an image in this environment's pipeline repository that no build record names.
//  Linked into Lz.Aws.Deployer, like the steps it sits beside: plain C#, no config, no SDK.
// ---------------------------------------------------------------------------------------------

/// <summary>One repository the sweep checks, and the build-record prefix its images' records are written under.</summary>
public sealed record CorroborateSource(string Repository, string RecordPrefix);

/// <summary>An image or artifact as the registry lists it.</summary>
/// <param name="ArtifactMediaType">What the manifest describes: an image config for a runnable image, a Notary signature for
/// the artifact managed signing adds beside it.</param>
public sealed record RepositoryImage(
    string Digest, string? ManifestMediaType, string? ArtifactMediaType, DateTimeOffset PushedAt, IReadOnlyList<string> Tags);

public interface IRepositoryImages
{
    /// <summary>Every image and artifact in the repository.</summary>
    Task<IReadOnlyList<RepositoryImage>> ListAsync(string repository);
}

public interface IRecordKeys
{
    /// <summary>The keys under <paramref name="prefix"/> that sort after <paramref name="startAfter"/>.</summary>
    Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, string startAfter);
}

public interface IEvidenceProbe
{
    /// <summary>Whether the object exists. A denied read is a fault, not an absence.</summary>
    Task<bool> ExistsAsync(string bucket, string key);
}

public interface IAlertPublisher
{
    Task PublishAsync(string topicArn, string subject, string message);
}

/// <summary>What the corroborate function is configured with.</summary>
public sealed record CorroborateSettings(
    string BuildRecordStore, string EvidenceStore, string AlertsTopicArn, IReadOnlyList<CorroborateSource> Sources)
{
    public static CorroborateSettings Read(Func<string, string?> env)
    {
        var sources = DeployerAlerts.DecodeSources(DeployerEnvironment.Required(env, DeployerEnvironment.CorroborateSources));
        return new CorroborateSettings(
            DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.EvidenceStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.AlertsTopic),
            sources);
    }
}

public static class DeployerAlerts
{
    /// <summary>
    /// How old an image must be before a missing record is an anomaly. The build workflow pushes, then writes the record,
    /// and replication takes seconds — measured on 2026-09-13, dev's replica landed 3.8 s AFTER its record — so fifteen
    /// minutes is a margin for a slow workflow step, not for the ordinary case. It equals the sweep's period, so every
    /// image is looked at by at least one run once it is old enough.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back a run looks. Ninety-six runs a day each see every image of the last day, so an image missed by one
    /// failed run is seen by the next; a sweep that fails for a whole day is itself an alert (its errors alarm).
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <summary>
    /// How much earlier than its image a record's key may sort. A key's stamp is the build's <c>builtAt</c>, taken before
    /// the push; six hours is far longer than any build.
    /// </summary>
    public static readonly TimeSpan RecordLead = TimeSpan.FromHours(6);

    private const char SourceSeparator = '=';

    public static string EncodeSources(IEnumerable<CorroborateSource> sources)
    {
        var encoded = new List<string>();
        foreach (var s in sources)
        {
            if (s.Repository.Contains(SourceSeparator) || s.RecordPrefix.Contains(SourceSeparator))
                throw new InvalidOperationException($"'{s.Repository}' or '{s.RecordPrefix}' contains '{SourceSeparator}', which separates a source's two halves.");
            encoded.Add($"{s.Repository}{SourceSeparator}{s.RecordPrefix}");
        }

        return DeployerEnvironment.Join(encoded);
    }

    public static IReadOnlyList<CorroborateSource> DecodeSources(string encoded)
    {
        var sources = new List<CorroborateSource>();
        foreach (var entry in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(SourceSeparator);
            if (parts.Length != 2 || parts[0].Length == 0 || !parts[1].StartsWith($"{DeployerTrigger.RecordClass}/", StringComparison.Ordinal)
                || !parts[1].EndsWith('/'))
                throw new InvalidOperationException(
                    $"'{entry}' in {DeployerEnvironment.CorroborateSources} is not repository=image/owner/name/. " +
                    "Re-run `lz bootstrapdeployer --apply`, which is what sets it.");
            sources.Add(new CorroborateSource(parts[0], parts[1]));
        }

        if (sources.Count == 0)
            throw new InvalidOperationException(
                $"{DeployerEnvironment.CorroborateSources} names no repository, so the sweep would check nothing and report that " +
                "all is well. Re-run `lz bootstrapdeployer --apply`, which is what sets it.");
        return sources;
    }

    /// <summary>
    /// Whether a registry entry is an image a task could run. SELECTED BY WHAT IT IS, not by what it is not: a Docker or
    /// OCI image config, or a multi-platform index. Everything else — the Notary signature managed signing pushes beside
    /// every image (measured in dev: an OCI manifest whose artifact type is <c>application/vnd.cncf.notary.signature</c>),
    /// an SBOM, an attestation — is an artifact ECS cannot deploy, so it needs no record.
    /// </summary>
    public static bool IsRunnableImage(string? manifestMediaType, string? artifactMediaType)
        => artifactMediaType is "application/vnd.docker.container.image.v1+json" or "application/vnd.oci.image.config.v1+json"
           || (string.IsNullOrEmpty(artifactMediaType)
               && manifestMediaType is "application/vnd.oci.image.index.v1+json" or "application/vnd.docker.distribution.manifest.list.v2+json");

    /// <summary>A time as a record key's stamp: <c>builtAt</c> with the dashes and colons removed.</summary>
    public static string StampAt(DateTimeOffset time)
        => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Where an anomaly is recorded, once: <c>anomalies/{repository}/{digest}.json</c>, the colon made a dash.</summary>
    public static string AnomalyKey(string repository, string digest)
        => $"anomalies/{repository}/{digest.Replace(':', '-')}.json";

    public static string AlertSubject(string repository) => $"lz pipeline: an image in {repository} has no build record";

    public static string AlertMessage(
        RepositoryImage image, CorroborateSource source, string buildRecordStore, string evidenceStore, int recordsRead, int recordsUnreadable)
    {
        var lines = new List<string>
        {
            $"{image.Digest} arrived in {source.Repository} at {image.PushedAt.UtcDateTime:O}, and no build record under " +
            $"s3://{buildRecordStore}/{source.RecordPrefix} names it ({recordsRead} record(s) from around then were read).",
            "",
            "The pipeline never deploys it: an execution starts only from a record. But if the build account's registry signed " +
            "it, the signature hook admits it on a direct deploy, such as `lz updatecontainer --digest`.",
            "",
            $"Tags: {(image.Tags.Count == 0 ? "none" : string.Join(", ", image.Tags))}.",
            $"Recorded at s3://{evidenceStore}/{AnomalyKey(source.Repository, image.Digest)}. This alert is sent once for this image.",
        };
        if (recordsUnreadable > 0)
            lines.Add($"{recordsUnreadable} record(s) in that window could not be read or parsed; one of them may name it.");
        return string.Join("\n", lines);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AnomalyEvidence(
        RepositoryImage image, CorroborateSource source, string buildRecordStore, DateTimeOffset recordedAt, int recordsRead, int recordsUnreadable)
        => JsonSerializer.Serialize(new JsonObject
        {
            ["schema"] = DeployEvidence.Schema,
            ["outcome"] = "anomaly",
            ["kind"] = "image-without-record",
            ["recordedAt"] = recordedAt.ToString("O"),
            ["repository"] = source.Repository,
            ["digest"] = image.Digest,
            ["pushedAt"] = image.PushedAt.ToString("O"),
            ["tags"] = new JsonArray(image.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["recordsSearched"] = new JsonObject
            {
                ["store"] = buildRecordStore,
                ["prefix"] = source.RecordPrefix,
                ["read"] = recordsRead,
                ["unreadable"] = recordsUnreadable,
            },
        }, Json);
}

/// <summary>
/// The corroborate function (P2 stage D3, DecoupledCd.md §4.3's "an artifact that appears with no matching build record is an
/// anomaly, recorded and alarmed, never deployed").
///
/// <para>A SWEEP, NOT AN EVENT HANDLER. ECR's events are best effort, and a push whose record lands a moment later would race
/// its own event; the repository and the store are what is true, so each run reads both.</para>
///
/// <para>ALERT, THEN RECORD. An anomaly already recorded is not alerted again. The alert is sent before the record is written,
/// so a failure between the two repeats an alert on the next run rather than losing one.</para>
/// </summary>
public static class CorroborateStep
{
    public static async Task<JsonObject> RunAsync(
        CorroborateSettings settings, IRepositoryImages images, IRecordKeys keys, IRecordStore records,
        IEvidenceProbe evidence, IEvidenceWriter writer, IAlertPublisher alerts, DateTimeOffset now)
    {
        var corroborated = new JsonArray();
        var anomalies = new JsonArray();
        var alreadyRecorded = new JsonArray();
        var tooNew = new JsonArray();

        foreach (var source in settings.Sources)
        {
            var runnable = (await images.ListAsync(source.Repository))
                .Where(i => DeployerAlerts.IsRunnableImage(i.ManifestMediaType, i.ArtifactMediaType))
                .ToList();

            foreach (var young in runnable.Where(i => i.PushedAt > now - DeployerAlerts.Grace))
                tooNew.Add(young.Digest);

            var due = runnable
                .Where(i => i.PushedAt <= now - DeployerAlerts.Grace && i.PushedAt >= now - DeployerAlerts.Window)
                .ToList();
            if (due.Count == 0) continue;

            // Only the records that could name these images: the ones whose builds started from a little before the oldest.
            var startAfter = source.RecordPrefix + DeployerAlerts.StampAt(due.Min(i => i.PushedAt) - DeployerAlerts.RecordLead);
            var named = new HashSet<string>(StringComparer.Ordinal);
            var read = 0;
            var unreadable = 0;

            foreach (var key in await keys.ListAsync(settings.BuildRecordStore, source.RecordPrefix, startAfter))
            {
                if (!key.EndsWith(".json", StringComparison.Ordinal)) continue;

                var stored = await records.ReadAsync(settings.BuildRecordStore, key);
                if (stored is null) continue;

                try
                {
                    if (BuildRecordFormat.Parse(stored.Json).Identity.Digest is { } digest)
                        named.Add(digest);
                    read++;
                }
                catch (InvalidOperationException)
                {
                    unreadable++;
                }
            }

            foreach (var image in due)
            {
                if (named.Contains(image.Digest))
                {
                    corroborated.Add(image.Digest);
                    continue;
                }

                var anomalyKey = DeployerAlerts.AnomalyKey(source.Repository, image.Digest);
                if (await evidence.ExistsAsync(settings.EvidenceStore, anomalyKey))
                {
                    alreadyRecorded.Add(image.Digest);
                    continue;
                }

                await alerts.PublishAsync(
                    settings.AlertsTopicArn,
                    DeployerAlerts.AlertSubject(source.Repository),
                    DeployerAlerts.AlertMessage(image, source, settings.BuildRecordStore, settings.EvidenceStore, read, unreadable));

                await writer.PutOnceAsync(
                    settings.EvidenceStore, anomalyKey,
                    DeployerAlerts.AnomalyEvidence(image, source, settings.BuildRecordStore, now, read, unreadable));

                anomalies.Add(image.Digest);
            }
        }

        return new JsonObject
        {
            ["corroborated"] = corroborated,
            ["anomalies"] = anomalies,
            ["alreadyRecorded"] = alreadyRecorded,
            ["tooNew"] = tooNew,
        };
    }
}
