using System.Security.Cryptography;
using System.Text;

namespace Lz.Aws.Webapp;

/// <summary>The three headers a web-app object is served with, as S3 stores them.</summary>
/// <param name="ContentEncoding">Null when the object carries none.</param>
public sealed record WebappObjectHeaders(string CacheControl, string ContentType, string? ContentEncoding);

/// <summary>
/// What a Blazor web-app bundle looks like in its bucket: the keys, the headers each object is served with, the path a
/// deploy invalidates, and the manifest that names the bundle's bytes (DecoupledCd.md P-2, P4 stage C).
///
/// <para><b>ONE DEFINITION FOR BOTH DEPLOYERS.</b> <c>lz deploywebapp</c> reaches its headers through five
/// <c>aws s3</c> passes (<c>WebappDeployer</c>), and the pipeline's <c>DeployBundle</c> function puts each object
/// with its headers directly. The rules below are those passes, written as the result they leave: each pass REPLACES all
/// three headers on the objects it matches, so an object's headers are those of the last pass that matched it. Measured
/// against the 573 objects <c>deploywebapp</c> left in <c>scu---webapp-sellerapp-4df6-b9c6</c> on 2026-09-14, every
/// object but three agrees; the three are <c>_content/BlazorUI/appConfig.js</c> and its two compressed siblings, which
/// Lz <c>dd82106</c> made no-cache after that deploy ran.</para>
///
/// <para>BCL ONLY, because the deployer's Lambda package compiles this file by link (Lz.Aws.Deployer.csproj).</para>
/// </summary>
public static class WebappSyncRules
{
    /// <summary>Where every web-app bucket keeps what CloudFront serves: its origin path is <c>/wwwroot{appPath}</c>.</summary>
    public const string StoragePrefix = "wwwroot/";

    /// <summary>Pass 1's baseline, for everything no later pass matches.</summary>
    public const string Baseline = "public, max-age=3600";

    /// <summary>Content-hashed framework files, which never change under their name.</summary>
    public const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>
    /// The manifests: fixed names whose content changes every build. Without <c>no-store</c> on purpose — the apps'
    /// recovery script refetches with <c>cache: 'reload'</c>, which needs the browser to keep the response.
    /// </summary>
    public const string NoCache = "no-cache, must-revalidate";

    /// <summary>
    /// The type given a file whose extension the table does not know. A choice, not a measurement: <c>deploywebapp</c>'s
    /// sync sends no type for such a file and leaves the default to S3, and no bundle deployed so far holds one.
    /// </summary>
    public const string UnknownContentType = "application/octet-stream";

    /// <summary>
    /// A system-scoped web-app bucket, <c>{sk}---webapp-{app}-{ss}</c>: the one <c>deploywebapp</c> deploys into on every
    /// topology without central auth, and the one <c>lz verify</c> checks among the system's resources.
    /// </summary>
    public static string SystemBucketName(string systemKey, string appName, string systemSuffix)
        => $"{systemKey}---webapp-{appName.ToLowerInvariant()}-{systemSuffix}";

    /// <summary>
    /// The one CloudFront path a deploy clears: everything under the prefix it synced (DecoupledCd.md P-9). An app or
    /// site at the root owns every path, so it clears "/*"; one under a path clears that path alone, never the
    /// distribution's other apps. The wildcard follows the prefix with no slash because CFRequest serves "/seller" as
    /// well as "/seller/...", rewriting both to the app's index.html.
    /// </summary>
    public static string InvalidationPath(string? syncedPrefix)
    {
        var prefix = (syncedPrefix ?? "").Trim().Trim('/');
        return prefix.Length == 0 ? "/*" : $"/{prefix}*";
    }

