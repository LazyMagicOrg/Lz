namespace Lz.Aws.Pipeline;

/// <summary>The intake's steps (DecoupledCd.md §15.4), as nodes of the plan V-B generates its state machine from.</summary>
public enum IntakeStep
{
    Start, CheckRun, CheckCommits, FetchAndStage, Gate1, Gate1Verdict, PushChoice, PushImage, AwaitSignature,
    WriteZips, WriteTestsRecord, WriteDeployRecords, RecordOutcome, RecordRefusal, RecordFault, End,
}

/// <summary>When an edge is taken.</summary>
public enum IntakeGuard { Always, Passed, NotPassed, Refused, Fault, HasImage, NoImage }

public sealed record IntakeEdge(IntakeStep From, IntakeStep To, IntakeGuard Guard = IntakeGuard.Always);

/// <summary>
/// THE INTAKE'S ORDER (DecoupledCd.md §15.4, V-12), as a graph a test can check before a state machine exists. V-B builds
/// the machine from <see cref="Default"/>; <see cref="Violations"/> is the rule set any plan must keep:
/// <list type="number">
/// <item>nothing is pushed, signed, zipped or recorded except through Gate 1's <c>passed</c> verdict;</item>
/// <item>the run and its commits are checked before anything is fetched, and Gate 1's verdict comes only from Gate 1;</item>
/// <item>a pushed image is signed before any record (no record names an unsigned image);</item>
/// <item>the <c>tests</c> record is written before the deployable ones (no deploy starts before Gate 2's bundle is recorded);</item>
/// <item>every route to the end records an outcome, a refusal or a fault (§5.3 rule 3).</item>
/// </list>
/// The verdict's binding to the staged bytes is a data check, not a route: <see cref="IntakeWrites.Gate1Reuse"/>.
/// </summary>
public static class IntakePlan
{
    public static readonly IReadOnlyList<IntakeStep> Guarded = new[]
    {
        IntakeStep.PushImage, IntakeStep.AwaitSignature, IntakeStep.WriteZips,
        IntakeStep.WriteTestsRecord, IntakeStep.WriteDeployRecords,
    };

    private static readonly IntakeStep[] Records = { IntakeStep.RecordOutcome, IntakeStep.RecordRefusal, IntakeStep.RecordFault };

    public static IReadOnlyList<IntakeEdge> Default()
    {
        var e = new List<IntakeEdge>
        {
            new(IntakeStep.Start, IntakeStep.CheckRun),
            new(IntakeStep.CheckRun, IntakeStep.CheckCommits, IntakeGuard.Passed),
            new(IntakeStep.CheckRun, IntakeStep.RecordRefusal, IntakeGuard.Refused),
            new(IntakeStep.CheckCommits, IntakeStep.FetchAndStage, IntakeGuard.Passed),
            new(IntakeStep.CheckCommits, IntakeStep.RecordRefusal, IntakeGuard.Refused),
            new(IntakeStep.FetchAndStage, IntakeStep.Gate1, IntakeGuard.Passed),
            new(IntakeStep.FetchAndStage, IntakeStep.RecordRefusal, IntakeGuard.Refused),
            new(IntakeStep.Gate1, IntakeStep.Gate1Verdict),
            new(IntakeStep.Gate1Verdict, IntakeStep.PushChoice, IntakeGuard.Passed),
            new(IntakeStep.Gate1Verdict, IntakeStep.RecordRefusal, IntakeGuard.NotPassed),
            new(IntakeStep.PushChoice, IntakeStep.PushImage, IntakeGuard.HasImage),
            new(IntakeStep.PushChoice, IntakeStep.WriteZips, IntakeGuard.NoImage),
            new(IntakeStep.PushImage, IntakeStep.AwaitSignature),
            new(IntakeStep.AwaitSignature, IntakeStep.WriteZips),
            new(IntakeStep.WriteZips, IntakeStep.WriteTestsRecord),
            new(IntakeStep.WriteTestsRecord, IntakeStep.WriteDeployRecords),
            new(IntakeStep.WriteDeployRecords, IntakeStep.RecordOutcome),
            new(IntakeStep.RecordOutcome, IntakeStep.End),
            new(IntakeStep.RecordRefusal, IntakeStep.End),
            new(IntakeStep.RecordFault, IntakeStep.End),
        };
        // A fault anywhere between the start and the outcome is recorded as a fault (§15.4): the poller retries it.
        foreach (var step in new[]
                 {
                     IntakeStep.CheckRun, IntakeStep.CheckCommits, IntakeStep.FetchAndStage, IntakeStep.Gate1,
                     IntakeStep.PushImage, IntakeStep.AwaitSignature, IntakeStep.WriteZips, IntakeStep.WriteTestsRecord,
                     IntakeStep.WriteDeployRecords,
                 })
            e.Add(new(step, IntakeStep.RecordFault, IntakeGuard.Fault));
        return e;
    }

