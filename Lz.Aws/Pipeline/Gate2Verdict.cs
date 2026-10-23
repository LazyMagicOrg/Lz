using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  GATE 2'S VERDICT (DecoupledCd.md §15.5, V-E), as pure functions over the result files: never an exit code, since
//  both runners exit 0 when tests skip. Each tier's file is judged to Passed, Failed or Incomplete; Gate2Decision turns
//  the tiers and the before/after reads into the outcome the result state writes.
// ---------------------------------------------------------------------------------------------

/// <summary>A tier's verdict. Failed is a decided failure of the build; Incomplete is not a decision against it.</summary>
public enum Gate2TierOutcome { Passed, Failed, Incomplete }

/// <summary>One tier's verdict: why it is not Passed, how many results it read, the failed tests with their messages, and
/// the tests that passed only on their retry (alerted, F22).</summary>
public sealed record Gate2TierVerdict(string Tier, Gate2TierOutcome Outcome, IReadOnlyList<string> Reasons, int Results,
    IReadOnlyList<string> Failed, IReadOnlyList<string> Flaky);

/// <summary>A result file the verdict cannot read as one: a fault, retried, never a pass.</summary>
public sealed class Gate2ResultUnreadable(string message) : Exception(message);

/// <summary>One TRX result: its test method (class and method, a theory's arguments apart), its own name, outcome and message.</summary>
public sealed record TrxResult(string Method, string Name, string Outcome, string? Message)
{
    public const string FailSkipPrefix = "FAIL_SKIP : ";

