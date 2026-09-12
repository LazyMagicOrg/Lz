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

/// <summary>The roll is under way. RETRIED by the definition; its retry limit is the rollout timeout.</summary>
public sealed class RolloutStillRolling(string message) : Exception(message);

/// <summary>The roll finished or failed and the deployed digest is not what runs. Not retried.</summary>
public sealed class RolloutNotDeployed(string message) : Exception(message);

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
public sealed record VerifySettings(
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> ScanBlockOn,
    string BuildRecordStore,
    IReadOnlyList<string> ImageRepositories)
{
    public static VerifySettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.List(env, DeployerEnvironment.Classes),
        DeployerEnvironment.List(env, DeployerEnvironment.ScanBlockOn),
        DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
        DeployerEnvironment.List(env, DeployerEnvironment.ImageRepositories));

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

        foreach (var field in new[] { "record", "target", "verified", "deploy", "deployResult" })
        {
            if (state[field] is JsonNode n) body[field] = n.DeepClone();
        }

        return JsonSerializer.Serialize(body, Json);
    }
}
