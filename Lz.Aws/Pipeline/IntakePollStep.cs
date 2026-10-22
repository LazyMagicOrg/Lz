using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>A repository the intake takes runs from, as the poller's configuration carries it, and its mode.</summary>
/// <param name="Class">The repository's artifact class (<c>image</c>, <c>client</c>, …), which decides how its artifacts are read.</param>
/// <param name="Artifacts">The artifacts each run must upload, by the names config gives them (the run attempt is appended).</param>
/// <param name="Gate1">The test bundle and how Gate 1 runs it (V-C); null for a repository with no Gate 1 yet.</param>
/// <param name="BuiltFrom">The artifact holding the build's <c>builtfrom.json</c>; null where the repository uploads none.</param>
/// <param name="Sbom">The artifact holding the build's SBOM, one <c>.json</c> document (V-19); null where it uploads none.</param>
public sealed record IntakeRepository(string Repo, long RepositoryId, long OwnerId, string WorkflowPath, string Mode, string Class,
    IReadOnlyList<string> Artifacts, IntakeGate1? Gate1 = null, string? BuiltFrom = null, string? Sbom = null)
{
    public IntakeSource Source => new(Repo, RepositoryId, OwnerId, WorkflowPath);
    public string WorkflowFile => WorkflowPath[(WorkflowPath.LastIndexOf('/') + 1)..];
}

/// <summary>Gate 1 for a repository (DecoupledCd.md §15.4, V-C), from the build-side file.</summary>
/// <param name="Artifact">The run artifact holding the test bundle, named for the attempt like the others.</param>
/// <param name="Environment">Variables the tests need, from config, passed with the build.</param>
/// <param name="BuildRoot">Where the bundle is unzipped: the path it was built at, or <c>/tmp/bundle</c>.</param>
public sealed record IntakeGate1(string Artifact, string Run, string Filter, string SdkVersion, string SdkSha512, IReadOnlyList<IntakeImage> Images,
    IReadOnlyDictionary<string, string>? Environment = null, string? BuildRoot = null);

/// <summary>An image Gate 1 pulls by digest and tags as the tests expect it.</summary>
public sealed record IntakeImage(string From, string As);

/// <summary>The environment variables that configure the intake's functions: written by the planner, read by the handlers.</summary>
public static class IntakeEnvironment
{
    public const string Repositories = "LZ_INTAKE_REPOSITORIES";
    public const string EvidenceStore = "LZ_INTAKE_EVIDENCE";
    public const string ScratchBucket = "LZ_INTAKE_SCRATCH";
    public const string AppSecret = "LZ_INTAKE_APP_SECRET";
    public const string StateMachine = "LZ_INTAKE_MACHINE";
    public const string AlertsTopic = "LZ_INTAKE_ALERTS";
    public const string Gate1Project = "LZ_INTAKE_GATE1_PROJECT";
    // The publish (V-D): where each image goes, and the two stores it writes.
    public const string PublishTargets = "LZ_INTAKE_PUBLISH_TARGETS";
    public const string BuildRecordStore = "LZ_INTAKE_BUILD_RECORD_STORE";
    public const string ArtifactStore = "LZ_INTAKE_ARTIFACT_STORE";
}

/// <summary>
/// The repositories in one environment variable: written by the planner, read by the intake's functions.
///
/// <para>COMPRESSED (V-D, 2026-10-22): <c>gz:</c> and the gzipped JSON in base64. As plain JSON, four repositories with
/// Gate 1 (3,395 bytes) pushed a function's configuration update past the 5,120 bytes Lambda allows one, and the list
/// repeats itself (the same keys, the same SDK hash), so it compresses to about a third. Plain JSON, which a function
/// configured before this carries, still decodes.</para>
/// </summary>
public static class IntakeRepositories
{
    private const string Compressed = "gz:";

