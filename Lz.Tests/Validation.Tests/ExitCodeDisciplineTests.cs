using System.Reflection;
using System.Text.RegularExpressions;

namespace Lz.Tests.Validation.Tests;

/// <summary>
/// "Failures that report and exit 0 now exit non-zero" — Docs/specs/DecoupledCd.md §8, the one
/// change in that design classed as a cross-system bug fix rather than gated on <c>Pipeline</c>,
/// and P0's "the exit-code fix, with a test per formerly-silent path".
///
/// <para>THREE PATHS WERE FOUND AND FIXED on 2026-09-11, all of the same shape — a plugin that was
/// FOUND and could not be LOADED, reported as a yellow "Warning:" while execution continued:</para>
///
/// <list type="number">
///   <item>the deploy plugin in <c>Main</c> — continuing left <c>plugin = null</c>, so every
///     command, topology descriptor and SERVICE DECLARATION it contributes silently ceased to
///     exist, and a deploy of a system with no services reported success;</item>
///   <item><c>GenPluginLoader.LoadGenPlugin</c> throwing in <c>lz gen</c>;</item>
///   <item><c>RegisterGenExtensions</c> throwing for either gen plugin — the worst of the three,
///     because it did not stop generation, it CHANGED it: without the custom directive and
///     artifact types <c>lz gen</c> emits different code over the tree and exits 0.</item>
/// </list>
///
/// <para>ABSENCE WAS NEVER THE PROBLEM AND IS UNCHANGED. <c>LoadPlugin</c> and
/// <c>LoadGenPlugin</c> RETURN NULL when no plugin is present — the supported "core commands only"
/// case — and reach none of this. The catch blocks fire only on a plugin that exists and is
/// broken. That distinction is what makes this safe for every sibling system: a system with no
/// plugin behaves exactly as before.</para>
///
/// <para>THESE ARE SOURCE-LEVEL PINS, NOT BEHAVIOURAL TESTS, and it is worth saying so plainly.
/// The paths live in <c>Lz.Cli/Program.cs</c>'s <c>Main</c> and command lambdas, and Lz.Tests does
/// not reference Lz.Cli — adding that reference to reach them would be a larger change than the
/// fix. What is pinned instead is the property that actually regressed: that no catch block
/// reports a failure without signalling one. That is stronger than three individual tests in one
/// respect — it covers paths nobody has thought of yet.</para>
/// </summary>
public class ExitCodeDisciplineTests
{
    /// <summary>Anything that makes a block a report to the user.</summary>
    private static readonly Regex Reports = new(
        @"Console\.(Error\.)?WriteLine|Console\.Write|AnsiConsole|Log(Error|Warning)|✗|❌",
        RegexOptions.Compiled);

    /// <summary>
    /// Anything that makes a failure visible to the caller. Includes the INDIRECT forms this file
    /// already used before the fix — a flag or a verdict the caller turns into an exit code — so
    /// that correct code is not reported as a defect.
    /// </summary>
    private static readonly Regex Signals = new(string.Join("|", new[]
    {
        @"Environment\.ExitCode\s*=",
        @"Environment\.Exit\s*\(",
        @"throw\b",
        @"return\s+[1-9]",
        @"anyFailure\s*=\s*true",
        @"verdict\s*=",
    }), RegexOptions.Compiled);

    /// <summary>
    /// Catch blocks that deliberately report without signalling, each with the reason it is not a
    /// defect. Keyed by the line the <c>catch</c> sits on, which is why the value restates the
    /// code — a line number alone would rot silently on the next edit, and the assertion below
    /// checks the restated text is still there.
    /// </summary>
    private static readonly (string Anchor, string Why)[] Deliberate =
    {
        ("No sharedconfig.yaml",
            "ABSENCE, not failure: the cognito/dynamodb/lambda topologies have no shared services, " +
            "so a missing sharedconfig is the normal case. It is noted only when shared was " +
            "explicitly requested."),

        ("Could not ask about THIS id",
            "Reported as UNDETERMINABLE rather than swallowed. The id stays out of byId, which " +
            "refuses it — the refusal is the signal, and this block exists so the report does not " +
            "then claim the registry does not serve the package."),

        ("RegisterGenExtensions threw",
            "Signals by RETURNING FALSE, which both call sites turn into Environment.ExitCode = 1 " +
            "and an early return. Added by the 2026-09-11 fix; it is in this list because a bare " +
            "`return false` is not recognisable as a failure signal in general, not because it is " +
            "silent."),
    };

