using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  THE ERRORS THE STATE MACHINE BRANCHES ON
//
//  These classes are named WITHOUT the usual "Exception" suffix, deliberately, because the name is
//  a wire contract. The .NET Lambda runtime reports a thrown exception's type as
//  `exception.GetType().Name` (Amazon.Lambda.RuntimeSupport, ExceptionInfo.cs, read from source),
//  and Step Functions matches Retry and Catch on exactly that string. The definition refers to them
//  by nameof(), so renaming a class renames the match with it instead of silently breaking a retry.
// ---------------------------------------------------------------------------------------------

/// <summary>Verify refused the record. Not retried: nothing about a refusal changes by waiting.</summary>
public sealed class DeployRefused : Exception
{
    public IReadOnlyList<VerifyRefusal> Refusals { get; }

    public DeployRefused(IReadOnlyList<VerifyRefusal> refusals)
        : base(string.Join(" | ", refusals.Select(r => $"[{r.Check}] {r.Reason}")))
        => Refusals = refusals;

    public DeployRefused(string check, string reason) : this(new[] { new VerifyRefusal(check, reason) }) { }
}

/// <summary>The image scan has not finished. RETRIED by the definition, a bounded number of times.</summary>
public sealed class ScanNotYetAvailable(string message) : Exception(message);

/// <summary>
/// The record passed every check, but its image is not in this account's registry yet. RETRIED by the definition,
/// a bounded number of times (DecoupledCd.md §14.1 item 2): a record lands seconds after its push and replication
/// takes six to seven seconds more, so an execution the record starts often looks before the replica exists.
/// Thrown only after provenance, class, lane and the repository allowlist have passed — a record with no right to
/// name an image is refused at once, never waited on.
/// </summary>
public sealed class ImageNotYetReplicated(string message) : Exception(message);

/// <summary>The roll is under way. RETRIED by the definition; its retry limit is the rollout timeout.</summary>
public sealed class RolloutStillRolling(string message) : Exception(message);

/// <summary>The roll finished or failed and the deployed digest is not what runs. Not retried.</summary>
public sealed class RolloutNotDeployed(string message) : Exception(message);

/// <summary>
/// The service already runs a NEWER build than this record's, so deploying it would put an older build back
/// (DecoupledCd.md P2 stage D2). Not retried: the newer build is not going to get older. Thrown by Prepare, before it
/// registers anything, unless the execution input says <c>"allowOlderBuild": true</c>.
/// </summary>
public sealed class DeploySuperseded(string message) : Exception(message);

/// <summary>
/// Another execution holds the app's deploy lease (<see cref="BundleMarker"/>), or took it between this one's read and
/// its claim. RETRIED by the definition, for longer than a lease lasts: a deploy that died holding one is waited out.
/// Thrown before this execution writes anything to the app's bucket.
/// </summary>
public sealed class BundleDeployInProgress(string message) : Exception(message);

/// <summary>
/// This execution's claim on the app's lease was gone when it came to release it: the lease expired mid-deploy and another
/// execution took it, so both wrote to the bucket. Not retried — what the bucket holds is unknown, and the newer build's
/// own deploy, or a new one, is what settles it.
/// </summary>
public sealed class BundleDeployRaced(string message) : Exception(message);

/// <summary>A CloudFront invalidation this execution created has not completed. RETRIED by the definition, a bounded number of times.</summary>
public sealed class BundleInvalidationInProgress(string message) : Exception(message);

/// <summary>What the app's bucket holds is not the bundle this execution deployed. Not retried.</summary>
public sealed class BundleNotDeployed(string message) : Exception(message);

/// <summary>
/// An execution's name for one deploy request, shared by the planner and the start function that names the
/// executions it starts (DecoupledCd.md §5.1).
/// </summary>
public static class DeployExecution
{
    /// <summary>
    /// <c>req-{request id}-{attempt}</c>.
    ///
    /// <para>THE NAME IS THE IDEMPOTENCY KEY, which is what absorbs S3's at-least-once, unordered
    /// delivery: a duplicate event produces the same name and the same input, and
    /// <c>StartExecution</c> is idempotent for a Standard workflow in exactly that case. A reused
    /// name with DIFFERENT input returns <c>ExecutionAlreadyExists</c> instead — an error worth
    /// getting, because it means two different things claimed one request id.</para>
    ///
    /// <para>Step Functions forbids <c>:</c> and <c>/</c> in execution names, which is why a raw
    /// digest can never be one — <c>sha256:…</c> contains the first. This refuses rather than
    /// sanitising: silently rewriting an id would break the idempotency the name exists to provide,
    /// since two ids could sanitise to one name.</para>
    /// </summary>
    public static string Name(string requestId, int attempt)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("execution name needs a request id.");

