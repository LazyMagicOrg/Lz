using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
/// <param name="Ref">
/// The git ref the build ran on, <c>GITHUB_REF</c> — <c>refs/heads/main</c> for a push to main. OPTIONAL, and the
/// one field of schema 1 that is: records written before 2026-09-13 carry none, and a record is immutable, so a
/// reader tolerates its absence rather than refusing every earlier build. What absence may NOT do is pass a check
/// that asks for a ref — the trigger starts only records whose ref it names (DecoupledCd.md §14.3, P2 stage D2).
/// </param>
public sealed record BuildRecordBuiltFrom(
    string Repo,
    string Commit,
    string Lane,
    IReadOnlyDictionary<string, string> Packages,
    string? Ref = null);

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
/// <param name="Intake">
/// What the intake checked (DecoupledCd.md §4.3, §15.4), present on every record the intake writes and on none that
/// today's push path writes. Its presence also changes the record's KEY, which then carries the run attempt
/// (<see cref="BuildRecordFormat.KeyFor"/>).
/// </param>
public sealed record BuildRecord(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("builtFrom")] BuildRecordBuiltFrom BuiltFrom,
    [property: JsonPropertyName("identity")] BuildRecordIdentity Identity,
    [property: JsonPropertyName("builtAt")] string BuiltAt,
    [property: JsonPropertyName("builtBy")] string BuiltBy,
    [property: JsonPropertyName("workflowRunId")] string WorkflowRunId,
    [property: JsonPropertyName("intake")] BuildRecordIntake? Intake = null);

/// <summary>
/// The intake's account of one artifact (DecoupledCd.md §4.3, §15.4, V-12, V-19): the run attempt and job it checked,
/// the digest GitHub reported for the artifact, and Gate 1's verdict - a record is written only on a pass, so the
/// parser refuses any other result. Image records also carry what the AipApi check and the supply chain need.
/// </summary>
/// <param name="RunId">The run; equal to the record's <c>workflowRunId</c>.</param>
/// <param name="RunAttempt">The attempt, from 1. Part of the record's key: each attempt is its own artifact.</param>
/// <param name="JobId">The job that built the artifact.</param>
/// <param name="WorkflowRef">The workflow file the run ran, <c>{owner}/{repo}/.github/workflows/{file}@{ref}</c>.</param>
/// <param name="GithubDigest">The <c>digest</c> GitHub's artifacts API reported, which the fetch compared the bytes with.</param>
/// <param name="Gate1">Gate 1's verdict for exactly these bytes.</param>
/// <param name="ServiceVersion">Image records: the build's NBGV version, for test's AipApi check (§15.5, T-13).</param>
/// <param name="BaseImageDigest">Image records: the pinned base image the build started from (V-19).</param>
/// <param name="Sbom">Image records: the SBOM, stored write-once beside the record (V-19).</param>
/// <param name="Environment">Site records only (V-24): the environment the bundle was built for, whose domain it carries.</param>
public sealed record BuildRecordIntake(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("runAttempt")] int RunAttempt,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("workflowRef")] string WorkflowRef,
    [property: JsonPropertyName("githubDigest")] string GithubDigest,
    [property: JsonPropertyName("gate1")] BuildRecordGate1 Gate1,
    [property: JsonPropertyName("serviceVersion")] string? ServiceVersion = null,
    [property: JsonPropertyName("baseImageDigest")] string? BaseImageDigest = null,
    [property: JsonPropertyName("sbom")] BuildRecordObjectRef? Sbom = null,
    [property: JsonPropertyName("environment")] string? Environment = null);

/// <param name="Result">Always <c>passed</c> on a record: nothing is recorded for a build that failed Gate 1.</param>
/// <param name="Evidence">The key of the verdict in the intake's evidence store.</param>
/// <param name="Lists">A <c>tests</c> record only: the lists Gate 1 recorded from these bytes under Gate 2's fixed filters,
/// which Gate 2's verdict holds what ran to (DecoupledCd.md §15.5). Absent from every other record, and from a tests record
/// written before 2026-10-23.</param>
public sealed record BuildRecordGate1(
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("lists")] BuildRecordGate1Lists? Lists = null);

