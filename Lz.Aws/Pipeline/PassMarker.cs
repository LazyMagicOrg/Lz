using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>A reference to an object in the writer's own evidence store: its key, and the version that was read or written.</summary>
public sealed record PassMarkerReference(string Key, string VersionId);

/// <summary>A member's build record, in the build account's record store, at the version the environment deployed from.</summary>
public sealed record PassMarkerRecordReference(string Bucket, string Key, string VersionId);

/// <summary>
/// The set's test evidence: Gate 2's <c>tests/{execution}.json</c>, which passed its blocking set with no skip. Null in a site
/// marker, since the site has no Gate 2 (V-24).
/// </summary>
public sealed record PassMarkerEvidence(PassMarkerReference? Tests);

/// <summary>
/// One member of the tested set (T-13): the Service image or a client bundle, with the record it was deployed from and the
/// environment's deploy evidence for it.
/// </summary>
/// <param name="Class"><c>image</c> or <c>client</c> in a set; <c>site</c>, alone, in a site marker (V-K step 5).</param>
/// <param name="Version">What the member names itself by: an image's Service version (its record's
/// <c>intake.serviceVersion</c>), a bundle's object version.</param>
/// <param name="BuiltFrom">Copied from the record, so the next environment compares the record it reads against it.</param>
/// <param name="Deploy">This environment's <c>deploys/</c> evidence for the member; a member without it reached the environment
/// outside the pipeline, and no marker is written for its set.</param>
public sealed record PassMarkerMember(
    string Class, string Repo, PassMarkerRecordReference Record, BuildRecordIdentity Identity, string Version,
    BuildRecordBuiltFrom BuiltFrom, PassMarkerReference Deploy);

/// <summary>
/// THE PASS MARKER, schema 1 (DecoupledCd.md §12 P5 stage C; T-12, T-13). Written once by an environment's MarkPassed, after
/// its Record and its Gate 2, at <c>passed/{markerId}.json</c> in its own evidence store, and read there by the next
/// environment in the chain. It promotes the SET the environment ran - everything but the site - never one artifact; or, as a
/// SITE MARKER (V-K step 5, <c>site/{sha256}</c>), the one site an execution deployed and verified.
/// </summary>
/// <param name="MarkerId"><c>sets/{sha256}</c> over the member identities (<see cref="PassMarkerFormat.MarkerIdFor"/>): the key
/// the marker is written under and the key its decisions take. Not the execution's name, which Step Functions frees after 90
/// days and a person can choose by hand.</param>
/// <param name="Environment">The writer's.</param>
/// <param name="Execution">The execution that wrote it, a field rather than the key.</param>
/// <param name="ToolingDigest">For classes 5 to 7, once they deploy (P3); refused until then.</param>
/// <param name="ConfigBundleVersion">For classes 5 to 7, once they deploy (P3); refused until then.</param>
public sealed record PassMarker(
    int Schema, string MarkerId, string Environment, IReadOnlyList<PassMarkerMember> Members, PassMarkerEvidence Evidence,
    string Execution, string WrittenAt, string? ToolingDigest = null, string? ConfigBundleVersion = null);

public static class PassMarkerFormat
{
    public const int CurrentSchema = 1;
    public const string Prefix = "passed/";
    public const string DecisionsPrefix = "decisions/";
    public const string SetsSegment = "sets/";

    /// <summary>
    /// A SITE MARKER's segment (V-K step 5): one run's site, passed by deploying and verifying it, with no Gate 2 (V-24). It is
    /// listed and decided apart from the sets, under <c>passed/site/</c> and <c>decisions/site/</c>.
    /// </summary>
    public const string SiteSegment = "site/";

    /// <summary>Both kinds of marker, as the promotion poller lists them and their decisions.</summary>
    public static readonly string[] Segments = { SetsSegment, SiteSegment };

    /// <summary>The classes a set names (T-13): the Service image and the clients. The site has a marker of its own.</summary>
    public static readonly string[] MemberClasses = { "image", "client" };

    /// <summary>The class a site marker's one member is of.</summary>
    public const string SiteClass = "site";