    /// <summary>
    /// A web app's configured path (<c>WebApps[].Path</c>, e.g. <c>/seller/,/seller</c>) as the base path its bundle
    /// publishes under: <c>seller/</c>, or "" for an app at the root. The first entry names it; the rest are aliases
    /// CloudFront routes to the same app.
    /// </summary>
    public static string BasePathFromBehaviorPath(string? behaviorPath)
    {
        var first = (behaviorPath ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var trimmed = first.Trim('/');
        return trimmed.Length == 0 ? "" : trimmed + "/";
    }

    /// <summary>
    /// The path a Blazor publish output serves under, from its files relative to the publish root: the folder holding
    /// <c>_framework</c>, with a trailing slash (<c>seller/</c>); "" for an app at the root, or for files with no
    /// framework at all.
    ///
    /// <para>REFUSES TWO. <c>WebappDeployer.BundleBasePath</c> takes whichever <c>_framework</c> directory the file
    /// system lists first, which is harmless on a workstation whose publish has one; a bundle with two is not one app, and
    /// a deploy that guessed would put the manifests' headers on the wrong files.</para>
    /// </summary>
    public static string BasePathOf(IEnumerable<string> relativeFiles)
    {
        var bases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in relativeFiles)
        {
            var file = raw.Replace('\\', '/');
            var at = file.StartsWith("_framework/", StringComparison.Ordinal)
                ? 0
                : file.IndexOf("/_framework/", StringComparison.Ordinal) is var i and >= 0 ? i + 1 : -1;
            if (at >= 0) bases.Add(file[..at]);
        }

        return bases.Count switch
        {
            0 => "",
            1 => bases.Min!,
            _ => throw new InvalidOperationException(
                $"the bundle holds {bases.Count} _framework folders ({string.Join(", ", bases.Select(b => b.Length == 0 ? "(root)" : b))}), " +
                "so it is not one Blazor app and its base path cannot be told."),
        };
    }

    /// <summary>
    /// The files pass 3 marks no-cache, relative to the bundle's base path: the fixed manifests, plus every
    /// <c>appConfig.js</c> and <c>indexinit.js</c> wherever the bundle holds one. The apps keep <c>appConfig.js</c> in
    /// their UI library, under <c>_content/BlazorUI/</c>, so the fixed root entry matched nothing and the file took the
    /// one-hour baseline. Compressed siblings are not entries: the pass covers each entry's .br and .gz itself.
    /// </summary>
    public static IReadOnlyList<(string Path, string ContentType)> NoCacheFiles(IEnumerable<string> bundleRelativeFiles)
    {
        var files = new List<(string Path, string ContentType)>
        {
            ("index.html",                       "text/html"),
            ("authentication/login.html",        "text/html"),
            ("_framework/blazor.boot.json",      "application/json"),
            ("_framework/blazor.webassembly.js", "application/javascript"),
            ("_framework/dotnet.js",             "application/javascript"),
            ("service-worker.js",                "application/javascript"),
            ("service-worker-assets.js",         "application/javascript"),
            ("appConfig.js",                     "application/javascript"),
            ("indexinit.js",                     "application/javascript"),
        };

        foreach (var file in bundleRelativeFiles.Select(f => f.Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = file[(file.LastIndexOf('/') + 1)..];
            if ((name == "appConfig.js" || name == "indexinit.js") && !files.Any(f => f.Path == file))
                files.Add((file, "application/javascript"));
        }

        return files;
    }

    /// <summary>
    /// The type pass 1 gives a file, by its extension after any <c>.br</c> or <c>.gz</c>: a compressed sibling takes its
    /// underlying file's type, as the aws CLI's guess does.
    ///
    /// <para><b>A TABLE, NOT A GUESS.</b> The aws CLI asks Python's <c>mimetypes</c>, which on Windows also reads the
    /// registry — so the types a workstation deploy sets depend on the workstation, and <c>.map</c> came out
    /// <c>text/plain</c> here. The entries for what a Blazor bundle holds are the types that deploy left (2026-09-14);
    /// the rest are the registered types.</para>
    /// </summary>
    public static string ContentTypeFor(string relativePath)
    {
        var name = relativePath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];

        var dot = name.LastIndexOf('.');
        if (dot <= 0) return UnknownContentType;

        return ContentTypes.TryGetValue(name[(dot + 1)..], out var type) ? type : UnknownContentType;
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Measured on the SellerApp bucket as deploywebapp left it.
        ["html"] = "text/html",
        ["css"] = "text/css",
        ["js"] = "application/javascript",
        ["json"] = "application/json",
        ["map"] = "text/plain",
        ["png"] = "image/png",
        // Registered types, for what a bundle may hold and none has yet.
        ["htm"] = "text/html",
        ["mjs"] = "application/javascript",
        ["wasm"] = "application/wasm",
        ["txt"] = "text/plain",
        ["xml"] = "text/xml",
        ["svg"] = "image/svg+xml",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["avif"] = "image/avif",
        ["ico"] = "image/vnd.microsoft.icon",
        ["woff"] = "font/woff",
        ["woff2"] = "font/woff2",
        ["ttf"] = "font/ttf",
        ["otf"] = "font/otf",
        ["pdf"] = "application/pdf",
        ["webmanifest"] = "application/manifest+json",
    };

