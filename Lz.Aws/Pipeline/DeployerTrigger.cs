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
//  CLIENT RECORDS (P4 stage D) take the same path where the environment deploys client bundles: the rules match
//  client/ beside image/, and a client repository's record starts one execution, into the web app it names.
//
//  PURE AND CONFIG-FREE, like CrossAccount: everything arrives as values, so the start function compiles
//  this file without SystemConfig, and the byte-identical guard's allowlist does not grow.
// ---------------------------------------------------------------------------------------------

/// <summary>One tenant's service that a record's image rolls.</summary>
public sealed record TriggerTarget(string TenantKey, DeployTarget Target);

/// <summary>The records one image repository writes, and every service its images roll.</summary>
/// <param name="RecordPrefix"><c>image/{owner}/{name}/</c>, the prefix that repository's writer role owns.</param>
public sealed record TriggerRoute(string RecordPrefix, IReadOnlyList<TriggerTarget> Targets);

/// <summary>The records one client repository writes, and the web app its bundles deploy into (P4 stage D).</summary>
/// <param name="RecordPrefix"><c>client/{owner}/{name}/</c>, the prefix that repository's writer role owns.</param>
/// <param name="App">The web app the repository's <c>Artifacts</c> names, lowercased: the last part of the execution's name, as
/// it is for an execution started by hand.</param>
/// <param name="Bucket">The app's bucket, the input's <c>target.bucket</c> — which Verify checks against the web app the
/// record's own repository names, so a route cannot aim one repository's bundle at another's app.</param>
public sealed record ClientTriggerRoute(string RecordPrefix, string App, string Bucket);