    /// <summary>Every rule the plan breaks, named; empty when it keeps them all.</summary>
    public static IReadOnlyList<string> Violations(IReadOnlyList<IntakeEdge> plan)
    {
        var v = new List<string>();

        if (!Reaches(plan, IntakeStep.Start, IntakeStep.End))
            v.Add("the end is not reachable from the start");
        foreach (var step in Guarded.Concat(Records))
            if (!Reaches(plan, IntakeStep.Start, step))
                v.Add($"{step} is never reached");

        // 1. Only through a passed verdict.
        var withoutPass = plan.Where(e => !(e.From == IntakeStep.Gate1Verdict && e.Guard == IntakeGuard.Passed)).ToList();
        foreach (var step in Guarded)
            if (Reaches(withoutPass, IntakeStep.Start, step))
                v.Add($"{step} is reachable without Gate 1's passed verdict");
        if (plan.Any(e => e.Guard == IntakeGuard.Passed && e.From == IntakeStep.Gate1Verdict && Records.Contains(e.To) && e.To != IntakeStep.RecordOutcome))
            v.Add("a passed verdict leads straight to a refusal or a fault");
        if (plan.Any(e => e.From == IntakeStep.Gate1Verdict && e.Guard == IntakeGuard.NotPassed && (Guarded.Contains(e.To) || e.To == IntakeStep.PushChoice)))
            v.Add("a verdict that did not pass leads to a push or a write");

        // 2. Checks before the fetch; the verdict only from Gate 1.
        foreach (var check in new[] { IntakeStep.CheckRun, IntakeStep.CheckCommits })
            if (Reaches(Without(plan, check), IntakeStep.Start, IntakeStep.FetchAndStage))
                v.Add($"bytes are fetched without {check}");
        if (Reaches(Without(plan, IntakeStep.Gate1), IntakeStep.Start, IntakeStep.Gate1Verdict))
            v.Add("Gate 1's verdict is reachable without Gate 1");
        if (Reaches(Without(plan, IntakeStep.FetchAndStage), IntakeStep.Start, IntakeStep.Gate1))
            v.Add("Gate 1 runs on bytes that were not staged");

        // 3. A pushed image is signed before any record.
        foreach (var record in new[] { IntakeStep.WriteTestsRecord, IntakeStep.WriteDeployRecords })
            if (Reaches(Without(plan, IntakeStep.AwaitSignature), IntakeStep.PushImage, record))
                v.Add($"{record} is reachable from the push without waiting for the signature");

        // 4. The tests record before the deployable ones.
        if (Reaches(Without(plan, IntakeStep.WriteTestsRecord), IntakeStep.Start, IntakeStep.WriteDeployRecords))
            v.Add("a deployable record is written before the tests record");

        // 5. Every route to the end records something.
        if (Reaches(Without(plan, Records), IntakeStep.Start, IntakeStep.End))
            v.Add("the intake can close having recorded neither an outcome, a refusal nor a fault");

        return v;
    }

