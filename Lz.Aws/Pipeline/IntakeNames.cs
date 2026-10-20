using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>
/// The intake's execution names (DecoupledCd.md §15.4, §5.3 rules 1 and 4). The name is the idempotency key: the poller
/// and the nudge start one run attempt under exactly this name, so they never take it in twice, and a fault's retry is
/// a new execution named for the attempt and the retry number.
/// <list type="bullet">
/// <item><c>{repositoryId}-{runId}-{runAttempt}</c>, the first try;</item>
/// <item><c>{repositoryId}-{runId}-{runAttempt}-r{n}</c>, the n-th retry after a fault, n from 1 to <see cref="MaxRetries"/>.</item>
/// </list>
/// Ids, never names: a renamed repository keeps its id, so a rename cannot make an attempt look new.
/// </summary>
public static class IntakeNames
{
    /// <summary>A fault is retried this many times, then alarmed (§5.3 rule 4).</summary>
    public const int MaxRetries = 3;

    private static readonly Regex Shape = new(@"^(\d+)-(\d+)-(\d+)(?:-r(\d+))?$", RegexOptions.Compiled);

    /// <summary>The execution name for one try of one run attempt; <paramref name="retry"/> 0 is the first try.</summary>
    public static string For(long repositoryId, long runId, int runAttempt, int retry = 0)
    {
        if (repositoryId < 1) throw new ArgumentOutOfRangeException(nameof(repositoryId), "a GitHub id is positive");
        if (runId < 1) throw new ArgumentOutOfRangeException(nameof(runId), "a GitHub id is positive");
        if (runAttempt < 1) throw new ArgumentOutOfRangeException(nameof(runAttempt), "GitHub numbers attempts from 1");
        if (retry < 0 || retry > MaxRetries)
            throw new ArgumentOutOfRangeException(nameof(retry), $"a fault is retried at most {MaxRetries} times, then alarmed");

        var name = $"{repositoryId}-{runId}-{runAttempt}";
        return retry == 0 ? name : $"{name}-r{retry}";
    }

    /// <summary>
    /// Reads a name back, or null when it is not an intake execution's name. Strict: a name the intake would not
    /// produce (a leading zero, a retry 0 or above <see cref="MaxRetries"/>) is not one of its names - which the closing
    /// round trip through <see cref="For"/> decides, so there is one definition of a well-formed name.
    /// </summary>
    public static IntakeName? Parse(string? name)
    {
        if (name is null) return null;
        var m = Shape.Match(name);
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value, out var repo) || !long.TryParse(m.Groups[2].Value, out var run)
            || !int.TryParse(m.Groups[3].Value, out var attempt))
            return null;
        var retry = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out var r) ? r : 0;
        try
        {
            var parsed = new IntakeName(repo, run, attempt, retry);
            return string.Equals(For(repo, run, attempt, retry), name, StringComparison.Ordinal) ? parsed : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>One intake execution's name, read back.</summary>
public sealed record IntakeName(long RepositoryId, long RunId, int RunAttempt, int Retry)
{
    /// <summary>The name of the first try of the same attempt: what the poller and the nudge start.</summary>
    public string Attempt => IntakeNames.For(RepositoryId, RunId, RunAttempt);
}
