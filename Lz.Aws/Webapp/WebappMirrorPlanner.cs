namespace Lz.Aws.Webapp;

/// <summary>One file of a bundle: its path relative to the publish root (<c>seller/index.html</c>), SHA-256 and size.</summary>
public sealed record BundleFile(string Path, string Sha256Hex, long Size);

/// <summary>
/// One object already in the app's bucket, as a HEAD reads it.
/// </summary>
/// <param name="Sha256Hex">S3's own SHA-256 of the whole object, lowercase hex; null when the object was not written
/// with one — the aws CLI's sync writes none — or S3 reports a composite checksum, which is not the object's.</param>
public sealed record StoredObject(string Key, string? Sha256Hex, string? CacheControl, string? ContentType, string? ContentEncoding);

/// <summary>One object a mirror writes, with the headers it is written with and why it is written.</summary>
/// <param name="Reason"><c>absent</c>, <c>content</c> (different bytes, or bytes S3 holds no SHA-256 for) or <c>headers</c>.</param>
public sealed record MirrorPut(string Key, BundleFile File, WebappObjectHeaders Headers, string Reason);

/// <summary>What one deploy of a bundle writes and deletes, in the order it must.</summary>
/// <param name="KeyPrefix">Everything under it is the app's: <c>wwwroot/seller/</c>.</param>
/// <param name="Assets">Written first: everything that is not a no-cache manifest.</param>
/// <param name="Manifests">Written second: the files that name the others, so none names a file not yet there.</param>
/// <param name="Deletes">Last: what the bundle lacks under the prefix, once nothing written names it.</param>
/// <param name="ManifestSha256">The bundle's manifest digest (<see cref="WebappSyncRules.ManifestSha256"/>).</param>
public sealed record MirrorPlan(
    string BasePath,
    string KeyPrefix,
    IReadOnlyList<MirrorPut> Assets,
    IReadOnlyList<MirrorPut> Manifests,
    IReadOnlyList<string> Deletes,
    int Unchanged,
    string ManifestSha256,
    long Bytes);

/// <summary>
/// Plans the mirror of a bundle into its web app's bucket (DecoupledCd.md P4 stage C, P-2): put what is new or changed
/// with the headers <see cref="WebappHeaderRules"/> gives it, delete what the bundle lacks under the app's prefix.
///
/// <para><b>CHANGED MEANS THE BYTES OR THE HEADERS.</b> The bytes are compared with the SHA-256 S3 keeps for an object
/// written with one, which every object this deploy writes is. An object with none — every object <c>deploywebapp</c>'s
/// sync wrote — is written again, so the first pipeline deploy of an app rewrites all of it, once.</para>
///
/// <para><b>THE ORDER IS THE POINT OF THE GROUPS.</b> <c>aws s3 sync --delete</c> uploads and deletes at once, so a
/// browser loading mid-deploy can get a new <c>index.html</c> naming a framework file not yet uploaded, or an old one
/// naming a file just deleted. Written as assets, then manifests, then deletes, every manifest a browser can read names
/// files that exist.</para>
///
/// <para>BCL ONLY: the deployer's Lambda package compiles this file by link.</para>
/// </summary>
public static class WebappMirrorPlanner
{
    /// <summary>More files than any bundle this system builds by far (573), and fewer than a zip bomb would claim.</summary>
    public const int MaxFiles = 10_000;

    /// <summary>
    /// Why a bundle's entry names cannot be mirrored, or an empty list when they can. Directory entries — a name ending in
    /// <c>/</c> — are not files and are not judged; everything else must be a relative path with <c>/</c> separators that
    /// cannot leave the directory it is expanded into or the key prefix it is written under.
    /// </summary>
    public static IReadOnlyList<string> RefusalsForEntryNames(IEnumerable<string> entryNames)
    {
        var refusals = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var name in entryNames)
        {
            if (name.EndsWith('/')) continue;
            count++;

            string? why = null;
            if (name.Length == 0) why = "is empty";
            else if (name.Contains('\\')) why = "contains a backslash";
            else if (name.StartsWith('/')) why = "is absolute";
            else if (name.Contains(':')) why = "contains a colon";
            else if (name.Any(char.IsControl)) why = "contains a control character";
            else if (name.Split('/').Any(s => s is "" or "." or "..")) why = "has an empty, '.' or '..' segment";
            // A 1,024-byte S3 key, less the storage prefix and a base path.
            else if (System.Text.Encoding.UTF8.GetByteCount(name) > 900) why = "is longer than 900 bytes";

            if (why != null) refusals.Add($"entry '{Printable(name)}' {why}");
            else if (!seen.Add(name)) refusals.Add($"entry '{name}' appears twice");

            if (refusals.Count >= 10) break;
        }

