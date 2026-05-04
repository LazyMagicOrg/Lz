using System.Diagnostics;

namespace Lz.Aws.Webapp;

/// <summary>
/// Builds a Blazor WASM application, syncs the publish output to S3,
/// and optionally invalidates the CloudFront cache.
/// </summary>
public class WebappDeployer
{
    /// <summary>
    /// Publishes a web application and deploys it to S3 + CloudFront.
    /// </summary>
    /// <param name="webappFolder">Absolute path to the webapp solution folder (e.g., .../StoreApp)</param>
    /// <param name="projectFolder">Subfolder containing the .csproj (e.g., "WASMApp")</param>
    /// <param name="projectName">Project name without .csproj extension (e.g., "WASMApp")</param>
    /// <param name="bucketName">S3 bucket name from Pulumi stack output</param>
    /// <param name="distributionId">CloudFront distribution ID from Pulumi stack output</param>
    /// <param name="profile">AWS profile name</param>
    /// <param name="region">AWS region</param>
    /// <param name="environment">Deployment environment (dev/test/prod)</param>
    public async Task DeployAsync(
        string webappFolder,
        string projectFolder,
        string projectName,
        string bucketName,
        string distributionId,
        string profile,
        string region,
        string environment)
    {
        // 1. Locate the .csproj file
        var csprojPath = Path.Combine(webappFolder, projectFolder, $"{projectName}.csproj");
        if (!File.Exists(csprojPath))
            throw new FileNotFoundException(
                $"Project file not found: {csprojPath}\n" +
                $"  Hint: Ensure --webapp points to the solution folder containing {projectFolder}/{projectName}.csproj");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Publishing {projectName}...");
        Console.ResetColor();

        // 2. dotnet publish (quiet — only show errors)
        // Pass AppEnvironment through so build-time config generation
        // (see BlazorUI.csproj's GenerateAppConfig target) picks the right
        // overlay file. The csproj can still resolve env via `lz getenv`
        // when invoked independently, but passing it explicitly here makes
        // the publish command self-documenting in logs and CI output.
        await RunAsync("dotnet",
            $"publish \"{csprojPath}\" --configuration Release --verbosity quiet " +
            $"-p:AppEnvironment={environment}");

        // 3. Find the most recent publish/wwwroot output
        var publishBasePath = Path.Combine(webappFolder, projectFolder, "bin", "Release");
        var publishPath = FindPublishWwwroot(publishBasePath);

        Console.WriteLine($"  Publish output: {publishPath}");
        Console.WriteLine($"  Target bucket:  {bucketName}");

        // 4. Ensure S3 bucket exists (create if not)
        var profileArg = string.IsNullOrEmpty(profile) ? "" : $"--profile \"{profile}\"";
        var bucketCheck = await RunSilentAsync("aws",
            $"s3api head-bucket --bucket \"{bucketName}\" --region {region} {profileArg}");
        if (bucketCheck != 0)
        {
            Console.WriteLine($"  Creating bucket {bucketName}...");
            await RunAsync("aws",
                $"s3api create-bucket --bucket \"{bucketName}\" --region {region} " +
                $"--create-bucket-configuration LocationConstraint={region} {profileArg}");
            await RunAsync("aws",
                $"s3api put-public-access-block --bucket \"{bucketName}\" --region {region} " +
                $"--public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true {profileArg}");
        }

        // Ensure CloudFront OAC bucket policy exists (allow any CF distribution in account)
        await EnsureBucketPolicyAsync(bucketName, region, profile);

        // 5. S3 sync with cache-control headers (prevents stale-cache issues
        // for returning users — see SyncWithCacheControlAsync for details).
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Syncing to S3...");
        Console.ResetColor();
        await SyncWithCacheControlAsync(publishPath, bucketName, region, profileArg);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Synced to s3://{bucketName}/wwwroot");
        Console.ResetColor();

        // 5. CloudFront invalidation (skip for dev — no caching)
        if (!environment.Equals("dev", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(distributionId))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Invalidating CloudFront cache...");
                Console.ResetColor();

                try
                {
                    await RunAsync("aws",
                        $"cloudfront create-invalidation --distribution-id {distributionId} --paths \"/*\" --region {region} {profileArg}");

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  CloudFront invalidation created");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  WARNING: CloudFront invalidation failed (non-fatal): {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
        else
        {
            Console.WriteLine("  Skipping CloudFront invalidation (dev environment)");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully deployed {projectName} to {bucketName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Deploys a static website (plain HTML/CSS/JS, no dotnet build) to S3 + CloudFront.
    /// The folder contents are synced directly to s3://{bucket}/wwwroot/.
    /// </summary>
    public async Task DeployStaticAsync(
        string sourceFolder,
        string bucketName,
        string distributionId,
        string profile,
        string region,
        string environment,
        string? targetPrefix = null)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Static site folder not found: {sourceFolder}");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Deploying static site from {sourceFolder}...");
        Console.ResetColor();

        Console.WriteLine($"  Target bucket: {bucketName}");

        // Ensure S3 bucket exists
        var profileArg = string.IsNullOrEmpty(profile) ? "" : $"--profile \"{profile}\"";
        var bucketCheck = await RunSilentAsync("aws",
            $"s3api head-bucket --bucket \"{bucketName}\" --region {region} {profileArg}");
        if (bucketCheck != 0)
        {
            Console.WriteLine($"  Creating bucket {bucketName}...");
            await RunAsync("aws",
                $"s3api create-bucket --bucket \"{bucketName}\" --region {region} " +
                $"--create-bucket-configuration LocationConstraint={region} {profileArg}");
            await RunAsync("aws",
                $"s3api put-public-access-block --bucket \"{bucketName}\" --region {region} " +
                $"--public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true {profileArg}");
        }

        // Ensure CloudFront OAC bucket policy
        await EnsureBucketPolicyAsync(bucketName, region, profile);

        // S3 sync with cache-control — folder contents go to /wwwroot/{prefix?}/
        // (matching CloudFront originPath + per-behavior subpath). The optional
        // prefix covers the /explore/* case: Hugo outputs a flat public/ that
        // must land under /wwwroot/explore/ so the /explore/* CF behavior resolves
        // /explore/home/ → /wwwroot/explore/home/index.html.
        var normalizedPrefix = NormalizePrefix(targetPrefix);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(string.IsNullOrEmpty(normalizedPrefix)
            ? "Syncing to S3..."
            : $"Syncing to S3 under prefix '{normalizedPrefix}/'...");
        Console.ResetColor();
        await SyncWithCacheControlAsync(sourceFolder, bucketName, region, profileArg, normalizedPrefix);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(string.IsNullOrEmpty(normalizedPrefix)
            ? $"  Synced to s3://{bucketName}/wwwroot"
            : $"  Synced to s3://{bucketName}/wwwroot/{normalizedPrefix}");
        Console.ResetColor();

        // CloudFront invalidation (skip for dev)
        if (!environment.Equals("dev", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(distributionId))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Invalidating CloudFront cache...");
                Console.ResetColor();

                try
                {
                    await RunAsync("aws",
                        $"cloudfront create-invalidation --distribution-id {distributionId} --paths \"/*\" --region {region} {profileArg}");

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  CloudFront invalidation created");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  WARNING: CloudFront invalidation failed (non-fatal): {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
        else
        {
            Console.WriteLine("  Skipping CloudFront invalidation (dev environment)");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully deployed static site to {bucketName}");
        Console.ResetColor();
    }

    // ---------------------------------------------------------------
    // Static helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Looks up the CloudFront distribution ID for a given domain alias using the AWS CLI.
    /// Returns empty string if not found (non-fatal).
    /// </summary>
    public static async Task<string> FindDistributionIdAsync(
        string domain, string profile, string region)
    {
        try
        {
            var profileArg = string.IsNullOrEmpty(profile) ? "" : $"--profile \"{profile}\"";
            var output = await RunCaptureAsync("aws",
                $"cloudfront list-distributions --query \"DistributionList.Items[?contains(Aliases.Items, '{domain}')].Id | [0]\" --output text --region {region} {profileArg}");

            var id = output.Trim();
            if (!string.IsNullOrEmpty(id) && !id.Equals("None", StringComparison.OrdinalIgnoreCase))
                return id;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: Could not look up CloudFront distribution: {ex.Message}");
            Console.ResetColor();
        }

        return "";
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Syncs the source folder to s3://{bucketName}/wwwroot with five passes
    /// that apply appropriate Cache-Control / Content-Encoding headers per
    /// file category:
    ///
    ///   1) Full sync (--delete) with "public, max-age=3600" — baseline for
    ///      all files; handles additions and removals.
    ///   2) Override metadata on /_framework/* (except manifests) with
    ///      "public, max-age=31536000, immutable" — these files are
    ///      content-hashed, so they can be cached forever.
    ///   3) Override metadata on manifest files (index.html, blazor.boot.json,
    ///      blazor.webassembly.js, service-worker.js, etc.) with
    ///      "no-cache, no-store, must-revalidate" — these files change every
    ///      deploy and reference all other fingerprinted files. They must
    ///      always be fetched fresh, otherwise returning users get stale
    ///      manifests that reference old asset hashes (or vice versa),
    ///      causing errors like "Could not find 'checkIfLoaded'".
    ///   4) Override metadata on hashed pre-compressed siblings
    ///      (/_framework/*.HASH.{wasm,js,dat}.{br,gz}, excluding the three
    ///      non-hashed manifest .js files) — sets Content-Encoding +
    ///      Content-Type matching the underlying media + immutable cache.
    ///      These are emitted by Blazor publish to enable Brotli/gzip
    ///      transfer; CFRequest.js rewrites the URI to the .br/.gz sibling
    ///      when Accept-Encoding allows it. Without proper Content-Encoding
    ///      metadata at the origin, the browser receives compressed bytes
    ///      labeled as octet-stream and fails to decompress.
    ///   5) Override metadata on the manifest .br/.gz siblings
    ///      (blazor.webassembly.js.{br,gz}, dotnet.js.{br,gz},
    ///      blazor.boot.json.{br,gz}) — same Content-Encoding and
    ///      Content-Type rules, but Cache-Control is no-cache since these
    ///      track the underlying non-hashed manifest files. Without this,
    ///      a returning user whose browser cached the old .br loops on
    ///      stale boot config until edge cache invalidates.
    ///
    /// Passes 2-5 use `aws s3 cp --metadata-directive REPLACE` with
    /// source == destination, which performs a server-side metadata update
    /// without re-uploading file content. This ensures correct metadata even
    /// on files that weren't re-uploaded (e.g. unchanged static assets on a
    /// bucket that was previously deployed without cache-control).
    /// </summary>
    /// <summary>
    /// Normalizes a target-prefix argument: trims leading/trailing slashes and
    /// returns null/empty for empty inputs. "/explore/" and "explore" both map
    /// to "explore"; "" maps to "".
    /// </summary>
    private static string NormalizePrefix(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim().Trim('/');

    private static async Task SyncWithCacheControlAsync(
        string sourcePath,
        string bucketName,
        string region,
        string profileArg,
        string targetPrefix = "")
    {
        var s3Root = string.IsNullOrEmpty(targetPrefix)
            ? $"s3://{bucketName}/wwwroot"
            : $"s3://{bucketName}/wwwroot/{targetPrefix}";

        // Pass 1: Full sync with --delete. Sets a 1-hour baseline cache-control
        // on all files. Subsequent passes override specific categories.
        await RunAsync("aws",
            $"s3 sync \"{sourcePath}\" \"{s3Root}\" --delete --quiet --region {region} {profileArg} " +
            $"--cache-control \"public, max-age=3600\"");

        // Pass 2: Override /_framework/* (except manifest files) with immutable
        // long-lived cache. Content-hashed names make indefinite caching safe.
        //
        // CRITICAL: `--metadata-directive REPLACE` drops ALL unspecified metadata.
        // Without `--content-type`, AWS defaults Content-Type to binary/octet-
        // stream, which breaks browser loading (modules rejected by strict MIME
        // check; .json rejected by parsers).
        //
        // AWS `s3 cp --recursive` can only set ONE --content-type per call, but
        // _framework/ mixes binary (.dll→.wasm in .NET 9, .pdb, .dat, .blat)
        // with .wasm runtime and .js loader files. Solved with three recursive
        // passes — each overrides the previous for its matching pattern, AWS
        // runs each server-side in bulk. Three API calls total, not per-file.
        //   2a: baseline → application/octet-stream (covers binary blobs)
        //   2b: *.wasm  → application/wasm
        //   2c: *.js    → application/javascript (excludes the 2 JS manifests)
        string frameworkRoot = $"\"{s3Root}/_framework/\" \"{s3Root}/_framework/\"";
        string immutableCache = "--cache-control \"public, max-age=31536000, immutable\"";
        string manifestExcludes =
            "--exclude \"blazor.boot.json\" " +
            "--exclude \"blazor.webassembly.js\" " +
            "--exclude \"service-worker-assets.js\"";

        // 2a — octet-stream baseline for everything non-manifest
        await RunAsync("aws",
            $"s3 cp {frameworkRoot} --recursive --quiet --region {region} {profileArg} " +
            $"--metadata-directive REPLACE {immutableCache} " +
            $"--content-type \"application/octet-stream\" " +
            manifestExcludes);

        // 2b — *.wasm override. Pattern can't match the .json/.js manifests.
        await RunAsync("aws",
            $"s3 cp {frameworkRoot} --recursive --quiet --region {region} {profileArg} " +
            $"--metadata-directive REPLACE {immutableCache} " +
            $"--content-type \"application/wasm\" " +
            $"--exclude \"*\" --include \"*.wasm\"");

        // 2c — *.js override, excluding non-hashed loaders/manifests
        // (handled by Pass 3 with no-cache). Three loaders here are
        // non-fingerprinted but rewritten on every Blazor publish — must
        // NOT be marked immutable, or browsers freeze on the previously-
        // cached copy for a year and never see new builds:
        //   blazor.webassembly.js
        //   dotnet.js                  ← MS rewrites this per build; the
        //                                hashed dotnet.runtime.{h}.js and
        //                                dotnet.native.{h}.js it loads ARE
        //                                immutable, but the loader itself
        //                                isn't.
        //   service-worker-assets.js
        await RunAsync("aws",
            $"s3 cp {frameworkRoot} --recursive --quiet --region {region} {profileArg} " +
            $"--metadata-directive REPLACE {immutableCache} " +
            $"--content-type \"application/javascript\" " +
            $"--exclude \"*\" --include \"*.js\" " +
            $"--exclude \"blazor.webassembly.js\" " +
            $"--exclude \"dotnet.js\" " +
            $"--exclude \"service-worker-assets.js\"");

        // Pass 3: Override manifest files with no-cache. These change every
        // deploy and reference all fingerprinted assets by hash. A stale
        // manifest paired with fresh assets (or vice versa) causes hard-to-
        // diagnose runtime errors. Always revalidate.
        //
        // Same `--content-type` requirement as Pass 2 — hardcoded per file
        // because we know each one's type.
        // Notes on paths:
        //   service-worker-assets.js lives at root (the Blazor SDK emits it
        //   there, not under _framework/).
        //   service-worker.published.js is NOT shipped at runtime — the publish
        //   process copies its content over service-worker.js and drops the
        //   .published variant. No entry needed here.
        //   authentication/login.html is the static OIDC initiator — we want
        //   edits to propagate immediately, not sit in browser cache for an
        //   hour, so it gets no-cache too.
        var manifests = new (string Path, string ContentType)[]
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

        foreach (var (path, contentType) in manifests)
        {
            // Use RunSilentAsync — some files may not exist in every deployment
            // (e.g. static sites don't have blazor.boot.json). That's fine.
            // no-cache + must-revalidate (WITHOUT no-store) means the browser
            // revalidates on every request, AND is allowed to store the
            // response. The "store" bit matters because our client-side
            // recovery script uses `fetch(url, {cache: 'reload'})` to
            // force-update stale cache entries — if the response has
            // no-store, the browser discards it and the stale entry
            // persists. With just no-cache, the recovery's force-fetch
            // writes a fresh entry and stuck users self-unstick.
            await RunSilentAsync("aws",
                $"s3 cp \"{s3Root}/{path}\" \"{s3Root}/{path}\" " +
                $"--quiet --region {region} {profileArg} " +
                $"--metadata-directive REPLACE " +
                $"--cache-control \"no-cache, must-revalidate\" " +
                $"--content-type \"{contentType}\"");
        }

        // ── Pass 4: hashed pre-compressed siblings ─────────────────────
        // Blazor publish emits .br and .gz alongside every asset under
        // /_framework/. CFRequest.js rewrites the request URI to the
        // .br/.gz sibling when the client's Accept-Encoding allows it,
        // so the actual bytes the browser receives are the compressed
        // ones. For the browser to know to decompress, the response
        // MUST carry Content-Encoding: br/gzip — set as origin metadata
        // here. Content-Type must match the underlying media (Pass 1's
        // sync left .br/.gz as application/octet-stream, which would
        // make the browser refuse to compile WASM modules).
        //
        // Three content-types × two encodings = six sub-passes. Each is
        // a single bulk operation (server-side metadata copy in S3, not
        // per-file network round trips).
        //
        // Manifest .br/.gz files (blazor.webassembly.js.{br,gz},
        // dotnet.js.{br,gz}, blazor.boot.json.{br,gz}) are EXCLUDED —
        // they're handled in Pass 5 with no-cache cache-control because
        // they track non-hashed manifest siblings that change every deploy.
        string manifestBrGzExcludes =
            "--exclude \"blazor.boot.json.br\" " +
            "--exclude \"blazor.boot.json.gz\" " +
            "--exclude \"blazor.webassembly.js.br\" " +
            "--exclude \"blazor.webassembly.js.gz\" " +
            "--exclude \"dotnet.js.br\" " +
            "--exclude \"dotnet.js.gz\"";

        var compressed = new (string Pattern, string ContentType, string Encoding)[]
        {
            ("*.wasm.br",  "application/wasm",         "br"),
            ("*.wasm.gz",  "application/wasm",         "gzip"),
            ("*.js.br",    "application/javascript",   "br"),
            ("*.js.gz",    "application/javascript",   "gzip"),
            ("*.dat.br",   "application/octet-stream", "br"),
            ("*.dat.gz",   "application/octet-stream", "gzip"),
        };

        foreach (var (pattern, contentType, encoding) in compressed)
        {
            await RunAsync("aws",
                $"s3 cp {frameworkRoot} --recursive --quiet --region {region} {profileArg} " +
                $"--metadata-directive REPLACE {immutableCache} " +
                $"--content-type \"{contentType}\" " +
                $"--content-encoding \"{encoding}\" " +
                $"--exclude \"*\" --include \"{pattern}\" " +
                manifestBrGzExcludes);
        }

        // ── Pass 5: manifest pre-compressed siblings ───────────────────
        // Same Content-Type + Content-Encoding rules as Pass 4, but the
        // underlying file is non-hashed and changes every deploy, so the
        // .br/.gz must also be no-cache. Without this, a returning user
        // whose browser cached the old .br loops on stale boot config
        // until edge cache invalidates manually — exactly the failure
        // mode Pass 3 exists to prevent for the uncompressed siblings.
        var compressedManifests = new (string Path, string ContentType, string Encoding)[]
        {
            ("_framework/blazor.boot.json.br",      "application/json",       "br"),
            ("_framework/blazor.boot.json.gz",      "application/json",       "gzip"),
            ("_framework/blazor.webassembly.js.br", "application/javascript", "br"),
            ("_framework/blazor.webassembly.js.gz", "application/javascript", "gzip"),
            ("_framework/dotnet.js.br",             "application/javascript", "br"),
            ("_framework/dotnet.js.gz",             "application/javascript", "gzip"),
        };

        foreach (var (path, contentType, encoding) in compressedManifests)
        {
            // RunSilentAsync because not every publish produces every
            // manifest .br/.gz (depends on framework version + flags).
            // Treat absence as a non-issue, same as Pass 3.
            await RunSilentAsync("aws",
                $"s3 cp \"{s3Root}/{path}\" \"{s3Root}/{path}\" " +
                $"--quiet --region {region} {profileArg} " +
                $"--metadata-directive REPLACE " +
                $"--cache-control \"no-cache, must-revalidate\" " +
                $"--content-type \"{contentType}\" " +
                $"--content-encoding \"{encoding}\"");
        }
    }

    /// <summary>
    /// Finds the most recently modified publish/wwwroot directory under the Release build output.
    /// Searches across all framework target directories (e.g., net10.0/).
    /// </summary>
    private static string FindPublishWwwroot(string publishBasePath)
    {
        if (!Directory.Exists(publishBasePath))
            throw new DirectoryNotFoundException(
                $"Release build directory not found: {publishBasePath}\n" +
                "  Hint: Ensure the project was built in Release mode.");

        var candidates = Directory.GetDirectories(publishBasePath)
            .Select(frameworkDir => Path.Combine(frameworkDir, "publish", "wwwroot"))
            .Where(Directory.Exists)
            .Select(p => new { Path = p, LastWrite = Directory.GetLastWriteTimeUtc(p) })
            .OrderByDescending(x => x.LastWrite)
            .ToList();

        if (candidates.Count == 0)
            throw new DirectoryNotFoundException(
                $"No publish/wwwroot directory found under {publishBasePath}\n" +
                "  Hint: Run 'dotnet publish --configuration Release' first.");

        return candidates[0].Path;
    }

    /// <summary>Run a command, streaming stdout/stderr to the console. Throws on non-zero exit.</summary>
    private static async Task RunAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                Console.WriteLine(line);
        });
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Error.WriteLine(line);
                Console.ResetColor();
            }
        });

        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {command} {args}");
    }

    /// <summary>
    /// Ensures the S3 bucket has a policy allowing CloudFront OAC access.
    /// Uses SourceAccount condition (not SourceArn) so any CloudFront distribution
    /// in the account can access the bucket — required for dynamic origin rewriting.
    /// </summary>
    private static async Task EnsureBucketPolicyAsync(string bucketName, string region, string profile)
    {
        var profileArg = string.IsNullOrEmpty(profile) ? "" : $"--profile \"{profile}\"";

        // Get account ID
        var accountId = (await RunCaptureAsync("aws",
            $"sts get-caller-identity --query Account --output text --region {region} {profileArg}")).Trim();

        var policy = $@"{{
            ""Version"": ""2012-10-17"",
            ""Statement"": [{{
                ""Sid"": ""AllowCloudFrontRead"",
                ""Effect"": ""Allow"",
                ""Principal"": {{ ""Service"": ""cloudfront.amazonaws.com"" }},
                ""Action"": ""s3:GetObject"",
                ""Resource"": ""arn:aws:s3:::{bucketName}/*"",
                ""Condition"": {{ ""StringEquals"": {{ ""AWS:SourceAccount"": ""{accountId}"" }} }}
            }}]
        }}";

        // Write policy to temp file (avoids shell escaping issues)
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, policy);
            await RunSilentAsync("aws",
                $"s3api put-bucket-policy --bucket \"{bucketName}\" --policy file://{tempFile} --region {region} {profileArg}");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>Run a command silently, return exit code (no throw).</summary>
    private static async Task<int> RunSilentAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    /// <summary>Run a command, capture stdout. Throws on non-zero exit.</summary>
    private static async Task<string> RunCaptureAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {command} {args}");

        return stdout;
    }
}
