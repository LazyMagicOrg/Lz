namespace Lz.Core.PackageLane;

/// <summary>One consumer's committed default for one package property.</summary>
public readonly record struct ConsumerPin(string PropertyName, string CommittedDefault, int Line);

/// <summary>
/// How one consumer file is wired into the package lane: the defaults it commits, and where the
/// override import sits relative to them.
/// </summary>
/// <param name="ImportLine">1-based line of the Packages.Local.props import, or -1 if absent.</param>
/// <param name="LastDefaultLine">1-based line of the last PkgVer_ default, or -1 if none.</param>
public readonly record struct ConsumerLane(
    string Path,
    IReadOnlyList<ConsumerPin> Pins,
    int ImportLine,
    int LastDefaultLine);

/// <summary>What a single pin resolves to, and whether that is the intended answer.</summary>
public enum PinVerdict
{
    /// <summary>The lane supplies a version and it differs from the committed default: local lane, working.</summary>
    Overridden,

    /// <summary>The lane supplies the same version the default already names.</summary>
    Agrees,

    /// <summary>No lane entry, and no feed builds this id - the committed default is the right answer.</summary>
    NotBuiltHere,

    /// <summary>
    /// A feed holds this id but the lane does not name it, so restore falls back to the committed
    /// default. That is the shape that fails as NU1101/NU1102 deep inside a consumer, naming a
    /// package rather than the lane.
    /// </summary>
    MissingFromLane,
}

public readonly record struct PinStatus(
    string PropertyName,
    string CommittedDefault,
    string? LaneValue,
    string? FeedNewest,
    PinVerdict Verdict);

/// <summary>
/// Reads the two-lane state back so it can be reviewed. <see cref="LocalPackageOverrides.Render"/>
/// writes the override; everything here reads - the override file, each consumer's committed
/// defaults, and the position of the import that decides which of the two wins.
///
/// <para>The import position is the point of this type. A consumer whose import sits ABOVE its
/// defaults is on the published lane while the workspace looks like it is on the local one, and
/// restore still succeeds - so the lane is a per-consumer fact that cannot be read off a single
/// global flag.</para>
/// </summary>
public static class PackageLaneStatus
{
    /// <summary>Reads back what <see cref="LocalPackageOverrides.Render"/> wrote.</summary>
    public static IReadOnlyDictionary<string, string> ParseOverride(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value, _) in ScanProperties(xml))
            result[name] = value;
        return result;
    }

    /// <summary>
    /// Reads one consumer's lane wiring. Works for both shapes in the workspace: a
    /// Directory.Packages.props under central package management, and a plain .csproj.
    /// </summary>
    public static ConsumerLane ParseConsumer(string path, string text)
    {
        var pins = new List<ConsumerPin>();
        var last = -1;
        foreach (var (name, value, line) in ScanProperties(text))
        {
            pins.Add(new ConsumerPin(name, value, line));
            if (line > last) last = line;
        }

        var importLine = -1;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var importAt = lines[i].IndexOf("<Import", StringComparison.Ordinal);
            if (importAt >= 0 &&
                lines[i].Contains(LocalOverrideFileName, StringComparison.Ordinal) &&
                // Not a commented-out import. Counting one would report a consumer as lane-wired
                // when restore reads only its committed defaults - wrong in the direction that
                // hides the problem rather than surfacing it.
                lines[i].LastIndexOf("<!--", importAt, StringComparison.Ordinal) < 0)
            {
                importLine = i + 1;
                break;
            }
        }

        return new ConsumerLane(path, pins, importLine, last);
    }

    public const string LocalOverrideFileName = "Packages.Local.props";

    /// <summary>
    /// True when the import can actually take effect: it exists and sits below every default it is
    /// meant to override. Null when the consumer has no import at all (published lane by
    /// construction, which is a different thing from being misordered).
    /// </summary>
    public static bool? ImportCanWin(ConsumerLane consumer)
    {
        if (consumer.ImportLine < 0) return null;
        if (consumer.LastDefaultLine < 0) return true;   // nothing to lose to
        return consumer.ImportLine > consumer.LastDefaultLine;
    }

    /// <summary>
    /// Joins a consumer's committed defaults against the override and the feeds. The join key is
    /// the property name, because that is the only thing all three share - the feed knows package
    /// ids, the consumer knows properties, and <see cref="LocalPackageOverrides.PropertyName"/> is
    /// the one-way map between them.
    /// </summary>
    public static IReadOnlyList<PinStatus> Evaluate(
        ConsumerLane consumer,
        IReadOnlyDictionary<string, string> laneOverride,
        IReadOnlyDictionary<string, string> feedNewestByProperty)
    {
        var rows = new List<PinStatus>(consumer.Pins.Count);
        var importWins = ImportCanWin(consumer) == true;

        foreach (var pin in consumer.Pins)
        {
            laneOverride.TryGetValue(pin.PropertyName, out var lane);
            feedNewestByProperty.TryGetValue(pin.PropertyName, out var feed);

            // A lane value that cannot win is not a lane value. Reporting it as effective would
            // hide exactly the misordering this type exists to surface.
            var effective = importWins ? lane : null;

            var verdict =
                effective is null && feed is null ? PinVerdict.NotBuiltHere
              : effective is null ? PinVerdict.MissingFromLane
              : string.Equals(effective, pin.CommittedDefault, StringComparison.Ordinal) ? PinVerdict.Agrees
              : PinVerdict.Overridden;

            rows.Add(new PinStatus(pin.PropertyName, pin.CommittedDefault, effective, feed, verdict));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.PropertyName, b.PropertyName));
        return rows;
    }

    /// <summary>Feed contents keyed by the property that would carry them, for the join above.</summary>
    public static IReadOnlyDictionary<string, string> ByProperty(IReadOnlyDictionary<string, string> chosenById)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, version) in chosenById)
            result[LocalPackageOverrides.PropertyName(id)] = version;
        return result;
    }

    /// <summary>
    /// Every PkgVer_ property assignment, with its 1-based line. Deliberately line-oriented rather
    /// than an XML parse: the position of a declaration relative to the import is load-bearing here,
    /// and an XML reader discards it.
    /// </summary>
    private static IEnumerable<(string Name, string Value, int Line)> ScanProperties(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var open = line.IndexOf("<PkgVer_", StringComparison.Ordinal);
            if (open < 0) continue;

            var nameEnd = line.IndexOf('>', open);
            if (nameEnd < 0) continue;
            var name = line.Substring(open + 1, nameEnd - open - 1);

            var closeTag = "</" + name + ">";
            var close = line.IndexOf(closeTag, nameEnd, StringComparison.Ordinal);
            if (close < 0) continue;

            yield return (name, line.Substring(nameEnd + 1, close - nameEnd - 1).Trim(), i + 1);
        }
    }
}