/// <summary>What the start function is configured with. The planner writes it; the function reads it.</summary>
/// <param name="AllowedRefs">The git refs whose records start a deploy — <c>refs/heads/main</c>. A record built from any
/// other ref, or naming none, is skipped.</param>
/// <param name="ClientRoutes">The client repositories' routes (P4 stage D); none where the environment deploys no client
/// bundle.</param>
/// <param name="ArtifactStore">The artifact store a client record's identity must name, without which no client record
/// parses. Set exactly when <paramref name="ClientRoutes"/> names a route.</param>
public sealed record TriggerSettings(
    string ArtifactAccountId, string BuildRecordStore, string StateMachineArn, IReadOnlyList<TriggerRoute> Routes,
    IReadOnlyList<string> AllowedRefs, IReadOnlyList<ClientTriggerRoute>? ClientRoutes = null, string? ArtifactStore = null)
{
    public static TriggerSettings Read(Func<string, string?> env)
    {
        var refs = DeployerEnvironment.List(env, DeployerEnvironment.TriggerRefs);
        if (refs.Count == 0)
            throw new InvalidOperationException(
                $"environment variable {DeployerEnvironment.TriggerRefs} names no ref, so no record could ever start a deploy. " +
                "Re-run `lz bootstrapdeployer --apply`, which is what sets it.");

        // CLIENT ROUTES ARE OPTIONAL, and only they are: an environment with no client target has no such variable (P4 stage D).
        // Where it has one, the store is required with it — without the store no client record parses, and each would be
        // refused into the dead-letter queue as unreadable rather than here, where the message names the variable.
        IReadOnlyList<ClientTriggerRoute>? clientRoutes = null;
        string? artifactStore = null;
        if (env(DeployerEnvironment.TriggerClientRoutes) is { Length: > 0 } encodedClientRoutes)
        {
            clientRoutes = DeployerTrigger.DecodeClientRoutes(encodedClientRoutes);
            artifactStore = DeployerEnvironment.Required(env, DeployerEnvironment.ArtifactStore);
        }

        return new TriggerSettings(
            DeployerEnvironment.Required(env, DeployerEnvironment.ArtifactAccount),
            DeployerEnvironment.Required(env, DeployerEnvironment.BuildRecordStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.StateMachine),
            DeployerTrigger.DecodeRoutes(DeployerEnvironment.Required(env, DeployerEnvironment.TriggerRoutes)),
            refs,
            clientRoutes,
            artifactStore);
    }
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

/// <summary>One execution the trigger starts, exactly as <c>StartExecution</c> receives it, and the record it deploys.</summary>
public sealed record TriggeredExecution(string Name, string Input, RecordLocation Record);

/// <summary>The trigger's decisions: which events start what, under which names.</summary>
public static class DeployerTrigger
{
    /// <summary>The class every trigger routes: service images, rolled per tenant.</summary>
    public const string RecordClass = "image";

    /// <summary>The class a trigger also routes where the environment deploys client bundles (P4 stage D).</summary>
    public const string ClientRecordClass = "client";

    /// <summary>How a record is written — <c>aws s3api put-object --if-none-match "*"</c> in the build workflow.</summary>
    public const string RecordWriteReason = "PutObject";

    /// <summary>The largest routes document the planner writes. Lambda allows 4 KB for all of a function's variables.</summary>
    public const int MaxRoutesLength = 3000;

    // {builtAt with its separators stripped}-{GitHub run id}.json, the file name BuildRecordFormat.KeyFor gives a
    // record and the build workflow writes. builtAt comes from `date -u +%Y-%m-%dT%H:%M:%SZ`, so the stamp is fixed width.
    private static readonly Regex RecordFile = new(@"^(?<stamp>\d{8}T\d{6}Z)-(?<run>\d{1,20})\.json$", RegexOptions.CultureInvariant);

    private static readonly Regex RoutePrefix = new(@"^image/[a-z0-9._-]+/[a-z0-9._-]+/$", RegexOptions.CultureInvariant);
    private static readonly Regex ClientRoutePrefix = new(@"^client/[a-z0-9._-]+/[a-z0-9._-]+/$", RegexOptions.CultureInvariant);
    // The last part of an execution's name, as a tenant key is: lowercase, and short enough that the name stays inside 80.
    private static readonly Regex App = new(@"^[a-z0-9][a-z0-9._-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex Bucket = new(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);
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

        // Route prefixes are {class}/{owner}/{name}/, so none is a prefix of another — across the two classes too — and the
        // record-name pattern below refuses a key nested deeper than its route.
        var clientRoutes = settings.ClientRoutes ?? Array.Empty<ClientTriggerRoute>();
        var route = settings.Routes.FirstOrDefault(r => key.StartsWith(r.RecordPrefix, StringComparison.Ordinal));
        var clientRoute = route is null ? clientRoutes.FirstOrDefault(r => key.StartsWith(r.RecordPrefix, StringComparison.Ordinal)) : null;
        if (route is null && clientRoute is null)
            throw new TriggerRefused(
                $"no route for '{key}': this environment's config names no image repository, and no client repository with a web " +
                "app, whose records are written there (routes: " +
                $"{string.Join(", ", settings.Routes.Select(r => r.RecordPrefix).Concat(clientRoutes.Select(r => r.RecordPrefix)))}). " +
                "Re-run `lz bootstrapdeployer --apply` after adding the repository, or start the execution by hand.");

        var file = RecordFile.Match(key[(route?.RecordPrefix ?? clientRoute!.RecordPrefix).Length..]);
        if (!file.Success)
            throw new TriggerRefused(
                $"'{key}' is not a build record's name ({{stamp}}-{{run id}}.json), so no execution name can be derived from it.");

        var record = new RecordLocation(settings.BuildRecordStore, key);
        var (stamp, run) = (file.Groups["stamp"].Value, file.Groups["run"].Value);

        // A CLIENT RECORD STARTS ONE EXECUTION: a repository's bundle deploys into the one app it names, under the name and
        // with the input a person starting it by hand uses (P4 stage C), so the two can only ever be one deploy.
        if (clientRoute != null)
            return new[]
            {
                new TriggeredExecution(DeployExecution.Name(RequestIdFor(stamp, run, clientRoute.App), 1), ClientInputFor(record, clientRoute.Bucket), record),
            };

        return route!.Targets
            .Select(t => new TriggeredExecution(DeployExecution.Name(RequestIdFor(stamp, run, t.TenantKey), 1), InputFor(record, t.Target), record))
            .ToList();
    }

    /// <summary>The refs whose records deploy on their own when the config names no other: main.</summary>
    public static readonly IReadOnlyList<string> DefaultRefs = new[] { "refs/heads/main" };

    /// <summary>
    /// Whether a record's build starts a deploy: only when it names a ref, and the ref is one the trigger allows
    /// (DecoupledCd.md §14.3, P2 stage D2).
    ///
    /// <para>A SKIP, NOT A REFUSAL. A build from a branch is an ordinary event — a person trying something — and not a
    /// failure anyone needs to act on, so it is logged and dropped rather than sent to the dead-letter queue. The record
    /// stays in the store and can be deployed by starting its execution by hand. A record naming no ref, written before
    /// the field existed, is skipped the same way: absence may not pass a check that asks for a branch.</para>
    /// </summary>
    public static (bool Start, string Reason) ShouldStart(BuildRecord record, IReadOnlyList<string> allowedRefs)
    {
        var allowed = string.Join(", ", allowedRefs);

        if (string.IsNullOrWhiteSpace(record.BuiltFrom.Ref))
            return (false,
                $"the record names no git ref (it predates builtFrom.ref); only builds from {allowed} deploy on their own. " +
                "Start its execution by hand to deploy it.");

        return allowedRefs.Contains(record.BuiltFrom.Ref, StringComparer.Ordinal)
            ? (true, $"built from {record.BuiltFrom.Ref}.")
            : (false,
                $"built from {record.BuiltFrom.Ref}; only builds from {allowed} deploy on their own. Start its execution by hand " +
                "to deploy it.");
    }

    /// <summary>
    /// <c>{stamp}-{run id}-{tenant or app}</c>: the build, then the tenant whose service it rolls or the web app its bundle deploys
    /// into.
    /// </summary>
    public static string RequestIdFor(string stamp, string runId, string targetKey) => $"{stamp}-{runId}-{targetKey}";

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
    /// <param name="classes">The record classes the trigger routes: <c>image</c>, and <c>client</c> where the environment deploys
    /// client bundles (P4 stage D, <c>DeployerPlanner.TriggerRecordClasses</c>). A class no branch starts is refused, so the
    /// rules never forward a record the start function could only dead-letter.</param>
    public static string EventPattern(string artifactAccountId, string buildRecordStore, IReadOnlyList<string> classes)
    {
        if (!Account.IsMatch(artifactAccountId))
            throw new InvalidOperationException($"'{artifactAccountId}' is not a 12-digit AWS account id.");
        if (string.IsNullOrWhiteSpace(buildRecordStore))
            throw new InvalidOperationException("the event pattern needs the build-record store's name.");
        if (classes.Count == 0)
            throw new InvalidOperationException("the event pattern names no record class, so the rules would forward nothing.");
        foreach (var cls in classes)
        {
            if (cls != RecordClass && cls != ClientRecordClass)
                throw new InvalidOperationException(
                    $"the trigger routes no '{cls}' records: only {RecordClass} and {ClientRecordClass} records have a branch to start.");
        }
        if (classes.Distinct(StringComparer.Ordinal).Count() != classes.Count)
            throw new InvalidOperationException($"the event pattern names a record class twice ({string.Join(", ", classes)}).");

        return new JsonObject
        {
            ["account"] = new JsonArray(artifactAccountId),
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject
            {
                ["bucket"] = new JsonObject { ["name"] = new JsonArray(buildRecordStore) },
                // One prefix matcher per class, in the order given, so ["image"] is the pattern P2 stage D wrote, byte for byte.
                ["object"] = new JsonObject
                {
                    ["key"] = new JsonArray(classes.Select(c => (JsonNode?)new JsonObject { ["prefix"] = $"{c}/" }).ToArray()),
                },
                ["reason"] = new JsonArray(RecordWriteReason),
            },
        }.ToJsonString();
    }

    /// <summary>
    /// The key prefixes a pattern of <see cref="EventPattern"/>'s matches, in order — what the appliers say a rule forwards, read
    /// from the pattern they are about to write rather than restated beside it.
    /// </summary>
    public static IReadOnlyList<string> RecordPrefixesOf(string eventPattern)
        => (JsonNode.Parse(eventPattern)?["detail"]?["object"]?["key"] as JsonArray
                ?? throw new InvalidOperationException("the event pattern has no detail.object.key matcher."))
            .Select(m => m?["prefix"]?.GetValue<string>() ?? throw new InvalidOperationException("a key matcher in the event pattern is not a prefix."))
            .ToList();

    /// <summary>
    /// A client bundle's execution input, <c>{ "record": {...}, "target": { "bucket": "…" } }</c> — the shape an execution started
    /// by hand takes (P4 stage C, <see cref="DeployerInput.BundleBucketFrom"/>), serialised the same way every time.
    /// </summary>
    public static string ClientInputFor(RecordLocation record, string bucket) => new JsonObject
    {
        ["record"] = new JsonObject { ["bucket"] = record.Bucket, ["key"] = record.Key },
        ["target"] = new JsonObject { ["bucket"] = bucket },
    }.ToJsonString();

    /// <summary>The client routes as the function's environment carries them (P4 stage D).</summary>
    public static string EncodeClientRoutes(IReadOnlyList<ClientTriggerRoute> routes)
    {
        ValidateClientRoutes(routes);

        var json = new JsonArray(routes.Select(r => (JsonNode?)new JsonObject
        {
            ["prefix"] = r.RecordPrefix,
            ["app"] = r.App,
            ["bucket"] = r.Bucket,
        }).ToArray()).ToJsonString();

        if (json.Length > MaxRoutesLength)
            throw new InvalidOperationException(
                $"the trigger's client routes are {json.Length} characters; a function's environment holds {MaxRoutesLength} of them here.");

        return json;
    }

    /// <summary>Read the client routes back, refusing anything the planner would not have written.</summary>
    public static IReadOnlyList<ClientTriggerRoute> DecodeClientRoutes(string json)
    {
        JsonArray array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray
                ?? throw new InvalidOperationException("the trigger's client routes are not a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the trigger's client routes are not valid JSON: {ex.Message}");
        }

        var routes = array.Select(node =>
        {
            var route = node as JsonObject ?? throw new InvalidOperationException("a client route is not a JSON object.");
            return new ClientTriggerRoute(Required(route, "prefix"), Required(route, "app"), Required(route, "bucket"));
        }).ToList();

        ValidateClientRoutes(routes);
        return routes;
    }

    /// <summary>Whether a prefix is one client repository's, <c>client/{owner}/{name}/</c>.</summary>
    public static bool IsClientRecordPrefix(string prefix) => ClientRoutePrefix.IsMatch(prefix);

    private static void ValidateClientRoutes(IReadOnlyList<ClientTriggerRoute> routes)
    {
        // Written only where a client route exists, so an empty list is not "none" but a variable that lost its routes.
        if (routes.Count == 0)
            throw new InvalidOperationException("the trigger's client routes name no route.");

        foreach (var route in routes)
        {
            if (!ClientRoutePrefix.IsMatch(route.RecordPrefix))
                throw new InvalidOperationException($"client route prefix '{route.RecordPrefix}' is not client/{{owner}}/{{name}}/.");
            if (!App.IsMatch(route.App))
                throw new InvalidOperationException(
                    $"web app '{route.App}' cannot be part of an execution name (lowercase letters, digits, '.', '_' and '-', at most 32).");
            if (!Bucket.IsMatch(route.Bucket))
                throw new InvalidOperationException($"client route '{route.RecordPrefix}' names '{route.Bucket}', which is not a bucket name.");
        }

        // One repository per app and per bucket: two routes to one app would mirror two repositories' bundles over each other.
        foreach (var (what, values) in new[]
                 {
                     ("prefix", routes.Select(r => r.RecordPrefix)),
                     ("app", routes.Select(r => r.App)),
                     ("bucket", routes.Select(r => r.Bucket)),
                 })
        {
            if (values.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new InvalidOperationException($"two client routes share the {what} '{twice.Key}'.");
        }
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
/// same deploy. A record built from a ref the trigger does not allow starts nothing (<see cref="DeployerTrigger.ShouldStart"/>).
///
/// <para>THE RECORD IS READ, BUT NOT JUDGED. The event says only where a record is, and the branch is inside it; so the
/// function reads the record for that one field, after the event has passed every check. Everything else about the
/// record is Verify's to decide, and a record this function cannot parse is refused into the dead-letter queue, where
/// Verify would have refused it too.</para>
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
    public static async Task<JsonObject> RunAsync(
        string eventJson, TriggerSettings settings, IRecordStore records, IExecutions executions)
    {
        var started = new JsonArray();
        var duplicates = new JsonArray();

        var planned = DeployerTrigger.ExecutionsFor(eventJson, settings);
        var location = planned[0].Record;

        var stored = await records.ReadAsync(location.Bucket, location.Key)
            ?? throw new TriggerRefused($"there is no build record at s3://{location.Bucket}/{location.Key}, though its event arrived.");

        BuildRecord record;
        try
        {
            // With the artifact store, which a bundle record must name and without which none parses (P4 stage A). An image
            // record reads the same with it as without.
            record = BuildRecordFormat.Parse(stored.Json, settings.ArtifactStore);
        }
        catch (InvalidOperationException ex)
        {
            throw new TriggerRefused($"s3://{location.Bucket}/{location.Key} cannot be read as a build record: {ex.Message}");
        }

        var (start, reason) = DeployerTrigger.ShouldStart(record, settings.AllowedRefs);
        if (!start)
            return new JsonObject
            {
                ["started"] = started,
                ["duplicates"] = duplicates,
                ["skipped"] = new JsonObject { ["record"] = location.Key, ["ref"] = record.BuiltFrom.Ref, ["reason"] = reason },
            };

        foreach (var execution in planned)
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
