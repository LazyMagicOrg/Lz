using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Ops;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  GATE 2 IN THE DEPLOYER (DecoupledCd.md §15.5, V-E): after Record, on the success branch only. Gate2Start takes the
//  environment's Gate 2 lease and reads what the environment runs; a CodeBuild project runs the tiers
//  (codebuild:startBuild.sync, the machine's own integration); Gate2Result judges the result files, reads what runs again,
//  writes tests/{execution}.json once, alerts an advisory change and releases the lease. The verdict is Gate2Verdict.cs.
// ---------------------------------------------------------------------------------------------

/// <summary>A service the environment runs: its cluster, its name, and the container whose image Gate 2 credits.</summary>
public sealed record Gate2Service(string Cluster, string Service, string Container);

/// <summary>A web app the environment serves, by the bucket whose <c>lz/deploy.json</c> names its bundle.</summary>
public sealed record Gate2App(string App, string Bucket);

/// <summary>EVERYTHING THE ENVIRONMENT RUNS that a marker would name (§6): its services and its apps, the site apart (V-24).
/// Planned from configuration and handed to both functions in their environment.</summary>
public sealed record Gate2Set(IReadOnlyList<Gate2Service> Services, IReadOnlyList<Gate2App> Apps)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Encode() => JsonSerializer.Serialize(this, Json);
    public static Gate2Set Decode(string value) => JsonSerializer.Deserialize<Gate2Set>(value, Json)
        ?? throw new InvalidOperationException("the Gate 2 set variable is empty");
}

/// <summary>What the environment ran at one moment: each service's tasks' digests and each app's bundle version.</summary>
public sealed record Gate2Running(IReadOnlyDictionary<string, IReadOnlyList<string?>> Digests, IReadOnlyDictionary<string, string?> Bundles)
{
    public JsonObject ToJson() => new()
    {
        ["digests"] = new JsonObject(Digests.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d =>
            KeyValuePair.Create(d.Key, (JsonNode?)new JsonArray(d.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())))),
        ["bundles"] = new JsonObject(Bundles.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b =>
            KeyValuePair.Create(b.Key, (JsonNode?)JsonValue.Create(b.Value)))),
    };

    /// <summary>The same set: every service's digests, as a multiset, and every app's version.</summary>
    public static bool Same(JsonNode? before, JsonNode? after)
    {
        static string Canonical(JsonNode? n) => n is JsonObject o
            ? new JsonObject
            {
                ["digests"] = new JsonObject((o["digests"] as JsonObject ?? new JsonObject()).OrderBy(d => d.Key, StringComparer.Ordinal).Select(d =>
                    KeyValuePair.Create(d.Key, (JsonNode?)new JsonArray((d.Value as JsonArray ?? new JsonArray()).Select(v => v?.ToJsonString() ?? "null")
                        .Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())))),
                ["bundles"] = o["bundles"]?.DeepClone(),
            }.ToJsonString()
            : "";
        return before is not null && after is not null && Canonical(before) == Canonical(after);
    }
}

/// <summary>Each app's deployed bundle version, from its <c>lz/deploy.json</c>; null when it has none.</summary>
public interface IGate2Markers
{
    Task<string?> DeployedVersionAsync(string bucket);
}