    /// <summary>
    /// <c>{sha256}  {path}\n</c> per file, sorted by the path's UTF-8 bytes: exactly the <c>manifest.txt</c> the bundle
    /// workflows print, so a deploy's manifest digest can be compared with the one in its build's log.
    /// </summary>
    public static string ManifestText(IEnumerable<(string Path, string Sha256Hex)> files)
    {
        var sb = new StringBuilder();
        foreach (var (path, hash) in files.OrderBy(f => f.Path, Utf8Ordinal.Instance))
            sb.Append(hash).Append("  ").Append(path).Append('\n');
        return sb.ToString();
    }

    /// <summary>The SHA-256 of <see cref="ManifestText"/>, lowercase hex.</summary>
    public static string ManifestSha256(IEnumerable<(string Path, string Sha256Hex)> files)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ManifestText(files))));

    /// <summary>
    /// The policy a web-app bucket carries, whole: CloudFront may read its objects when the request comes from a
    /// distribution in <paramref name="accountId"/>, any distribution, because the edge rewrites its origin per request.
    /// </summary>
    public static string CloudFrontReadPolicy(string bucketName, string accountId)
        => new System.Text.Json.Nodes.JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["Sid"] = "AllowCloudFrontRead",
                ["Effect"] = "Allow",
                ["Principal"] = new System.Text.Json.Nodes.JsonObject { ["Service"] = "cloudfront.amazonaws.com" },
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{bucketName}/*",
                ["Condition"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["StringEquals"] = new System.Text.Json.Nodes.JsonObject { ["AWS:SourceAccount"] = accountId },
                },
            }),
        }.ToJsonString();

    /// <summary>Byte order of the UTF-8 encoding — <c>LC_ALL=C sort</c>'s order, which the workflows' manifests use.</summary>
    public sealed class Utf8Ordinal : IComparer<string>
    {
        public static readonly Utf8Ordinal Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return ((ReadOnlySpan<byte>)Encoding.UTF8.GetBytes(x)).SequenceCompareTo(Encoding.UTF8.GetBytes(y));
        }
    }
}

/// <summary>
/// The headers <c>deploywebapp</c>'s five passes leave on each object of one bundle, precomputed for its file set.
/// </summary>
public sealed class WebappHeaderRules
{
    // Pass 2's exclusions, matched against the path below _framework/ exactly as the aws CLI matches an --exclude.
    private static readonly HashSet<string> FrameworkBaselineExcluded = new(StringComparer.Ordinal)
        { "blazor.boot.json", "blazor.webassembly.js", "service-worker-assets.js" };

    private static readonly HashSet<string> FrameworkJsExcluded = new(StringComparer.Ordinal)
        { "blazor.webassembly.js", "dotnet.js", "service-worker-assets.js" };

    // Pass 4 leaves these to pass 5.
    private static readonly HashSet<string> CompressedManifestNames = new(StringComparer.Ordinal)
    {
        "blazor.boot.json.br", "blazor.boot.json.gz", "blazor.webassembly.js.br", "blazor.webassembly.js.gz",
        "dotnet.js.br", "dotnet.js.gz",
    };

    private static readonly (string Suffix, string ContentType, string Encoding)[] CompressedFramework =
    {
        (".wasm.br", "application/wasm", "br"),
        (".wasm.gz", "application/wasm", "gzip"),
        (".js.br", "application/javascript", "br"),
        (".js.gz", "application/javascript", "gzip"),
        (".dat.br", "application/octet-stream", "br"),
        (".dat.gz", "application/octet-stream", "gzip"),
    };

    private static readonly (string Path, string ContentType, string Encoding)[] CompressedManifests =
    {
        ("_framework/blazor.boot.json.br", "application/json", "br"),
        ("_framework/blazor.boot.json.gz", "application/json", "gzip"),
        ("_framework/blazor.webassembly.js.br", "application/javascript", "br"),
        ("_framework/blazor.webassembly.js.gz", "application/javascript", "gzip"),
        ("_framework/dotnet.js.br", "application/javascript", "br"),
        ("_framework/dotnet.js.gz", "application/javascript", "gzip"),
    };

    private readonly string _basePath;
    private readonly string _framework;
    private readonly Dictionary<string, (string ContentType, string? Encoding)> _noCache = new(StringComparer.Ordinal);

    /// <param name="basePath">The bundle's base path, <c>seller/</c> or "".</param>
    /// <param name="relativeFiles">Every file of the bundle, relative to the publish root.</param>
    public WebappHeaderRules(string basePath, IEnumerable<string> relativeFiles)
    {
        _basePath = basePath;
        _framework = basePath + "_framework/";

        var underBase = relativeFiles.Select(f => f.Replace('\\', '/'))
            .Where(f => f.StartsWith(basePath, StringComparison.Ordinal))
            .Select(f => f[basePath.Length..]);

        // Pass 3 copies each entry and its two compressed siblings onto themselves; a missing one is skipped.
        foreach (var (path, contentType) in WebappSyncRules.NoCacheFiles(underBase))
        {
            _noCache[basePath + path] = (contentType, null);
            _noCache[basePath + path + ".br"] = (contentType, "br");
            _noCache[basePath + path + ".gz"] = (contentType, "gzip");
        }
    }

    /// <summary>The headers for one file, by its path relative to the publish root.</summary>
    public WebappObjectHeaders For(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');

        // Pass 1: the sync's baseline and the guessed type, no encoding.
        var headers = new WebappObjectHeaders(WebappSyncRules.Baseline, WebappSyncRules.ContentTypeFor(path), null);

        var inFramework = path.StartsWith(_framework, StringComparison.Ordinal);
        var below = inFramework ? path[_framework.Length..] : "";

        if (inFramework)
        {
            // Pass 2a: everything under _framework/ but three names, immutable octet-stream.
            if (!FrameworkBaselineExcluded.Contains(below))
                headers = new(WebappSyncRules.Immutable, "application/octet-stream", null);

            // Pass 2b: *.wasm.
            if (below.EndsWith(".wasm", StringComparison.Ordinal))
                headers = new(WebappSyncRules.Immutable, "application/wasm", null);

            // Pass 2c: *.js but the three non-hashed loaders.
            if (below.EndsWith(".js", StringComparison.Ordinal) && !FrameworkJsExcluded.Contains(below))
                headers = new(WebappSyncRules.Immutable, "application/javascript", null);
        }

        // Pass 3: the manifests and their compressed siblings, no-cache.
        if (_noCache.TryGetValue(path, out var manifest))
            headers = new(WebappSyncRules.NoCache, manifest.ContentType, manifest.Encoding);

        // Pass 4: the framework's compressed siblings, immutable, with their encoding — but not the manifests'.
        if (inFramework && !CompressedManifestNames.Contains(below))
        {
            foreach (var (suffix, contentType, encoding) in CompressedFramework)
            {
                if (below.EndsWith(suffix, StringComparison.Ordinal))
                    headers = new(WebappSyncRules.Immutable, contentType, encoding);
            }
        }

        // Pass 5: the manifests' compressed siblings under _framework/, no-cache.
        foreach (var (manifestPath, contentType, encoding) in CompressedManifests)
        {
            if (path == _basePath + manifestPath)
                headers = new(WebappSyncRules.NoCache, contentType, encoding);
        }

        return headers;
    }
}
