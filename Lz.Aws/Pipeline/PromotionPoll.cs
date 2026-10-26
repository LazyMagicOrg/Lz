using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>A set marker the poller found in its predecessor's <c>passed/sets/</c>: where, at which version, what it says, and when it was written.</summary>
public sealed record SeenMarker(string Key, string VersionId, PassMarker Marker, DateTimeOffset WrittenAt);

/// <summary>
/// What the poller knows of this environment at a tick: the hold (held or missing counts as held), whether any execution of
/// its deployer is running or awaiting approval, the markers it has ever started an execution for, the markers decided here
/// with their outcome, and when its last execution closed.
/// </summary>
public sealed record PollState(bool Held, bool InFlight, IReadOnlySet<string> Started, IReadOnlyDictionary<string, string> Decided, DateTimeOffset? LastClosedAt);

/// <summary>A tick's decision: the marker to start (if any), the older ones to decide superseded, and what to alarm on.</summary>
public sealed record PollDecision(SeenMarker? Start, IReadOnlyList<SeenMarker> Supersede, IReadOnlyList<string> Alerts);

/// <summary>A build record's age: its builtAt stamp, then its run attempt. Compared ordinally, never by culture.</summary>
public readonly record struct RecordAge(string Stamp, int Attempt) : IComparable<RecordAge>
{
    public int CompareTo(RecordAge other)
    {
        var byStamp = string.CompareOrdinal(Stamp, other.Stamp);
        return byStamp != 0 ? byStamp : Attempt.CompareTo(other.Attempt);
    }
}

/// <summary>
/// THE PROMOTION POLLER'S DECISION (DecoupledCd.md §12 P5 stage I, §5.3, T-12, T-13), pure: of the predecessor's set
/// markers, which to start here, which to decide superseded, and what to alarm on. The poller's function reads what this needs
/// and does what it says.
///
/// <list type="bullet">
/// <item>HELD OR MISSING, nothing: no start, no decision, no alarm (rule 5). On release it resumes at the newest by dominance.</item>
/// <item>"HANDLED" IS THE DECISION: a decided marker is never started. A marker started whose execution closed without a
/// decision is not restarted, and alarms; one still running is neither restarted nor superseded.</item>
/// <item>SUPERSEDED: a marker never started, older than a set deployed here, is decided superseded (rule 3). A refused or
/// failed set supersedes nothing.</item>
/// <item>ONE SET IN FLIGHT: while any execution runs or awaits approval, nothing starts, and waiting does not alarm.</item>
/// <item>THE NEWEST BY DOMINANCE: of the candidates, the one none of whose members is older than any other's same member.
/// Neither of two dominating starts nothing and alarms for a person, since a break-glass deploy can leave two such sets.</item>
/// <item>LATE (rule 4): the newest startable marker not started ten minutes after it became startable - after it was
/// written or after the last execution here closed, whichever is later - alarms, as it is started.</item>
/// </list>
/// </summary>
public static class PromotionPoll
{
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(10);

    // {class}/{owner}/{repo}/{builtAt stamp}-{runId}[-{attempt}].json; a record from before the intake has no attempt.
    private static readonly Regex Stem = new(@"/(?<stamp>\d{8}T\d{6}Z)-(?<run>\d+)(?:-(?<attempt>\d+))?\.json$", RegexOptions.CultureInvariant);

    public static RecordAge AgeOf(string recordKey)
    {
        var m = Stem.Match(recordKey);
        if (!m.Success) throw new InvalidOperationException($"'{recordKey}' is not a build record key: no builtAt and run in its name.");
        return new RecordAge(m.Groups["stamp"].Value, m.Groups["attempt"].Success ? int.Parse(m.Groups["attempt"].Value) : 1);
    }

    /// <summary>Whether no member of <paramref name="a"/> is older than <paramref name="b"/>'s member of the same class and repository.</summary>
    public static bool Dominates(PassMarker a, PassMarker b)
    {
        foreach (var m in a.Members)
        {
            var other = b.Members.FirstOrDefault(o => o.Class == m.Class && o.Repo == m.Repo);
            if (other is not null && AgeOf(m.Record.Key).CompareTo(AgeOf(other.Record.Key)) < 0) return false;
        }
        return true;
    }

    public static PollDecision Decide(IReadOnlyList<SeenMarker> markers, PollState state, DateTimeOffset now)
    {
        if (state.Held) return new(null, Array.Empty<SeenMarker>(), Array.Empty<string>());

        var alerts = new List<string>();
        string Id(SeenMarker m) => m.Marker.MarkerId;
        bool Decided(SeenMarker m) => state.Decided.ContainsKey(Id(m));
        bool Started(SeenMarker m) => state.Started.Contains(Id(m));

        // CLOSED WITHOUT A DECISION: not restarted. While a set is in flight it may be that one, still running.
        if (!state.InFlight)
            foreach (var m in markers.Where(m => Started(m) && !Decided(m)))
                alerts.Add($"{Id(m)} was started here and its execution closed without a decision; it is not restarted. A person decides what happens to it.");

        var deployed = markers.Where(m => state.Decided.TryGetValue(Id(m), out var o) && o == DecisionStep.Deployed).ToList();
        var supersede = markers
            .Where(m => !Decided(m) && !Started(m) && deployed.Any(d => Id(d) != Id(m) && Dominates(d.Marker, m.Marker)))
            .ToList();

        if (state.InFlight) return new(null, supersede, alerts);

        var candidates = markers.Where(m => !Decided(m) && !Started(m) && !supersede.Contains(m)).ToList();
        if (candidates.Count == 0) return new(null, supersede, alerts);

        var newest = candidates.Where(c => candidates.All(o => o == c || Dominates(c.Marker, o.Marker))).ToList();
        if (newest.Count != 1)
        {
            alerts.Add($"of {candidates.Count} undecided set markers, neither of two dominates the other ({string.Join(", ", candidates.Select(Id))}); " +
                       "nothing is started. A break-glass deploy can leave such sets; a person decides which goes on.");
            return new(null, supersede, alerts);
        }

        var start = newest[0];
        var startable = state.LastClosedAt is { } closed && closed > start.WrittenAt ? closed : start.WrittenAt;
        if (now - startable > LateAfter)
            alerts.Add($"{Id(start)} became startable at {startable:O} and is started only now, more than ten minutes later.");
        return new(start, supersede, alerts);
    }

    /// <summary><c>pm-{20 hex of the set's hash}-1</c>: derived from the marker, so two pollers racing name one execution.</summary>
    public static string ExecutionNameFor(string markerId)
    {
        var hash = markerId.StartsWith(PassMarkerFormat.SetsSegment, StringComparison.Ordinal) ? markerId[PassMarkerFormat.SetsSegment.Length..] : markerId;
        return $"pm-{hash[..20]}-1";
    }
}