    /// <summary>A skip, either way xUnit writes one: NotExecuted, or under FailSkips a failure marked FAIL_SKIP.</summary>
    public bool IsSkip => Outcome == "NotExecuted" || (Message?.StartsWith(FailSkipPrefix, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// THE e2e TIERS' TRX. The methods that ran must equal the list Gate 1 recorded from the same bundle under the same fixed
/// filter — METHODS, not cases, since discovery without a live environment lists an e2e theory once — and every result,
/// each case of a theory included, must be Passed. The TRX's counters are never read: measured, they count no
/// NotExecuted result.
/// </summary>
public static class Gate2Trx
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TrxResult> Parse(string trx)
    {
        XElement root;
        try { root = XDocument.Parse(trx).Root ?? throw new Gate2ResultUnreadable("the TRX has no root"); }
        catch (System.Xml.XmlException ex) { throw new Gate2ResultUnreadable($"the TRX is not XML: {ex.Message}"); }

        var methods = root.Descendants(Ns + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => u.Element(Ns + "TestMethod") is { } m ? $"{(string?)m.Attribute("className")}.{(string?)m.Attribute("name")}" : null);

        return root.Descendants(Ns + "UnitTestResult").Select(r =>
        {
            var id = (string?)r.Attribute("testId") ?? "";
            var method = methods.GetValueOrDefault(id)
                ?? throw new Gate2ResultUnreadable($"the TRX's result {(string?)r.Attribute("testName")} names no test definition");
            return new TrxResult(method, (string?)r.Attribute("testName") ?? method, (string?)r.Attribute("outcome") ?? "",
                (string?)r.Element(Ns + "Output")?.Element(Ns + "ErrorInfo")?.Element(Ns + "Message"));
        }).ToList();
    }

    public static Gate2TierVerdict Judge(string tier, IReadOnlyList<TrxResult> results, IReadOnlyList<string> recordedMethods)
    {
        var reasons = new List<string>();
        if (results.Count == 0) reasons.Add($"the {tier} run ran nothing");

        foreach (var skip in results.Where(r => r.IsSkip))
            reasons.Add($"{skip.Name} skipped: {Trim(skip.Message?.StartsWith(TrxResult.FailSkipPrefix, StringComparison.Ordinal) == true ? skip.Message[TrxResult.FailSkipPrefix.Length..] : skip.Message)}");

        var failed = results.Where(r => !r.IsSkip && r.Outcome != "Passed").Select(r => $"{r.Name}: {r.Outcome}: {Trim(r.Message)}").ToList();

        var ran = results.Select(r => r.Method).ToHashSet(StringComparer.Ordinal);
        foreach (var m in recordedMethods.Where(m => !ran.Contains(m))) reasons.Add($"{m} is in Gate 1's list and did not run");
        foreach (var m in ran.Where(m => !recordedMethods.Contains(m, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            reasons.Add($"{m} ran and is not in Gate 1's list");

        var outcome = failed.Count > 0 ? Gate2TierOutcome.Failed : reasons.Count > 0 ? Gate2TierOutcome.Incomplete : Gate2TierOutcome.Passed;
        return new(tier, outcome, reasons, results.Count, failed, Array.Empty<string>());
    }

    private static string Trim(string? message)
    {
        var m = (message ?? "(no message)").Replace("\r", " ").Replace("\n", " ").Trim();
        return m.Length > 300 ? m[..300] + "…" : m;
    }
}

/// <summary>
/// THE BROWSER TIER'S PLAYWRIGHT REPORT, from <c>--grep @gate --forbid-only --retries=1 --reporter=json</c>: it passes
/// only if <c>errors</c> is empty, <c>unexpected</c> and <c>skipped</c> are 0, and <c>expected</c> plus <c>flaky</c>
/// equals the <c>@gate</c> count Gate 1 recorded. A test passing on its retry counts as passing, and is listed.
/// </summary>
public static class Gate2Playwright
{
    public static Gate2TierVerdict Judge(JsonObject report, int recordedGateCount)
    {
        var stats = report["stats"] as JsonObject ?? throw new Gate2ResultUnreadable("the Playwright report has no stats");
        int Stat(string name) => stats[name]?.GetValue<int>() ?? throw new Gate2ResultUnreadable($"the Playwright report's stats have no {name}");
        var (expected, unexpected, flaky, skipped) = (Stat("expected"), Stat("unexpected"), Stat("flaky"), Stat("skipped"));

        var reasons = new List<string>();
        var errors = report["errors"] as JsonArray ?? new JsonArray();
        foreach (var e in errors) reasons.Add($"the run reported an error: {e?["message"]?.GetValue<string>() ?? e?.ToJsonString()}");
        if (skipped > 0) reasons.Add($"{skipped} gate test(s) skipped");
        if (expected + flaky == 0) reasons.Add("the browser run ran nothing");
        else if (expected + flaky + unexpected != recordedGateCount)
            reasons.Add($"{expected + flaky + unexpected} gate test(s) ran, and Gate 1 recorded {recordedGateCount}");

        var failed = SpecsWith(report, "unexpected");
        if (unexpected > 0 && failed.Count == 0) failed = new[] { $"{unexpected} unexpected result(s)" };

        var outcome = unexpected > 0 ? Gate2TierOutcome.Failed : reasons.Count > 0 ? Gate2TierOutcome.Incomplete : Gate2TierOutcome.Passed;
        return new("browser", outcome, reasons, expected + flaky + unexpected + skipped, failed, SpecsWith(report, "flaky"));
    }

    private static IReadOnlyList<string> SpecsWith(JsonObject report, string status)
    {
        var found = new List<string>();
        void Walk(JsonArray? suites)
        {
            foreach (var suite in (suites ?? new JsonArray()).OfType<JsonObject>())
            {
                foreach (var spec in (suite["specs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if ((spec["tests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(t => t["status"]?.GetValue<string>() == status))
                        found.Add($"{spec["file"]?.GetValue<string>()}:{spec["line"]?.GetValue<int>()} {spec["title"]?.GetValue<string>()}");
                Walk(suite["suites"] as JsonArray);
            }
        }
        Walk(report["suites"] as JsonArray);
        return found;
    }
}

/// <summary>What the result state writes: a pass, a decided failure, not decided, or tests that ran against other code.</summary>
public enum Gate2Outcome { Passed, PostDeployTestsFailed, TestsIncomplete, TestsSuperseded }

/// <summary>
/// THE OUTCOME (§15.5). The before-and-after reads first: if what runs changed during the gate, TestsSuperseded, whatever
/// the tiers said, since a build is never promoted on, nor failed by, tests that ran against other code. Then the
/// preconditions, which test the environment: any not passing is TestsIncomplete. Then the blocking tiers: a failure is
/// PostDeployTestsFailed, anything incomplete is TestsIncomplete, and every tier passing is a Pass.
/// </summary>
public static class Gate2Decision
{
    public static (Gate2Outcome Outcome, IReadOnlyList<string> Reasons) Decide(
        bool readsUnchanged, Gate2TierVerdict preconditions, IReadOnlyList<Gate2TierVerdict> blocking)
    {
        if (!readsUnchanged)
            return (Gate2Outcome.TestsSuperseded, new[] { "what the environment runs changed during the gate" });

        if (preconditions.Outcome != Gate2TierOutcome.Passed)
            return (Gate2Outcome.TestsIncomplete,
                new[] { "a precondition did not pass, so the environment, not the build, is in question" }
                    .Concat(preconditions.Reasons).Concat(preconditions.Failed).ToList());

        var failed = blocking.Where(t => t.Outcome == Gate2TierOutcome.Failed).ToList();
        if (failed.Count > 0)
            return (Gate2Outcome.PostDeployTestsFailed, failed.SelectMany(t => t.Failed.Select(f => $"{t.Tier}: {f}")).ToList());

        var incomplete = blocking.Where(t => t.Outcome == Gate2TierOutcome.Incomplete).ToList();
        if (incomplete.Count > 0)
            return (Gate2Outcome.TestsIncomplete, incomplete.SelectMany(t => t.Reasons.Select(r => $"{t.Tier}: {r}")).ToList());

        return (Gate2Outcome.Passed, Array.Empty<string>());
    }
}

/// <summary>An advisory test whose result differs from the previous run's.</summary>
public sealed record Gate2AdvisoryChange(string Test, string? Was, string? Now)
{
    public override string ToString() => $"{Test}: {Was ?? "(not run)"} -> {Now ?? "(not run)"}";
}

/// <summary>
/// THE ADVISORY SET (§15.5, the review's F41): recorded every run, ALERTED ONLY WHEN A RESULT CHANGES from the previous
/// run's, including a test that appears or stops running; never blocking. A first run has nothing to change from.
/// </summary>
public static class Gate2Advisory
{
    public static IReadOnlyList<Gate2AdvisoryChange> Changes(IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> current)
    {
        if (previous is null) return Array.Empty<Gate2AdvisoryChange>();
        return previous.Keys.Union(current.Keys).Order(StringComparer.Ordinal)
            .Select(t => new Gate2AdvisoryChange(t, previous.GetValueOrDefault(t), current.GetValueOrDefault(t)))
            .Where(c => c.Was != c.Now)
            .ToList();
    }
}