        if (attempt < 1)
            throw new InvalidOperationException(
                $"attempt must be 1 or greater; got {attempt}. Attempt 0 and attempt 1 would be two " +
                "names for one try.");

        foreach (var c in new[] { ':', '/', ' ', '\\', '?', '*', '<', '>', '|', '"', '#' })
        {
            if (requestId.Contains(c))
                throw new InvalidOperationException(
                    $"request id '{requestId}' contains '{c}', which Step Functions forbids in an " +
                    "execution name. Refusing rather than sanitising: two ids that sanitised to one " +
                    "name would collapse into a single execution and the second deploy would " +
                    "silently never run.");
        }

        var name = $"req-{requestId}-{attempt}";
        if (name.Length > 80)
            throw new InvalidOperationException(
                $"execution name '{name}' is {name.Length} characters; Step Functions allows 80.");

        return name;
    }
}

/// <summary>Where a build record is stored.</summary>
public sealed record RecordLocation(string Bucket, string Key);

/// <summary>The service a class-1 deploy rolls.</summary>
/// <param name="Container">The container in the task definition whose image is replaced.</param>
/// <param name="Repository">The repository, in this account's registry, the image is pulled from.</param>
public sealed record DeployTarget(string Cluster, string Service, string Container, string Repository);

/// <summary>
/// What starts a class-1 execution: which record, and which service it rolls.
///
/// <para>THE TARGET IS NAMED IN THE INPUT, not derived, because the deployer has no tenant list — a
/// service is named per tenant and the deployer deliberately does not know the tenants (the same
/// reason its PassRole grant is a pattern). Stage D's trigger is what builds this input. What the
/// deployer DOES check is that the named repository is one of this environment's pipeline
/// repositories, so an input cannot point it at an arbitrary image.</para>
/// </summary>
public sealed record DeployerInput(RecordLocation Record, DeployTarget Target)
{
    /// <summary>The top-level fields an execution's input carries. Everything else a state reads, a state wrote.</summary>
    public static readonly string[] InputFields = { "record", "target" };

    /// <summary>
    /// Fields an input MAY carry, and only a person starting an execution by hand would write: the trigger never does.
    /// <c>allowOlderBuild</c> deploys a record older than the build the service runs (<see cref="DeploySuperseded"/>).
    /// </summary>
    public static readonly string[] OptionalInputFields = { "allowOlderBuild" };

