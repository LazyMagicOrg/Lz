namespace Lz.Aws.Verification;

/// <summary>
/// The pure <c>--expect</c> verdict behind <c>lz verify</c>. Extracted from the
/// CLI handler so the load-bearing rules are pinned by unit tests — in particular
/// that the runtime smoke gate cannot silently drop out of the 'deployed' verdict
/// (E2ETestPlan P0.7), and that smoke probes can never veto a teardown.
/// </summary>
public static class VerifyVerdict
{
    /// <summary>
    /// Compute whether the expectation holds over the full (unfiltered) result set.
    /// Returns null when <paramref name="expect"/> is null or unrecognized.
    ///
    /// 'deployed': every Stack resource Present AND every Smoke probe Present —
    /// resources existing but the public surfaces misbehaving is NOT deployed.
    /// Tombstoned secrets count as "not cleanly present".
    ///
    /// 'destroyed': STACK-only. Smoke probes of a torn-down system report Absent
    /// (RunSmoke maps unreachable to Absent, never Error), and an edge cache may
    /// serve briefly during teardown, so smoke states are ignored here.
    ///
    /// Any Error state (auth, throttle — the check itself failed) downgrades a MET
    /// verdict to NOT MET: an unverifiable expectation is not a met one.
    /// </summary>
    public static bool? Compute(string? expect, IReadOnlyList<ResourceCheckResult> results)
    {
        bool? met = expect switch
        {
            "deployed" => results.All(r =>
                r.Category switch
                {
                    ResourceCategory.Stack => r.State == ResourceState.Present,
                    ResourceCategory.Smoke => r.State == ResourceState.Present,
                    _ => true, // Persistent is always informational
                }),
            "destroyed" => results
                .Where(r => r.Category == ResourceCategory.Stack)
                .All(r => r.State == ResourceState.Absent),
            _ => null,
        };
        if (met == true && results.Any(r => r.State == ResourceState.Error))
            met = false;
        return met;
    }
}
