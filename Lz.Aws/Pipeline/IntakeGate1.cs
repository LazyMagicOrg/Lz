using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>One look at a Gate 1 build: CodeBuild's status, the phase that failed if one did, and its test reports.</summary>
/// <param name="Status">IN_PROGRESS, SUCCEEDED, FAILED, FAULT, TIMED_OUT or STOPPED.</param>
/// <param name="FailedPhase">The first phase that did not succeed, with CodeBuild's message; null when none did.</param>
/// <param name="LogGroup">The build's CloudWatch log group and stream, where it prints Gate 2's lists; null before it has one.</param>
public sealed record Gate1Build(string Status, string? FailedPhase, IReadOnlyList<string> ReportArns, DateTimeOffset? Started, DateTimeOffset? Ended,
    string? LogGroup = null, string? LogStream = null);

/// <summary>A test report's counts by status, as CodeBuild keeps them (SUCCEEDED, FAILED, SKIPPED, ERROR, UNKNOWN).</summary>
public sealed record Gate1Report(int Total, IReadOnlyDictionary<string, int> StatusCounts);

/// <summary>What Gate 1's functions ask of CodeBuild.</summary>
public interface IGate1Builds
{
    /// <summary>Starts the project's build with these variables; the build's id.</summary>
    Task<string> StartAsync(string project, IReadOnlyDictionary<string, string> variables);

    Task<Gate1Build> DescribeAsync(string buildId);

    /// <summary>The reports' counts, summed; null when none exists.</summary>
    Task<Gate1Report?> ReportAsync(IReadOnlyList<string> reportArns);

    /// <summary>Up to <paramref name="max"/> names of the reports' failed test cases.</summary>
    Task<IReadOnlyList<string>> FailedCasesAsync(IReadOnlyList<string> reportArns, int max);

    /// <summary>The build log's lines that start with <see cref="Gate2Lists.Prefix"/>, in the order printed; empty when it has no log.</summary>
    Task<IReadOnlyList<string>> ListLinesAsync(Gate1Build build);
}

/// <summary>
/// GATE 2'S FIXED FILTERS, which Gate 1 lists (DecoupledCd.md §15.5, V-C). Fixed here, not settings: the preconditions, the
/// blocking set and the advisory set of the e2e tier, by each test's <c>Gate2</c> trait (a test marked <c>Out</c> is in none).
/// Gate 1 lists each from the build's own bundle BEFORE it runs a test, printing one block per list into its log, and the
/// verdict records them, so Gate 2 can require its results to be exactly the list the build was gated with. The block is as
/// trustworthy as the reports: both come out of a container running the repository's own code.
/// </summary>
public static class Gate2Lists
{
    public const string Prefix = "GATE1-LIST";

    public static readonly IReadOnlyList<(string Name, string Filter)> Filters = new[]
    {
        ("precondition", "Category=E2E&Gate2=Precondition"),
        ("blocking", "Category=E2E&Gate2=Blocking"),
        ("advisory", "Category=E2E&Gate2=Advisory"),
    };

    /// <summary>The build variable: one "name filter" pair per line.</summary>
    public static string Variable() => string.Join("\n", Filters.Select(f => $"{f.Name} {f.Filter}"));

    /// <summary>
    /// A listed test's METHOD: a theory's case without its arguments. The lists are of methods because what discovery
    /// gives depends on where it runs (measured 2026-10-21): an e2e theory with no live environment is skipped and listed
    /// once, as in Gate 1, and with one, as in Gate 2, once per case. Gate 2 compares its results' methods with them.
    /// </summary>
    public static string Method(string listed) => listed.IndexOf('(') is var i and >= 0 ? listed[..i] : listed;

    /// <summary>
    /// Each list from the FIRST complete block of its name — <c>GATE1-LIST-BEGIN name</c>, then <c>GATE1-LIST name test</c>
    /// lines, then <c>GATE1-LIST-END name</c> — so a test printing a block later cannot replace one; null unless every list
    /// has a complete block. A list may be empty: a repository with no e2e tests.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Parse(IEnumerable<string> lines)
    {
        var done = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string? open = null;
        List<string>? names = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.StartsWith(Prefix + "-BEGIN ", StringComparison.Ordinal))
            {
                var name = line[(Prefix.Length + 7)..];
                if (open is null && !done.ContainsKey(name)) (open, names) = (name, new List<string>());
            }
            else if (line.StartsWith(Prefix + "-END ", StringComparison.Ordinal))
            {
                if (open is not null && line[(Prefix.Length + 5)..] == open) { done[open] = names!; (open, names) = (null, null); }
            }
            else if (open is not null && line.StartsWith($"{Prefix} {open} ", StringComparison.Ordinal)
                     && Method(line[(Prefix.Length + open.Length + 2)..]) is var method && !names!.Contains(method))
                names!.Add(method);
        }
        return Filters.All(f => done.ContainsKey(f.Name)) ? Filters.ToDictionary(f => f.Name, f => done[f.Name]) : null;
    }
}

