using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// An environment promoted into (DecoupledCd.md §12 P5): its predecessor in the build-side chain, the predecessor's
/// evidence store where the pass markers it admits are written, and its own evidence store, where its decisions are.
/// </summary>
public sealed record PromotionSettings(string Predecessor, string PredecessorEvidenceStore, string EvidenceStore);

public interface IVersionedObjects
{
    /// <summary>The object at exactly this version, or null when the store serves no such version.</summary>
    Task<StoredRecord?> ReadVersionAsync(string bucket, string key, string versionId);
}

/// <summary>
/// TEST'S VERIFY ADMITS BY MARKER (§12 P5 stage F, §4.4 step 2). Before anything this environment does, the input's marker,
/// in the predecessor's store at the version named: it parses as the predecessor's, sits at its own id, and is undecided
/// here; the set's test evidence passed; every member has deploy evidence that landed; and every member's record, at the
/// marker's version in the build-record store, carries the marker's class, repository, identity and builtFrom. Then the
/// AipApi check: no client newer, by the AipApi it was built against, than the Service it runs beside. Only then is each
/// member put through this environment's own Verify, the image first, and the record version that Verify read must be the
/// marker's. Every refusal names the field it failed on. An input without a marker is refused, a hand-started one included:
/// a promoted environment deploys only what its predecessor passed (stage J's revert is the one other way in).
/// </summary>
public static class VerifySetStep
{
    public static async Task<JsonObject> RunAsync(JsonObject state, PromotionSettings promotion, VerifySettings settings,
        IVersionedObjects versions, IEvidenceProbe own, Func<JsonObject, Task<JsonObject>> verifyMember)
    {
        var (key, versionId) = MarkerFrom(state);

        // BEFORE ANY READ: a promoted environment admits pass markers only, and a key elsewhere in the predecessor's store is
        // evidence of something else.
        if (!key.StartsWith(PassMarkerFormat.Prefix + PassMarkerFormat.SetsSegment, StringComparison.Ordinal))
            throw new DeployRefused("marker.key", $"the input names '{key}', which is not a set marker under {PassMarkerFormat.Prefix}{PassMarkerFormat.SetsSegment}.");

        var stored = await versions.ReadVersionAsync(promotion.PredecessorEvidenceStore, key, versionId)
            ?? throw new DeployRefused("marker", $"{promotion.Predecessor}'s store {promotion.PredecessorEvidenceStore} serves no {key} at version {versionId}.");
        PassMarker marker;
        try { marker = PassMarkerFormat.Parse(stored.Json, promotion.Predecessor); }
        catch (InvalidOperationException ex) { throw new DeployRefused("marker", ex.Message); }

        if (!string.Equals(PassMarkerFormat.KeyFor(marker.MarkerId), key, StringComparison.Ordinal))
            throw new DeployRefused("marker.key", $"the marker at {key} is {marker.MarkerId}, whose key is {PassMarkerFormat.KeyFor(marker.MarkerId)}.");

        if (await own.ExistsAsync(promotion.EvidenceStore, PassMarkerFormat.DecisionKeyFor(marker.MarkerId)))
            throw new DeployRefused("decision",
                $"{marker.MarkerId} is decided here already ({PassMarkerFormat.DecisionKeyFor(marker.MarkerId)}): what was decided stays decided, and a " +
                "set is promoted once per environment (§5.3 rule 2).");

        var tests = await Outcome(versions, promotion.PredecessorEvidenceStore, marker.Evidence.Tests, "evidence.tests");
        if (tests != "Passed")
            throw new DeployRefused("evidence.tests", $"{promotion.Predecessor}'s Gate 2 for this set ended {tests}, not Passed.");

        var images = marker.Members.Where(m => m.Class == "image").ToList();
        if (images.Count != 1)
            throw new DeployRefused("members", $"the set names {images.Count} image members; a set is the Service image and the clients beside it, so exactly one.");

        var records = new BuildRecord[marker.Members.Count];
        for (var i = 0; i < marker.Members.Count; i++)
        {
            var m = marker.Members[i];
            var deployed = await Outcome(versions, promotion.PredecessorEvidenceStore, m.Deploy, $"members[{i}].deploy");
            if (deployed != "deployed")
                throw new DeployRefused($"members[{i}].deploy", $"{m.Repo}'s deploy in {promotion.Predecessor} ended {deployed}, not deployed.");
            records[i] = await RecordAsync(versions, settings, m, $"members[{i}].record");
        }

        var imageAt = Enumerable.Range(0, marker.Members.Count).First(i => marker.Members[i].Class == "image");
        var serviceVersion = records[imageAt].Intake?.ServiceVersion;
        if (string.IsNullOrWhiteSpace(serviceVersion) || !Version.TryParse(serviceVersion, out var service))
            throw new DeployRefused($"members[{imageAt}].serviceVersion",
                $"the image record names Service version '{serviceVersion}', so no client's AipApi can be checked against it.");
        for (var i = 0; i < marker.Members.Count; i++)
        {
            if (marker.Members[i].Class != "client") continue;
            var aipApi = records[i].BuiltFrom.Packages.GetValueOrDefault("AipApi");
            if (aipApi is null || !Version.TryParse(aipApi, out var api))
                throw new DeployRefused($"members[{i}].aipApi", $"{marker.Members[i].Repo}'s record names AipApi '{aipApi}', which cannot be compared with a version.");
            if (api > service)
                throw new DeployRefused($"members[{i}].aipApi",
                    $"{marker.Members[i].Repo} was built against AipApi {aipApi}, newer than the Service {serviceVersion} it would run beside (T-13).");
        }

        // EACH MEMBER THROUGH THIS ENVIRONMENT'S OWN VERIFY, the image first (T-13): its registry, its service, its web apps.
        JsonObject? image = null;
        var bundles = new JsonArray();
        foreach (var i in Enumerable.Range(0, marker.Members.Count).OrderBy(i => marker.Members[i].Class == "image" ? 0 : 1))
        {
            var m = marker.Members[i];
            var target = m.Class == "image"
                ? state["target"]?.DeepClone() as JsonObject ?? throw new DeployRefused("target", "the input names no target for the image member.")
                : new JsonObject
                {
                    ["bucket"] = (settings.ClientTargets ?? Array.Empty<ClientTarget>()).FirstOrDefault(t => t.Repo == m.Repo)?.Bucket
                        ?? throw new DeployRefused($"members[{i}].target", $"{m.Repo} deploys as no web app in this environment."),
                };
            var memberState = new JsonObject
            {
                ["record"] = new JsonObject { ["bucket"] = m.Record.Bucket, ["key"] = m.Record.Key },
                ["target"] = target,
            };
            var verified = await verifyMember(memberState);
            var read = verified["recordVersionId"]?.GetValue<string>();
            if (!string.Equals(read, m.Record.VersionId, StringComparison.Ordinal))
                throw new DeployRefused($"members[{i}].record", $"this environment's Verify read {m.Record.Key} at version {read}, not the marker's {m.Record.VersionId}.");

            memberState["verified"] = verified;
            if (m.Class == "image") image = memberState; else bundles.Add(memberState);
        }

        return new JsonObject
        {
            ["markerId"] = marker.MarkerId,
            ["marker"] = new JsonObject { ["key"] = key, ["versionId"] = versionId },
            ["predecessor"] = promotion.Predecessor,
            ["image"] = image,
            ["bundles"] = bundles,
        };
    }

