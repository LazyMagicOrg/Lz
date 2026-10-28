using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  THE OUTCOMES (each environment's deploy-outcomes topic): one email-sized message for every deployer execution that ends,
//  whatever its status, and for every build the intake refuses or cannot decide. The alerts topic is for what needs acting
//  on; this one is for the people who want to know what reached the environment. Linked into Lz.Aws.Deployer, like the
//  steps it sits beside: plain C#, no config, no SDK.
// ---------------------------------------------------------------------------------------------

/// <summary>What the notify function is configured with.</summary>
/// <param name="Environment">The environment's name, as the message's first word says it.</param>
public sealed record OutcomeSettings(string Environment, string TopicArn, string EvidenceStore, string Region)
{
    public static OutcomeSettings Read(Func<string, string?> env)
        => new(
            DeployerEnvironment.Required(env, DeployerEnvironment.OutcomesEnvironment),
            DeployerEnvironment.Required(env, DeployerEnvironment.OutcomesTopic),
            DeployerEnvironment.Required(env, DeployerEnvironment.EvidenceStore),
            DeployerEnvironment.Required(env, DeployerEnvironment.Region));
}

/// <summary>A deployer execution that ended, as EventBridge's status-change event reports it.</summary>
/// <param name="RecordKey">The build record the execution was started on, from its input; null when the input names none.</param>
public sealed record DeployFinished(
    string ExecutionName, string ExecutionArn, string Status, DateTimeOffset? Started, DateTimeOffset? Stopped, string? RecordKey)
{
    /// <summary>
    /// Reads a "Step Functions Execution Status Change" event. The input arrives as a JSON string; an input too large for the
    /// event is left out by Step Functions, and then the message says only what the execution's name says.
    /// </summary>
    public static DeployFinished FromEvent(JsonObject e)
    {
        var detail = e["detail"] as JsonObject
            ?? throw new InvalidOperationException("the event has no detail; it is not a Step Functions status change.");

        string Required(string name) => Text(detail, name)
            ?? throw new InvalidOperationException($"the event's detail has no {name}; it is not a Step Functions status change.");

        string? recordKey = null;
        if (Text(detail, "input") is { } input)
        {
            try
            {
                recordKey = (JsonNode.Parse(input) as JsonObject)?["record"]?["key"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // An input that is not the deployer's: the message names the execution and its status, and nothing else.
            }
        }

        return new DeployFinished(Required("name"), Required("executionArn"), Required("status"),
            Epoch(detail["startDate"]), Epoch(detail["stopDate"]), recordKey);
    }

    private static DateTimeOffset? Epoch(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static string? Text(JsonObject o, string name)
        => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>One piece of evidence as the notify function found it: its body, or why there is none to read.</summary>
public sealed record EvidenceRead(string Key, JsonObject? Body, string? Problem)
{
    public static EvidenceRead Absent(string key) => new(key, null, null);
}

public static class DeployOutcomes
{
    /// <summary>SNS refuses a subject over 100 characters, or one holding anything but printable ASCII.</summary>
    public const int SubjectLimit = 100;

    /// <summary>How much of a cause the message quotes. A refusal's list can be long; the evidence holds all of it.</summary>
    public const int CauseLimit = 3000;

    public static string TopicName(string systemKey, string environment) => $"{systemKey}-{environment}-deploy-outcomes";

    public static string TopicArn(string region, string accountId, string systemKey, string environment)
        => $"arn:aws:sns:{region}:{accountId}:{TopicName(systemKey, environment)}";

    /// <summary>
    /// What a record key says: <c>{class}/{owner}/{name}/[{environment}/]{stamp}-{run}-{attempt}.json</c>. Null fields where
    /// the key is not that shape — the message then says less, and never guesses.
    /// </summary>
    public static (string? Class, string? Repo, string? RunId) FromRecordKey(string? key)
    {
        if (key is null) return (null, null, null);
        var parts = key.Split('/');
        if (parts.Length < 4 || parts.Any(p => p.Length == 0)) return (null, null, null);
        var stem = parts[^1].EndsWith(".json", StringComparison.Ordinal) ? parts[^1][..^".json".Length] : parts[^1];
        var pieces = stem.Split('-');
        var run = pieces.Length >= 3 && long.TryParse(pieces[^2], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? pieces[^2] : null;
        return (parts[0], $"{parts[1]}/{parts[2]}", run);
    }

    /// <summary>
    /// The evidence keys an ended execution may have written: its deploy evidence (only when the record key names the class
    /// and repository it is filed under), its failure, and its Gate 2 result.
    /// </summary>
    public static (string? Deployed, string Failed, string Tests) EvidenceKeysFor(DeployFinished finished)
    {
        var (cls, repo, _) = FromRecordKey(finished.RecordKey);
        return (cls is null || repo is null ? null : DeployEvidence.DeployedKey(cls, repo, finished.ExecutionName),
            DeployEvidence.FailureKey(finished.ExecutionName),
            $"{Gate2ResultStep.TestsPrefix}{finished.ExecutionName}.json");
    }

    /// <summary>
    /// The message for an ended execution. SUCCEEDED is "deployed"; anything else is not, and says why as the evidence does —
    /// the execution's own error is a fixed string pointing at that evidence, so it is never what the message quotes. A deploy
    /// that landed before something later failed (Gate 2, a pass marker) says both.
    /// </summary>
    public static (string Subject, string Message) ForDeploy(
        OutcomeSettings settings, DeployFinished finished, EvidenceRead? deployed, EvidenceRead failed, EvidenceRead tests)
    {
        var (cls, keyRepo, keyRun) = FromRecordKey(finished.RecordKey);
        // What Verify established, from whichever evidence holds it: a landed deploy's, or the failure's copy of the state.
        var verified = deployed?.Body?["verified"] as JsonObject ?? failed.Body?["verified"] as JsonObject;
        var builtFrom = verified?["builtFrom"] as JsonObject;
        var repo = Text(builtFrom, "repo") ?? keyRepo;
        var commit = Text(builtFrom, "commit");
        var run = Text(verified, "workflowRunId") ?? keyRun;
        var landed = deployed?.Body is not null;

        var outcome = finished.Status switch
        {
            "SUCCEEDED" => "deployed",
            "FAILED" when landed => "deployed, then FAILED",
            "FAILED" => "FAILED",
            "TIMED_OUT" => landed ? "deployed, then TIMED OUT" : "TIMED OUT",
            "ABORTED" => landed ? "deployed, then ABORTED" : "ABORTED",
            var other => other,
        };

        var what = repo is null ? finished.ExecutionName : $"{repo} {(commit is null ? (run is null ? "" : $"run {run}") : Short(commit))}".TrimEnd();
        var subject = Subject($"[{settings.Environment}] {outcome}: {what}{(cls is null ? "" : $" ({cls})")}");

        var lines = new List<string>
        {
            $"Environment: {settings.Environment}",
            $"Outcome:     {outcome}",
        };
        if (repo is not null) lines.Add($"Repository:  {repo}{(cls is null ? "" : $" (class {cls})")}");
        if (commit is not null && repo is not null) lines.Add($"Commit:      {commit}  https://github.com/{repo}/commit/{commit}");
        if (Text(builtFrom, "ref") is { } gitRef) lines.Add($"Ref:         {gitRef}");
        if (run is not null && repo is not null) lines.Add($"Build run:   {run}  https://github.com/{repo}/actions/runs/{run}");
        if (finished.Started is { } started) lines.Add($"Started:     {started.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        if (finished.Stopped is { } stopped)
            lines.Add($"Finished:    {stopped.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" +
                      (finished.Started is { } s ? $" ({Duration(stopped - s)})" : ""));

        if (Text(verified, "summary") is { } summary)
        {
            lines.Add("");
            lines.Add(summary);
        }

        // WHY IT DID NOT DEPLOY, or what failed after it did.
        if (finished.Status != "SUCCEEDED")
        {
            lines.Add("");
            if (failed.Body?["error"] is JsonObject error && (Text(error, "type") is not null || error["cause"] is not null))
                lines.Add($"Why: {Text(error, "type") ?? "an error"}: {Cause(error["cause"])}");
            else if (tests.Body is { } t && Text(t, "outcome") is { } testOutcome && testOutcome != "Passed")
                lines.Add($"Why: Gate 2 {testOutcome}: {string.Join("; ", Strings(t["reasons"]))}");
            else if (finished.Status is "TIMED_OUT" or "ABORTED")
                lines.Add($"Why: the execution was {(finished.Status == "ABORTED" ? "stopped" : "timed out")} before it recorded an outcome.");
            else
                lines.Add("Why: no failure evidence was found; this execution's history holds the error.");
        }

        if (tests.Body is { } gate2 && Text(gate2, "outcome") is { } gate2Outcome)
            lines.Add($"Gate 2: {gate2Outcome}");

        lines.Add("");
        lines.Add($"Execution: {finished.ExecutionName}");
        lines.Add($"  https://{settings.Region}.console.aws.amazon.com/states/home?region={settings.Region}#/v2/executions/details/{finished.ExecutionArn}");
        foreach (var e in new[] { deployed, failed, tests })
        {
            if (e is null) continue;
            if (e.Body is not null) lines.Add($"Evidence:  s3://{settings.EvidenceStore}/{e.Key}");
            else if (e.Problem is not null) lines.Add($"Evidence:  s3://{settings.EvidenceStore}/{e.Key} could not be read: {e.Problem}");
        }

        return (subject, string.Join("\n", lines));
    }

    /// <summary>
    /// The message for a build the intake did not take in: refused by its run check, its fetch or Gate 1, or a fault on its
    /// last retry. Nothing about it reached any environment; the environment named is the one its build would have gone to.
    /// </summary>
    public static (string Subject, string Message) ForIntake(
        string environment, string region, string evidenceStore, string evidenceKey, string repo, long runId, int runAttempt,
        string? commit, string outcome, IReadOnlyList<string> reasons)
    {
        var refused = outcome == "refused";
        var headline = refused ? "not deployed, intake refused" : "not deployed, intake could not decide";
        var subject = Subject($"[{environment}] {headline}: {repo} {(commit is null ? $"run {runId}" : Short(commit))}");

        var lines = new List<string>
        {
            $"Environment: {environment}",
            $"Outcome:     {headline}",
            $"Repository:  {repo}",
        };
        if (commit is not null) lines.Add($"Commit:      {commit}  https://github.com/{repo}/commit/{commit}");
        lines.Add($"Build run:   {runId} attempt {runAttempt}  https://github.com/{repo}/actions/runs/{runId}/attempts/{runAttempt}");
        lines.Add("");
        lines.Add(refused
            ? "The build was refused before it reached any environment. Nothing was published, and nothing deploys it."
            : "The intake failed on its last retry, so this build was neither taken in nor refused. Nothing deploys it; a new run, or a re-run of this one, is taken in afresh.");
        if (reasons.Count > 0)
        {
            lines.Add("");
            lines.AddRange(reasons.Select(r => $"  - {Clip(r, CauseLimit)}"));
        }

        lines.Add("");
        lines.Add($"Evidence:  s3://{evidenceStore}/{evidenceKey}  (region {region})");
        return (subject, string.Join("\n", lines));
    }

    /// <summary>A subject SNS accepts: printable ASCII only, on one line, at most <see cref="SubjectLimit"/> characters.</summary>
    public static string Subject(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c is >= ' ' and <= '~' ? c : c is '\r' or '\n' or '\t' ? ' ' : '?');
        var s = sb.ToString().Trim();
        return s.Length <= SubjectLimit ? s : s[..(SubjectLimit - 3)] + "...";
    }

    /// <summary>
    /// A Step Functions cause as a person reads it: a Lambda error's cause is JSON holding <c>errorMessage</c>, which is the
    /// part worth quoting; anything else is quoted as it is.
    /// </summary>
    public static string Cause(JsonNode? cause)
    {
        var text = cause is JsonValue v && v.TryGetValue<string>(out var s) ? s : cause?.ToJsonString() ?? "no cause given";
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && o["errorMessage"] is JsonValue m && m.TryGetValue<string>(out var message))
                text = message;
        }
        catch (JsonException)
        {
            // Not JSON: quoted as it is.
        }

        return Clip(text.Trim(), CauseLimit);
    }

    public static IReadOnlyList<string> Strings(JsonNode? node)
        => node is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString()).OfType<string>().ToList()
            : Array.Empty<string>();

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + " ... (cut; the evidence holds all of it)";

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private static string Duration(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    private static string? Text(JsonObject? o, string name)
        => o?[name] is JsonValue v && (v.TryGetValue<string>(out var s) ? !string.IsNullOrWhiteSpace(s) ? s : null
            : v.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture) : null) is { } t ? t : null;
}

/// <summary>
/// The notify function: an ended execution of this environment's deployer, sent to its outcomes topic.
///
/// <para>EVIDENCE IS READ, NOT THE EXECUTION'S OUTPUT. The machine's Fail states carry a fixed error that points at the
/// evidence, and a failed execution has no output; the evidence is what says why, and what deployed.</para>
///
/// <para>A READ THAT FAILS DOES NOT STOP THE MESSAGE. It says what it could not read; only a publish that fails throws, so
/// Lambda retries it.</para>
/// </summary>
public static class DeployNotifyStep
{
    public static async Task<JsonObject> RunAsync(JsonObject e, OutcomeSettings settings, IRecordStore evidence, IAlertPublisher publisher)
    {
        var finished = DeployFinished.FromEvent(e);
        var (deployedKey, failedKey, testsKey) = DeployOutcomes.EvidenceKeysFor(finished);

        async Task<EvidenceRead> ReadAsync(string key)
        {
            try
            {
                var stored = await evidence.ReadAsync(settings.EvidenceStore, key);
                if (stored is null) return EvidenceRead.Absent(key);
                return JsonNode.Parse(stored.Json) is JsonObject body ? new EvidenceRead(key, body, null) : new EvidenceRead(key, null, "it is not a JSON object");
            }
            catch (Exception ex)
            {
                return new EvidenceRead(key, null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        var deployed = deployedKey is null ? null : await ReadAsync(deployedKey);
        var failed = await ReadAsync(failedKey);
        var tests = await ReadAsync(testsKey);

        var (subject, message) = DeployOutcomes.ForDeploy(settings, finished, deployed, failed, tests);
        await publisher.PublishAsync(settings.TopicArn, subject, message);
        return new JsonObject { ["execution"] = finished.ExecutionName, ["status"] = finished.Status, ["subject"] = subject };
    }
}
