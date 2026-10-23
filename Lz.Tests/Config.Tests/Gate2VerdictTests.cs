using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Gate 2's verdict (DecoupledCd.md §15.5, V-E), from the result files and never an exit code, since both runners exit 0
/// when tests skip. The TRX shapes are xUnit's VSTest adapter's (v3.1.4, measured 2026-10-23): a skip is NotExecuted, or,
/// under the gate's FailSkips, Failed with "FAIL_SKIP : " before its reason, and the TRX's own counters do not count a
/// NotExecuted result. The Playwright report is the JSON reporter's (1.62.1, the wave-1 run of 2026-10-23).
/// </summary>
public sealed class Gate2VerdictTests
{
    private const string Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>A TRX as the adapter writes one: each result names its definition by testId, and the definition its method.</summary>
    private static string Trx(params (string Class, string Method, string Name, string Outcome, string? Message)[] results)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);   // a theory's quoted arguments, as &quot;
        var rs = string.Join("\n", results.Select((r, i) =>
            $"""<UnitTestResult testId="t{i}" testName="{X(r.Name)}" outcome="{r.Outcome}">""" +
            (r.Message is null ? "" : $"<Output><ErrorInfo><Message>{X(r.Message)}</Message></ErrorInfo></Output>") + "</UnitTestResult>"));
        var defs = string.Join("\n", results.Select((r, i) =>
            $"""<UnitTest name="{X(r.Name)}" id="t{i}"><Execution id="e{i}" /><TestMethod className="{r.Class}" name="{r.Method}" /></UnitTest>"""));
        return $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="{Ns}"><Results>{rs}</Results><TestDefinitions>{defs}</TestDefinitions><ResultSummary outcome="Completed"><Counters total="{results.Length}" executed="0" passed="0" failed="0" notExecuted="0" /></ResultSummary></TestRun>""";
    }

    private const string C = "ServiceTest.E2E.E2ELeadRowsTests", M = "AStaffRebuild";
    private const string Smoke = "ServiceTest.E2E.E2ELiveSmokeTests";
    private static readonly string[] Recorded = { $"{C}.{M}", $"{Smoke}.AuthenticatedCall" };

    private static (string, string, string, string, string?) Passed(string cls, string method, string? args = null)
        => (cls, method, $"{cls}.{method}{args}", "Passed", null);

    // ---- e2e: the TRX ----

    [Fact]
    public void ATheorysCases_AreOneMethod_AndEveryCasePassing_WithTheRecordedMethods_Passes()
    {
        var results = Gate2Trx.Parse(Trx(
            Passed(C, M, "(licence: \"E2E-SOLAR-1\")"), Passed(C, M, "(licence: \"E2E-SOLAR-2\")"),
            Passed(Smoke, "AuthenticatedCall")));

        var verdict = Gate2Trx.Judge("blocking", results, Recorded);

        Assert.Equal(Gate2TierOutcome.Passed, verdict.Outcome);
        Assert.Empty(verdict.Reasons);
        Assert.Equal(3, verdict.Results);
    }

    [Theory]
    [InlineData("NotExecuted", "E2E tests disabled: SCUTARA_TESTS_SKIP_E2E=1.")]   // a skip without FailSkips
    [InlineData("Failed", "FAIL_SKIP : E2E tests disabled: SCUTARA_TESTS_SKIP_E2E=1.")]   // the same skip under it
    public void ASkip_EitherWay_IsIncomplete_NotAFailure(string outcome, string message)
    {
        var verdict = Gate2Trx.Judge("blocking", Gate2Trx.Parse(Trx(
            (C, M, $"{C}.{M}", outcome, message), Passed(Smoke, "AuthenticatedCall"))), Recorded);

        Assert.Equal(Gate2TierOutcome.Incomplete, verdict.Outcome);
        Assert.Contains(verdict.Reasons, r => r.Contains($"{C}.{M}") && r.Contains("skipped") && r.Contains("SCUTARA_TESTS_SKIP_E2E"));
        Assert.Empty(verdict.Failed);
    }

    [Fact]
    public void AFailedCase_FailsTheTier_NamingTheCaseAndItsMessage()
    {
        var verdict = Gate2Trx.Judge("blocking", Gate2Trx.Parse(Trx(
            Passed(C, M, "(licence: \"E2E-SOLAR-1\")"),
            (C, M, $"{C}.{M}(licence: \"E2E-SOLAR-2\")", "Failed", "Assert.Equal() Failure: Expected 0, Actual 1"),
            Passed(Smoke, "AuthenticatedCall"))), Recorded);

        Assert.Equal(Gate2TierOutcome.Failed, verdict.Outcome);
        var failed = Assert.Single(verdict.Failed);
        Assert.Contains("E2E-SOLAR-2", failed);
        Assert.Contains("Expected 0, Actual 1", failed);
    }

    [Fact]
    public void ARecordedMethodThatDidNotRun_OrOneThatRanUnrecorded_IsIncomplete()
    {
        var missing = Gate2Trx.Judge("blocking", Gate2Trx.Parse(Trx(Passed(C, M))), Recorded);
        Assert.Equal(Gate2TierOutcome.Incomplete, missing.Outcome);
        Assert.Contains(missing.Reasons, r => r.Contains($"{Smoke}.AuthenticatedCall") && r.Contains("did not run"));

        var extra = Gate2Trx.Judge("blocking", Gate2Trx.Parse(Trx(Passed(C, M), Passed(Smoke, "AuthenticatedCall"), Passed(Smoke, "Stray"))), Recorded);
        Assert.Equal(Gate2TierOutcome.Incomplete, extra.Outcome);
        Assert.Contains(extra.Reasons, r => r.Contains($"{Smoke}.Stray") && r.Contains("not in Gate 1's list"));
    }

    [Fact]
    public void AnEmptyRun_NeverPasses_EvenAgainstAnEmptyList()
    {
        var verdict = Gate2Trx.Judge("precondition", Gate2Trx.Parse(Trx()), Array.Empty<string>());
        Assert.Equal(Gate2TierOutcome.Incomplete, verdict.Outcome);
        Assert.Contains(verdict.Reasons, r => r.Contains("ran nothing"));
    }

    [Fact]
    public void TheTrxsOwnCounters_AreNotRead()
    {
        // Measured: a run of two NotExecuted results reports executed 0, failed 0, notExecuted 0.
        var verdict = Gate2Trx.Judge("blocking", Gate2Trx.Parse(Trx((C, M, $"{C}.{M}", "NotExecuted", "skipped"))), new[] { $"{C}.{M}" });
        Assert.Equal(Gate2TierOutcome.Incomplete, verdict.Outcome);
    }

    [Fact]
    public void AResultWhoseDefinitionIsMissing_IsAFaultInTheFile_NotAPass()
        => Assert.Throws<Gate2ResultUnreadable>(() => Gate2Trx.Parse(
            $"""<TestRun xmlns="{Ns}"><Results><UnitTestResult testId="nope" testName="x" outcome="Passed" /></Results><TestDefinitions /></TestRun>"""));

    // ---- browser: the Playwright report ----

    private static JsonObject Report(int expected, int unexpected = 0, int flaky = 0, int skipped = 0, JsonArray? errors = null, params (string Title, string Status)[] specs)
        => new()
        {
            ["config"] = new JsonObject(),
            ["suites"] = new JsonArray(new JsonObject
            {
                ["title"] = "seller-console.spec.ts", ["file"] = "seller-console.spec.ts",
                ["specs"] = new JsonArray(specs.Select(s => (JsonNode)new JsonObject
                {
                    ["title"] = s.Title, ["tags"] = new JsonArray("gate"), ["file"] = "seller-console.spec.ts", ["line"] = 45,
                    ["tests"] = new JsonArray(new JsonObject { ["status"] = s.Status, ["projectName"] = "chromium" }),
                }).ToArray()),
            }),
            ["errors"] = errors ?? new JsonArray(),
            ["stats"] = new JsonObject { ["expected"] = expected, ["unexpected"] = unexpected, ["flaky"] = flaky, ["skipped"] = skipped },
        };

    [Fact]
    public void EveryGateTestExpected_WithTheRecordedCount_Passes()
        => Assert.Equal(Gate2TierOutcome.Passed, Gate2Playwright.Judge(Report(12), recordedGateCount: 12).Outcome);

    [Fact]
    public void ATestPassingOnItsRetry_Passes_AndIsListedAsFlaky()
    {
        var verdict = Gate2Playwright.Judge(Report(11, flaky: 1, specs: ("seller console: login → home → leads", "flaky")), 12);

        Assert.Equal(Gate2TierOutcome.Passed, verdict.Outcome);
        Assert.Equal(new[] { "seller-console.spec.ts:45 seller console: login → home → leads" }, verdict.Flaky);
    }

    [Fact]
    public void AnUnexpectedResult_FailsTheTier_NamingTheTest()
    {
        var verdict = Gate2Playwright.Judge(Report(11, unexpected: 1, specs: ("seller console: login → home → leads", "unexpected")), 12);

        Assert.Equal(Gate2TierOutcome.Failed, verdict.Outcome);
        Assert.Equal(new[] { "seller-console.spec.ts:45 seller console: login → home → leads" }, verdict.Failed);
    }

    [Fact]
    public void ASkip_ARunError_OrACountThatIsNotTheRecordedOne_IsIncomplete()
    {
        Assert.Equal(Gate2TierOutcome.Incomplete, Gate2Playwright.Judge(Report(11, skipped: 1), 12).Outcome);
        Assert.Equal(Gate2TierOutcome.Incomplete,
            Gate2Playwright.Judge(Report(12, errors: new JsonArray(new JsonObject { ["message"] = "Error: No tests found" })), 12).Outcome);
        var fewer = Gate2Playwright.Judge(Report(11), 12);
        Assert.Equal(Gate2TierOutcome.Incomplete, fewer.Outcome);
        Assert.Contains(fewer.Reasons, r => r.Contains("11") && r.Contains("12"));
    }

    [Fact]
    public void AnEmptyBrowserRun_NeverPasses()
        => Assert.Equal(Gate2TierOutcome.Incomplete, Gate2Playwright.Judge(Report(0), recordedGateCount: 0).Outcome);

    // ---- the outcome ----

    private static Gate2TierVerdict Tier(string name, Gate2TierOutcome outcome, string? reason = null)
        => new(name, outcome, reason is null ? Array.Empty<string>() : new[] { reason }, 1, outcome == Gate2TierOutcome.Failed ? new[] { reason ?? "x" } : Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void EveryTierPassing_WithTheReadsUnchanged_Passes()
        => Assert.Equal(Gate2Outcome.Passed, Gate2Decision.Decide(true,
            Tier("precondition", Gate2TierOutcome.Passed), new[] { Tier("blocking", Gate2TierOutcome.Passed), Tier("browser", Gate2TierOutcome.Passed) }).Outcome);

    [Fact]
    public void ABlockingFailure_IsPostDeployTestsFailed()
        => Assert.Equal(Gate2Outcome.PostDeployTestsFailed, Gate2Decision.Decide(true,
            Tier("precondition", Gate2TierOutcome.Passed), new[] { Tier("blocking", Gate2TierOutcome.Passed), Tier("browser", Gate2TierOutcome.Failed, "t") }).Outcome);

    [Fact]
    public void AFailedPrecondition_IsTheEnvironments_TestsIncomplete_WhateverTheTiersSaid()
    {
        var decided = Gate2Decision.Decide(true, Tier("precondition", Gate2TierOutcome.Failed, "CdnErrorRegime_Canary"),
            new[] { Tier("blocking", Gate2TierOutcome.Failed, "t") });
        Assert.Equal(Gate2Outcome.TestsIncomplete, decided.Outcome);
        Assert.Contains(decided.Reasons, r => r.Contains("CdnErrorRegime_Canary"));
    }

    [Fact]
    public void ASkipInTheBlockingSet_IsTestsIncomplete()
        => Assert.Equal(Gate2Outcome.TestsIncomplete, Gate2Decision.Decide(true,
            Tier("precondition", Gate2TierOutcome.Passed), new[] { Tier("blocking", Gate2TierOutcome.Incomplete, "skipped") }).Outcome);

    [Fact]
    public void WhatRanChangingDuringTheGate_IsTestsSuperseded_WhateverTheTiersSaid()
        => Assert.Equal(Gate2Outcome.TestsSuperseded, Gate2Decision.Decide(false,
            Tier("precondition", Gate2TierOutcome.Passed), new[] { Tier("blocking", Gate2TierOutcome.Failed, "t") }).Outcome);

    // ---- the advisory set: alerted only on a change ----

    [Fact]
    public void AnAdvisoryResult_IsAChange_OnlyWhenItDiffersFromThePreviousRun()
    {
        var previous = new Dictionary<string, string> { ["a"] = "passed", ["b"] = "failed", ["gone"] = "passed" };
        var current = new Dictionary<string, string> { ["a"] = "passed", ["b"] = "passed", ["new"] = "failed" };

        var changes = Gate2Advisory.Changes(previous, current);

        Assert.Equal(new[] { "b: failed -> passed", "gone: passed -> (not run)", "new: (not run) -> failed" }, changes.Select(c => c.ToString()));
    }

    [Fact]
    public void TheFirstAdvisoryRun_HasNothingToChangeFrom_SoAlertsNothing()
        => Assert.Empty(Gate2Advisory.Changes(null, new Dictionary<string, string> { ["a"] = "failed" }));
}
