using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lz.Aws.Pipeline;

/// <summary>
/// What an artifact IS, in one of the only two shapes this system has (DecoupledCd.md §2): an
/// image named by digest, or a bundle named by its S3 object version.
/// </summary>
/// <param name="Kind"><c>image</c> or <c>bundle</c>.</param>
/// <param name="Digest">Images: <c>sha256:…</c>, the manifest digest. Null for bundles.</param>
/// <param name="Bucket">Bundles: the artifact store. Null for images.</param>
/// <param name="Key">Bundles: the object key. Null for images.</param>
/// <param name="VersionId">Bundles: the object VERSION — the identity itself, not the key.</param>
/// <param name="Sha256">Bundles: checksum of the zip, so integrity does not rest on S3 alone.</param>
public sealed record BuildRecordIdentity(
    string Kind,
    string? Digest = null,
    string? Bucket = null,
    string? Key = null,
    string? VersionId = null,
    string? Sha256 = null);

/// <summary>
/// The inputs that determined the bytes (DecoupledCd.md §4.3). Absorbs repo and commit rather than
/// sitting beside them: the block is the complete set of inputs, not a supplement to a partial one.
/// </summary>
/// <param name="Repo">The producing repository, <c>owner/name</c>.</param>
/// <param name="Commit">Full SHA.</param>
/// <param name="Lane"><c>published</c> or <c>local</c>. Verify refuses anything but published.</param>
/// <param name="Packages">
/// Resolved FIRST-PARTY package versions, from <c>project.assets.json</c> — resolved, never the
/// declared pins, because under CPM a bare Version is a floor and the gap is silent. REQUIRED, with
/// an empty map meaning "this build consumed none"; absence is a fault.
/// </param>
public sealed record BuildRecordBuiltFrom(
    string Repo,
    string Commit,
    string Lane,
    IReadOnlyDictionary<string, string> Packages);

/// <summary>
/// One build record: <em>this artifact exists, and here is its provenance</em>.
///
/// <para>It is the uniform trigger for the whole pipeline (§4.3) — an ECR push event exists for one
/// of the eight classes, so it cannot be the general notification, and the two general event
/// candidates are both best-effort delivery rather than a queue. The record is durable, enumerable
/// and evidential where an event is none of those.</para>
///
/// <para>IT IS DELIBERATELY NOT SIGNED, and P1's line "the build-record format and its signing key"
/// was wrong to imply otherwise — as was §4.4 step 1's "verifies its signature". §4.2 states the
/// reason and is the passage that argues rather than asserts: the role that writes it holds
/// <c>s3:PutObject</c> and no signing action of any kind, so a signature could only be made by the
/// same GitHub-assumable identity that writes the record, and a compromised role would forge it
/// just as easily. Authority rests on prefix-scoped write-once storage plus CloudTrail. The IMAGE
/// signature is a different thing and does real work: ECR managed signing happens at push under a
/// profile no human permission set can use.</para>
/// </summary>
/// <param name="Schema">
/// Format version. Present from the first record ever written, for the same reason
/// <see cref="BuiltFrom"/> is: a field added after records exist is absent from all of them, so a
/// reader either rejects the old ones or tolerates absence forever. With a version, a later format
/// can be recognised rather than guessed at.
/// </param>
/// <param name="Class">One of DecoupledCd.md §3's six class names.</param>
/// <param name="BuiltAt">RFC 3339 UTC, and the sort key inside a prefix — see <see cref="BuildRecordFormat"/>.</param>
/// <param name="BuiltBy">The GitHub identity that ran the build, for the human reading the store.</param>
/// <param name="WorkflowRunId">The run, so a record can be traced back to its logs.</param>
public sealed record BuildRecord(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("builtFrom")] BuildRecordBuiltFrom BuiltFrom,
    [property: JsonPropertyName("identity")] BuildRecordIdentity Identity,
    [property: JsonPropertyName("builtAt")] string BuiltAt,
    [property: JsonPropertyName("builtBy")] string BuiltBy,
    [property: JsonPropertyName("workflowRunId")] string WorkflowRunId);

