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
/// <item>THE NEWEST BY DOMINANCE: of the candidates, the one that has every member each other has, none of them older.
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

    /// <summary>
    /// Whether <paramref name="a"/> has every member <paramref name="b"/> has, by class and repository, and none of them older.
    /// A set lacking a member does not dominate one that has it: a web app newly added to the chain is newer than nothing,
    /// and was otherwise decided superseded by the set deployed before it (dev's pass of 2026-10-30 03:09).
    /// </summary>
    public static bool Dominates(PassMarker a, PassMarker b)
    {
        if (b.Members.Any(o => !a.Members.Any(m => m.Class == o.Class && m.Repo == o.Repo))) return false;
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

        // SETS AND SITES APART (V-K step 5): they share no member, so one would "dominate" the other vacuously. Each is ordered
        // and superseded among its own kind only; one execution at a time across both (choice 2).
        var deployed = markers.Where(m => state.Decided.TryGetValue(Id(m), out var o) && o == DecisionStep.Deployed).ToList();
        var supersede = markers
            .Where(m => !Decided(m) && !Started(m)
                        && deployed.Any(d => Id(d) != Id(m) && SameKind(d, m) && Dominates(d.Marker, m.Marker)))
            .ToList();

        if (state.InFlight) return new(null, supersede, alerts);

        var startable = new List<SeenMarker>();
        foreach (var kind in markers.Where(m => !Decided(m) && !Started(m) && !supersede.Contains(m)).GroupBy(Kind))
        {
            var candidates = kind.ToList();
            var newest = candidates.Where(c => candidates.All(o => o == c || Dominates(c.Marker, o.Marker))).ToList();
            if (newest.Count != 1)
            {
                alerts.Add($"of {candidates.Count} undecided {kind.Key} markers, neither of two dominates the other ({string.Join(", ", candidates.Select(Id))}); " +
                           "nothing of them is started. A break-glass deploy can leave such markers; a person decides which goes on.");
                continue;
            }
            startable.Add(newest[0]);
        }
        if (startable.Count == 0) return new(null, supersede, alerts);

        // THE ONE WRITTEN FIRST goes first; the other kind's waits for it, as a newer set waits for a running one.
        var start = startable.OrderBy(m => m.WrittenAt).ThenBy(Id, StringComparer.Ordinal).First();
        var startableAt = state.LastClosedAt is { } closed && closed > start.WrittenAt ? closed : start.WrittenAt;
        if (now - startableAt > LateAfter)
            alerts.Add($"{Id(start)} became startable at {startableAt:O} and is started only now, more than ten minutes later.");
        return new(start, supersede, alerts);
    }

    private static string Kind(SeenMarker m) => PassMarkerFormat.IsSite(m.Marker) ? "site" : "set";
    private static bool SameKind(SeenMarker a, SeenMarker b) => Kind(a) == Kind(b);

    /// <summary>
    /// <c>pm-{20 hex of the set's hash}-1</c>, or <c>ps-</c> for a site (V-K step 5): derived from the marker, so two pollers
    /// racing name one execution.
    /// </summary>
    public static string ExecutionNameFor(string markerId)
    {
        if (markerId.StartsWith(PassMarkerFormat.SiteSegment, StringComparison.Ordinal))
            return $"ps-{markerId[PassMarkerFormat.SiteSegment.Length..][..20]}-1";
        var hash = markerId.StartsWith(PassMarkerFormat.SetsSegment, StringComparison.Ordinal) ? markerId[PassMarkerFormat.SetsSegment.Length..] : markerId;
        return $"pm-{hash[..20]}-1";
    }
}

/// <summary>An execution of this environment's deployer, as the poller reads it.</summary>
public sealed record PromotionExecution(string Name, string Status, DateTimeOffset StartedAt, DateTimeOffset? StoppedAt);

/// <summary>The poller's settings: its predecessor and that environment's store, its own store, deployer, hold, alerts topic and image target.</summary>
public sealed record PromotionPollSettings(
    string Predecessor, string PredecessorEvidenceStore, string EvidenceStore, string StateMachineArn, string HoldParameter,
    string AlertsTopicArn, System.Text.Json.Nodes.JsonObject Target);

/// <summary>What the poller reads.</summary>
public interface IPromotionReads
{
    /// <summary>The hold parameter's value, or null when there is no such parameter.</summary>
    Task<string?> HoldAsync(string parameter);

    /// <summary>The keys under <c>passed/sets/</c> and <c>passed/site/</c> in the predecessor's store, with when each was written.</summary>
    Task<IReadOnlyList<(string Key, DateTimeOffset LastModified)>> ListMarkersAsync(string bucket);

    /// <summary>A marker as read, with the version S3 served; null when it is not there.</summary>
    Task<StoredRecord?> ReadMarkerAsync(string bucket, string key);