/// <summary>A short-lived URL for one object in the scratch bucket, which grants that object and nothing more.</summary>
public interface IScratchPresigner
{
    string GetUrl(string key, TimeSpan lifetime);
}

/// <summary>The Gate 1 build is still running. RETRIED by the machine; the retry limit is Gate 1's timeout.</summary>
public sealed class Gate1StillRunning(string message) : Exception(message);

/// <summary>
/// GATE 1, STARTED (DecoupledCd.md §15.4, V-C): the test bundle the fetch staged, handed to the CodeBuild project by a URL
/// that grants that one object for fifteen minutes, with its SHA-256, which the build checks before it unzips; and how to
/// run it, from the build-side file. Gate 1's own role holds no S3 right at all.
/// </summary>
public static class IntakeGate1StartStep
{
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(15);
    public const string DefaultRoot = "/tmp/bundle";

    /// <summary>The build's variables: config's own first, then Gate 1's, which config cannot name. The image list is one "from as" pair per line.</summary>
    public static IReadOnlyDictionary<string, string> VariablesFor(IntakeGate1 gate1, string url, string sha256)
    {
        var variables = new Dictionary<string, string>(gate1.Environment ?? new Dictionary<string, string>());
        foreach (var (name, value) in Gate1Variables(gate1, url, sha256)) variables[name] = value;   // Gate 1's own always win
        return variables;
    }

    private static Dictionary<string, string> Gate1Variables(IntakeGate1 gate1, string url, string sha256) => new()
    {
        ["BUNDLE_URL"] = url,
        ["BUNDLE_SHA256"] = sha256,
        ["GATE1_RUN"] = gate1.Run,
        // The path the bundle was built at, so a test that reads its source by [CallerFilePath] finds it (measured: two did not).
        ["GATE1_ROOT"] = gate1.BuildRoot ?? DefaultRoot,
        ["GATE1_FILTER"] = gate1.Filter,
        ["SDK_VERSION"] = gate1.SdkVersion,
        ["SDK_SHA512"] = gate1.SdkSha512,
        ["GATE1_IMAGES"] = string.Join("\n", gate1.Images.Select(i => $"{i.From} {i.As}")),
        ["GATE1_LISTS"] = Gate2Lists.Variable(),
    };

    public static async Task<JsonObject> RunAsync(
        JsonObject state, IReadOnlyList<IntakeRepository> repositories, IScratchPresigner presigner, IGate1Builds builds, string project)
    {
        var input = IntakeInput.From(state);
        var gate1 = input.In(repositories)?.Gate1 ?? throw new IntakeFault($"{input.Repo} has no Gate 1; the machine should not have started one");
        var tests = state["fetch"]?["tests"] as JsonObject ?? throw new IntakeFault("the fetch staged no test bundle for Gate 1");
        var key = tests["key"]!.GetValue<string>();
        var sha256 = tests["sha256"]!.GetValue<string>();

        var buildId = await builds.StartAsync(project, VariablesFor(gate1, presigner.GetUrl(key, UrlLifetime), sha256));
        return new JsonObject { ["buildId"] = buildId, ["bundle"] = key, ["bundleSha256"] = sha256 };
    }
}

/// <summary>Gate 1's answer: passed, failed (a decided refusal), still running, or a fault the poller retries.</summary>
public enum Gate1Outcome { Running, Passed, Failed, Fault }