    /// <summary>
    /// Whether the input asks to deploy an older build on purpose. Only a JSON <c>true</c> does; absent is false, and
    /// anything else is refused rather than read as a yes or a no — a quoted "true" is exactly the kind of override
    /// that should not be guessed at.
    /// </summary>
    public static bool AllowsOlderBuild(JsonObject state)
    {
        var node = state["allowOlderBuild"];
        if (node is null) return false;

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new DeployRefused("allowOlderBuild",
                $"allowOlderBuild is {node.ToJsonString()}; it must be the JSON boolean true or false."),
        };
    }

    /// <summary>
    /// Every Lambda state receives <c>{ "state": $, "executionName": $$.Execution.Name }</c>: the
    /// execution's state, plus the name that makes evidence keys unique. Anything else is refused.
    /// </summary>
    public static (string ExecutionName, JsonObject State) Unwrap(string payload)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new DeployRefused("input", $"the state payload is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject root
            || root["state"] is not JsonObject state
            || root["executionName"]?.GetValueKind() != JsonValueKind.String)
            throw new DeployRefused("input",
                "the payload must be { \"state\": {...}, \"executionName\": \"...\" }, which is what " +
                "every Lambda state's Parameters block passes.");

        return (DeployEvidence.RequireSafeExecutionName(root["executionName"]!.GetValue<string>()), state);
    }

    /// <summary>
    /// Only the record's location, which every class's input names in the same shape. Verify reads the record before it
    /// knows the class, and the class decides what the target must name (P4 stage C).
    /// </summary>
    public static RecordLocation RecordFrom(JsonObject state)
    {
        string? Field(string name)
            => state["record"] is JsonObject o && o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>()
                : null;

        var bucket = Field("bucket");
        var key = Field("key");
        var missing = new[] { ("record.bucket", bucket), ("record.key", key) }
            .Where(f => string.IsNullOrWhiteSpace(f.Item2)).Select(f => f.Item1).ToList();

        if (missing.Count > 0)
            throw new DeployRefused("input",
                $"the execution input is missing {string.Join(", ", missing)}. A deploy names the record it deploys; nothing " +
                "is defaulted.");

        return new RecordLocation(bucket!, key!);
    }

    /// <summary>
    /// A client bundle's target: the bucket it deploys into, <c>{"target": {"bucket": "…"}}</c>. Everything else about the
    /// target — its base path, its distributions — is configuration, which Verify resolves and checks the bucket against.
    /// </summary>
    public static string BundleBucketFrom(JsonObject state)
    {
        var bucket = state["target"] is JsonObject o && o["bucket"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : null;

        return string.IsNullOrWhiteSpace(bucket)
            ? throw new DeployRefused("input",
                "the execution input is missing target.bucket. A client bundle's deploy names the web-app bucket it deploys " +
                "into; nothing is defaulted.")
            : bucket;
    }

    /// <summary>Read the record location and target from an execution's state. Refuses anything missing.</summary>
    public static DeployerInput From(JsonObject state)
    {
        var missing = new List<string>();

        string Field(string parent, string name)
        {
            var value = state[parent] is JsonObject o
                        && o[name] is JsonValue v
                        && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>()
                : null;

            if (string.IsNullOrWhiteSpace(value)) missing.Add($"{parent}.{name}");
            return value ?? "";
        }

        var input = new DeployerInput(
            new RecordLocation(Field("record", "bucket"), Field("record", "key")),
            new DeployTarget(
                Field("target", "cluster"), Field("target", "service"),
                Field("target", "container"), Field("target", "repository")));

        if (missing.Count > 0)
            throw new DeployRefused("input",
                $"the execution input is missing {string.Join(", ", missing)}. A deploy names the record " +
                "it deploys and the service it rolls; nothing is defaulted.");

        return input;
    }
}

/// <summary>
/// The environment variables that configure each deployer function, and how they are read.
///
/// <para>ONE DEFINITION FOR BOTH SIDES: <see cref="DeployerPlanner"/> writes these names and the
/// handlers read them, so the two cannot drift apart — a function configured with a variable its
/// handler never reads would run on a default nobody chose.</para>
///
/// <para>A MISSING VARIABLE IS A FAULT, NOT A DEFAULT. A Verify with no class list would otherwise
/// have to decide between accepting everything and refusing everything, and the planner is where
/// that decision was already made.</para>
/// </summary>
public static class DeployerEnvironment
{
    public const string Classes = "LZ_PIPELINE_CLASSES";
    public const string ScanBlockOn = "LZ_SCAN_BLOCK_ON";
    public const string BuildRecordStore = "LZ_BUILD_RECORD_STORE";
    public const string ImageRepositories = "LZ_IMAGE_REPOSITORIES";
    public const string Registry = "LZ_REGISTRY";
    public const string EvidenceStore = "LZ_EVIDENCE_STORE";
    public const string TrustedProfiles = "LZ_TRUSTED_SIGNING_PROFILES";
    public const string RegistryScopes = "LZ_TRUST_REGISTRY_SCOPES";
    public const string ArtifactAccount = "LZ_ARTIFACT_ACCOUNT";
    public const string StateMachine = "LZ_STATE_MACHINE";
    public const string TriggerRoutes = "LZ_TRIGGER_ROUTES";
    public const string TriggerRefs = "LZ_TRIGGER_REFS";
    public const string AlertsTopic = "LZ_ALERTS_TOPIC";
    public const string CorroborateSources = "LZ_CORROBORATE_SOURCES";

    // Client bundles (P4 stage C).
    public const string ArtifactStore = "LZ_ARTIFACT_STORE";
    public const string ClientTargets = "LZ_CLIENT_TARGETS";
    public const string TargetAccount = "LZ_TARGET_ACCOUNT";
    public const string BucketVersioning = "LZ_BUCKET_VERSIONING";
    public const string NoncurrentExpirationDays = "LZ_NONCURRENT_EXPIRATION_DAYS";

    // The trigger and the sweep for client bundles (P4 stage D).
    public const string TriggerClientRoutes = "LZ_TRIGGER_CLIENT_ROUTES";
    public const string CorroborateBundleSources = "LZ_CORROBORATE_BUNDLE_SOURCES";

