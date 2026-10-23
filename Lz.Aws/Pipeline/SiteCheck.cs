using System.Text;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>One environment's site bundle, expanded: each file's path relative to the site's root, and its bytes.</summary>
public sealed record SiteBundle(string Environment, Dictionary<string, byte[]> Files);

/// <summary>
/// What the site check is told by the build-side file (DecoupledCd.md V-24): the environments whose site may be indexed, and
/// the hosts a page may load a script or stylesheet from without SRI, which a third party's changing file cannot carry.
/// </summary>
public sealed record SiteCheckSettings(IReadOnlyList<string> Indexed, IReadOnlyList<string> ThirdPartyHosts);

/// <summary>The site check's verdict, every reason it refused, and the host each environment's bundle names.</summary>
public sealed record SiteCheckResult(bool Passed, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string> Hosts);

/// <summary>A start tag: its name and attributes, both lowercased names, the values decoded.</summary>
public sealed record SiteTag(string Name, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// GATE 1 FOR A SITE (DecoupledCd.md V-24, §15.7 V-K): lz's own check of the bundles one run built, one per environment.
/// It runs no repository code, so it needs no CodeBuild and no isolation, and its verdict binds the staged bytes as Gate 1's
/// does. For each bundle:
/// <list type="bullet">
/// <item>the pages the edge relies on exist: <c>index.html</c>, <c>404.html</c>, and <c>park.html</c>, which park mode's
/// rewrite resolves to this bucket;</item>
/// <item>every local link, image and asset resolves inside the bundle, as the edge resolves a directory to its
/// <c>index.html</c>;</item>
/// <item>the canonical and Open Graph URLs name one host, the home page's, which DeploySite compares with the environment's
/// domain;</item>
/// <item><c>noindex</c> is on every page of an environment that is not indexed, and not on an indexed one's home page;</item>
/// <item>every same-origin stylesheet and script carries SRI, and a third-party one comes from a named host.</item>
/// </list>
/// Across bundles, the same files with the same bytes once each bundle's host and its <c>noindex</c> are set aside, so what
/// one environment served is, apart from those, what the next one serves — checked rather than asserted.
/// </summary>
public static class SiteCheck
{
    /// <summary>The pages a site must hold: the home page, the error page, and park mode's page.</summary>
    public static readonly string[] RequiredPages = { "index.html", "404.html", "park.html" };

    private const int MaxReasons = 50;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xml", ".txt", ".json", ".css", ".js", ".mjs", ".svg", ".webmanifest",
    };

    public static SiteCheckResult Check(IReadOnlyList<SiteBundle> bundles, SiteCheckSettings settings)
    {
        var reasons = new List<string>();
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bundles.Count == 0)
            return new SiteCheckResult(false, new[] { "there is no site bundle to check" }, hosts);

        foreach (var twice in bundles.GroupBy(b => b.Environment, StringComparer.Ordinal).Where(g => g.Count() > 1))
            reasons.Add($"two bundles name environment {twice.Key}");

        foreach (var bundle in bundles.DistinctBy(b => b.Environment))
        {
            if (CheckOne(bundle, settings, reasons) is { } host) hosts[bundle.Environment] = host;
        }

        foreach (var shared in hosts.GroupBy(h => h.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            reasons.Add($"{string.Join(" and ", shared.Select(h => h.Key))} both name host {shared.Key}");

        // ACROSS BUNDLES: each against the first, by file, once its own host and noindex are set aside.
        var first = bundles[0];
        foreach (var other in bundles.Skip(1).Where(b => b.Environment != first.Environment))
            Compare(first, hosts.GetValueOrDefault(first.Environment), other, hosts.GetValueOrDefault(other.Environment), reasons);

        var kept = reasons.Count > MaxReasons
            ? reasons.Take(MaxReasons).Append($"and {reasons.Count - MaxReasons} more").ToList()
            : reasons;
        return new SiteCheckResult(reasons.Count == 0, kept, hosts);
    }

    /// <summary>
    /// The host a bundle's home page names as its canonical URL; null when it names none. DeploySite reads it too, to hold the
    /// bundle to the environment's domain before anything is written.
    /// </summary>
    public static string? HostOf(IReadOnlyDictionary<string, byte[]> files)
        => files.TryGetValue("index.html", out var index) ? Read(Encoding.UTF8.GetString(index)).Canonical : null;

    private static string? CheckOne(SiteBundle bundle, SiteCheckSettings settings, List<string> reasons)
    {
        var env = bundle.Environment;
        foreach (var page in RequiredPages.Where(p => !bundle.Files.ContainsKey(p)))
            reasons.Add($"{env}: {page} is missing");

        var pages = bundle.Files.Keys.Where(IsHtml).OrderBy(p => p, StringComparer.Ordinal)
            .ToDictionary(p => p, p => Read(Encoding.UTF8.GetString(bundle.Files[p])), StringComparer.Ordinal);

        string? host = null;
        if (pages.TryGetValue("index.html", out var home))
        {
            host = home.Canonical;
            if (host is null) reasons.Add($"{env}: index.html names no canonical URL");
        }

        var indexed = settings.Indexed.Contains(env, StringComparer.Ordinal);
        var thirdParty = settings.ThirdPartyHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, page) in pages)
        {
            if (host is not null)
                foreach (var named in page.Hosts.Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                    reasons.Add($"{env}: {path} names host {named}; index.html names {host}");

            if (!indexed && !page.NoIndex)
                reasons.Add($"{env}: {path} carries no noindex, and {env} is not indexed");
            if (indexed && path == "index.html" && page.NoIndex)
                reasons.Add($"{env}: index.html carries noindex, and {env} is indexed");

            foreach (var reference in page.References.Distinct(StringComparer.Ordinal))
            {
                if (Local(reference, path, host) is not { } local) continue;
                if (!Resolves(local, bundle.Files))
                    reasons.Add($"{env}: {path} links to {reference}, which is not in the bundle");
            }

            foreach (var (src, integrity) in page.Loads)
            {
                if (Local(src, path, host) is not null)
                {
                    if (string.IsNullOrWhiteSpace(integrity)) reasons.Add($"{env}: {path} loads {src} without SRI");
                }
                else if (ExternalHost(src) is { } external && !thirdParty.Contains(external) && string.IsNullOrWhiteSpace(integrity))
                    reasons.Add($"{env}: {path} loads {src} from {external}, which is not a named third-party host");
            }
        }

        return host;
    }

    private static void Compare(SiteBundle first, string? firstHost, SiteBundle other, string? otherHost, List<string> reasons)
    {
        var env = other.Environment;
        foreach (var path in other.Files.Keys.Except(first.Files.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            reasons.Add($"{env}: {path} is not in {first.Environment}'s bundle");
        foreach (var path in first.Files.Keys.Except(other.Files.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            reasons.Add($"{env}: {path} is in {first.Environment}'s bundle and not this one");

        foreach (var path in first.Files.Keys.Intersect(other.Files.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var (a, b) = (first.Files[path], other.Files[path]);
            if (TextExtensions.Contains(Path.GetExtension(path)))
            {
                if (!string.Equals(Normalize(Encoding.UTF8.GetString(a), firstHost), Normalize(Encoding.UTF8.GetString(b), otherHost), StringComparison.Ordinal))
                    reasons.Add($"{env}: {path} differs from {first.Environment}'s beyond the host and noindex");
            }
            else if (!a.AsSpan().SequenceEqual(b))
                reasons.Add($"{env}: {path} differs from {first.Environment}'s");
        }
    }

    /// <summary>
    /// A text file with its host and its robots <c>noindex</c> set aside: the host only where a URL names it (<c>//host</c>),
    /// so an e-mail address on the same domain is compared as written.
    /// </summary>
    private static string Normalize(string text, string? host)
    {
        if (host is not null) text = text.Replace("//" + host, "//{host}", StringComparison.OrdinalIgnoreCase);
        return MetaTag.Replace(text, m => SiteHtml.Tags(m.Value).FirstOrDefault() is { } tag && IsNoIndex(tag) ? "" : m.Value);
    }

    private static readonly Regex MetaTag = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record PageRead(string? Canonical, IReadOnlyList<string> Hosts, bool NoIndex,
        IReadOnlyList<string> References, IReadOnlyList<(string Src, string? Integrity)> Loads);

    private static PageRead Read(string html)
    {
        string? canonical = null;
        var hosts = new List<string>();
        var noIndex = false;
        var references = new List<string>();
        var loads = new List<(string, string?)>();

        foreach (var tag in SiteHtml.Tags(html))
        {
            var a = tag.Attributes;
            string? Attr(string name) => a.TryGetValue(name, out var v) ? v : null;
            var rel = (Attr("rel") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tag.Name == "link" && rel.Contains("canonical", StringComparer.OrdinalIgnoreCase) && Attr("href") is { } c && HostIn(c) is { } ch)
            {
                canonical ??= ch;
                hosts.Add(ch);
            }
            if (tag.Name == "meta" && string.Equals(Attr("property"), "og:url", StringComparison.OrdinalIgnoreCase) && Attr("content") is { } og && HostIn(og) is { } oh)
                hosts.Add(oh);
            if (tag.Name == "meta" && IsNoIndex(tag)) noIndex = true;

            switch (tag.Name)
            {
                case "a" or "area" when Attr("href") is { } href:
                    references.Add(href);
                    break;
                case "link" when Attr("href") is { } href:
                    // A preconnect or dns-prefetch names an origin, not a file.
                    if (!rel.Any(r => r.Equals("preconnect", StringComparison.OrdinalIgnoreCase) || r.Equals("dns-prefetch", StringComparison.OrdinalIgnoreCase)))
                        references.Add(href);
                    if (rel.Contains("stylesheet", StringComparer.OrdinalIgnoreCase)) loads.Add((href, Attr("integrity")));
                    break;
                case "script" when Attr("src") is { } src:
                    references.Add(src);
                    loads.Add((src, Attr("integrity")));
                    break;
                case "img" or "source" or "video" or "audio" or "track" or "iframe" or "embed":
                    if (Attr("src") is { } s) references.Add(s);
                    if (Attr("srcset") is { } set)
                        references.AddRange(set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(candidate => candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
                    if (Attr("poster") is { } poster) references.Add(poster);
                    break;
            }
        }

        return new PageRead(canonical, hosts, noIndex, references, loads);
    }

    private static bool IsNoIndex(SiteTag tag)
        => tag.Name == "meta"
           && tag.Attributes.TryGetValue("name", out var name) && string.Equals(name, "robots", StringComparison.OrdinalIgnoreCase)
           && tag.Attributes.TryGetValue("content", out var content) && content.Contains("noindex", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtml(string path)
        => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static string? HostIn(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host : null;

    private static string? ExternalHost(string url)
        => url.StartsWith("//", StringComparison.Ordinal) ? HostIn("https:" + url) : HostIn(url);

    /// <summary>
    /// The path inside the bundle a reference on <paramref name="page"/> names, without a leading slash; null when it names
    /// none: another host, a fragment, or a scheme that is not a page's.
    /// </summary>
    private static string? Local(string reference, string page, string? host)
    {
        var value = reference.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return null;

        Uri resolved;
        if (value.StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*:"))
        {
            var absolute = value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;
            if (!Uri.TryCreate(absolute, UriKind.Absolute, out resolved!) || resolved.Scheme is not ("http" or "https")) return null;
            if (host is null || !string.Equals(resolved.Host, host, StringComparison.OrdinalIgnoreCase)) return null;
        }
        else
        {
            var directory = page.Contains('/') ? page[..(page.LastIndexOf('/') + 1)] : "";
            if (!Uri.TryCreate(new Uri("https://site.invalid/" + directory), value, out resolved!)) return null;
        }

        return Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
    }

    /// <summary>As the edge resolves a path: the file, or a directory's <c>index.html</c> (a path without a slash is redirected to one).</summary>
    private static bool Resolves(string path, IReadOnlyDictionary<string, byte[]> files)
        => path.Length == 0 || path.EndsWith('/')
            ? files.ContainsKey(path + "index.html")
            : files.ContainsKey(path) || files.ContainsKey(path + "/index.html");
}

/// <summary>
/// The start tags of an HTML document, as the site check needs them: attribute values quoted either way or not at all (a
/// minified page drops the quotes), entities decoded, comments skipped, and nothing read inside a <c>script</c> or
/// <c>style</c> element. Not a parser of the whole language; enough for links, canonicals, robots and SRI.
/// </summary>
public static class SiteHtml
{
    private static readonly Regex TagStart = new(@"\G<([A-Za-z][A-Za-z0-9-]*)", RegexOptions.CultureInvariant);
    private static readonly Regex Attribute = new(
        @"\G([^\s=/>""']+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?", RegexOptions.CultureInvariant);

    public static IEnumerable<SiteTag> Tags(string html)
    {
        var i = 0;
        while (i < html.Length && (i = html.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var start = TagStart.Match(html, i);
            if (!start.Success) { i++; continue; }

            var name = start.Groups[1].Value.ToLowerInvariant();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var at = start.Index + start.Length;
            while (at < html.Length)
            {
                while (at < html.Length && char.IsWhiteSpace(html[at])) at++;
                if (at >= html.Length || html[at] == '>') { at++; break; }
                if (html[at] == '/') { at++; continue; }

                var attribute = Attribute.Match(html, at);
                if (!attribute.Success) { at++; continue; }
                var key = attribute.Groups[1].Value.ToLowerInvariant();
                var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value
                    : attribute.Groups[4].Success ? attribute.Groups[4].Value
                    : "";
                attributes.TryAdd(key, Decode(value));
                at = attribute.Index + attribute.Length;
            }

            yield return new SiteTag(name, attributes);
            i = at;

            // A script's or a style's text is not markup.
            if (name is "script" or "style")
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
            }
        }
    }

    private static string Decode(string value)
        => value.Contains('&')
            ? value.Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&#x27;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
            : value;
}