        if (count > MaxFiles) refusals.Add($"the bundle holds {count} files; a mirror takes at most {MaxFiles}");
        if (count == 0) refusals.Add("the bundle holds no files");
        return refusals;
    }

    /// <summary>Plan the mirror, or throw with why the bundle does not belong where it is being deployed.</summary>
    /// <param name="bundle">Every file of the bundle.</param>
    /// <param name="stored">What a listing under the app's key prefix found, each read by a HEAD.</param>
    /// <param name="expectedBasePath">The app's configured base path (<see cref="WebappSyncRules.BasePathFromBehaviorPath"/>).</param>
    public static MirrorPlan Plan(IReadOnlyList<BundleFile> bundle, IReadOnlyList<StoredObject> stored, string expectedBasePath)
    {
        if (bundle.Count == 0)
            throw new InvalidOperationException("the bundle holds no files; mirroring it would delete the app.");

        // BUILT FOR THIS PATH. A bundle's base path is compiled into it (StaticWebAssetBasePath); deployed under another,
        // its index.html names files the edge never routes to it.
        var basePath = WebappSyncRules.BasePathOf(bundle.Select(f => f.Path));
        if (!string.Equals(basePath, expectedBasePath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the bundle is built for base path '{basePath}', and this app is served at '{expectedBasePath}'. Its pages would name " +
                "files under the wrong path.");

        // EVERYTHING UNDER IT. The mirror deletes only under the app's prefix, so a file outside would be written once and
        // then never cleaned up, and could overwrite what another app keeps there.
        var outside = bundle.Where(f => !f.Path.StartsWith(basePath, StringComparison.Ordinal)).Select(f => f.Path).Take(3).ToList();
        if (outside.Count > 0)
            throw new InvalidOperationException(
                $"the bundle holds files outside its base path '{basePath}' ({string.Join(", ", outside)}); only the app's own " +
                "prefix is mirrored.");

        var keyPrefix = WebappSyncRules.StoragePrefix + basePath;
        var strays = stored.Where(o => !o.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Select(o => o.Key).Take(3).ToList();
        if (strays.Count > 0)
            throw new InvalidOperationException(
                $"the bucket's listing holds keys outside '{keyPrefix}' ({string.Join(", ", strays)}), so it was not listed under " +
                "the app's prefix and cannot decide what to delete.");

        var rules = new WebappHeaderRules(basePath, bundle.Select(f => f.Path));
        var byKey = stored.ToDictionary(o => o.Key, StringComparer.Ordinal);

        var assets = new List<MirrorPut>();
        var manifests = new List<MirrorPut>();
        var unchanged = 0;

        foreach (var file in bundle.OrderBy(f => f.Path, WebappSyncRules.Utf8Ordinal.Instance))
        {
            var key = WebappSyncRules.StoragePrefix + file.Path;
            var headers = rules.For(file.Path);

            string? reason = !byKey.TryGetValue(key, out var current) ? "absent"
                : !string.Equals(current.Sha256Hex, file.Sha256Hex, StringComparison.Ordinal) ? "content"
                : !WebappSyncRules.Carries(headers, current.CacheControl, current.ContentType, current.ContentEncoding) ? "headers"
                : null;

            if (reason is null)
            {
                unchanged++;
                continue;
            }

            (headers.CacheControl == WebappSyncRules.NoCache ? manifests : assets).Add(new MirrorPut(key, file, headers, reason));
        }

        var keys = bundle.Select(f => WebappSyncRules.StoragePrefix + f.Path).ToHashSet(StringComparer.Ordinal);
        var deletes = stored.Select(o => o.Key).Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        return new MirrorPlan(
            basePath, keyPrefix, assets, manifests, deletes, unchanged,
            WebappSyncRules.ManifestSha256(bundle.Select(f => (f.Path, f.Sha256Hex))),
            bundle.Sum(f => f.Size));
    }

    private static string Printable(string name)
        => new(name.Select(c => char.IsControl(c) ? '?' : c).Take(120).ToArray());
}
