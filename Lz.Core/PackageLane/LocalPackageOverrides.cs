using System.Text;

namespace Lz.Core.PackageLane;

/// <summary>One package found in a workspace feed.</summary>
/// <param name="Id">Package id as it appears in the file name, original casing.</param>
/// <param name="Version">The version string, exactly as the file name spells it.</param>
public readonly record struct FeedPackage(string Id, string Version);

/// <summary>
/// What the local lane's override file should contain, derived purely from what is on disk in the
/// workspace feeds. No I/O, no MSBuild, no NuGet client — the CLI only reads the directories and
/// writes the file this produces.
///
/// <para>WHY AN OVERRIDE FILE AT ALL. Under derived versioning a producer's version changes with
/// every commit, so a consumer's committed pin cannot name it. The pin stays in
/// <c>Directory.Packages.props</c> for the published lane; the local lane redirects it through an
/// MSBuild property that this file sets. That indirection is the mechanism MigrationPlan §7
/// decision 8 chose over <c>VersionOverride</c>, and it is verified: with the override file absent a
/// restore takes the committed pin, and with it present the overridden version, measured on disk
/// rather than read from a log.</para>
///
/// <para>ORDER IS LOAD-BEARING IN THE CONSUMER. The import of this file must come AFTER the
/// property defaults it overrides. Placed before them, the defaults win, the restore still succeeds,
/// and the wrong version is used with no diagnostic anywhere — measured, not assumed. That is why
/// <see cref="Render"/> emits a header saying so: the file is generated, but the line that imports
/// it is written by hand once and is easy to put in the wrong place.</para>
/// </summary>
public static class LocalPackageOverrides
{
    /// <summary>The MSBuild property that carries the local version of <paramref name="packageId"/>.</summary>
    /// <remarks>
    /// One property per package ID rather than one per producer repo. Grouping by repo would need
    /// the tool to know which ids a repo produces, and the shared <c>repos/Packages</c> feed proves
    /// that is not derivable from the feed layout: it holds the Service family and the BaseAppLib
    /// family at two different versions in one directory. Per-id also degrades correctly — an id
    /// absent from every feed simply keeps its committed pin, which is the right answer for a
    /// package this workspace does not build.
    /// </remarks>
    public static string PropertyName(string packageId)
        => "PkgVer_" + packageId.Replace('.', '_');

