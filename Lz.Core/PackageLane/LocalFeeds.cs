using System.Xml.Linq;

namespace Lz.Core.PackageLane;

/// <summary>
/// Which directories are the workspace's local package feeds, read from the workspace
/// <c>NuGet.Config</c> rather than hard-coded.
///
/// <para>Hard-coding the three known feeds would go stale the moment one is added or moved, and the
/// config is already the authority — it is the file that decides where restore looks. Reading it
/// also means the tool and the restore can never disagree about what "the workspace feeds" are.</para>
/// </summary>
public static class LocalFeeds
{
    /// <summary>
    /// The relative feed paths declared in a <c>NuGet.Config</c>: every <c>packageSources</c> entry
    /// whose value is not an absolute URI. Order is preserved, because the config's own comments
    /// say entry order decides a same-version collision.
    /// </summary>
    public static IReadOnlyList<string> FromNuGetConfig(string xml)
    {
        var doc = XDocument.Parse(xml);
        var sources = doc.Root?.Element("packageSources");
        if (sources is null) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var add in sources.Elements("add"))
        {
            var value = add.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            // A remote source is a URI with a scheme; anything else is a path on this machine.
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                continue;
            paths.Add(value);
        }
        return paths;
    }

    /// <summary>
    /// Every package file in the given feed directories, parsed. Missing directories are skipped
    /// rather than refused: a feed that has never been built is empty, not broken, and the caller's
    /// own report makes an empty result visible.
    /// </summary>
    public static IReadOnlyList<FeedPackage> Scan(string root, IEnumerable<string> relativeFeeds)
    {
        var found = new List<FeedPackage>();
        foreach (var rel in relativeFeeds)
        {
            var dir = Path.GetFullPath(Path.Combine(root, rel));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.nupkg"))
            {
                var parsed = LocalPackageOverrides.ParseFileName(Path.GetFileName(file));
                if (parsed is not null) found.Add(parsed.Value);
            }
        }
        return found;
    }
}