    private static (string Key, string VersionId) MarkerFrom(JsonObject state)
    {
        var marker = state["marker"] as JsonObject
            ?? throw new DeployRefused("marker",
                "the input names no marker. This environment deploys only what its predecessor passed, by marker; a person " +
                "going back starts a revert, not a hand-made execution.");
        string Field(string name) => marker[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : throw new DeployRefused("marker", $"the input's marker has no {name}.");
        return (Field("key"), Field("versionId"));
    }

    /// <summary>The <c>outcome</c> of an evidence document at exactly the referenced version.</summary>
    private static async Task<string> Outcome(IVersionedObjects versions, string bucket, PassMarkerReference reference, string check)
    {
        var stored = await versions.ReadVersionAsync(bucket, reference.Key, reference.VersionId)
            ?? throw new DeployRefused(check, $"{bucket} serves no {reference.Key} at version {reference.VersionId}.");
        try
        {
            return JsonNode.Parse(stored.Json)?["outcome"]?.GetValue<string>()
                ?? throw new DeployRefused(check, $"{reference.Key} names no outcome.");
        }
        catch (JsonException ex) { throw new DeployRefused(check, $"{reference.Key} is not valid JSON: {ex.Message}"); }
    }

    /// <summary>The member's record at the marker's version, which must say what the marker says of it, field for field.</summary>
    private static async Task<BuildRecord> RecordAsync(IVersionedObjects versions, VerifySettings settings, PassMarkerMember m, string check)
    {
        if (!string.Equals(m.Record.Bucket, settings.BuildRecordStore, StringComparison.Ordinal))
            throw new DeployRefused(check, $"the marker names {m.Repo}'s record in {m.Record.Bucket}, not the build-record store {settings.BuildRecordStore}.");
        var stored = await versions.ReadVersionAsync(m.Record.Bucket, m.Record.Key, m.Record.VersionId)
            ?? throw new DeployRefused(check, $"the build-record store serves no {m.Record.Key} at version {m.Record.VersionId}.");
        BuildRecord r;
        try { r = BuildRecordFormat.Parse(stored.Json, settings.ArtifactStore); }
        catch (InvalidOperationException ex) { throw new DeployRefused(check, ex.Message); }

        var differs = new List<string>();
        if (r.Class != m.Class) differs.Add("class");
        if (r.BuiltFrom.Repo != m.Repo) differs.Add("repo");
        if (Canonical(r.Identity) != Canonical(m.Identity)) differs.Add("identity");
        if (Canonical(r.BuiltFrom) != Canonical(m.BuiltFrom)) differs.Add("builtFrom");
        if (differs.Count > 0)
            throw new DeployRefused(check, $"{m.Record.Key} at version {m.Record.VersionId} differs from the marker in {string.Join(", ", differs)}.");
        return r;
    }

    private static string Canonical(BuildRecordIdentity i) => $"{i.Kind}|{i.Digest}|{i.Bucket}|{i.Key}|{i.VersionId}|{i.Sha256}";

    private static string Canonical(BuildRecordBuiltFrom b) => $"{b.Repo}|{b.Commit}|{b.Lane}|{b.Ref}|" +
        string.Join(",", b.Packages.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>A deployed set's decision was already written: another execution decided the same marker. Alerted, as a failure.</summary>
public sealed class DecisionAlreadyWritten(string message) : Exception(message);

/// <summary>
/// A PROMOTED ENVIRONMENT'S DECISION (§12 P5 stage F, §5.3 rule 2), written once at <c>decisions/{markerId}.json</c> in its
/// own evidence store: <c>deployed</c> after the set's last member is recorded, and, after RecordFailure, <c>refused</c> for a
/// Verify refusal or <c>failed</c> for anything else. What is decided stays decided: a set is promoted once per environment,
/// and a refused or failed one is never deployed from the same marker again.
///
/// <para>The marker's id is the set's, from <c>$.set</c>, or, where Verify refused before reading it, from the input's marker
/// key. A failure with no set marker to name decides nothing; the failure path never throws, since RecordFailure's evidence is
/// already written by then. A deployed set whose decision already exists does throw: two executions raced to it.</para>
/// </summary>
public static class DecisionStep
{
    public const string Deployed = "deployed", Refused = "refused", Failed = "failed";

    private static readonly System.Text.RegularExpressions.Regex SetKey = new(@$"^{PassMarkerFormat.Prefix}(sets/[0-9a-f]{{64}})\.json$");

    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, string evidenceStore, IEvidenceWriter writer, DateTimeOffset now)
    {
        var error = state["error"] as JsonObject;
        var outcome = error is null ? Deployed : error["Error"]?.GetValue<string>() == nameof(DeployRefused) ? Refused : Failed;

        var markerId = state["set"]?["markerId"]?.GetValue<string>();
        var key = state["marker"]?["key"] is JsonValue k && k.TryGetValue<string>(out var s) ? s : null;
        if (markerId is null && key is not null && SetKey.Match(key) is { Success: true } m) markerId = m.Groups[1].Value;
        if (markerId is null)
        {
            if (outcome == Deployed) throw new InvalidOperationException("a deployed set names no marker, so it cannot be decided.");
            return new JsonObject { ["written"] = false, ["outcome"] = outcome, ["reason"] = "the input names no set marker to decide" };
        }

        var decisionKey = PassMarkerFormat.DecisionKeyFor(markerId);
        var body = new JsonObject
        {
            ["schema"] = 1,
            ["markerId"] = markerId,
            ["marker"] = state["marker"]?.DeepClone(),
            ["outcome"] = outcome,
            ["execution"] = executionName,
            ["decidedAt"] = now.ToString("O"),
        };
        if (error is not null) body["error"] = new JsonObject { ["type"] = error["Error"]?.DeepClone(), ["cause"] = error["Cause"]?.DeepClone() };

        var written = await writer.PutOnceAsync(evidenceStore, decisionKey, body.ToJsonString());
        if (!written && outcome == Deployed)
            throw new DecisionAlreadyWritten($"{decisionKey} was already written when {executionName} deployed its set: another execution decided it.");
        return new JsonObject { ["key"] = decisionKey, ["outcome"] = outcome, ["written"] = written };
    }
}