    /// <summary>
    /// One pass over Program.cs: the blocks that report without signalling, and which allowlist
    /// anchors were actually used to excuse one.
    ///
    /// <para>The two are returned together on purpose. An earlier version asked separately whether
    /// each anchor still appeared ANYWHERE in the file, and a mutation proved that worthless:
    /// "No sharedconfig.yaml" also appears in two bare <c>catch { }</c> comments elsewhere, so
    /// changing the block the entry describes left the guard green. An exemption is only current
    /// if the scan actually used it.</para>
    /// </summary>
    private static (List<string> Offenders, HashSet<string> UsedAnchors, int Scanned) Scan()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "Lz.Cli", "Program.cs"));
        var offenders = new List<string>();
        var used = new HashSet<string>();
        var scanned = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*catch\b")) continue;
            scanned++;

            var body = BlockAt(lines, i);
            if (!Reports.IsMatch(body) || Signals.IsMatch(body)) continue;

            var excuse = Deliberate.FirstOrDefault(d => body.Contains(d.Anchor));
            if (excuse.Anchor != null) { used.Add(excuse.Anchor); continue; }

            var preview = body.Split('\n').Skip(1).Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0) ?? "";
            offenders.Add($"Program.cs:{i + 1}  {preview}");
        }

        return (offenders, used, scanned);
    }

    [Fact]
    public void NoCatchBlockReportsAFailureWithoutSignallingOne()
    {
        var (offenders, _, scanned) = Scan();

        // The scan is worthless if it found nothing to scan — a path or brace-matching bug would
        // otherwise read as "all clear" forever.
        Assert.True(scanned > 20, $"only {scanned} catch blocks found in Program.cs; the scan is broken");

        Assert.True(offenders.Count == 0,
            "These catch blocks report a failure to the user but leave the exit code at 0:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nA caller — a script, CI, or the lz runner — sees success. Signal the failure " +
            "(Environment.ExitCode = 1, a non-zero return, or a flag the caller checks), or add the " +
            "block to this test's Deliberate list WITH the reason it is genuinely not a failure. " +
            "See DecoupledCd.md section 8: this is a cross-system bug fix, not a gated change.");
    }

    [Fact]
    public void EveryDeliberateExceptionIsStillEarningItsPlace()
    {
        // An exemption that excuses nothing is worse than no exemption: it is a standing licence
        // for the next block that happens to contain the same words.
        var (_, used, _) = Scan();

        var stale = Deliberate.Where(d => !used.Contains(d.Anchor)).ToList();

        Assert.True(stale.Count == 0,
            "These entries in the Deliberate allowlist no longer excuse any catch block:\n  " +
            string.Join("\n  ", stale.Select(d => $"\"{d.Anchor}\" — was exempt because: {d.Why}")) +
            "\n\nEither the block was fixed or removed (delete the entry), or it changed shape and " +
            "the anchor no longer matches it (update the anchor). Do not leave a stale exemption " +
            "behind — it would silently excuse the next block that contains the same text.");
    }

    [Fact]
    public void TheVersionQueryStaysReachableWhenThePluginIsBroken()
    {
        // --version is the ONE carve-out from the plugin-load refusal, and it earns it: it is the
        // command that names the runner/cli/plugin axes with provenance, so it is what you run to
        // diagnose exactly this failure. Pinned by ORDER, because the way this breaks is someone
        // hoisting the refusal above the intercept while tidying Main.
        var body = File.ReadAllText(Path.Combine(RepoRoot(), "Lz.Cli", "Program.cs"));

        var intercept = body.IndexOf("PrintVersionInfo(plugin);", StringComparison.Ordinal);
        var refusal = body.IndexOf("Everything that is not --version refuses", StringComparison.Ordinal);

        Assert.True(intercept > 0, "the --version intercept was not found");
        Assert.True(refusal > 0, "the plugin-load refusal was not found");
        Assert.True(intercept < refusal,
            "The plugin-load refusal now runs BEFORE the --version intercept, so `lz --version` " +
            "fails when a plugin is broken — removing the diagnostic you would use to find out " +
            "which plugin path was resolved. Keep the intercept first.");
    }

    /// <summary>Walk up from the test assembly to the directory holding Lz.slnx.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The source of the brace-delimited block beginning at or after <paramref name="start"/>.</summary>
    private static string BlockAt(string[] lines, int start)
    {
        var j = start;
        while (j < lines.Length && !lines[j].Contains('{')) j++;

        var depth = 0;
        var started = false;
        var body = new List<string>();

        for (; j < lines.Length; j++)
        {
            foreach (var ch in lines[j])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            body.Add(lines[j]);
            if (started && depth == 0) break;
        }

        return string.Join("\n", body);
    }
}