/// <summary>
/// Serialising, parsing and KEYING build records — the format frozen before the first record
/// exists, which is the only moment it can be (DecoupledCd.md punchlist P1).
/// </summary>
public static class BuildRecordFormat
{
    /// <summary>The only schema version that has ever existed.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Classes whose identity is an image digest; everything else is a bundle.</summary>
    private static readonly string[] ImageClasses = { "image", "tooling" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// The S3 key for a record: <c>{class}/{repo}/{builtAt}-{runId}.json</c>, lowercased.
    ///
    /// <para>CLASS FIRST, THEN REPOSITORY, and that order is doing two jobs at once. The writer
    /// roles are prefix-scoped — a role may PutObject under <c>{class}/{repo}/</c> and nowhere else
    /// — so one compromised build repository cannot write another's records. And §4.3 asks for a
    /// key that "sorts by time within its class", which class-first gives: listing
    /// <c>image/</c> returns that class's records, ordered by repository and then, because
    /// <c>builtAt</c> is RFC 3339 UTC with fixed width, by time.</para>
    ///
    /// <para>ONE HONEST LIMIT: with more than one repository in a class the ordering is by
    /// repository first and time second, not strictly by time. Prefix-scoped writing is a security
    /// property and the ordering is a convenience, so the security property wins; a reconciler that
    /// needs global time order sorts in memory, over a store that holds one small object per build.</para>
    /// </summary>
    public static string KeyFor(BuildRecord record)
    {
        Require(record.Class, "class");
        Require(record.BuiltFrom?.Repo, "builtFrom.repo");
        Require(record.BuiltAt, "builtAt");
        Require(record.WorkflowRunId, "workflowRunId");

        // ':' is legal in an S3 key but awkward in URLs and shell quoting, and a timestamp is the
        // one field guaranteed to contain it.
        var stamp = record.BuiltAt.Replace(":", "").Replace("-", "");
        return $"{PrefixFor(record.Class, record.BuiltFrom!.Repo)}{stamp}-{record.WorkflowRunId}.json";
    }

    /// <summary>
    /// The prefix one repository's writer role owns for one class: <c>{class}/{repo}/</c>,
    /// lowercased. Also the artifact store's layout, so both stores scope writers identically.
    /// </summary>
    public static string PrefixFor(string cls, string repo)
        => $"{cls.ToLowerInvariant()}/{repo.ToLowerInvariant().Trim('/')}/";

    /// <summary>Serialise a record for its one and only write.</summary>
    public static string Serialize(BuildRecord record) => JsonSerializer.Serialize(record, Json);

    /// <summary>
    /// Parse a record, REFUSING anything it cannot fully understand.
    ///
    /// <para>Fail-closed throughout, because the alternative is a deployer acting on a record it
    /// half-read. Every refusal below is a state that would otherwise reach <c>Verify</c> as a
    /// plausible-looking object: a missing <c>builtFrom</c> silently defaulting to "no packages", a
    /// bundle identity with no version id deploying whatever the key currently points at, or a
    /// record from a future schema read with today's assumptions.</para>
    /// </summary>
    public static BuildRecord Parse(string json)
    {
        BuildRecord? r;
        try
        {
            r = JsonSerializer.Deserialize<BuildRecord>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"build record is not valid JSON: {ex.Message}", ex);
        }

        if (r is null)
            throw new InvalidOperationException("build record is empty.");

        if (r.Schema != CurrentSchema)
            throw new InvalidOperationException(
                $"build record declares schema {r.Schema}; this build of lz understands " +
                $"{CurrentSchema}. Refusing rather than reading a different format with today's " +
                "assumptions — a record is immutable, so the writer cannot be asked to try again.");

        Require(r.Class, "class");
        Require(r.BuiltAt, "builtAt");
        Require(r.BuiltBy, "builtBy");
        Require(r.WorkflowRunId, "workflowRunId");

        // builtFrom is REQUIRED, and "packages present but empty" is a statement while "builtFrom
        // absent" is a fault. That distinction is the whole reason the field was specified before
        // any record existed (DecoupledCd.md §4.3).
        if (r.BuiltFrom is null)
            throw new InvalidOperationException(
                "build record has no builtFrom. It is required for every class — a class that " +
                "consumes no first-party packages says so with an empty `packages` map. Absence " +
                "cannot be distinguished from a writer that forgot, so it is refused.");

        Require(r.BuiltFrom.Repo, "builtFrom.repo");
        Require(r.BuiltFrom.Commit, "builtFrom.commit");
        Require(r.BuiltFrom.Lane, "builtFrom.lane");

        if (r.BuiltFrom.Packages is null)
            throw new InvalidOperationException(
                "build record has builtFrom but no `packages`. Use an empty map to mean \"this " +
                "build consumed no first-party packages\"; omitting it is a fault.");

        if (r.Identity is null)
            throw new InvalidOperationException("build record has no identity.");

        var expected = ImageClasses.Contains(r.Class) ? "image" : "bundle";
        if (r.Identity.Kind != expected)
            throw new InvalidOperationException(
                $"build record class '{r.Class}' carries a '{r.Identity.Kind}' identity; expected " +
                $"'{expected}'. Class and identity kind are not independent — an image class named " +
                "by an object version, or a bundle named by a digest, is a record nothing can deploy.");

        if (r.Identity.Kind == "image")
        {
            Require(r.Identity.Digest, "identity.digest");
            if (!r.Identity.Digest!.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"identity.digest is '{r.Identity.Digest}', which is not a sha256: digest. A tag " +
                    "is a mutable pointer and cannot be an identity.");
        }
        else
        {
            Require(r.Identity.Bucket, "identity.bucket");
            Require(r.Identity.Key, "identity.key");
            // THE VERSION IS THE IDENTITY, not the key. Without it a deployer would fetch whatever
            // the key currently points at, which is the mutable-pointer problem digests exist to
            // avoid, one storage layer down.
            Require(r.Identity.VersionId, "identity.versionId");
            Require(r.Identity.Sha256, "identity.sha256");
        }

        return r;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"build record is missing required field '{field}'.");
    }
}