    /// <summary>Whether this is a site marker: its id is under <see cref="SiteSegment"/>, as its one site member makes it.</summary>
    public static bool IsSite(PassMarker marker) => marker.MarkerId.StartsWith(SiteSegment, StringComparison.Ordinal);

    public static string KeyFor(string markerId) => $"{Prefix}{markerId}.json";
    public static string DecisionKeyFor(string markerId) => $"{DecisionsPrefix}{markerId}.json";

    /// <summary>
    /// <c>sets/{sha256}</c> over the members' identities - class, repository and what the artifact IS (a digest, or a bundle's
    /// key at its object version) - sorted, so one set has one id whatever order it was read in. Nothing else enters it: a
    /// second pass of the same set, by another execution or at another time, is the same marker and meets the write-once
    /// statement.
    /// </summary>
    public static string MarkerIdFor(IEnumerable<PassMarkerMember> members)
    {
        var list = members.ToList();
        var sites = list.Count(m => m.Class == SiteClass);
        if (sites > 0 && list.Count != 1)
            throw new InvalidOperationException(
                $"a site marker names its one site and nothing else; this one names {sites} site(s) among {list.Count} member(s).");
        var identities = list.Select(IdentityOf).ToList();
        if (identities.Count == 0) throw new InvalidOperationException("a pass marker names at least one member.");
        var twice = identities.GroupBy(i => i, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null) throw new InvalidOperationException($"a pass marker names {twice.Key} twice.");
        var canonical = string.Join("\n", identities.OrderBy(i => i, StringComparer.Ordinal));
        return (sites == 1 ? SiteSegment : SetsSegment) + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string IdentityOf(PassMarkerMember m) => m.Identity.Kind == "image"
        ? $"{m.Class}|{m.Repo}|image|{m.Identity.Digest}"
        : $"{m.Class}|{m.Repo}|bundle|{m.Identity.Bucket}/{m.Identity.Key}@{m.Identity.VersionId}";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(PassMarker marker) => JsonSerializer.Serialize(marker, Json);

    private static readonly string[] Fields = { "schema", "markerId", "environment", "members", "evidence", "execution", "writtenAt" };
    private static readonly string[] MemberFields = { "class", "repo", "record", "identity", "version", "builtFrom", "deploy" };

    /// <summary>
    /// Reads a marker the next environment found in <paramref name="environment"/>'s store, FAILING CLOSED: every field
    /// required and named when it is not there, every reference versioned, no field it does not know, members of the classes
    /// a marker names only, and the marker id the hash of its members. A marker another environment wrote is refused.
    /// </summary>
    public static PassMarker Parse(string json, string environment)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("a pass marker is a JSON object."); }
        catch (JsonException ex) { throw new InvalidOperationException($"pass marker is not valid JSON: {ex.Message}", ex); }

        if (root["schema"] is null) throw Missing("schema");
        if (root["schema"]!.GetValueKind() != JsonValueKind.Number || root["schema"]!.GetValue<int>() != CurrentSchema)
            throw new InvalidOperationException(
                $"pass marker declares schema {root["schema"]!.ToJsonString()}; this build of lz reads schema {CurrentSchema}, and refuses " +
                "rather than reading another format with today's assumptions.");

        foreach (var key in root.Select(p => p.Key))
        {
            if (key is "toolingDigest" or "configBundleVersion")
                throw new InvalidOperationException(
                    $"pass marker names {key}, which only a set with a member of classes 5 to 7 carries, and none deploys yet (P3).");
            if (!Fields.Contains(key)) throw new InvalidOperationException($"pass marker names '{key}', a field schema 1 does not have.");
        }
        foreach (var field in Fields) if (root[field] is null) throw Missing(field);

        var written = String(root, "environment");
        if (!string.Equals(written, environment, StringComparison.Ordinal))
            throw new InvalidOperationException($"pass marker was written by '{written}', and was read as '{environment}'s.");

        if (root["members"] is not JsonArray array || array.Count == 0) throw new InvalidOperationException("pass marker names no members.");
        var members = array.Select((n, i) => Member(n as JsonObject ?? throw new InvalidOperationException($"members[{i}] is not an object."), i)).ToList();