    /// <summary>The decisions in this environment's store, by marker id, with their outcome.</summary>
    Task<IReadOnlyDictionary<string, string>> DecisionsAsync(string bucket);

    /// <summary>This environment's deployer's executions.</summary>
    Task<IReadOnlyList<PromotionExecution>> ExecutionsAsync(string stateMachineArn);
}

public interface IExecutionStarter
{
    /// <summary>Start an execution under this name; false when one by that name already exists.</summary>
    Task<bool> StartAsync(string stateMachineArn, string name, string input);
}

/// <summary>
/// THE POLLER'S TICK (§12 P5 stage I): the hold first, and nothing else under one; then the predecessor's set markers, this
/// environment's decisions and its deployer's executions; then what <see cref="PromotionPoll.Decide"/> says. A marker that
/// does not parse is alarmed and left out, never started. Superseded markers are decided here, write-once, by the poller.
/// </summary>
public static class PromotionPollStep
{
    public const string Superseded = "superseded";

    public static async Task<System.Text.Json.Nodes.JsonObject> RunAsync(
        PromotionPollSettings settings, IPromotionReads reads, IExecutionStarter starter, IEvidenceWriter writer, IAlertPublisher alerts, DateTimeOffset now)
    {
        var hold = await reads.HoldAsync(settings.HoldParameter);
        if (!string.Equals(hold?.Trim(), "off", StringComparison.Ordinal))
            return new System.Text.Json.Nodes.JsonObject { ["held"] = true, ["hold"] = hold };

        var problems = new List<string>();
        var seen = new List<SeenMarker>();
        foreach (var (key, at) in await reads.ListMarkersAsync(settings.PredecessorEvidenceStore))
        {
            var stored = await reads.ReadMarkerAsync(settings.PredecessorEvidenceStore, key);
            if (stored?.VersionId is not { Length: > 0 } version || version == "null") { problems.Add($"{key} was listed and could not be read at a version."); continue; }
            try
            {
                var marker = PassMarkerFormat.Parse(stored.Json, settings.Predecessor);
                if (PassMarkerFormat.KeyFor(marker.MarkerId) != key) { problems.Add($"{key} holds {marker.MarkerId}, whose key is another."); continue; }
                seen.Add(new SeenMarker(key, version, marker, at));
            }
            catch (InvalidOperationException ex) { problems.Add($"{key} is left out: {ex.Message}"); }
        }

        var executions = await reads.ExecutionsAsync(settings.StateMachineArn);
        var names = executions.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var state = new PollState(
            Held: false,
            InFlight: executions.Any(e => e.Status == "RUNNING"),
            Started: seen.Where(m => names.Contains(PromotionPoll.ExecutionNameFor(m.Marker.MarkerId))).Select(m => m.Marker.MarkerId).ToHashSet(),
            Decided: await reads.DecisionsAsync(settings.EvidenceStore),
            LastClosedAt: executions.Where(e => e.StoppedAt is not null).Select(e => e.StoppedAt).Max());
        var decision = PromotionPoll.Decide(seen, state, now);

        foreach (var m in decision.Supersede)
        {
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["schema"] = 1, ["markerId"] = m.Marker.MarkerId,
                ["marker"] = new System.Text.Json.Nodes.JsonObject { ["key"] = m.Key, ["versionId"] = m.VersionId },
                ["outcome"] = Superseded, ["execution"] = "promotion-poller", ["decidedAt"] = now.ToString("O"),
            };
            await writer.PutOnceAsync(settings.EvidenceStore, PassMarkerFormat.DecisionKeyFor(m.Marker.MarkerId), body.ToJsonString());
        }

        string? started = null;
        if (decision.Start is { } start)
        {
            var input = new System.Text.Json.Nodes.JsonObject
            {
                ["marker"] = new System.Text.Json.Nodes.JsonObject { ["key"] = start.Key, ["versionId"] = start.VersionId },
            };
            // A SET'S IMAGE TARGET; a site's is test's own website, which its Verify knows from its settings (V-K step 5).
            if (!PassMarkerFormat.IsSite(start.Marker)) input["target"] = settings.Target.DeepClone();
            started = PromotionPoll.ExecutionNameFor(start.Marker.MarkerId);
            await starter.StartAsync(settings.StateMachineArn, started, input.ToJsonString());
        }

        foreach (var message in problems.Concat(decision.Alerts))
            await alerts.PublishAsync(settings.AlertsTopicArn, "lz promotion poller", message);

        return new System.Text.Json.Nodes.JsonObject
        {
            ["held"] = false,
            ["markers"] = seen.Count,
            ["started"] = started,
            ["superseded"] = decision.Supersede.Count,
            ["alerts"] = problems.Count + decision.Alerts.Count,
        };
    }
}