/// <summary>The lists Gate 1 recorded from the bundle that Gate 2 runs, and the <c>@gate</c> count of the browser tests.</summary>
public sealed record Gate2Recorded(IReadOnlyList<string> Precondition, IReadOnlyList<string> Blocking, IReadOnlyList<string> Advisory, int GateCount)
{
    public JsonObject ToJson() => new()
    {
        ["precondition"] = new JsonArray(Precondition.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        ["blocking"] = new JsonArray(Blocking.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        ["advisory"] = new JsonArray(Advisory.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        ["gateCount"] = GateCount,
    };

    public static Gate2Recorded From(JsonNode? node)
    {
        static IReadOnlyList<string> List(JsonNode? n) => (n as JsonArray ?? new JsonArray()).Select(v => v!.GetValue<string>()).ToList();
        return node is JsonObject o
            ? new(List(o["precondition"]), List(o["blocking"]), List(o["advisory"]), o["gateCount"]?.GetValue<int>() ?? 0)
            : throw new Gate2ResultUnreadable("the state has no Gate 1 lists for this gate");
    }
}

/// <summary>What the build runs: its variables (the bundles' URLs and SHA-256s) and what Gate 1 recorded of them.</summary>
public sealed record Gate2Bundles(IReadOnlyDictionary<string, string> Variables, Gate2Recorded Recorded);

/// <summary>
/// FINDS WHAT THE GATE RUNS (§15.5): the e2e tier from the tests record of the image member that runs — for an image's
/// gate, the same run attempt's; for a client's, the running image's — and the browser tier from the newest
/// ScutaraE2ETests record that passed its own Gate 1.
/// </summary>
public interface IGate2Bundles
{
    Task<Gate2Bundles> ResolveAsync(JsonObject state, Gate2Running running);
}

/// <summary>A result file the build uploaded, by the execution it ran for; null when there is none.</summary>
public interface IGate2Results
{
    Task<string?> ReadAsync(string execution, string file);
}

/// <summary>Reads of this environment's evidence store, for the previous gate's advisory results.</summary>
public interface IGate2EvidenceReader
{
    Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix);
    Task<string?> ReadAsync(string bucket, string key);
}

/// <summary>The alerts topic.</summary>
public interface IGate2Alerts
{
    Task PublishAsync(string subject, string message);
}

/// <summary>Another gate holds the environment's Gate 2 lease. RETRIED by the machine.</summary>
public sealed class Gate2LeaseHeld(string message) : Exception(message);

/// <summary>What runs, read the same way before and after the tiers (§15.5).</summary>
public static class Gate2Reads
{
    public static async Task<Gate2Running> ReadAsync(Gate2Set set, IServices services, IGate2Markers markers)
    {
        var digests = new Dictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
        foreach (var s in set.Services)
            digests[$"{s.Cluster}/{s.Service}"] = DeployVerification.RunningDigests(await services.RunningTasksAsync(s.Cluster, s.Service), s.Container);
        var bundles = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var a in set.Apps) bundles[a.App] = await markers.DeployedVersionAsync(a.Bucket);
        return new(digests, bundles);
    }

    /// <summary>
    /// Why this execution's own artifact is NOT the one running, or null when it is: an image's digest on every running task
    /// of its service (and at least one task), or a client's bundle version in its app's <c>lz/deploy.json</c>.
    /// </summary>
    public static string? NotOurs(JsonObject state, Gate2Running running)
    {
        var verified = state["verified"] as JsonObject ?? throw new DeployRefused("verified", "Gate 2 runs after Verify; the state has no Verify result.");
        var cls = verified["class"]?.GetValue<string>();
        if (cls == "client")
        {
            var app = verified["target"]?["app"]?.GetValue<string>() ?? throw new DeployRefused("verified", "a client's Verify result names no app.");
            var version = verified["identity"]?["versionId"]?.GetValue<string>() ?? throw new DeployRefused("verified", "a client's Verify result names no bundle version.");
            var serving = running.Bundles.GetValueOrDefault(app);
            return serving == version ? null : $"{app} serves bundle version {serving ?? "(none)"}, not this execution's {version}";
        }

        var digest = verified["digest"]?.GetValue<string>() ?? throw new DeployRefused("verified", "an image's Verify result names no digest.");
        var key = $"{state["target"]?["cluster"]?.GetValue<string>()}/{state["target"]?["service"]?.GetValue<string>()}";
        var tasks = running.Digests.GetValueOrDefault(key) ?? Array.Empty<string?>();
        if (tasks.Count == 0) return $"{key} runs no task";
        var others = tasks.Where(d => d != digest).Distinct().ToList();
        return others.Count == 0 ? null : $"{key} runs {string.Join(", ", others.Select(o => o ?? "(no digest)"))} beside this execution's {digest}";
    }
}

/// <summary>
/// GATE 2, STARTED: the lease, then what runs. With this execution's artifact running, the bundles the build runs and its
/// variables; otherwise <c>superseded</c>, and the result state records TestsSuperseded without a build.
/// </summary>
public static class Gate2StartStep
{
    public static string LeaseHolder(string executionName) => $"gate2:{executionName}";

    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, Gate2Set set, IServiceLeaseStore leases, string leaseKey, IServices services,
        IGate2Markers markers, IGate2Bundles bundles, string resultsBucket, DateTimeOffset now)
    {
        var holder = LeaseHolder(executionName);
        var claim = await ServiceLease.ClaimKeyReentrantAsync(leases, leaseKey, "Gate 2", holder, $"Gate 2 of {executionName}", now);
        if (claim is ServiceLeaseClaim.Held held)
            throw new Gate2LeaseHeld($"another gate is running here: {ServiceLease.Describe(held.By)}");
        var lease = ((ServiceLeaseClaim.Claimed)claim).Lease;

        var before = await Gate2Reads.ReadAsync(set, services, markers);
        var result = new JsonObject
        {
            ["lease"] = new JsonObject { ["key"] = leaseKey, ["holder"] = holder, ["expiresAt"] = lease.ExpiresAt.ToString("O") },
            ["before"] = before.ToJson(),
        };

        if (Gate2Reads.NotOurs(state, before) is { } reason)
        {
            result["verdict"] = "superseded";
            result["reason"] = reason;
            return result;
        }

        Gate2Bundles resolved;
        try
        {
            resolved = await bundles.ResolveAsync(state, before);
        }
        catch (Gate2NotResolvable ex)
        {
            // Nothing to run whole: no build, and the result state records TestsIncomplete with this reason.
            result["verdict"] = "incomplete";
            result["reason"] = ex.Message;
            return result;
        }
        var variables = new Dictionary<string, string>(resolved.Variables)
        {
            ["GATE2_EXECUTION"] = executionName,
            ["GATE2_RESULTS"] = $"s3://{resultsBucket}/{executionName}/",
        };
        result["verdict"] = "ours";
        result["recorded"] = resolved.Recorded.ToJson();
        result["variables"] = new JsonArray(variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v =>
            (JsonNode?)new JsonObject { ["Name"] = v.Key, ["Value"] = v.Value, ["Type"] = "PLAINTEXT" }).ToArray());
        return result;
    }
}

