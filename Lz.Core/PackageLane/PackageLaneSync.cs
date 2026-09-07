namespace Lz.Core.PackageLane;

/// <summary>One committed default this sync would move, and where.</summary>
public readonly record struct PinChange(string PropertyName, string From, string To, int Line)
{
    /// <summary>
    /// True when the new version sorts BELOW the old one. Under derived versioning height only
    /// increases, so this should not happen - if it does, something built an older branch into the
    /// feed, and the report says so rather than moving the default down quietly.
    /// </summary>
    public bool IsDowngrade => LocalPackageOverrides.Compare(To, From) < 0;
}

/// <summary>
/// Re-baselines a consumer's COMMITTED defaults - the published lane - onto what the producers
/// currently mint.
///
/// <para>This is the counterpart to <see cref="LocalPackageOverrides.Render"/>, and the difference
/// matters: that writes a gitignored override, this edits a TRACKED file. A committed default is
/// only ever read in published mode, which is what a fresh clone or CI restores, so getting it
/// wrong is invisible locally and visible to everyone else.</para>
///
/// <para><b>It re-baselines; it does not cure.</b> Under derived versioning a producer's version
/// moves on every commit, so a default synced today is stale tomorrow. The gesture belongs
/// immediately before a publish, when the version being written is the one about to exist in a
/// registry - not on a schedule. That is also why this is a command rather than something
/// build.ps1 runs: a build that quietly rewrote tracked files would be a worse problem than the
/// stale pins it fixed.</para>
/// </summary>
public static class PackageLaneSync
{
    /// <summary>
    /// Rewrites each default the feeds can supply, leaving everything else - indentation,
    /// conditions, third-party pins, comments - exactly as it was.
    /// </summary>
    /// <param name="consumer">
    /// Parsed by <see cref="PackageLaneStatus.ParseConsumer"/>, whose line numbers this relies on.
    /// Because that parser masks comments, a commented-out default is not in <c>Pins</c> and is
    /// therefore never resurrected here.
    /// </param>
    public static (string Text, IReadOnlyList<PinChange> Changes, IReadOnlyList<PinChange> Refused) Apply(
        string text,
        ConsumerLane consumer,
        IReadOnlyDictionary<string, string> feedByProperty)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var changes = new List<PinChange>();
        var refused = new List<PinChange>();

        foreach (var pin in consumer.Pins)
        {
            if (!feedByProperty.TryGetValue(pin.PropertyName, out var wanted)) continue;
            if (string.Equals(wanted, pin.CommittedDefault, StringComparison.Ordinal)) continue;

            // REFUSE a version that was not built as a public release. A committed default IS the
            // published lane - read by a fresh clone or CI, out of a registry - and a version
            // carrying NBGV's commit-id discriminator exists only in the local feed that produced
            // it. Writing one here would swap a stale-but-plausible version for an unrestorable
            // one, which is strictly worse than leaving it alone. Refused per pin rather than per
            // file, because the other producers in the same file are usually fine to sync.
            if (CarriesACommitId(wanted))
            {
                refused.Add(new PinChange(pin.PropertyName, pin.CommittedDefault, wanted, pin.Line));
                continue;
            }

            var i = pin.Line - 1;
            if (i < 0 || i >= lines.Length) continue;

            var replaced = ReplaceValue(lines[i], pin.PropertyName, wanted);
            if (replaced is null) continue;

            lines[i] = replaced;
            changes.Add(new PinChange(pin.PropertyName, pin.CommittedDefault, wanted, pin.Line));
        }

        return (string.Join(newline, lines), changes, refused);
    }

    /// <summary>
    /// Swaps only the element's text, so an attribute survives:
    /// <c>&lt;PkgVer_X Condition="..."&gt;1.0.0&lt;/PkgVer_X&gt;</c> keeps its condition.
    /// Returns null if the line does not have the shape this expects, so a surprise is skipped and
    /// reported as unchanged rather than mangled.
    /// </summary>
    /// <summary>
    /// NBGV stamps a non-public build with its commit id — <c>3.0.23-g6fc0b7081e</c>. That suffix is
    /// the only signal available locally that a version was not built as a public release, and so
    /// cannot be expected to exist in any registry.
    ///
    /// <para>Deliberately narrower than "is a prerelease": a producer may legitimately publish a
    /// prerelease line one day (LazyMagic carried <c>-alpha</c> until 2026-09-06), and pinning that
    /// would be correct. It is the commit-id discriminator, not the prerelease-ness, that makes a
    /// version local-only.</para>
    /// </summary>
    public static bool CarriesACommitId(string version)
    {
        var at = version.IndexOf("-g", StringComparison.Ordinal);
        if (at < 0) return false;
        var rest = version[(at + 2)..];
        return rest.Length >= 7 && rest.All(Uri.IsHexDigit);
    }

    private static string? ReplaceValue(string line, string property, string value)
    {
        var open = line.IndexOf("<" + property, StringComparison.Ordinal);
        if (open < 0) return null;

        var openEnd = line.IndexOf('>', open);
        if (openEnd < 0) return null;

        var close = line.IndexOf("</" + property + ">", openEnd, StringComparison.Ordinal);
        if (close < 0) return null;

        return line[..(openEnd + 1)] + value + line[close..];
    }
}