    private static IReadOnlyList<IntakeEdge> Without(IReadOnlyList<IntakeEdge> plan, params IntakeStep[] removed)
        => plan.Where(e => !removed.Contains(e.From) && !removed.Contains(e.To)).ToList();

    private static bool Reaches(IReadOnlyList<IntakeEdge> plan, IntakeStep from, IntakeStep to)
    {
        var seen = new HashSet<IntakeStep> { from };
        var queue = new Queue<IntakeStep>(new[] { from });
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == to) return true;
            foreach (var e in plan.Where(e => e.From == at))
                if (seen.Add(e.To)) queue.Enqueue(e.To);
        }
        return false;
    }
}

public enum WriteDecision { Write, AlreadyDone, Refuse }

/// <summary>Gate 1's verdict as the intake's evidence holds it: the bytes it ran for.</summary>
public sealed record Gate1Verdict(bool Passed, string ManifestDigest, IReadOnlyDictionary<string, string> ArtifactSha256);

public enum Gate1ReuseDecision { Run, Reuse, Refuse }

/// <summary>
/// A RETRY CONTINUES; IT DOES NOT REDO (DecoupledCd.md §15.4). Every store is write-once, so each write first looks at
/// its key, and anything found there that is not this attempt's own is refused and alarmed, never overwritten.
/// </summary>
public static class IntakeWrites
{
    /// <summary>The push reads the tag first: absent, push; the digest being pushed, done; another digest, refuse.</summary>
    public static WriteDecision Push(string? digestAtTag, string manifestDigest)
        => digestAtTag is null ? WriteDecision.Write
         : string.Equals(digestAtTag, manifestDigest, StringComparison.Ordinal) ? WriteDecision.AlreadyDone
         : WriteDecision.Refuse;

    /// <summary>A zip counts as written if the object at its key carries the zip's SHA-256; other bytes there are refused.</summary>
    public static WriteDecision Zip(string? sha256AtKey, string zipSha256)
        => sha256AtKey is null ? WriteDecision.Write
         : string.Equals(sha256AtKey, zipSha256, StringComparison.OrdinalIgnoreCase) ? WriteDecision.AlreadyDone
         : WriteDecision.Refuse;

    /// <summary>
    /// A build record counts as written if the one at its key names the same run attempt and identity, and is left as
    /// it is; a record for another attempt, or another artifact, at that key is refused.
    /// </summary>
    public static WriteDecision Record(BuildRecord? atKey, BuildRecord ours)
    {
        if (atKey is null) return WriteDecision.Write;
        return atKey.Intake is { } theirs && ours.Intake is { } mine
               && theirs.RunId == mine.RunId && theirs.RunAttempt == mine.RunAttempt
               && atKey.Class == ours.Class && atKey.Identity == ours.Identity
            ? WriteDecision.AlreadyDone
            : WriteDecision.Refuse;
    }

    /// <summary>
    /// Gate 1's verdict, once held for the attempt, is reused rather than run again - but only for exactly the bytes it
    /// ran for: the manifest digest and every artifact's SHA-256. Unequal hashes are refused and alarmed, not retried.
    /// </summary>
    public static Gate1ReuseDecision Gate1Reuse(Gate1Verdict? held, string manifestDigest, IReadOnlyDictionary<string, string> stagedSha256)
    {
        if (held is null) return Gate1ReuseDecision.Run;
        var same = string.Equals(held.ManifestDigest, manifestDigest, StringComparison.Ordinal)
                   && held.ArtifactSha256.Count == stagedSha256.Count
                   && held.ArtifactSha256.All(kv => stagedSha256.TryGetValue(kv.Key, out var s)
                                                    && string.Equals(s, kv.Value, StringComparison.OrdinalIgnoreCase));
        return same ? Gate1ReuseDecision.Reuse : Gate1ReuseDecision.Refuse;
    }
}