/// <summary>
/// GATE 1'S VERDICT, as a pure function (DecoupledCd.md §15.4, V-C). A report with a failed test is a FAILED Gate 1,
/// whatever the build says: decided, recorded and alerted, never retried. Every test passing in a build that succeeded is a
/// PASS. Anything else is a FAULT, retried: a build that failed before it ran a test (the SDK, the download, the bundle's
/// SHA-256), one that ran nothing, or one that timed out or was stopped. A bundle whose bytes do not match is therefore
/// retried, and on its last retry alarms for a person.
/// </summary>
public static class Gate1Judge
{
    public static (Gate1Outcome Outcome, string Reason) Judge(Gate1Build build, Gate1Report? report)
    {
        if (build.Status == "IN_PROGRESS") return (Gate1Outcome.Running, "the Gate 1 build is running");

        var failed = report is null ? 0 : report.StatusCounts.GetValueOrDefault("FAILED") + report.StatusCounts.GetValueOrDefault("ERROR");
        if (failed > 0)
            return (Gate1Outcome.Failed, $"{failed} of {report!.Total} tests failed");

        if (build.Status == "SUCCEEDED")
            return report is { Total: > 0 }
                ? (Gate1Outcome.Passed, $"{report.StatusCounts.GetValueOrDefault("SUCCEEDED")} of {report.Total} tests passed")
                : (Gate1Outcome.Fault, "the build succeeded and reported no tests, so it ran nothing");

        return (Gate1Outcome.Fault,
            $"the build ended {build.Status} without a failed test{(build.FailedPhase is { } p ? $": {p}" : "")}");
    }
}

/// <summary>
/// GATE 1, DECIDED (V-C): one look at the build; while it runs, <see cref="Gate1StillRunning"/>; when it ends, the verdict
/// written once to the intake's evidence under <c>gate1/{executionName}</c>, BINDING THE BYTES IT RAN FOR: the image's
/// manifest digest and every staged artifact's SHA-256, from the fetch. A failure names up to twenty failed tests. Gate 2's
/// lists, read from the build's log, go with it; a PASS without them is a fault, since Gate 2 could not check its results.
/// </summary>
public static class IntakeGate1VerdictStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IGate1Builds builds, IIntakeEvidenceStore evidence, DateTimeOffset now)
    {
        var buildId = state["gate1Start"]?["buildId"]?.GetValue<string>() ?? throw new IntakeFault("no Gate 1 build was started");
        var build = await builds.DescribeAsync(buildId);
        var report = build.Status == "IN_PROGRESS" ? null : await builds.ReportAsync(build.ReportArns);
        var (outcome, reason) = Gate1Judge.Judge(build, report);

        switch (outcome)
        {
            case Gate1Outcome.Running: throw new Gate1StillRunning($"{buildId}: {reason}");
            case Gate1Outcome.Fault: throw new IntakeFault($"Gate 1 {buildId}: {reason}");
        }

        var lists = Gate2Lists.Parse(await builds.ListLinesAsync(build));
        if (outcome == Gate1Outcome.Passed && lists is null)
            throw new IntakeFault($"Gate 1 {buildId}: {reason}, but its log holds no complete Gate 2 lists");

        var failing = outcome == Gate1Outcome.Failed ? await builds.FailedCasesAsync(build.ReportArns, 20) : Array.Empty<string>();
        var fetch = state["fetch"] as JsonObject;
        var verdict = new JsonObject
        {
            ["verdict"] = outcome == Gate1Outcome.Passed ? "passed" : "failed",
            ["reasons"] = outcome == Gate1Outcome.Failed
                ? new JsonArray(new[] { $"Gate 1 failed: {reason}" }.Concat(failing).Select(s => (JsonNode?)s).ToArray())
                : null,
            ["summary"] = reason,
            ["buildId"] = buildId,
            ["seconds"] = build.Started is { } s && build.Ended is { } e ? (int)(e - s).TotalSeconds : null,
            ["counts"] = new JsonObject(report!.StatusCounts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
            ["manifestDigest"] = fetch?["manifestDigest"]?.DeepClone(),
            ["artifactSha256"] = new JsonObject((fetch?["staged"] as JsonArray ?? new JsonArray())
                .Select(a => KeyValuePair.Create(a!["artifact"]!.GetValue<string>(), (JsonNode?)a["sha256"]!.GetValue<string>()))),
            ["gate2Lists"] = lists is null ? null : new JsonObject(lists.Select(l => KeyValuePair.Create(
                l.Key, (JsonNode?)new JsonArray(l.Value.Select(n => (JsonNode?)n).ToArray())))),
            ["at"] = now.ToString("O"),
        };
        await evidence.PutOnceAsync(IntakeEvidence.Gate1(executionName), verdict.ToJsonString());   // there already: a retry of this step
        return verdict;
    }
}
