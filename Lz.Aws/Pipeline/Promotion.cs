using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// An environment promoted into (DecoupledCd.md §12 P5): its predecessor in the build-side chain, the predecessor's
/// evidence store where the pass markers it admits are written, and its own evidence store, where its decisions are.
/// </summary>
public sealed record PromotionSettings(string Predecessor, string PredecessorEvidenceStore, string EvidenceStore);

/// <summary>What a revert's Verify reads of this environment (P5 stage J): its decisions and its deployer's executions.</summary>
public interface IRevertReads
{
    /// <summary>The outcome decided here for this marker, or null when it is undecided.</summary>
    Task<string?> DecisionOutcomeAsync(string markerId);

    /// <summary>The executions of this environment's deployer that are running or awaiting approval, but this one.</summary>
    Task<IReadOnlyList<string>> OtherRunningAsync(string ownExecution);

    /// <summary>The marker id of the set decided deployed here most recently, or null when none was.</summary>
    Task<string?> NewestDeployedAsync();
}

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
    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, PromotionSettings promotion, VerifySettings settings,
        IVersionedObjects versions, IEvidenceProbe own, IRevertReads here, Func<JsonObject, Task<JsonObject>> verifyMember)
    {
        // A REVERT (stage J, T-14) names the set it goes back to as revertTo; a promotion names the predecessor's marker. Never both.
        var revert = state["revertTo"] is not null;
        if (revert && state["marker"] is not null)
            throw new DeployRefused("revertTo", "the input names both a marker and revertTo; a promotion and a revert are different executions.");
        var (key, versionId) = MarkerFrom(state, revert ? "revertTo" : "marker");

        // BEFORE ANY READ: a promoted environment admits pass markers only, and a key elsewhere in the predecessor's store is
        // evidence of something else.
        var isSite = key.StartsWith(PassMarkerFormat.Prefix + PassMarkerFormat.SiteSegment, StringComparison.Ordinal);
        if (!isSite && !key.StartsWith(PassMarkerFormat.Prefix + PassMarkerFormat.SetsSegment, StringComparison.Ordinal))
            throw new DeployRefused("marker.key",
                $"the input names '{key}', which is not a set marker under {PassMarkerFormat.Prefix}{PassMarkerFormat.SetsSegment} " +
                $"or a site marker under {PassMarkerFormat.Prefix}{PassMarkerFormat.SiteSegment}.");
        // NO REVERT FOR A SITE (V-K step 5, choice 3): a bad site is fixed forward, by a push to the site's main.
        if (isSite && revert)
            throw new DeployRefused("revertTo", $"'{key}' is a site marker; a site is never reverted - it is fixed forward, by a push to its main.");

        var stored = await versions.ReadVersionAsync(promotion.PredecessorEvidenceStore, key, versionId)
            ?? throw new DeployRefused("marker", $"{promotion.Predecessor}'s store {promotion.PredecessorEvidenceStore} serves no {key} at version {versionId}.");
        PassMarker marker;
        try { marker = PassMarkerFormat.Parse(stored.Json, promotion.Predecessor); }
        catch (InvalidOperationException ex) { throw new DeployRefused("marker", ex.Message); }

        if (!string.Equals(PassMarkerFormat.KeyFor(marker.MarkerId), key, StringComparison.Ordinal))
            throw new DeployRefused("marker.key", $"the marker at {key} is {marker.MarkerId}, whose key is {PassMarkerFormat.KeyFor(marker.MarkerId)}.");

        string? replaces = null;
        if (revert)
        {
            // ADMITTED ONLY FOR A SET THIS ENVIRONMENT DEPLOYED AND DECIDED DEPLOYED, and only with nothing else in flight (§4.4 step 2a).
            var outcome = await here.DecisionOutcomeAsync(marker.MarkerId);
            if (outcome != DecisionStep.Deployed)
                throw new DeployRefused("revertTo",
                    $"{marker.MarkerId} was {(outcome is null ? "never decided" : $"decided {outcome}")} here; a revert goes back only to a set this environment deployed.");
            var others = await here.OtherRunningAsync(executionName);
            if (others.Count > 0)
                throw new DeployRefused("executions", $"{string.Join(", ", others)} is running or awaiting approval; a revert runs alone.");
            var newest = await here.NewestDeployedAsync();
            replaces = newest == marker.MarkerId ? null : newest;
        }
        else if (await own.ExistsAsync(promotion.EvidenceStore, PassMarkerFormat.DecisionKeyFor(marker.MarkerId)))
            throw new DeployRefused("decision",
                $"{marker.MarkerId} is decided here already ({PassMarkerFormat.DecisionKeyFor(marker.MarkerId)}): what was decided stays decided, and a " +
                "set is promoted once per environment (§5.3 rule 2).");

        if (PassMarkerFormat.IsSite(marker))
            return await AdmitSiteAsync(marker, key, versionId, promotion, settings, versions, verifyMember);

        var tests = await Outcome(versions, promotion.PredecessorEvidenceStore,
            marker.Evidence.Tests ?? throw new DeployRefused("evidence.tests", "a set marker names no test evidence."), "evidence.tests");
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
            ["revert"] = revert,
            ["replaces"] = replaces,
            ["image"] = image,
            ["bundles"] = bundles,
        };
    }

    /// <summary>
    /// A SITE, ADMITTED BY ITS MARKER (V-K step 5): dev's deploy landed and dev's record says what the marker says; then this
    /// environment's own bundle of the same run - the record beside dev's, under this environment's directory, same stem -
    /// through this environment's site Verify, which must find it built from what dev's was. The two bundles' bytes differ by
    /// their hosts only, which Gate 1 checked when it took the run in.
    /// </summary>
    private static async Task<JsonObject> AdmitSiteAsync(PassMarker marker, string key, string versionId, PromotionSettings promotion,
        VerifySettings settings, IVersionedObjects versions, Func<JsonObject, Task<JsonObject>> verifyMember)
    {
        var m = marker.Members[0];
        var deployed = await Outcome(versions, promotion.PredecessorEvidenceStore, m.Deploy, "members[0].deploy");
        if (deployed != "deployed")
            throw new DeployRefused("members[0].deploy", $"{m.Repo}'s site deploy in {promotion.Predecessor} ended {deployed}, not deployed.");
        await RecordAsync(versions, settings, m, "members[0].record");

        var target = (settings.SiteTargets ?? Array.Empty<SiteTarget>()).FirstOrDefault(t => t.Repo == m.Repo)
            ?? throw new DeployRefused("site.target", $"{m.Repo} deploys no site in this environment, so its marker has nothing to deploy here.");
        var predecessorDirectory = $"{BuildRecordFormat.PrefixFor(BuildRecordFormat.SiteClass, m.Repo)}{promotion.Predecessor}/";
        if (!m.Record.Key.StartsWith(predecessorDirectory, StringComparison.Ordinal))
            throw new DeployRefused("members[0].record", $"{m.Record.Key} is not {promotion.Predecessor}'s site record, under {predecessorDirectory}.");
        var ownKey = $"{BuildRecordFormat.PrefixFor(BuildRecordFormat.SiteClass, m.Repo)}{target.Environment}/{m.Record.Key[predecessorDirectory.Length..]}";

        var memberState = new JsonObject
        {
            ["record"] = new JsonObject { ["bucket"] = m.Record.Bucket, ["key"] = ownKey },
            ["target"] = new JsonObject { ["bucket"] = target.Bucket },
        };
        var verified = await verifyMember(memberState);
        var built = verified["builtFrom"] as JsonObject ?? throw new DeployRefused("site.builtFrom", $"this environment's Verify of {ownKey} names no builtFrom.");
        var ownBuiltFrom = new BuildRecordBuiltFrom(
            built["repo"]?.GetValue<string>() ?? "", built["commit"]?.GetValue<string>() ?? "", built["lane"]?.GetValue<string>() ?? "",
            (built["packages"] as JsonObject ?? new JsonObject()).ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? ""),
            built["ref"]?.GetValue<string>());
        if (Canonical(ownBuiltFrom) != Canonical(m.BuiltFrom))
            throw new DeployRefused("site.builtFrom",
                $"{ownKey} was built from {ownBuiltFrom.Repo}@{ownBuiltFrom.Commit}, and the site {promotion.Predecessor} passed from {m.BuiltFrom.Repo}@{m.BuiltFrom.Commit}.");
        memberState["verified"] = verified;

        return new JsonObject
        {
            ["markerId"] = marker.MarkerId,
            ["marker"] = new JsonObject { ["key"] = key, ["versionId"] = versionId },
            ["predecessor"] = promotion.Predecessor,
            ["revert"] = false,
            ["replaces"] = null,
            ["image"] = null,
            ["bundles"] = new JsonArray(),
            ["site"] = memberState,
        };
    }

    private static (string Key, string VersionId) MarkerFrom(JsonObject state, string field)
    {
        var marker = state[field] as JsonObject
            ?? throw new DeployRefused(field,
                "the input names no marker. This environment deploys only what its predecessor passed, by marker; a person " +
                "going back starts a revert, not a hand-made execution.");
        string Field(string name) => marker[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : throw new DeployRefused(field, $"the input's {field} has no {name}.");
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
        // A REVERT DECIDES NOTHING (stage J, T-14): both sets were decided when first deployed here, so its failure is the
        // failure evidence and the alert, and its success is reverts/.
        if (state["revertTo"] is JsonObject)
            return new JsonObject { ["written"] = false, ["reason"] = "a revert writes no decision" };

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

/// <summary>This environment's deployer's executions, listed and started: the revert-start function's only calls.</summary>
public interface IPromotionExecutions
{
    Task<IReadOnlyList<PromotionExecution>> ExecutionsAsync(string stateMachineArn);

    /// <summary>Start an execution under this name; false when one by that name already exists.</summary>
    Task<bool> StartAsync(string stateMachineArn, string name, string input);
}

/// <summary>Writes the hold (P5 stage J): a revert's, only.</summary>
public interface IHoldWriter
{
    Task SetAsync(string parameter, string value);
}

/// <summary>
/// THE REVERT-START FUNCTION (§12 P5 stage J, T-14, §4.4 step 2a), invoked by the operator set in test (the approver set in
/// prod): starts this environment's deployer only with an input naming <c>revertTo</c> - an earlier set marker, by key and
/// version - under a name derived from it and the minute, and refuses while any other execution is running or awaiting
/// approval. It reads no hold: the hold is for automated starts, and a revert is the way back under one.
/// </summary>
public static class RevertStartStep
{
    public static async Task<JsonObject> RunAsync(JsonObject input, string stateMachineArn, JsonObject target, IPromotionExecutions sfn, DateTimeOffset now)
    {
        var revertTo = input["revertTo"] as JsonObject
            ?? throw new DeployRefused("revertTo", "a revert names the set it goes back to: {\"revertTo\": {\"key\": \"passed/sets/...json\", \"versionId\": \"...\"}}.");
        var key = revertTo["key"] is JsonValue k && k.TryGetValue<string>(out var ks) ? ks : null;
        var version = revertTo["versionId"] is JsonValue v && v.TryGetValue<string>(out var vs) ? vs : null;
        if (key is null || !key.StartsWith(PassMarkerFormat.Prefix + PassMarkerFormat.SetsSegment, StringComparison.Ordinal) || !key.EndsWith(".json", StringComparison.Ordinal))
            throw new DeployRefused("revertTo.key", $"'{key}' is not a set marker under {PassMarkerFormat.Prefix}{PassMarkerFormat.SetsSegment}.");
        if (string.IsNullOrWhiteSpace(version))
            throw new DeployRefused("revertTo.versionId", "a revert names the marker's version, as every promotion does.");

        var running = (await sfn.ExecutionsAsync(stateMachineArn)).Where(e => e.Status == "RUNNING").Select(e => e.Name).ToList();
        if (running.Count > 0)
            throw new DeployRefused("executions", $"{string.Join(", ", running)} is running or awaiting approval; a revert starts only when nothing else is in flight.");

        var hash = key[(PassMarkerFormat.Prefix.Length + PassMarkerFormat.SetsSegment.Length)..^".json".Length];
        var name = $"rv-{hash[..Math.Min(20, hash.Length)]}-{now.UtcDateTime:yyyyMMddHHmm}";
        var body = new JsonObject
        {
            ["revertTo"] = new JsonObject { ["key"] = key, ["versionId"] = version },
            ["target"] = target.DeepClone(),
        };
        var started = await sfn.StartAsync(stateMachineArn, name, body.ToJsonString());
        return new JsonObject { ["started"] = name, ["new"] = started };
    }
}

/// <summary>
/// THE REVERT'S HOLD (§12 P5 stage J, T-14): set before Prepare, in an execution whose input names <c>revertTo</c> and in no
/// other, so nothing automated starts on top of the way back. It is released by a person, once the predecessor has passed a
/// set beyond the fault.
/// </summary>
public static class SetHoldStep
{
    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, string holdParameter, IHoldWriter ssm)
    {
        if (state["revertTo"] is not JsonObject)
            throw new DeployRefused("revertTo", $"{executionName} is not a revert; only a revert sets the hold.");
        await ssm.SetAsync(holdParameter, "on");
        return new JsonObject { ["hold"] = holdParameter, ["value"] = "on" };
    }
}

/// <summary>
/// A REVERT'S RECORD (§12 P5 stage J, T-14): <c>reverts/{execution}.json</c>, naming the set restored and the set it replaced.
/// No decision is written: both sets were decided when they were first deployed here.
/// </summary>
public static class RecordRevertStep
{
    public const string Prefix = "reverts/";

    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, string evidenceStore, IEvidenceWriter writer, DateTimeOffset now)
    {
        var key = $"{Prefix}{executionName}.json";
        var body = new JsonObject
        {
            ["schema"] = 1,
            ["execution"] = executionName,
            ["restored"] = new JsonObject { ["markerId"] = state["set"]?["markerId"]?.DeepClone(), ["marker"] = state["revertTo"]?.DeepClone() },
            ["replaced"] = state["set"]?["replaces"]?.DeepClone(),
            ["recordedAt"] = now.ToString("O"),
        };
        var written = await writer.PutOnceAsync(evidenceStore, key, body.ToJsonString());
        return new JsonObject { ["key"] = key, ["written"] = written };
    }
}