    /// <summary>
    /// Newest version per id, with a note naming any id that had more than one. A tie is not an
    /// error — a resumed or <c>-SkipClean</c> build legitimately leaves an older package behind —
    /// but it is never silent, because "which of these did it pick" is exactly the question a
    /// mis-resolved build makes someone ask.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Chosen, IReadOnlyList<string> Ambiguous)
        Resolve(IEnumerable<FeedPackage> packages)
    {
        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            if (!byId.TryGetValue(p.Id, out var list)) byId[p.Id] = list = new List<string>();
            if (!list.Contains(p.Version, StringComparer.OrdinalIgnoreCase)) list.Add(p.Version);
        }

        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new List<string>();
        foreach (var (id, versions) in byId)
        {
            versions.Sort((a, b) => Compare(b, a));   // newest first
            chosen[id] = versions[0];
            if (versions.Count > 1)
                ambiguous.Add($"{id}: chose {versions[0]} from {string.Join(", ", versions)}");
        }
        ambiguous.Sort(StringComparer.Ordinal);
        return (chosen, ambiguous);
    }

    /// <summary>
    /// Two package versions, ordered as NuGet orders them: numeric parts numerically, and a
    /// prerelease sorting BEFORE the release it qualifies (<c>3.0.18-alpha &lt; 3.0.18</c>).
    /// Prerelease identifiers compare numerically when both are numeric, otherwise ordinally.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (aNums, aPre) = Split(a);
        var (bNums, bPre) = Split(b);

        for (var i = 0; i < Math.Max(aNums.Length, bNums.Length); i++)
        {
            var x = i < aNums.Length ? aNums[i] : 0;
            var y = i < bNums.Length ? bNums[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        // A release outranks any prerelease of the same numbers.
        if (aPre.Length == 0 && bPre.Length == 0) return 0;
        if (aPre.Length == 0) return 1;
        if (bPre.Length == 0) return -1;

        for (var i = 0; i < Math.Max(aPre.Length, bPre.Length); i++)
        {
            if (i >= aPre.Length) return -1;          // shorter set of identifiers ranks lower
            if (i >= bPre.Length) return 1;
            var xs = aPre[i];
            var ys = bPre[i];
            var xn = int.TryParse(xs, out var xi);
            var yn = int.TryParse(ys, out var yi);
            if (xn && yn) { if (xi != yi) return xi.CompareTo(yi); }
            else
            {
                if (xn != yn) return xn ? -1 : 1;      // numeric identifiers rank below alphanumeric
                var c = string.CompareOrdinal(xs, ys);
                if (c != 0) return c;
            }
        }
        return 0;
    }

    private static (int[] Numbers, string[] Prerelease) Split(string version)
    {
        var plus = version.IndexOf('+');                       // build metadata is not compared
        if (plus >= 0) version = version[..plus];
        var dash = version.IndexOf('-');
        var core = dash >= 0 ? version[..dash] : version;
        var pre = dash >= 0 ? version[(dash + 1)..] : string.Empty;

        var nums = core.Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
        var preParts = pre.Length == 0
            ? Array.Empty<string>()
            : pre.Split('.', '-');                             // NBGV emits both -alpha-gSHA and -alpha.gSHA
        return (nums, preParts);
    }

    /// <summary>The override file's contents. Deterministic: ids in ordinal order, so a no-op
    /// refresh rewrites byte-identical content and leaves no spurious diff.</summary>
    public static string Render(IReadOnlyDictionary<string, string> chosen, string generatedBy)
    {
        var sb = new StringBuilder();
        sb.Append("<Project>\n");
        sb.Append("  <!--\n");
        sb.Append("    GENERATED - do not edit, and do not commit. Written by `").Append(generatedBy).Append("`.\n");
        sb.Append("\n");
        sb.Append("    The local lane's package versions, taken from what the workspace feeds actually hold.\n");
        sb.Append("    Each property overrides the committed pin for one package id; an id with no entry here\n");
        sb.Append("    keeps the version in Directory.Packages.props, which is the right answer for a package\n");
        sb.Append("    this workspace does not build.\n");
        sb.Append("\n");
        sb.Append("    The consumer must import this AFTER its property defaults. Imported before them the\n");
        sb.Append("    defaults win, restore still succeeds, and the wrong version is used with no diagnostic\n");
        sb.Append("    anywhere.\n");
        sb.Append("  -->\n");
        sb.Append("  <PropertyGroup>\n");
        foreach (var id in chosen.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var prop = PropertyName(id);
            sb.Append("    <").Append(prop).Append('>').Append(chosen[id]).Append("</").Append(prop).Append(">\n");
        }
        sb.Append("  </PropertyGroup>\n");
        sb.Append("</Project>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Split a <c>.nupkg</c> file name into id and version. Returns null for anything that is not
    /// <c>{id}.{version}.nupkg</c> with a numeric first version part, which is what keeps an id
    /// containing dots (every id here does) from being mistaken for a version.
    /// </summary>
    public static FeedPackage? ParseFileName(string fileName)
    {
        const string ext = ".nupkg";
        if (!fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = fileName[..^ext.Length];

        // Walk the dots left to right; the version starts at the first part that is a number AND is
        // followed by at least one more part. "LazyMagic.Blazor.3.0.18" -> id "LazyMagic.Blazor".
        var parts = stem.Split('.');
        for (var i = 1; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out _)) continue;
            var version = string.Join('.', parts[i..]);
            if (version.Split('.').Length < 2) continue;        // need at least major.minor
            return new FeedPackage(string.Join('.', parts[..i]), version);
        }
        return null;
    }
}