/// <summary>
/// GATE 2, DECIDED: the result files judged by tier, what runs read again, the outcome written to
/// <c>tests/{execution}.json</c> once (this state is the only writer the evidence store's pin admits under <c>tests/</c>),
/// an advisory change or a test that passed only on its retry alerted, and the lease released.
/// </summary>
public static class Gate2ResultStep
{
    public const string TestsPrefix = "tests/";

    /// <summary>The files the build uploads under <c>{results}/{execution}/</c>.</summary>
    public static readonly (string File, string Tier)[] Files =
    {
        ("precondition.trx", "precondition"), ("blocking.trx", "blocking"), ("advisory.trx", "advisory"), ("browser.json", "browser"),
    };

    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, Gate2Set set, IServiceLeaseStore leases, string leaseKey, IServices services,
        IGate2Markers markers, IGate2Results results, string evidenceBucket, IEvidenceWriter writer, IGate2EvidenceReader reader,
        IGate2Alerts alerts, DateTimeOffset now)
    {
        var start = state["gate2Start"] as JsonObject ?? throw new Gate2ResultUnreadable("the state has no Gate2Start result");
        var record = new JsonObject { ["schema"] = 1, ["execution"] = executionName, ["at"] = now.ToString("O"), ["before"] = start["before"]?.DeepClone() };
        IReadOnlyList<string> reasons;
        Gate2Outcome outcome;
        var advisoryResults = new Dictionary<string, string>(StringComparer.Ordinal);

        if (start["verdict"]?.GetValue<string>() == "superseded")
        {
            (outcome, reasons) = (Gate2Outcome.TestsSuperseded, new[] { start["reason"]?.GetValue<string>() ?? "this execution's artifact was not running" });
        }
        else if (start["verdict"]?.GetValue<string>() != "ours")
        {
            (outcome, reasons) = (Gate2Outcome.TestsIncomplete, new[] { start["reason"]?.GetValue<string>() ?? "Gate2Start found nothing to run" });
        }
        else
        {
            var recorded = Gate2Recorded.From(start["recorded"]);
            var tiers = new Dictionary<string, Gate2TierVerdict>(StringComparer.Ordinal);
            foreach (var (file, tier) in Files)
            {
                var text = await results.ReadAsync(executionName, file);
                if (text is null)
                {
                    tiers[tier] = new(tier, Gate2TierOutcome.Incomplete, new[] { $"the build left no {file}" }, 0, Array.Empty<string>(), Array.Empty<string>());
                    continue;
                }
                if (tier == "browser")
                {
                    tiers[tier] = Gate2Playwright.Judge(JsonNode.Parse(text) as JsonObject ?? throw new Gate2ResultUnreadable($"{file} is not a JSON object"), recorded.GateCount);
                    continue;
                }
                var parsed = Gate2Trx.Parse(text);
                tiers[tier] = Gate2Trx.Judge(tier, parsed, tier switch { "precondition" => recorded.Precondition, "blocking" => recorded.Blocking, _ => recorded.Advisory });
                if (tier == "advisory")
                    foreach (var r in parsed) advisoryResults[r.Name] = r.IsSkip ? "skipped" : r.Outcome == "Passed" ? "passed" : "failed";
            }

            var after = await Gate2Reads.ReadAsync(set, services, markers);
            record["after"] = after.ToJson();
            (outcome, reasons) = Gate2Decision.Decide(Gate2Running.Same(start["before"], after.ToJson()), tiers["precondition"],
                new[] { tiers["blocking"], tiers["browser"] });
            record["tiers"] = new JsonObject(tiers.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)TierJson(t.Value))));
            if (state["gate2BuildError"] is { } buildError) record["buildError"] = buildError.DeepClone();

            var flaky = tiers["browser"].Flaky;
            if (flaky.Count > 0)
                await alerts.PublishAsync($"Gate 2: {flaky.Count} test(s) passed only on their retry",
                    $"{executionName}\n" + string.Join("\n", flaky));
        }

        // THE ADVISORY SET, alerted only on a change from the previous gate's results.
        var previous = await PreviousAdvisoryAsync(reader, evidenceBucket, executionName);
        var changes = advisoryResults.Count == 0 ? Array.Empty<Gate2AdvisoryChange>() : Gate2Advisory.Changes(previous, advisoryResults);
        record["advisory"] = new JsonObject
        {
            ["results"] = new JsonObject(advisoryResults.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => KeyValuePair.Create(a.Key, (JsonNode?)JsonValue.Create(a.Value)))),
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)JsonValue.Create(c.ToString())).ToArray()),
        };
        if (changes.Count > 0)
            await alerts.PublishAsync($"Gate 2: {changes.Count} advisory result(s) changed", $"{executionName}\n" + string.Join("\n", changes));

        record["outcome"] = outcome.ToString();
        record["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        var key = $"{TestsPrefix}{executionName}.json";
        await writer.PutOnceAsync(evidenceBucket, key, record.ToJsonString());   // false: a retry after the write landed
        await ServiceLease.ReleaseKeyHeldByAsync(leases, leaseKey, Gate2StartStep.LeaseHolder(executionName));

        return new JsonObject
        {
            ["outcome"] = outcome.ToString(),
            ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["evidence"] = key,
        };
    }

    private static JsonObject TierJson(Gate2TierVerdict t) => new()
    {
        ["outcome"] = t.Outcome.ToString(),
        ["results"] = t.Results,
        ["reasons"] = new JsonArray(t.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["failed"] = new JsonArray(t.Failed.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["flaky"] = new JsonArray(t.Flaky.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
    };

    /// <summary>The newest other gate's advisory results in this environment, or null when there is none to compare with.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> PreviousAdvisoryAsync(IGate2EvidenceReader reader, string bucket, string executionName)
    {
        var own = $"{TestsPrefix}{executionName}.json";
        foreach (var key in (await reader.KeysAsync(bucket, TestsPrefix)).Where(k => k != own).OrderByDescending(k => k, StringComparer.Ordinal))
        {
            if (await reader.ReadAsync(bucket, key) is not { } body) continue;
            if (JsonNode.Parse(body)?["advisory"]?["results"] is JsonObject results && results.Count > 0)
                return results.ToDictionary(r => r.Key, r => r.Value!.GetValue<string>(), StringComparer.Ordinal);
        }
        return null;
    }
}
