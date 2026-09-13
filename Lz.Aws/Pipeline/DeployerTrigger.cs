using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  THE TRIGGER (DecoupledCd.md §4.3, P2 stage D). A build record lands in the build account's record
//  store; its S3 event is forwarded to this environment's trigger bus; a rule there invokes the start
//  function; and the start function turns the event into executions of the deployer.
//
//  WHY A FUNCTION AND NOT THE STATE MACHINE AS THE RULE'S TARGET: an EventBridge target cannot name the
//  execution it starts (the Target type has no Step Functions parameters), and the name is the idempotency
//  key that absorbs S3's at-least-once delivery (§5.1). A rule-started execution would get a random name, so
//  a duplicate event would roll the service twice.
//
//  PURE AND CONFIG-FREE, like CrossAccount: everything arrives as values, so the start function compiles
//  this file without SystemConfig, and the byte-identical guard's allowlist does not grow.
// ---------------------------------------------------------------------------------------------

/// <summary>One tenant's service that a record's image rolls.</summary>
public sealed record TriggerTarget(string TenantKey, DeployTarget Target);

/// <summary>The records one image repository writes, and every service its images roll.</summary>
/// <param name="RecordPrefix"><c>image/{owner}/{name}/</c>, the prefix that repository's writer role owns.</param>
public sealed record TriggerRoute(string RecordPrefix, IReadOnlyList<TriggerTarget> Targets);

/// <summary>What the start function is configured with. The planner writes it; the function reads it.</summary>
public sealed record TriggerSettings(
    string ArtifactAccountId, string BuildRecordStore, string StateMachineArn, IReadOnlyList<TriggerRoute> Routes)
{
    public static TriggerSettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.Required(env, DeployerEnvironment.ArtifactAccount),
        DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
        DeployerEnvironment.Required(env, DeployerEnvironment.StateMachine),
        DeployerTrigger.DecodeRoutes(DeployerEnvironment.Required(env, DeployerEnvironment.TriggerRoutes)));
}

/// <summary>
/// The event is not one the trigger starts anything for. The function throws it, so the event ends in the
/// dead-letter queue with this message, rather than being dropped where nobody would look.
/// </summary>
public sealed class TriggerRefused(string message) : Exception(message);

/// <summary>
/// An execution already holds the name this event implies, and its input is not the input this event implies:
/// two different deploys claimed one request id. Never resolved by starting a second execution under another name.
/// </summary>
public sealed class ExecutionConflict(string message) : Exception(message);

/// <summary>One execution the trigger starts, exactly as <c>StartExecution</c> receives it.</summary>
public sealed record TriggeredExecution(string Name, string Input);

/// <summary>The trigger's decisions: which events start what, under which names.</summary>
public static class DeployerTrigger
{
    /// <summary>The only class with a deployer. The rules forward nothing else.</summary>
    public const string RecordClass = "image";

    /// <summary>How a record is written — <c>aws s3api put-object --if-none-match "*"</c> in the build workflow.</summary>
    public const string RecordWriteReason = "PutObject";

    /// <summary>The largest routes document the planner writes. Lambda allows 4 KB for all of a function's variables.</summary>
    public const int MaxRoutesLength = 3000;

    // {builtAt with its separators stripped}-{GitHub run id}.json, the file name BuildRecordFormat.KeyFor gives a
    // record and the build workflow writes. builtAt comes from `date -u +%Y-%m-%dT%H:%M:%SZ`, so the stamp is fixed width.
    private static readonly Regex RecordFile = new(@"^(?<stamp>\d{8}T\d{6}Z)-(?<run>\d{1,20})\.json$", RegexOptions.CultureInvariant);