    public static string Encode(IReadOnlyList<IntakeRepository> repositories)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize))
            gzip.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(repositories)));
        return Compressed + Convert.ToBase64String(buffer.ToArray());
    }

    public static IReadOnlyList<IntakeRepository> Decode(string value)
    {
        var json = value;
        if (value.StartsWith(Compressed, StringComparison.Ordinal))
        {
            using var gzip = new System.IO.Compression.GZipStream(
                new MemoryStream(Convert.FromBase64String(value[Compressed.Length..])), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        return JsonSerializer.Deserialize<List<IntakeRepository>>(json) is { Count: > 0 } list
            ? list
            : throw new InvalidOperationException("the intake is configured with no repositories");
    }
}

/// <summary>
/// THE INTAKE'S EVIDENCE KEYS (DecoupledCd.md §15.4, §5.3 rule 1), in its own write-once store. An attempt is decided once,
/// under its first-try name; each try's fault and staging are under that try's execution name.
/// </summary>
public static class IntakeEvidence
{
    public static string TakenIn(string attemptName) => $"taken-in/{attemptName}.json";
    public static string Refused(string attemptName) => $"refused/{attemptName}.json";
    public static string Fault(string executionName) => $"faults/{executionName}.json";
    public static string Staged(string executionName) => $"staged/{executionName}.json";
    public static string Gate1(string executionName) => $"gate1/{executionName}.json";
    public static string Alarm(string alarm) => $"alarms/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(alarm)))[..24].ToLowerInvariant()}.json";

    public static readonly string[] DecisionPrefixes = { "taken-in/", "refused/", "faults/" };

    /// <summary>
    /// What the evidence and the intake machine's executions say about each attempt, keyed by first-try name, as
    /// <see cref="IntakePoller.Choose"/> reads it. Running: any try of the attempt is running. Closed without record: its latest
    /// try's execution has closed and recorded neither an outcome nor its own fault.
    /// </summary>
    public static IReadOnlyDictionary<string, IntakeAttemptState> States(
        IEnumerable<string> evidenceKeys, IEnumerable<(string Name, string Status)> executions)
    {
        var outcomes = new Dictionary<string, IntakeOutcome>(StringComparer.Ordinal);
        var faults = new Dictionary<string, int>(StringComparer.Ordinal);       // attempt -> highest faulted try
        var faultedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in evidenceKeys)
        {
            var stem = Stem(key);
            if (key.StartsWith("taken-in/", StringComparison.Ordinal) && IntakeNames.Parse(stem) is { Retry: 0 })
                outcomes[stem] = IntakeOutcome.TakenIn;
            else if (key.StartsWith("refused/", StringComparison.Ordinal) && IntakeNames.Parse(stem) is { Retry: 0 })
                outcomes.TryAdd(stem, IntakeOutcome.Refused);
            else if (key.StartsWith("faults/", StringComparison.Ordinal) && IntakeNames.Parse(stem) is { } f)
            {
                faultedNames.Add(stem);
                var attempt = IntakeNames.For(f.RepositoryId, f.RunId, f.RunAttempt);
                faults[attempt] = Math.Max(faults.GetValueOrDefault(attempt, -1), f.Retry);
            }
        }

        var latest = new Dictionary<string, (int Retry, string Name, string Status)>(StringComparer.Ordinal);
        var running = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, status) in executions)
        {
            if (IntakeNames.Parse(name) is not { } n) continue;
            var attempt = IntakeNames.For(n.RepositoryId, n.RunId, n.RunAttempt);
            if (string.Equals(status, "RUNNING", StringComparison.Ordinal)) running.Add(attempt);
            if (!latest.TryGetValue(attempt, out var l) || n.Retry > l.Retry) latest[attempt] = (n.Retry, name, status);
        }

        var states = new Dictionary<string, IntakeAttemptState>(StringComparer.Ordinal);
        foreach (var attempt in outcomes.Keys.Concat(faults.Keys).Concat(latest.Keys).Distinct(StringComparer.Ordinal))
        {
            var closedWithoutRecord = !outcomes.ContainsKey(attempt) && !running.Contains(attempt)
                && latest.TryGetValue(attempt, out var l) && !faultedNames.Contains(l.Name);
            states[attempt] = new IntakeAttemptState(
                outcomes.TryGetValue(attempt, out var o) ? o : null,
                faults.TryGetValue(attempt, out var k) ? k : null,
                running.Contains(attempt),
                closedWithoutRecord);
        }
        return states;
    }

    private static string Stem(string key)
    {
        var slash = key.IndexOf('/');
        var name = slash >= 0 ? key[(slash + 1)..] : key;
        return name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name;
    }
}

