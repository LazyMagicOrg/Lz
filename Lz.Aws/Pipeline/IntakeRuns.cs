namespace Lz.Aws.Pipeline;

/// <summary>A workflow run attempt as GitHub's workflow-runs API answers for it: only the fields the intake reads.</summary>
public sealed record GitHubRun(
    long Id,
    int RunAttempt,
    long RepositoryId,
    long OwnerId,
    long HeadRepositoryId,
    string Path,
    string HeadBranch,
    string Event,
    string Status,
    string? Conclusion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A job of a run attempt as GitHub's jobs API answers for it.</summary>
public sealed record GitHubJob(long Id, string? RunnerGroupName, IReadOnlyList<string> Labels);

/// <summary>
/// One repository the intake takes runs from, as the build-side file names it (V-21): by ids, so a rename or a
/// look-alike organization cannot pass, and its build workflow's path.
/// </summary>
public sealed record IntakeSource(string Repo, long RepositoryId, long OwnerId, string WorkflowPath);

public enum RunCheckKind { Passed, NotYet, Refused }

/// <summary>The run check's answer. <see cref="RunCheckKind.NotYet"/> is not a refusal: the attempt is still running.</summary>
public sealed record RunCheckResult(RunCheckKind Kind, IReadOnlyList<string> Reasons)
{
    public static RunCheckResult Passed { get; } = new(RunCheckKind.Passed, Array.Empty<string>());
}

/// <summary>
/// THE RUN CHECK (DecoupledCd.md §15.4): the checks the OIDC token's claims would have made, read from GitHub's API
/// instead. A run attempt passes only if it completed with <c>success</c>, belongs to the allowlisted repository and
/// owner by id, was not built from a fork, ran that repository's build workflow, on <c>main</c>, for a <c>push</c>,
/// on GitHub-hosted runners only. Every failing check is named, not only the first, because a refusal is recorded
/// once and read by a person.
/// </summary>
public static class RunCheck
{
    public const string Branch = "main";
    public const string Event = "push";

    /// <summary>
    /// How GitHub's jobs API marks a GitHub-hosted runner. ASSUMED, NOT YET MEASURED: §15.8 item 1 measures it on the
    /// first run (V-B). Until then the check is the conservative one: the group GitHub names for its hosted runners,
    /// and no <c>self-hosted</c> label.
    /// </summary>
    public const string HostedRunnerGroup = "GitHub Actions";

    public static RunCheckResult Check(IntakeSource source, GitHubRun run, IReadOnlyList<GitHubJob>? jobs)
    {
        if (!string.Equals(run.Status, "completed", StringComparison.Ordinal))
            return new RunCheckResult(RunCheckKind.NotYet, new[] { $"the attempt is {run.Status}, not completed" });

        var reasons = new List<string>();
        if (!string.Equals(run.Conclusion, "success", StringComparison.Ordinal))
            reasons.Add($"the attempt concluded '{run.Conclusion ?? "(none)"}', not success");
        if (run.RepositoryId != source.RepositoryId)
            reasons.Add($"repository id {run.RepositoryId} is not {source.Repo}'s ({source.RepositoryId})");
        if (run.OwnerId != source.OwnerId)
            reasons.Add($"owner id {run.OwnerId} is not {source.Repo}'s owner ({source.OwnerId})");
        if (run.HeadRepositoryId != source.RepositoryId)
            reasons.Add($"the head repository ({run.HeadRepositoryId}) is not {source.Repo}: a fork");
        if (!string.Equals(run.Path, source.WorkflowPath, StringComparison.Ordinal))
            reasons.Add($"the workflow is '{run.Path}', not '{source.WorkflowPath}'");
        if (!string.Equals(run.HeadBranch, Branch, StringComparison.Ordinal))
            reasons.Add($"the branch is '{run.HeadBranch}', not {Branch}");
        if (!string.Equals(run.Event, Event, StringComparison.Ordinal))
            reasons.Add($"the event is '{run.Event}', not {Event}");

        if (jobs is null || jobs.Count == 0)
            reasons.Add("the attempt's jobs could not be read, so its runners cannot be shown to be GitHub-hosted");
        else
            foreach (var job in jobs)
                if (!string.Equals(job.RunnerGroupName, HostedRunnerGroup, StringComparison.Ordinal)
                    || job.Labels.Any(l => string.Equals(l, "self-hosted", StringComparison.OrdinalIgnoreCase)))
                    reasons.Add($"job {job.Id} ran on a runner that is not GitHub-hosted ('{job.RunnerGroupName ?? "(no group)"}')");

        return reasons.Count == 0 ? RunCheckResult.Passed : new RunCheckResult(RunCheckKind.Refused, reasons);
    }
}

/// <summary>What the intake's evidence store records about one run attempt, as the poller reads it.</summary>
/// <param name="Outcome">Taken in or refused: decided, never offered again (§5.3 rule 2). Null while undecided.</param>
/// <param name="FaultedTry">The highest try (0 the first, 1..3 the retries) that ended in a recorded fault; null if none.</param>
/// <param name="Running">An intake execution for the attempt is running.</param>
/// <param name="ClosedWithoutRecord">Its latest execution closed with neither an outcome nor a fault recorded.</param>
public sealed record IntakeAttemptState(
    IntakeOutcome? Outcome = null, int? FaultedTry = null, bool Running = false, bool ClosedWithoutRecord = false);

public enum IntakeOutcome { TakenIn, Refused }

/// <summary>One start the poller makes: an execution name and the run attempt it takes in.</summary>
public sealed record IntakeStart(string ExecutionName, long RunId, int RunAttempt, int Retry);

/// <summary>A tick's plan: what to start, and what to alarm about (§5.3 rule 4).</summary>
public sealed record IntakePollPlan(IReadOnlyList<IntakeStart> Starts, IReadOnlyList<string> Alarms);

/// <summary>
/// THE INTAKE POLLER'S CHOICE (DecoupledCd.md §15.4, §5.3 rules 1-4), for one repository and one tick. It takes every
/// successful run attempt that completed within the artifacts' retention; "handled" is the evidence, never an execution
/// name. A decided attempt (taken in or refused), a running one, one faulted on its last retry, and one whose execution
/// closed with nothing recorded are not started; the last two alarm, as does a successful attempt still without an
/// outcome ten minutes after it finished. A fault is retried under the next <c>-r{n}</c>.
/// </summary>
public static class IntakePoller
{
    /// <summary>The run artifacts' retention: an attempt older than this has nothing left to fetch.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    /// <summary>How long a successful attempt may wait for its outcome before it alarms (§5.3 rule 4).</summary>
    public static readonly TimeSpan OutcomeDue = TimeSpan.FromMinutes(10);

    public static IntakePollPlan Choose(
        IntakeSource source, IReadOnlyList<GitHubRun> runs, DateTimeOffset now,
        IReadOnlyDictionary<string, IntakeAttemptState> evidence)
    {
        var starts = new List<IntakeStart>();
        var alarms = new List<string>();

        foreach (var run in runs)
        {
            if (!string.Equals(run.Status, "completed", StringComparison.Ordinal)) continue;          // not yet: the next tick
            if (!string.Equals(run.Conclusion, "success", StringComparison.Ordinal)) continue;        // only successful attempts
            if (run.UpdatedAt < now - Retention) continue;                                           // its artifacts are gone

            var name = IntakeNames.For(source.RepositoryId, run.Id, run.RunAttempt);
            evidence.TryGetValue(name, out var state);
            state ??= new IntakeAttemptState();

            if (state.Outcome is not null) continue;
            if (now - run.UpdatedAt > OutcomeDue)
                alarms.Add($"{source.Repo} run {run.Id} attempt {run.RunAttempt} finished {run.UpdatedAt:u} and has no outcome after {OutcomeDue.TotalMinutes:0} minutes");
            if (state.Running) continue;
            if (state.ClosedWithoutRecord)
            {
                alarms.Add($"{name}: its intake closed with neither an outcome nor a fault recorded; a person decides (§5.3 rule 3)");
                continue;
            }
            if (state.FaultedTry is { } k)
            {
                if (k >= IntakeNames.MaxRetries)
                {
                    alarms.Add($"{name}: faulted on its last retry (-r{IntakeNames.MaxRetries}); a person decides (§5.3 rule 4)");
                    continue;
                }
                starts.Add(new IntakeStart(IntakeNames.For(source.RepositoryId, run.Id, run.RunAttempt, k + 1), run.Id, run.RunAttempt, k + 1));
                continue;
            }
            starts.Add(new IntakeStart(name, run.Id, run.RunAttempt, 0));
        }

        return new IntakePollPlan(starts, alarms);
    }
}