    private static readonly Regex RoutePrefix = new(@"^image/[a-z0-9._-]+/[a-z0-9._-]+/$", RegexOptions.CultureInvariant);
    private static readonly Regex Tenant = new(@"^[a-z0-9]{1,16}$", RegexOptions.CultureInvariant);
    private static readonly Regex Account = new(@"^\d{12}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The executions one forwarded S3 event starts: one per target of the route its key belongs to.
    ///
    /// <para>FAIL CLOSED, field by field, although the rules already filter on most of this. The rule is one
    /// document and this is another; a rule edited by hand, or an event put on the bus by something the
    /// policy admits, meets the same checks here. The account is the one that matters most: an event's
    /// <c>account</c> is set by EventBridge to the account that put it, so a pinned account is the proof it came
    /// from the build account's forwarding rule.</para>
    ///
    /// <para>The name is <c>req-{stamp}-{run id}-{tenant}-1</c>. The stamp and run id identify the build and the
    /// tenant key the service, so a duplicate delivery of the same record produces the same names and inputs,
    /// and two tenants produce two executions that cannot collide.</para>
    /// </summary>
    public static IReadOnlyList<TriggeredExecution> ExecutionsFor(string eventJson, TriggerSettings settings)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(eventJson) as JsonObject
                ?? throw new TriggerRefused("the event is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new TriggerRefused($"the event is not valid JSON: {ex.Message}");
        }

        var source = Text(root, "source");
        if (source != "aws.s3")
            throw new TriggerRefused($"the event's source is '{source}', not aws.s3. Only a build record's S3 event starts a deploy.");

        var detailType = Text(root, "detail-type");
        if (detailType != "Object Created")
            throw new TriggerRefused($"the event is '{detailType}', not 'Object Created'.");

        var account = Text(root, "account");
        if (!string.Equals(account, settings.ArtifactAccountId, StringComparison.Ordinal))
            throw new TriggerRefused(
                $"the event came from account {account ?? "(none)"}, not the build account {settings.ArtifactAccountId}. " +
                "EventBridge sets an event's account to whoever put it, so nothing else's event starts a deploy.");

        var detail = root["detail"] as JsonObject
            ?? throw new TriggerRefused("the event has no detail.");

        var bucket = (detail["bucket"] as JsonObject) is { } b ? Text(b, "name") : null;
        if (!string.Equals(bucket, settings.BuildRecordStore, StringComparison.Ordinal))
            throw new TriggerRefused($"the object is in bucket '{bucket}', not the build-record store {settings.BuildRecordStore}.");

        var reason = Text(detail, "reason");
        if (reason != RecordWriteReason)
            throw new TriggerRefused(
                $"the object was created by {reason ?? "(no reason)"}, not {RecordWriteReason}. Build records are written " +
                "with a conditional PutObject; anything else did not come from a build.");

        var key = (detail["object"] as JsonObject) is { } o ? Text(o, "key") : null;
        if (key is null)
            throw new TriggerRefused("the event names no object key.");

        // Route prefixes are image/{owner}/{name}/, so none is a prefix of another, and the record-name pattern below
        // refuses a key nested deeper than its route.
        var route = settings.Routes.FirstOrDefault(r => key.StartsWith(r.RecordPrefix, StringComparison.Ordinal));
        if (route is null)
            throw new TriggerRefused(
                $"no route for '{key}': this environment's config names no image repository whose records are written " +
                $"there (routes: {string.Join(", ", settings.Routes.Select(r => r.RecordPrefix))}). Re-run " +
                "`lz bootstrapdeployer --apply` after adding the repository, or start the execution by hand.");

        var file = RecordFile.Match(key[route.RecordPrefix.Length..]);
        if (!file.Success)
            throw new TriggerRefused(
                $"'{key}' is not a build record's name ({{stamp}}-{{run id}}.json), so no execution name can be derived from it.");

        return route.Targets
            .Select(t => new TriggeredExecution(
                DeployExecution.Name(RequestIdFor(file.Groups["stamp"].Value, file.Groups["run"].Value, t.TenantKey), 1),
                InputFor(new RecordLocation(settings.BuildRecordStore, key), t.Target)))
            .ToList();
    }

    /// <summary><c>{stamp}-{run id}-{tenant}</c>: the build, then the tenant whose service it rolls.</summary>
    public static string RequestIdFor(string stamp, string runId, string tenantKey) => $"{stamp}-{runId}-{tenantKey}";

    /// <summary>
    /// The execution input, <c>{ "record": {...}, "target": {...} }</c> — <see cref="DeployerInput.InputFields"/> and
    /// nothing else — serialised the same way every time, because StartExecution's idempotency compares input.
    /// </summary>
    public static string InputFor(RecordLocation record, DeployTarget target) => new JsonObject
    {
        ["record"] = new JsonObject { ["bucket"] = record.Bucket, ["key"] = record.Key },
        ["target"] = new JsonObject
        {
            ["cluster"] = target.Cluster,
            ["service"] = target.Service,
            ["container"] = target.Container,
            ["repository"] = target.Repository,
        },
    }.ToJsonString();