    /// <summary>The region Lambda runs a function in, which it sets itself.</summary>
    public const string Region = "AWS_REGION";

    /// <summary>Encode a list. Refuses a value containing the separator rather than corrupting it.</summary>
    public static string Join(IEnumerable<string> values)
    {
        var list = values.ToList();
        foreach (var v in list)
        {
            if (v.Contains(','))
                throw new InvalidOperationException(
                    $"'{v}' contains a comma, which separates list values in a function's environment.");
        }
        return string.Join(",", list);
    }

    public static string Required(Func<string, string?> read, string name)
    {
        var value = read(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"environment variable {name} is not set. The function is misconfigured — re-run " +
                "`lz bootstrapdeployer --apply`, which is what sets it.");
        return value;
    }

    /// <summary>
    /// Read a list. The variable must EXIST; an empty value is an empty list, which is how the planner
    /// says "none" (an empty class allowlist, which accepts nothing).
    /// </summary>
    public static IReadOnlyList<string> List(Func<string, string?> read, string name)
    {
        var value = read(name)
            ?? throw new InvalidOperationException(
                $"environment variable {name} is not set. The function is misconfigured — re-run " +
                "`lz bootstrapdeployer --apply`, which is what sets it.");

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>What the Verify function is configured with.</summary>
/// <param name="ArtifactStore">The store a bundle record's identity must name. Null where no client target is configured.</param>
/// <param name="ClientTargets">The web apps a client bundle may deploy into, one per producing repository. Empty where
/// none is configured.</param>
public sealed record VerifySettings(
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> ScanBlockOn,
    string BuildRecordStore,
    IReadOnlyList<string> ImageRepositories,
    string? ArtifactStore = null,
    IReadOnlyList<ClientTarget>? ClientTargets = null)
{
    /// <summary>
    /// The two client-bundle variables are the exception to "a missing variable is a fault", and safely: absent, the store
    /// leaves every bundle record unparseable and the targets leave every client record without a target, so their
    /// absence can only refuse. The planner writes them only where a client target is configured, which keeps a
    /// class-1-only environment's functions as they were.
    /// </summary>
    public static VerifySettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.List(env, DeployerEnvironment.Classes),
        DeployerEnvironment.List(env, DeployerEnvironment.ScanBlockOn),
        DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
        DeployerEnvironment.List(env, DeployerEnvironment.ImageRepositories),
        env(DeployerEnvironment.ArtifactStore) is { Length: > 0 } store ? store : null,
        env(DeployerEnvironment.ClientTargets) is { Length: > 0 } targets ? Pipeline.ClientTargets.Decode(targets) : Array.Empty<ClientTarget>());

    /// <summary>The pipeline config C1's decisions take, rebuilt from what the planner wrote.</summary>
    public PipelineConfig AsPipeline() => new()
    {
        Enabled = true,
        Classes = Classes.ToList(),
        Scan = new PipelineScanConfig { BlockOn = ScanBlockOn.ToList() },
    };
}

/// <summary>What the signature hook is configured with.</summary>
public sealed record HookSettings(IReadOnlyList<string> TrustedProfiles, IReadOnlyList<string> RegistryScopes)
{
    public static HookSettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.List(env, DeployerEnvironment.TrustedProfiles),
        DeployerEnvironment.List(env, DeployerEnvironment.RegistryScopes));
}

/// <summary>
/// The evidence the deployer writes (DecoupledCd.md §4.7): what was deployed, from what, over what,
/// and what it replaced — or, on the failure path, what went wrong and how far the execution got.
///
/// <para>NOTHING HERE MAY CARRY A CONTAINER DEFINITION. Task definitions hold environment values that
/// include a plaintext client secret (AwsContainerUpdater says so where it copies them), and evidence
/// is kept, replicated and read by people. Every field below is an identity, a location or a verdict,
/// and the execution state these are built from never holds a definition either — Prepare registers
/// the revision itself and hands back only its ARN.</para>
/// </summary>
public static class DeployEvidence
{
    public const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The execution name becomes part of an S3 key, so it is checked rather than trusted. Step
    /// Functions already forbids <c>/</c> in names; this refuses the rest of what could move a key.
    /// </summary>
    public static string RequireSafeExecutionName(string? executionName)
    {
        if (string.IsNullOrWhiteSpace(executionName)
            || executionName.Contains('/') || executionName.Contains('\\')
            || executionName.Contains("..", StringComparison.Ordinal)
            || executionName.Any(char.IsWhiteSpace))
            throw new DeployRefused("input",
                $"execution name '{executionName}' cannot be used in an evidence key.");

        return executionName;
    }