        // A SET names the test evidence its Gate 2 wrote; a SITE has no Gate 2 (V-24), so its marker names none, and one that
        // did would claim a test that never ran.
        var evidenceObject = Object(root, "evidence", "evidence");
        var isSite = members.Any(m => m.Class == SiteClass);
        if (isSite && evidenceObject.Count > 0)
            throw new InvalidOperationException("a site marker names evidence; the site has no Gate 2, so it has none to name.");
        var evidence = new PassMarkerEvidence(isSite ? null : Reference(Object(evidenceObject, "tests", "evidence.tests"), "evidence.tests"));

        var markerId = String(root, "markerId");
        var expected = MarkerIdFor(members);
        if (!string.Equals(markerId, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"pass marker's markerId is {markerId}, but its members hash to {expected}.");

        return new PassMarker(CurrentSchema, markerId, written, members, evidence, String(root, "execution"), String(root, "writtenAt"));
    }

    private static PassMarkerMember Member(JsonObject m, int i)
    {
        var at = $"members[{i}]";
        foreach (var key in m.Select(p => p.Key))
            if (!MemberFields.Contains(key)) throw new InvalidOperationException($"{at} names '{key}', a field a member does not have.");
        foreach (var field in MemberFields) if (m[field] is null) throw Missing($"{at}.{field}");

        var cls = String(m, "class", at);
        if (!MemberClasses.Contains(cls) && cls != SiteClass)
            throw new InvalidOperationException(
                $"{at} is of class '{cls}'; a pass marker names {string.Join(" and ", MemberClasses)} members, or one {SiteClass}, only.");

        var record = Object(m, "record", $"{at}.record");
        var recordRef = new PassMarkerRecordReference(String(record, "bucket", $"{at}.record"), String(record, "key", $"{at}.record"),
            String(record, "versionId", $"{at}.record", label: "record.versionId"));

        var id = Object(m, "identity", $"{at}.identity");
        BuildRecordIdentity identity;
        if (cls == "image")
        {
            var digest = String(id, "digest", $"{at}.identity");
            if (String(id, "kind", $"{at}.identity") != "image" || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
                throw new InvalidOperationException($"{at}.identity is not an image named by its sha256 digest.");
            identity = new BuildRecordIdentity("image", Digest: digest);
        }
        else
        {
            if (String(id, "kind", $"{at}.identity") != "bundle") throw new InvalidOperationException($"{at}.identity is not a bundle.");
            identity = new BuildRecordIdentity("bundle", Bucket: String(id, "bucket", $"{at}.identity"), Key: String(id, "key", $"{at}.identity"),
                VersionId: String(id, "versionId", $"{at}.identity", label: "identity.versionId"), Sha256: String(id, "sha256", $"{at}.identity"));
        }

        var built = Object(m, "builtFrom", $"{at}.builtFrom");
        var packages = Object(built, "packages", $"{at}.builtFrom.packages")
            .ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? throw Missing($"{at}.builtFrom.packages.{p.Key}"));
        var builtFrom = new BuildRecordBuiltFrom(String(built, "repo", $"{at}.builtFrom"), String(built, "commit", $"{at}.builtFrom"),
            String(built, "lane", $"{at}.builtFrom"), packages, built["ref"]?.GetValue<string>());

        return new PassMarkerMember(cls, String(m, "repo", at), recordRef, identity, String(m, "version", at), builtFrom,
            Reference(Object(m, "deploy", $"{at}.deploy"), $"{at}.deploy", label: "deploy"));
    }

    private static PassMarkerReference Reference(JsonObject o, string at, string? label = null)
        => new(String(o, "key", at), String(o, "versionId", at, label: label is null ? null : $"{label}.versionId"));

    private static JsonObject Object(JsonObject o, string field, string at)
        => o[field] as JsonObject ?? throw new InvalidOperationException($"{at} is missing or is not an object.");

    private static string String(JsonObject o, string field, string? at = null, string? label = null)
    {
        var value = o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(value))
            throw Missing(label is not null ? $"{at}: {label}" : at is null ? field : $"{at}.{field}");
        return value;
    }

    private static InvalidOperationException Missing(string field)
        => new($"pass marker has no {field}; schema 1 requires it, and a reader cannot tell its absence from a writer that forgot.");
}