    /// <summary>
    /// Is an existing execution's input the one this event implies? Compared as JSON, not as text, since the
    /// service hands input back in whatever form it stores. Unreadable input is not the same input.
    /// </summary>
    public static bool SameInput(string ours, string? theirs)
    {
        if (theirs is null) return false;
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(ours), JsonNode.Parse(theirs));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// An execution's ARN from its state machine's: <c>arn:aws:states:{region}:{account}:execution:{machine}:{name}</c>.
    /// </summary>
    public static string ExecutionArn(string stateMachineArn, string executionName)
    {
        const string marker = ":stateMachine:";
        var at = stateMachineArn.IndexOf(marker, StringComparison.Ordinal);
        if (!stateMachineArn.StartsWith("arn:", StringComparison.Ordinal) || at < 0
            || stateMachineArn.AsSpan(at + marker.Length).Contains(':'))
            throw new InvalidOperationException($"'{stateMachineArn}' is not an unqualified state machine ARN.");

        return $"{stateMachineArn[..at]}:execution:{stateMachineArn[(at + marker.Length)..]}:{executionName}";
    }

    /// <summary>
    /// The event pattern both rules use — the build account's, which forwards, and this environment's, which
    /// invokes the start function. One definition, so the two cannot disagree about what a record's event is.
    ///
    /// <para>THE ACCOUNT IS PINNED in both. The build account's default bus only carries its own events, but this
    /// environment's bus admits another account, and AWS's guidance for a bus that does is a pattern naming the
    /// account: a rule without one matches events from every account the policy admits.</para>
    /// </summary>
    public static string EventPattern(string artifactAccountId, string buildRecordStore)
    {
        if (!Account.IsMatch(artifactAccountId))
            throw new InvalidOperationException($"'{artifactAccountId}' is not a 12-digit AWS account id.");
        if (string.IsNullOrWhiteSpace(buildRecordStore))
            throw new InvalidOperationException("the event pattern needs the build-record store's name.");

        return new JsonObject
        {
            ["account"] = new JsonArray(artifactAccountId),
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject
            {
                ["bucket"] = new JsonObject { ["name"] = new JsonArray(buildRecordStore) },
                ["object"] = new JsonObject { ["key"] = new JsonArray(new JsonObject { ["prefix"] = $"{RecordClass}/" }) },
                ["reason"] = new JsonArray(RecordWriteReason),
            },
        }.ToJsonString();
    }

    /// <summary>The routes as the function's environment carries them.</summary>
    public static string EncodeRoutes(IReadOnlyList<TriggerRoute> routes)
    {
        Validate(routes);

        var json = new JsonArray(routes.Select(r => (JsonNode?)new JsonObject
        {
            ["prefix"] = r.RecordPrefix,
            ["targets"] = new JsonArray(r.Targets.Select(t => (JsonNode?)new JsonObject
            {
                ["tenant"] = t.TenantKey,
                ["cluster"] = t.Target.Cluster,
                ["service"] = t.Target.Service,
                ["container"] = t.Target.Container,
                ["repository"] = t.Target.Repository,
            }).ToArray()),
        }).ToArray()).ToJsonString();

        if (json.Length > MaxRoutesLength)
            throw new InvalidOperationException(
                $"the trigger's routes are {json.Length} characters; a function's environment holds {MaxRoutesLength} of them " +
                "here. That many tenant services need the routes somewhere other than an environment variable.");

        return json;
    }

    /// <summary>Read the routes back, refusing anything the planner would not have written.</summary>
    public static IReadOnlyList<TriggerRoute> DecodeRoutes(string json)
    {
        JsonArray array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray
                ?? throw new InvalidOperationException("the trigger routes are not a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the trigger routes are not valid JSON: {ex.Message}");
        }

        var routes = array.Select(node =>
        {
            var route = node as JsonObject ?? throw new InvalidOperationException("a trigger route is not a JSON object.");
            var targets = route["targets"] as JsonArray ?? throw new InvalidOperationException("a trigger route has no targets array.");

            return new TriggerRoute(
                Required(route, "prefix"),
                targets.Select(t =>
                {
                    var target = t as JsonObject ?? throw new InvalidOperationException("a trigger target is not a JSON object.");
                    return new TriggerTarget(
                        Required(target, "tenant"),
                        new DeployTarget(
                            Required(target, "cluster"), Required(target, "service"),
                            Required(target, "container"), Required(target, "repository")));
                }).ToList());
        }).ToList();