    /// <summary>
    /// <c>deploys/{class}/{repo}/{execution}.json</c> — the build-record layout with the execution
    /// name in place of the build stamp, so evidence sorts beside the records it answers.
    /// </summary>
    public static string DeployedKey(string cls, string repo, string executionName)
        => $"deploys/{BuildRecordFormat.PrefixFor(cls, repo)}{RequireSafeExecutionName(executionName)}.json";

    /// <summary>
    /// <c>failures/{execution}.json</c>. Not grouped by class, because a failure can happen before the
    /// record was even readable, when the class is unknown.
    /// </summary>
    public static string FailureKey(string executionName)
        => $"failures/{RequireSafeExecutionName(executionName)}.json";

    /// <summary>The body of a successful deploy's evidence.</summary>
    public static string Deployed(
        string executionName, DateTimeOffset recordedAt, DeployerInput input, JsonObject verified,
        JsonObject deploy, IReadOnlyList<string?> runningDigests)
        => JsonSerializer.Serialize(new JsonObject
        {
            ["schema"] = Schema,
            ["outcome"] = "deployed",
            ["execution"] = executionName,
            ["recordedAt"] = recordedAt.ToString("O"),
            ["record"] = new JsonObject { ["bucket"] = input.Record.Bucket, ["key"] = input.Record.Key },
            ["target"] = new JsonObject
            {
                ["cluster"] = input.Target.Cluster, ["service"] = input.Target.Service,
                ["container"] = input.Target.Container, ["repository"] = input.Target.Repository,
            },
            // What Verify established, and what Prepare registered — copied, not re-derived, so the
            // evidence states what the execution acted on.
            ["verified"] = verified.DeepClone(),
            ["deploy"] = deploy.DeepClone(),
            ["runningDigests"] = new JsonArray(runningDigests.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
        }, Json);

    /// <summary>
    /// The body of a client bundle's deploy evidence (P4 stage C): the record, the target Verify resolved, and the bundle's
    /// version and checksum inside <c>verified.identity</c>; what DeployBundle wrote; and what VerifyBundle read back.
    /// Copied from state, never re-derived, as <see cref="Deployed"/> is.
    /// </summary>
    public static string BundleDeployed(
        string executionName, DateTimeOffset recordedAt, RecordLocation record, JsonObject verified, JsonObject deploy,
        JsonObject verification)
        => JsonSerializer.Serialize(new JsonObject
        {
            ["schema"] = Schema,
            ["outcome"] = "deployed",
            ["execution"] = executionName,
            ["recordedAt"] = recordedAt.ToString("O"),
            ["record"] = new JsonObject { ["bucket"] = record.Bucket, ["key"] = record.Key },
            ["target"] = verified["target"]?.DeepClone(),
            ["verified"] = verified.DeepClone(),
            ["deploy"] = deploy.DeepClone(),
            ["verification"] = verification.DeepClone(),
        }, Json);

    /// <summary>
    /// The body of a failure's evidence: the error as Step Functions caught it, and the state the
    /// execution had reached. The state is whitelisted field by field rather than copied whole, so a
    /// future state field cannot leak into evidence without someone choosing to put it there.
    /// </summary>
    public static string Failed(string executionName, DateTimeOffset recordedAt, JsonObject state)
    {
        var error = state["error"] as JsonObject;

        var body = new JsonObject
        {
            ["schema"] = Schema,
            ["outcome"] = "failed",
            ["execution"] = executionName,
            ["recordedAt"] = recordedAt.ToString("O"),
            ["error"] = new JsonObject
            {
                ["type"] = error?["Error"]?.DeepClone(),
                ["cause"] = error?["Cause"]?.DeepClone(),
            },
        };

        // "rollout" since 2026-09-12: when Record fails after a landed roll, the failure still says the roll
        // landed — and carries, in rollout.evidence, the deploy evidence that could not be written.
        foreach (var field in new[] { "record", "target", "verified", "deploy", "deployResult", "rollout" })
        {
            if (state[field] is JsonNode n) body[field] = n.DeepClone();
        }

        return JsonSerializer.Serialize(body, Json);
    }
}
