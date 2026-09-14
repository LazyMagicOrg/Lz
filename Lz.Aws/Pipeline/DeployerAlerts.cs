using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  THE SWEEP (P2 stage D3): an image in this environment's pipeline repository that no build record names — and,
//  where the environment deploys client bundles (P4 stage D), a bundle version in the artifact store that none names.
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

/// <summary>One version of an object, as the artifact store lists it (P4 stage D). A delete marker is not one.</summary>
public sealed record StoredVersion(string Key, string VersionId, DateTimeOffset LastModified);

public interface IArtifactVersions
{
    /// <summary>Every object version under <paramref name="prefix"/>, all pages, with delete markers left out. A denial throws.</summary>
    Task<IReadOnlyList<StoredVersion>> ListAsync(string bucket, string prefix);
}

/// <summary>What the corroborate function is configured with.</summary>
/// <param name="ArtifactStore">The build account's artifact store, whose client bundles the sweep checks (P4 stage D). Null
/// where the environment deploys none.</param>
/// <param name="BundlePrefixes">The client repositories' prefixes, <c>client/{owner}/{name}/</c> — the same in both stores.</param>
public sealed record CorroborateSettings(
    string BuildRecordStore, string EvidenceStore, string AlertsTopicArn, IReadOnlyList<CorroborateSource> Sources,
    string? ArtifactStore = null, IReadOnlyList<string>? BundlePrefixes = null)
{
    public static CorroborateSettings Read(Func<string, string?> env)
    {
        var sources = DeployerAlerts.DecodeSources(DeployerEnvironment.Required(env, DeployerEnvironment.CorroborateSources));

        // BUNDLE SOURCES ARE OPTIONAL, as the trigger's client routes are: absent where no client target is configured, and
        // where present the artifact store is required with them, since the versions are listed there.
        IReadOnlyList<string>? bundlePrefixes = null;
        string? artifactStore = null;
        if (env(DeployerEnvironment.CorroborateBundleSources) is { Length: > 0 } encodedBundles)
        {
            bundlePrefixes = DeployerAlerts.DecodeBundleSources(encodedBundles);
            artifactStore = DeployerEnvironment.Required(env, DeployerEnvironment.ArtifactStore);
        }

        return new CorroborateSettings(
            DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.EvidenceStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.AlertsTopic),
            sources,
            artifactStore,
            bundlePrefixes);
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

    /// <summary>The client repositories' prefixes, as the sweep's environment carries them (P4 stage D).</summary>
    public static string EncodeBundleSources(IEnumerable<string> prefixes)
    {
        var list = prefixes.ToList();
        foreach (var prefix in list)
        {
            if (!DeployerTrigger.IsClientRecordPrefix(prefix))
                throw new InvalidOperationException($"'{prefix}' is not a client repository's prefix, client/{{owner}}/{{name}}/.");
        }

        return DeployerEnvironment.Join(list);
    }

    public static IReadOnlyList<string> DecodeBundleSources(string encoded)
    {
        var prefixes = encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var prefix in prefixes)
        {
            if (!DeployerTrigger.IsClientRecordPrefix(prefix))
                throw new InvalidOperationException(
                    $"'{prefix}' in {DeployerEnvironment.CorroborateBundleSources} is not client/owner/name/. " +
                    "Re-run `lz bootstrapdeployer --apply`, which is what sets it.");
        }

        // Written only where a client target exists, so an empty value is a variable that lost its sources, not "none".
        if (prefixes.Length == 0)
            throw new InvalidOperationException(
                $"{DeployerEnvironment.CorroborateBundleSources} names no prefix, so the sweep would check no bundle and report that " +
                "all is well. Re-run `lz bootstrapdeployer --apply`, which is what sets it.");
        return prefixes;
    }

    /// <summary>
    /// The key a bundle's record would have: the zip's stem with <c>.json</c> for <c>.zip</c>, as <see cref="BuildRecordFormat.KeyFor"/>
    /// and <see cref="BuildRecordFormat.BundleKeyFor"/> give one build's two objects. Null for a key no bundle has.
    /// </summary>
    public static string? RecordKeyForBundle(string key)
        => key.EndsWith(".zip", StringComparison.Ordinal) && key.Length > ".zip".Length ? key[..^".zip".Length] + ".json" : null;

    /// <summary>How the sweep's result names a bundle version: its key and the version, which together are its identity.</summary>
    public static string BundleName(StoredVersion version) => $"{version.Key}?versionId={version.VersionId}";

    /// <summary>
    /// Where a bundle anomaly is recorded, once: <c>anomalies/{key}/{version id}.json</c>. The version is escaped, so an id that
    /// held a slash could not name a key outside its folder, and two ids cannot escape to one.
    /// </summary>
    public static string BundleAnomalyKey(string key, string versionId) => $"anomalies/{key}/{Uri.EscapeDataString(versionId)}.json";

    public static string BundleAlertSubject(string artifactStore) => $"lz pipeline: a bundle in {artifactStore} has no build record";

    public static string BundleAlertMessage(StoredVersion version, string artifactStore, string evidenceStore, string reason)
        => string.Join("\n", new[]
        {
            $"s3://{artifactStore}/{version.Key} (version {version.VersionId}) was written at {version.LastModified.UtcDateTime:O}, " +
            $"and no build record names it: {reason.TrimEnd('.')}.",
            "",
            "No execution deploys it: Verify reads a bundle only at the version its build record names, and compares S3's SHA-256 " +
            "of that version with the record's.",
            "",
            $"Recorded at s3://{evidenceStore}/{BundleAnomalyKey(version.Key, version.VersionId)}. This alert is sent once for this version.",
        });

    public static string BundleAnomalyEvidence(
        StoredVersion version, string artifactStore, string buildRecordStore, string? recordKey, string reason, DateTimeOffset recordedAt)
        => JsonSerializer.Serialize(new JsonObject
        {
            ["schema"] = DeployEvidence.Schema,
            ["outcome"] = "anomaly",
            ["kind"] = "bundle-without-record",
            ["recordedAt"] = recordedAt.ToString("O"),
            ["store"] = artifactStore,
            ["key"] = version.Key,
            ["versionId"] = version.VersionId,
            ["lastModified"] = version.LastModified.ToString("O"),
            ["recordSearched"] = new JsonObject { ["store"] = buildRecordStore, ["key"] = recordKey },
            ["reason"] = reason,
        }, Json);

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
///
/// <para>A BUNDLE IS FOUND BY ITS RECORD'S KEY, NOT BY LISTING RECORDS (P4 stage D): one build writes its zip and its record under
/// one stem, so the record that could name a zip is at exactly one key, and it names the zip only when it names that key and that
/// version — the version is the identity, and a second version under one key is a write no build made.</para>
/// </summary>
public static class CorroborateStep
{
    public static async Task<JsonObject> RunAsync(
        CorroborateSettings settings, IRepositoryImages images, IArtifactVersions versions, IRecordKeys keys, IRecordStore records,
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

        if (settings.BundlePrefixes is { Count: > 0 } bundlePrefixes)
        {
            var store = settings.ArtifactStore
                ?? throw new InvalidOperationException("the sweep has bundle sources but no artifact store to list them in.");

            foreach (var prefix in bundlePrefixes)
            {
                var listed = await versions.ListAsync(store, prefix);

                foreach (var young in listed.Where(v => v.LastModified > now - DeployerAlerts.Grace))
                    tooNew.Add(DeployerAlerts.BundleName(young));

                foreach (var version in listed.Where(v => v.LastModified <= now - DeployerAlerts.Grace && v.LastModified >= now - DeployerAlerts.Window))
                {
                    var recordKey = DeployerAlerts.RecordKeyForBundle(version.Key);
                    var reason = await UnnamedBecauseAsync(settings.BuildRecordStore, store, version, recordKey, records);
                    if (reason is null)
                    {
                        corroborated.Add(DeployerAlerts.BundleName(version));
                        continue;
                    }

                    var anomalyKey = DeployerAlerts.BundleAnomalyKey(version.Key, version.VersionId);
                    if (await evidence.ExistsAsync(settings.EvidenceStore, anomalyKey))
                    {
                        alreadyRecorded.Add(DeployerAlerts.BundleName(version));
                        continue;
                    }

                    await alerts.PublishAsync(
                        settings.AlertsTopicArn,
                        DeployerAlerts.BundleAlertSubject(store),
                        DeployerAlerts.BundleAlertMessage(version, store, settings.EvidenceStore, reason));

                    await writer.PutOnceAsync(
                        settings.EvidenceStore, anomalyKey,
                        DeployerAlerts.BundleAnomalyEvidence(version, store, settings.BuildRecordStore, recordKey, reason, now));

                    anomalies.Add(DeployerAlerts.BundleName(version));
                }
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

    /// <summary>Why no build record names this bundle version, or null when its record does.</summary>
    private static async Task<string?> UnnamedBecauseAsync(
        string buildRecordStore, string artifactStore, StoredVersion version, string? recordKey, IRecordStore records)
    {
        if (recordKey is null)
            return "its key does not end in .zip, so it is no build's bundle";

        var stored = await records.ReadAsync(buildRecordStore, recordKey);
        if (stored is null)
            return $"there is no build record at s3://{buildRecordStore}/{recordKey}";

        BuildRecord record;
        try
        {
            // With the store: a record naming any other bucket, or a key that is not its own build's zip, does not parse.
            record = BuildRecordFormat.Parse(stored.Json, artifactStore);
        }
        catch (InvalidOperationException ex)
        {
            return $"the build record at s3://{buildRecordStore}/{recordKey} cannot be read: {ex.Message}";
        }

        var identity = record.Identity;
        if (identity.Kind != "bundle")
            return $"the build record at s3://{buildRecordStore}/{recordKey} names an image, {identity.Digest}";

        return identity.Key == version.Key && identity.VersionId == version.VersionId
            ? null
            : $"the build record at s3://{buildRecordStore}/{recordKey} names s3://{identity.Bucket}/{identity.Key} version {identity.VersionId}";
    }
}