public interface IIntakeRuns
{
    Task<IReadOnlyList<GitHubRun>> CompletedRunsAsync(string repo, string workflowFile, DateTimeOffset since);
}

public interface IIntakeEvidenceStore
{
    Task<IReadOnlyList<string>> KeysAsync(string prefix);

    /// <summary>Write once; false when the key already existed.</summary>
    Task<bool> PutOnceAsync(string key, string body);
}

public interface IIntakeExecutions
{
    /// <summary>The intake machine's executions started at or after <paramref name="since"/>: name and status.</summary>
    Task<IReadOnlyList<(string Name, string Status)>> SinceAsync(DateTimeOffset since);

    /// <summary>Starts an execution by name; false when one by that name already exists.</summary>
    Task<bool> StartAsync(string name, string input);
}

public interface IIntakeAlerts
{
    Task PublishAsync(string subject, string message);
}

/// <summary>
/// THE INTAKE POLLER'S TICK (DecoupledCd.md §15.4, §5.3; V-B): for each repository the intake takes, its build workflow's
/// completed runs on <c>main</c>, what the evidence and the executions say about each attempt, then
/// <see cref="IntakePoller.Choose"/>'s starts and alarms. The intake is started with ids only and reads each run back
/// itself, so nothing in the input is trusted.
///
/// <para><b>AN ALARM IS SENT ONCE</b>: Choose raises an overdue outcome again on every tick, so each alarm is written once to
/// the evidence store under a hash of its text, and published only when that write is new.</para>
///
/// <para><b>NOT BUILT: the conditional requests</b> (§15.4's ETag and list kept in a state item). The poller re-lists each
/// minute; at one request per repository a minute that is 420 an hour for seven, inside the App's 5,000.</para>
/// </summary>
public static class IntakePollStep
{
    /// <summary>How far back the runs are listed: past the artifacts' retention, so a re-run of an older run, which completes
    /// later, is still seen while its artifacts last.</summary>
    public static readonly TimeSpan ListWindow = TimeSpan.FromDays(7);

    public static async Task<JsonObject> RunAsync(
        IReadOnlyList<IntakeRepository> repositories, IIntakeRuns github, IIntakeEvidenceStore evidence,
        IIntakeExecutions executions, IIntakeAlerts alerts, DateTimeOffset now)
    {
        var keys = new List<string>();
        foreach (var prefix in IntakeEvidence.DecisionPrefixes) keys.AddRange(await evidence.KeysAsync(prefix));
        var states = IntakeEvidence.States(keys, await executions.SinceAsync(now - IntakePoller.Retention - TimeSpan.FromDays(1)));

        var started = new JsonArray();
        var alarmed = new JsonArray();
        foreach (var repository in repositories)
        {
            var runs = await github.CompletedRunsAsync(repository.Repo, repository.WorkflowFile, now - ListWindow);
            var plan = IntakePoller.Choose(repository.Source, runs, now, states);

            foreach (var start in plan.Starts)
            {
                var input = new JsonObject
                {
                    ["repo"] = repository.Repo,
                    ["repositoryId"] = repository.RepositoryId,
                    ["runId"] = start.RunId,
                    ["runAttempt"] = start.RunAttempt,
                    ["retry"] = start.Retry,
                };
                if (await executions.StartAsync(start.ExecutionName, input.ToJsonString()))
                    started.Add(start.ExecutionName);
            }

            foreach (var alarm in plan.Alarms)
            {
                var body = new JsonObject { ["alarm"] = alarm, ["at"] = now.ToString("O") }.ToJsonString();
                if (!await evidence.PutOnceAsync(IntakeEvidence.Alarm(alarm), body)) continue;   // sent before
                await alerts.PublishAsync("intake alarm", alarm);
                alarmed.Add(alarm);
            }
        }

        return new JsonObject { ["started"] = started, ["alarmed"] = alarmed };
    }
}
