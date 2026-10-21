using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>One look at a Gate 1 build: CodeBuild's status, the phase that failed if one did, and its test reports.</summary>
/// <param name="Status">IN_PROGRESS, SUCCEEDED, FAILED, FAULT, TIMED_OUT or STOPPED.</param>
/// <param name="FailedPhase">The first phase that did not succeed, with CodeBuild's message; null when none did.</param>
public sealed record Gate1Build(string Status, string? FailedPhase, IReadOnlyList<string> ReportArns, DateTimeOffset? Started, DateTimeOffset? Ended);

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

    /// <summary>The build's variables. The image list is one "from as" pair per line.</summary>
    public static IReadOnlyDictionary<string, string> VariablesFor(IntakeGate1 gate1, string url, string sha256) => new Dictionary<string, string>
    {
        ["BUNDLE_URL"] = url,
        ["BUNDLE_SHA256"] = sha256,
        ["GATE1_RUN"] = gate1.Run,
        ["GATE1_FILTER"] = gate1.Filter,
        ["SDK_VERSION"] = gate1.SdkVersion,
        ["SDK_SHA512"] = gate1.SdkSha512,
        ["GATE1_IMAGES"] = string.Join("\n", gate1.Images.Select(i => $"{i.From} {i.As}")),
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
/// manifest digest and every staged artifact's SHA-256, from the fetch. A failure names up to twenty failed tests.
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
            ["at"] = now.ToString("O"),
        };
        await evidence.PutOnceAsync(IntakeEvidence.Gate1(executionName), verdict.ToJsonString());   // there already: a retry of this step
        return verdict;
    }
}