/// <summary>Gate 1's lists of a test bundle: the e2e methods by Gate 2 trait, and, for the browser tests, the <c>@gate</c> count.</summary>
public sealed record BuildRecordGate1Lists(
    [property: JsonPropertyName("precondition")] IReadOnlyList<string> Precondition,
    [property: JsonPropertyName("blocking")] IReadOnlyList<string> Blocking,
    [property: JsonPropertyName("advisory")] IReadOnlyList<string> Advisory,
    [property: JsonPropertyName("gateCount")] int? GateCount = null);

/// <summary>An S3 object named by key AND version, so it names those bytes and no later ones.</summary>
public sealed record BuildRecordObjectRef(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("versionId")] string VersionId);

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

    /// <summary>
    /// Whether a class's artifact is an image named by digest (<c>image</c>, <c>tooling</c>) rather than a bundle
    /// named by object version. One definition for the format and for the build-side planner, which gives these
    /// classes a registry, a signing profile and an ECR push role.
    /// </summary>
    public static bool IsImageClass(string cls) => ImageClasses.Contains(cls, StringComparer.Ordinal);

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

        return NameFor(record, ".json");
    }

    /// <summary>
    /// The artifact store key for a bundle: <c>{class}/{repo}/{builtAt}-{runId}.zip</c>, the record's own key with
    /// <c>.zip</c> for <c>.json</c> (DecoupledCd.md §12, P4 stage A). One stem for both objects of a build, under the
    /// same <c>{class}/{repo}/</c> prefix the writer's role may write in each store, so the zip is found from its
    /// record without a listing, and a record naming any other key is refused by <see cref="Parse"/>.
    /// </summary>
    public static string BundleKeyFor(BuildRecord record)
    {
        Require(record.Class, "class");
        Require(record.BuiltFrom?.Repo, "builtFrom.repo");
        Require(record.BuiltAt, "builtAt");
        Require(record.WorkflowRunId, "workflowRunId");

        return NameFor(record, ".zip");
    }

    private static string NameFor(BuildRecord record, string extension)
    {
        // ':' is legal in an S3 key but awkward in URLs and shell quoting, and a timestamp is the
        // one field guaranteed to contain it.
        var stamp = record.BuiltAt.Replace(":", "").Replace("-", "");

        // THE RUN ATTEMPT, ON AN INTAKE RECORD ONLY (DecoupledCd.md §15.4). Each attempt of a run is its own artifact,
        // built again, so two attempts must not share a key - in a write-once store the second would be refused. A record
        // written by today's push path has no intake block and keeps its {builtAt}-{runId} stem, so every record and zip
        // already in the stores still has the key it was written under.
        if (record.Intake is { } intake)
        {
            if (intake.RunAttempt < 1)
                throw new InvalidOperationException(
                    $"build record's intake.runAttempt is {intake.RunAttempt}; GitHub numbers attempts from 1, and the " +
                    "attempt is part of the key.");
            // A SITE'S RECORD (V-24) sits under its environment, so the records one run writes for each environment's bundle
            // keep apart and a route can tell its own; the writer's prefix stays the repository's.
            var environment = record.Class == SiteClass
                ? $"{RequireEnvironment(intake.Environment)}/"
                : "";
            return $"{PrefixFor(record.Class, record.BuiltFrom!.Repo)}{environment}{stamp}-{record.WorkflowRunId}-{intake.RunAttempt}{extension}";
        }

        return $"{PrefixFor(record.Class, record.BuiltFrom!.Repo)}{stamp}-{record.WorkflowRunId}{extension}";
    }

    /// <summary>
    /// A record's <c>builtAt</c> under v5: the run's <c>created_at</c> from GitHub's workflow-runs API, in the record's
    /// fixed-width RFC 3339 UTC form (DecoupledCd.md §15.8 item 11, decided 2026-10-20). Every attempt and every retry of
    /// one run computes the same value, so a run's attempts tie (and <c>runAttempt</c> breaks the tie), and a re-run of
    /// an older run stays older than a later push's build: Prepare, DeployBundle and the promotion poller never take it
    /// over a newer one. Nothing the repository writes can set it.
    /// </summary>
    public static string BuiltAtFor(DateTimeOffset runCreatedAt)
        => runCreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The prefix one repository's writer role owns for one class: <c>{class}/{repo}/</c>,
    /// lowercased. Also the artifact store's layout, so both stores scope writers identically.
    /// </summary>
    public static string PrefixFor(string cls, string repo)
        => $"{cls.ToLowerInvariant()}/{repo.ToLowerInvariant().Trim('/')}/";

    /// <summary>The class of a site's bundles (DecoupledCd.md V-24), one per environment, each built for that environment's domain.</summary>
    public const string SiteClass = "site";

    /// <summary>
    /// An environment's name as a site record spells it, a directory of its key: lowercase, as lz's config file names do.
    /// <c>SiteArtifacts.EnvironmentPattern</c> in Lz.Core says the same, which the deployer's package cannot compile; a test
    /// holds the two equal.
    /// </summary>
    public const string EnvironmentPattern = "^[a-z0-9][a-z0-9-]{0,31}$";

    /// <summary>The environment a site record's bundle was built for; null for every other class.</summary>
    public static string? EnvironmentOf(BuildRecord record) => record.Class == SiteClass ? record.Intake?.Environment : null;

    private static string RequireEnvironment(string? environment)
    {
        if (environment is null)
            throw new InvalidOperationException(
                "a site record names no intake.environment; each environment's bundle carries that environment's domain, so its " +
                "record says which one it is.");
        if (!Regex.IsMatch(environment, EnvironmentPattern))
            throw new InvalidOperationException($"intake.environment '{environment}' is not an environment's name ({EnvironmentPattern}).");
        return environment;
    }

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
    /// <param name="json">The record as stored.</param>
    /// <param name="artifactStore">
    /// The artifact store's name (<c>PipelineBootstrapPlanner.ArtifactStoreFor</c>). Required to read a BUNDLE record,
    /// whose identity must name that bucket; an image record needs none, so the readers of image records pass nothing.
    /// </param>
    public static BuildRecord Parse(string json, string? artifactStore = null)
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

        // A SITE RECORD (V-24) is the intake's only, and names its environment; no other record names one. Checked before the
        // identity, whose key the environment is part of.
        if (r.Class == SiteClass)
        {
            if (r.Intake is null)
                throw new InvalidOperationException(
                    "a site record is written by the intake only, and this one has no intake block; nothing else was ever " +
                    "entitled to write one.");
            RequireEnvironment(r.Intake.Environment);
        }
        else if (r.Intake?.Environment is not null)
            throw new InvalidOperationException(
                $"intake.environment is '{r.Intake.Environment}' on a '{r.Class}' record; only a site record names intake.environment.");

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
            RequireTrustworthyBundle(r, artifactStore);
        }

        if (r.Intake is not null)
            RequireTrustworthyIntake(r);

        return r;
    }

    private static readonly Regex Sha256Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// What an intake record must say before anything acts on it (DecoupledCd.md §4.3, §15.4). Checked here, before the
    /// first one is written, for the reason <see cref="RequireTrustworthyBundle"/> gives.
    /// </summary>
    private static void RequireTrustworthyIntake(BuildRecord r)
    {
        var i = r.Intake!;
        Require(i.RunId, "intake.runId");
        Require(i.JobId, "intake.jobId");
        Require(i.WorkflowRef, "intake.workflowRef");
        Require(i.GithubDigest, "intake.githubDigest");

        // ONE RUN, NAMED ONCE. The key carries workflowRunId; a record whose intake names another run describes an
        // artifact its key does not.
        if (!string.Equals(i.RunId, r.WorkflowRunId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"intake.runId is '{i.RunId}' but workflowRunId is '{r.WorkflowRunId}'. A record names one run.");

        if (i.RunAttempt < 1)
            throw new InvalidOperationException(
                $"intake.runAttempt is {i.RunAttempt}; GitHub numbers attempts from 1, and the attempt is part of the key.");

        if (!Sha256Digest.IsMatch(i.GithubDigest))
            throw new InvalidOperationException(
                $"intake.githubDigest is '{i.GithubDigest}', not 'sha256:' and 64 lowercase hex characters, the form " +
                "GitHub's artifacts API reports and the fetch compared the bytes with.");

        // ONLY A PASS IS RECORDED (V-12): the intake pushes and records nothing for a build that failed Gate 1, so a
        // record carrying any other verdict was written by something that is not the intake.
        if (i.Gate1 is null)
            throw new InvalidOperationException("build record has an intake block but no intake.gate1.");
        if (!string.Equals(i.Gate1.Result, "passed", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"intake.gate1.result is '{i.Gate1.Result}'. The intake records a build only after Gate 1 passed it.");
        Require(i.Gate1.Evidence, "intake.gate1.evidence");

        // IMAGE RECORDS carry what the AipApi check and the supply chain need (§15.5, T-13, V-19). Test's Verify refuses
        // an image member without serviceVersion, so a record missing it would be deployable in dev and never promoted.
        if (IsImageClass(r.Class))
        {
            Require(i.ServiceVersion, "intake.serviceVersion");
            Require(i.BaseImageDigest, "intake.baseImageDigest");
            if (!Sha256Digest.IsMatch(i.BaseImageDigest!))
                throw new InvalidOperationException(
                    $"intake.baseImageDigest is '{i.BaseImageDigest}', not a sha256 digest. A base image named by tag is " +
                    "a mutable pointer (V-19).");
            if (i.Sbom is null)
                throw new InvalidOperationException("an image record's intake block has no sbom (V-19).");
            Require(i.Sbom.Key, "intake.sbom.key");
            Require(i.Sbom.VersionId, "intake.sbom.versionId");
            if (string.Equals(i.Sbom.VersionId, "null", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "intake.sbom.versionId is S3's \"null\", which the next write to its key replaces.");
        }
    }

    private static readonly Regex LowercaseHexSha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// What a bundle identity must also be before anything acts on it (DecoupledCd.md §12, P4 stage A). Checked in the
    /// parser, before the first bundle record exists, because a written record is immutable: a check added later has
    /// to keep admitting every record written before it — the argument §4.3 made for <c>builtFrom</c>.
    /// </summary>
    private static void RequireTrustworthyBundle(BuildRecord r, string? artifactStore)
    {
        // THE ARTIFACT STORE, EXACTLY. A reader that cannot name it cannot tell a record naming it from one naming any
        // other bucket it can read, the build-record store among them.
        if (string.IsNullOrWhiteSpace(artifactStore))
            throw new InvalidOperationException(
                "a bundle record was read without the artifact store to check it against. Its identity names a bucket, " +
                "and only the artifact store may hold a bundle, so a reader of bundle records must say which bucket " +
                "that is (PipelineBootstrapPlanner.ArtifactStoreFor).");

        if (!string.Equals(r.Identity.Bucket, artifactStore, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"identity.bucket is '{r.Identity.Bucket}', not the artifact store '{artifactStore}'. A bundle lives in " +
                "the artifact store and nowhere else.");

        // THE MIRRORED KEY, EXACTLY: under the record's own {class}/{repo}/ prefix, the one its writer's role may write,
        // with the record's own stem. A key that were merely under the prefix could name another build's zip.
        var expectedKey = BundleKeyFor(r);
        if (!string.Equals(r.Identity.Key, expectedKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"identity.key is '{r.Identity.Key}'; a bundle's key mirrors its record's: '{expectedKey}'. Any other " +
                "key names an object this build did not write under its own name, or one outside the prefix its role " +
                "may write.");

        // S3 reports the version of an object written without versioning as the string "null", and the next write to
        // that key replaces the object: the mutable pointer again, dressed as a version id.
        if (string.Equals(r.Identity.VersionId, "null", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "identity.versionId is S3's \"null\", the version of an object written without versioning, which the " +
                "next write to its key replaces. The artifact store is versioned, so a real bundle never has it.");

        if (!LowercaseHexSha256.IsMatch(r.Identity.Sha256!))
            throw new InvalidOperationException(
                $"identity.sha256 is '{r.Identity.Sha256}', which is not 64 lowercase hex characters. The record states " +
                "the zip's SHA-256 in one spelling, so a reader compares it as a string; S3's own checksum header is " +
                "base64 and is not this field.");
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"build record is missing required field '{field}'.");
    }
}
