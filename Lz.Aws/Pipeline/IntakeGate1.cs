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

    /// <summary>
    /// The log lines the verdict is given: only those with the prefix, so every marker a judge reads - the lists, the
    /// collection's count, the vitest run's counts - starts with it. A marker without it never reaches its judge.
    /// </summary>
    public static bool Kept(string message) => message.StartsWith(Prefix, StringComparison.Ordinal);

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
/// WHAT A GATE 1 BUILD TESTS, FOR ITS LOG: the run and its commit, the intake execution that started it, and every artifact
/// the fetch staged with its SHA-256 — the ones Publish later holds each file to — which the buildspec prints before anything
/// else. DISPLAY ONLY: the build checks the test bundle by BUNDLE_SHA256 alone and trusts none of these. A field the state
/// does not hold is printed as such rather than refused, since nothing decides on it.
/// </summary>
public sealed record Gate1Header(string Repo, long RunId, int RunAttempt, string Commit, string WorkflowRef, string Execution, string Tests,
    IReadOnlyList<(string Artifact, string Sha256)> Staged)
{
    public const string Absent = "(not in the state)";

    public static Gate1Header From(JsonObject state, string executionName)
    {
        var input = IntakeInput.From(state);
        static string Read(JsonNode? node) => node?.GetValue<string>() is { Length: > 0 } v ? v : Absent;
        var staged = (state["fetch"]?["staged"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(a => (Read(a["artifact"]), Read(a["sha256"]))).ToList();
        return new Gate1Header(input.Repo, input.RunId, input.RunAttempt, Read(state["check"]?["headSha"]), Read(state["check"]?["workflowRef"]),
            executionName, Read(state["fetch"]?["tests"]?["artifact"]), staged);
    }

    /// <summary>The build's INTAKE_ variables; the staged list is one "artifact sha256" pair per line.</summary>
    public IReadOnlyDictionary<string, string> Variables() => new Dictionary<string, string>
    {
        ["INTAKE_REPO"] = Repo,
        ["INTAKE_RUN_ID"] = RunId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["INTAKE_RUN_ATTEMPT"] = RunAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["INTAKE_COMMIT"] = Commit,
        ["INTAKE_WORKFLOW_REF"] = WorkflowRef,
        ["INTAKE_EXECUTION"] = Execution,
        ["INTAKE_TESTS"] = Tests,
        ["INTAKE_ARTIFACTS"] = string.Join("\n", Staged.Select(a => $"{a.Artifact} {a.Sha256}")),
    };
}

/// <summary>
/// GATE 1, STARTED (DecoupledCd.md §15.4, V-C): the test bundle the fetch staged, handed to the CodeBuild project by a URL
/// that grants that one object for fifteen minutes, with its SHA-256, which the build checks before it unzips; and how to
/// run it, from the build-side file; and, for its log, what it tests (<see cref="Gate1Header"/>). Gate 1's own role holds no
/// S3 right at all.
/// </summary>
public static class IntakeGate1StartStep
{
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(15);
    public const string DefaultRoot = "/tmp/bundle";

    /// <summary>The build's variables: config's own first, then the header's and Gate 1's, which config cannot name. The image list is one "from as" pair per line.</summary>
    public static IReadOnlyDictionary<string, string> VariablesFor(IntakeGate1 gate1, string url, string sha256, Gate1Header header)
    {
        var variables = new Dictionary<string, string>(gate1.Environment ?? new Dictionary<string, string>());
        foreach (var (name, value) in header.Variables()) variables[name] = value;
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
        // A test run of the bundle, or the browser tests' collection check (§15.4): the buildspec branches on it.
        ["GATE1_KIND"] = gate1.Kind,
    };

    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IReadOnlyList<IntakeRepository> repositories, IScratchPresigner presigner, IGate1Builds builds, string project)
    {
        var input = IntakeInput.From(state);
        var gate1 = input.In(repositories)?.Gate1 ?? throw new IntakeFault($"{input.Repo} has no Gate 1; the machine should not have started one");
        var tests = state["fetch"]?["tests"] as JsonObject ?? throw new IntakeFault("the fetch staged no test bundle for Gate 1");
        var key = tests["key"]!.GetValue<string>();
        var sha256 = tests["sha256"]!.GetValue<string>();

        var buildId = await builds.StartAsync(project, VariablesFor(gate1, presigner.GetUrl(key, UrlLifetime), sha256, Gate1Header.From(state, executionName)));
        return new JsonObject { ["buildId"] = buildId, ["bundle"] = key, ["bundleSha256"] = sha256, ["kind"] = gate1.Kind };
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
/// THE BROWSER TESTS' GATE 1, JUDGED (DecoupledCd.md §15.4): their collection, which writes no test report, so it is judged from
/// its log's <c>GATE1-LIST-COLLECTED</c> line. A pass is a build that succeeded and collected specs with at least one <c>@gate</c>
/// test, whose count Gate 2's browser verdict is held to. A build that failed in its BUILD phase — a spec that does not load,
/// or one marked <c>.only</c>, which <c>--forbid-only</c> refuses — is a decided FAILURE. Anything else decided nothing, and is
/// a fault, retried: a failure in an earlier phase (the bundle, npm), or a success that printed no count.
/// </summary>
public static class Gate1CollectionJudge
{
    public const string Marker = Gate2Lists.Prefix + "-COLLECTED";

    public static (Gate1Outcome Outcome, string Reason, int? GateCount) Judge(Gate1Build build, IReadOnlyList<string> lines)
    {
        if (build.Status == "IN_PROGRESS") return (Gate1Outcome.Running, "the collection check is running", null);
        if (build.Status == "FAILED" && build.FailedPhase?.StartsWith("BUILD", StringComparison.Ordinal) == true)
            return (Gate1Outcome.Failed, $"the specs do not collect: {build.FailedPhase}", null);
        if (build.Status != "SUCCEEDED")
            return (Gate1Outcome.Fault, $"the collection check ended {build.Status} before collecting{(build.FailedPhase is { } p ? $": {p}" : "")}", null);

        var count = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, $@"^{Marker} total (\d+) gate (\d+)$"))
            .FirstOrDefault(m => m.Success);
        if (count is null) return (Gate1Outcome.Fault, "the collection check succeeded and printed no count", null);
        var (total, gate) = (int.Parse(count.Groups[1].Value), int.Parse(count.Groups[2].Value));
        if (total == 0) return (Gate1Outcome.Fault, "the collection check collected no spec", null);
        if (gate == 0) return (Gate1Outcome.Fault, $"{total} test(s) collected and none tagged @gate, so Gate 2's browser tier would run nothing", null);
        return (Gate1Outcome.Passed, $"{total} test(s) collected, {gate} tagged @gate", gate);
    }
}

/// <summary>
/// A NODE REPOSITORY'S GATE 1, JUDGED (BuyerAppReactPunchlist BR-P3): its vitest run, which writes no test report, so it is
/// judged from its log's <c>GATE1-LIST-VITEST</c> line, printed whether the tests passed or not. A failed test, or a test file
/// that failed to load, is a decided FAILURE whatever the build says. A build that succeeded and counted at least one test,
/// every one passing, is a PASS. Anything else decided nothing, and is a fault, retried: a failure before the run (the
/// bundle, npm), a run that printed no count or an unreadable one, or one that ran no test.
/// </summary>
public static class Gate1VitestJudge
{
    public const string Marker = Gate2Lists.Prefix + "-VITEST";

    public static (Gate1Outcome Outcome, string Reason, IReadOnlyDictionary<string, int>? Counts) Judge(Gate1Build build, IReadOnlyList<string> lines)
    {
        if (build.Status == "IN_PROGRESS") return (Gate1Outcome.Running, "the vitest run is running", null);
        var count = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, $@"^{Marker} total (\d+) passed (\d+) failed (\d+) suites-failed (\d+)$"))
            .FirstOrDefault(m => m.Success);
        if (count is null)
            return (Gate1Outcome.Fault, $"the vitest run ended {build.Status} and printed no count{(build.FailedPhase is { } p ? $": {p}" : "")}", null);
        var (total, passed, failed, suites) = (int.Parse(count.Groups[1].Value), int.Parse(count.Groups[2].Value),
            int.Parse(count.Groups[3].Value), int.Parse(count.Groups[4].Value));
        var counts = new Dictionary<string, int> { ["SUCCEEDED"] = passed, ["FAILED"] = failed };
        if (failed > 0) return (Gate1Outcome.Failed, $"{failed} of {total} tests failed", counts);
        if (suites > 0) return (Gate1Outcome.Failed, $"{suites} test file(s) failed to run", counts);
        if (build.Status != "SUCCEEDED")
            return (Gate1Outcome.Fault, $"the vitest run ended {build.Status} with no failed test{(build.FailedPhase is { } p2 ? $": {p2}" : "")}", counts);
        if (total == 0) return (Gate1Outcome.Fault, "the vitest run succeeded and ran no test", counts);
        return (Gate1Outcome.Passed, $"{passed} of {total} tests passed", counts);
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

        // A COLLECTION CHECK writes no test report: it is judged from its log, and its @gate count is what it records.
        var kind = state["gate1Start"]?["kind"]?.GetValue<string>();
        var collection = kind == "collection";
        // A VITEST RUN writes no test report either: judged from its log, its counts are what it records.
        var vitest = kind == "vitest";
        Gate1Report? report = null;
        int? gateCount = null;
        IReadOnlyDictionary<string, int>? logCounts = null;
        Gate1Outcome outcome;
        string reason;
        if (collection)
            (outcome, reason, gateCount) = Gate1CollectionJudge.Judge(build,
                build.Status == "IN_PROGRESS" ? Array.Empty<string>() : await builds.ListLinesAsync(build));
        else if (vitest)
            (outcome, reason, logCounts) = Gate1VitestJudge.Judge(build,
                build.Status == "IN_PROGRESS" ? Array.Empty<string>() : await builds.ListLinesAsync(build));
        else
        {
            report = build.Status == "IN_PROGRESS" ? null : await builds.ReportAsync(build.ReportArns);
            (outcome, reason) = Gate1Judge.Judge(build, report);
        }

        switch (outcome)
        {
            case Gate1Outcome.Running: throw new Gate1StillRunning($"{buildId}: {reason}");
            case Gate1Outcome.Fault: throw new IntakeFault($"Gate 1 {buildId}: {reason}");
        }

        // Gate 2's lists come from a .NET run only: a collection or a vitest run gives Gate 2 nothing to hold results to.
        var dotnet = !collection && !vitest;
        var lists = dotnet ? Gate2Lists.Parse(await builds.ListLinesAsync(build)) : null;
        if (dotnet && outcome == Gate1Outcome.Passed && lists is null)
            throw new IntakeFault($"Gate 1 {buildId}: {reason}, but its log holds no complete Gate 2 lists");

        var failing = dotnet && outcome == Gate1Outcome.Failed ? await builds.FailedCasesAsync(build.ReportArns, 20) : Array.Empty<string>();
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
            ["counts"] = report is not null ? new JsonObject(report.StatusCounts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value)))
                : logCounts is not null ? new JsonObject(logCounts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))) : null,
            ["gateCount"] = gateCount,
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
