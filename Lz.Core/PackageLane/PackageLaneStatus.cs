namespace Lz.Core.PackageLane;

/// <summary>One consumer's committed default for one package property.</summary>
public readonly record struct ConsumerPin(string PropertyName, string CommittedDefault, int Line);

/// <summary>
/// How one consumer file is wired into the package lane: the defaults it commits, and where the
/// override import sits relative to them.
/// </summary>
/// <param name="ImportLine">1-based line of the Packages.Local.props import, or -1 if absent.</param>
/// <param name="LastDefaultLine">1-based line of the last PkgVer_ default, or -1 if none.</param>
/// <param name="ImportProject">
/// The raw Project attribute of the override import, or null when there is none. Carried because
/// every consumer's import is guarded by Exists(), so an import with the wrong number of ../
/// segments silently does nothing - and a report that only counted the line would call that
/// consumer wired.
/// </param>
public readonly record struct ConsumerLane(
    string Path,
    IReadOnlyList<ConsumerPin> Pins,
    int ImportLine,
    int LastDefaultLine,
    string? ImportProject = null);

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

/// <param name="CommittedIsDead">
/// The committed default names a version BELOW what the producer can still mint. ORTHOGONAL to
/// Verdict, deliberately: a pin can be correctly overridden by the lane AND carry a dead default,
/// and folding the two together would hide the working lane behind the latent hazard.
///
/// <para>Under derived versioning height only increases, so such a version can never be built
/// again - and it is the one state that produces no diagnostic at all. Under central package
/// management a bare Version is a FLOOR, not a pin: warm, NuGet binds the old package still in the
/// global-packages folder; cold, it drifts upward with an NU1603 nothing gates. Measured both
/// ways - same commit, two different dependency graphs, neither failing.</para>
/// </param>
public readonly record struct PinStatus(
    string PropertyName,
    string CommittedDefault,
    string? LaneValue,
    string? FeedNewest,
    PinVerdict Verdict,
    bool CommittedIsDead = false);

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
        // Mask comments ONCE, up front, so nothing below has to reason about them. Blanking rather
        // than deleting keeps every line and column where it was, which matters because the
        // position of the import relative to the defaults is the answer this type exists to give.
        var masked = MaskComments(text.Replace("\r\n", "\n"));

        var pins = new List<ConsumerPin>();
        var last = -1;
        foreach (var (name, value, line) in ScanProperties(masked))
        {
            pins.Add(new ConsumerPin(name, value, line));
            if (line > last) last = line;
        }

        var (importLine, project) = FindImport(masked);
        return new ConsumerLane(path, pins, importLine, last, project);
    }

    /// <summary>
    /// The override import, if it is live. Reads the whole masked text rather than line by line so
    /// an element whose attributes wrap onto the next line is still found - routine XML formatting,
    /// and the real import lines are already ~140 characters.
    /// </summary>
    private static (int Line, string? Project) FindImport(string masked)
    {
        var search = 0;
        while (true)
        {
            var open = masked.IndexOf("<Import", search, StringComparison.Ordinal);
            if (open < 0) return (-1, null);
            var close = masked.IndexOf('>', open);
            if (close < 0) return (-1, null);

            var element = masked.Substring(open, close - open + 1);
            if (element.Contains(LocalOverrideFileName, StringComparison.Ordinal))
            {
                var line = 1;
                for (var i = 0; i < open; i++) if (masked[i] == '\n') line++;
                return (line, ProjectAttribute(element));
            }
            search = close + 1;
        }
    }

    private static string? ProjectAttribute(string element)
    {
        var at = element.IndexOf("Project=\"", StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + "Project=\"".Length;
        var end = element.IndexOf('"', start);
        return end < 0 ? null : element.Substring(start, end - start);
    }

    /// <summary>
    /// Replaces every comment's contents with spaces, leaving newlines intact. An unterminated
    /// comment masks to end of file, which is what MSBuild would refuse to parse anyway.
    /// </summary>
    private static string MaskComments(string text)
    {
        var sb = new System.Text.StringBuilder(text);
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0) break;
            var close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close + 3;
            for (var j = open; j < end; j++)
                if (sb[j] != '\n') sb[j] = ' ';
            if (close < 0) break;
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether the import actually points at <paramref name="overrideFullPath"/>. The Exists()
    /// guard on every consumer import means a wrong ../ depth is silent: restore succeeds on the
    /// committed defaults and nothing reports it.
    /// </summary>
    public static bool ImportResolvesTo(ConsumerLane consumer, string overrideFullPath)
    {
        if (consumer.ImportProject is null) return false;
        var dir = Path.GetDirectoryName(Path.GetFullPath(consumer.Path));
        if (dir is null) return false;

        // $(MSBuildThisFileDirectory) is the consumer's own directory, with a trailing separator.
        var expanded = consumer.ImportProject
            .Replace("$(MSBuildThisFileDirectory)", dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (expanded.Contains("$(", StringComparison.Ordinal)) return false;  // another property: cannot tell

        try
        {
            return string.Equals(
                Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(dir, expanded)),
                Path.GetFullPath(overrideFullPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

            // Computed ALONGSIDE the verdict, not as one of its rungs: the local lane hides this
            // completely, so a consumer can look perfectly healthy and still be one
            // "packages mode published" away from binding a version nothing can rebuild.
            var dead = feed is not null
                    && LocalPackageOverrides.Compare(pin.CommittedDefault, feed) < 0;

            var verdict =
                effective is null && feed is null ? PinVerdict.NotBuiltHere
              : effective is null ? PinVerdict.MissingFromLane
              : string.Equals(effective, pin.CommittedDefault, StringComparison.Ordinal) ? PinVerdict.Agrees
              : PinVerdict.Overridden;

            rows.Add(new PinStatus(pin.PropertyName, pin.CommittedDefault, effective, feed, verdict, dead));
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
            // Stop the NAME at whitespace so a conditioned default - <PkgVer_X Condition="..."> -
            // is still read. Taking everything up to '>' made the tag name include the attribute,
            // so the closing tag was never found and the pin vanished from the report entirely:
            // a missing pin reads as "nothing to worry about", the wrong direction to fail in.
            var nameLen = nameEnd - open - 1;
            var space = line.IndexOfAny(new[] { ' ', '	' }, open + 1, nameLen);
            var name = space >= 0
                ? line.Substring(open + 1, space - open - 1)
                : line.Substring(open + 1, nameLen);

            var closeTag = "</" + name + ">";
            var close = line.IndexOf(closeTag, nameEnd, StringComparison.Ordinal);
            if (close < 0) continue;

            yield return (name, line.Substring(nameEnd + 1, close - nameEnd - 1).Trim(), i + 1);
        }
    }
}