        Validate(routes);
        return routes;
    }

    private static void Validate(IReadOnlyList<TriggerRoute> routes)
    {
        if (routes.Count == 0)
            throw new InvalidOperationException("the trigger has no routes, so it could start nothing.");

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!RoutePrefix.IsMatch(route.RecordPrefix))
                throw new InvalidOperationException($"trigger route prefix '{route.RecordPrefix}' is not image/{{owner}}/{{name}}/.");
            if (!prefixes.Add(route.RecordPrefix))
                throw new InvalidOperationException($"two trigger routes share the prefix '{route.RecordPrefix}'.");
            if (route.Targets.Count == 0)
                throw new InvalidOperationException($"trigger route '{route.RecordPrefix}' has no targets.");

            var tenants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in route.Targets)
            {
                if (!Tenant.IsMatch(target.TenantKey))
                    throw new InvalidOperationException(
                        $"tenant key '{target.TenantKey}' cannot be part of an execution name (lowercase letters and digits, at most 16).");
                if (!tenants.Add(target.TenantKey))
                    throw new InvalidOperationException(
                        $"trigger route '{route.RecordPrefix}' names tenant '{target.TenantKey}' twice; both would claim one execution name.");

                foreach (var (field, value) in new[]
                         {
                             ("cluster", target.Target.Cluster), ("service", target.Target.Service),
                             ("container", target.Target.Container), ("repository", target.Target.Repository),
                         })
                {
                    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
                        throw new InvalidOperationException($"trigger target {target.TenantKey} has an unusable {field} '{value}'.");
                }
            }
        }
    }

    private static string Required(JsonObject o, string name)
        => Text(o, name) ?? throw new InvalidOperationException($"a trigger route entry is missing '{name}'.");

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>Starting executions, and reading one back. The start function's only AWS calls.</summary>
public interface IExecutions
{
    /// <summary>
    /// Start an execution. True when <c>StartExecution</c> succeeds, which includes its idempotent answer for a
    /// RUNNING execution with the same name and input. False when the name is taken (<c>ExecutionAlreadyExists</c>):
    /// the execution with that name has closed, or has different input.
    /// </summary>
    Task<bool> StartAsync(string stateMachineArn, string name, string input);

    /// <summary>The input of the execution with this name, or null when there is none.</summary>
    Task<string?> InputOfAsync(string executionArn);
}

/// <summary>
/// The start function: the executions an event implies, started — or, for a name already taken, confirmed to be the
/// same deploy.
///
/// <para>A DUPLICATE AFTER THE FIRST EXECUTION FINISHED is the case StartExecution does not absorb: it answers
/// <c>ExecutionAlreadyExists</c> for a closed execution even when the input matches. So the existing execution's
/// input is read, and only an identical one is a duplicate. Anything else is a conflict, thrown into the dead-letter
/// queue rather than resolved.</para>
///
/// <para>STARTED IN ORDER, NOT ALL-OR-NOTHING. If the second of two targets fails, the event is retried whole, and the
/// first target's execution then answers as the duplicate it is.</para>
/// </summary>
public static class StartStep
{
    public static async Task<JsonObject> RunAsync(string eventJson, TriggerSettings settings, IExecutions executions)
    {
        var started = new JsonArray();
        var duplicates = new JsonArray();

        foreach (var execution in DeployerTrigger.ExecutionsFor(eventJson, settings))
        {
            if (await executions.StartAsync(settings.StateMachineArn, execution.Name, execution.Input))
            {
                started.Add(execution.Name);
                continue;
            }

            var existing = await executions.InputOfAsync(DeployerTrigger.ExecutionArn(settings.StateMachineArn, execution.Name));
            if (!DeployerTrigger.SameInput(execution.Input, existing))
                throw new ExecutionConflict(
                    existing is null
                        ? $"StartExecution says {execution.Name} exists, but it could not be read back."
                        : $"{execution.Name} already exists with different input. Two deploys claimed one request id; " +
                          $"nothing was started. Existing: {existing} This event: {execution.Input}");

            duplicates.Add(execution.Name);
        }

        return new JsonObject { ["started"] = started, ["duplicates"] = duplicates };
    }
}
