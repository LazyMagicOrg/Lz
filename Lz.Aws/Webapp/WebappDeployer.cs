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

        // 2a. Clean stale publish output before publishing.
        //     `dotnet publish` does NOT clean its output directory between runs,
        //     so framework files from prior SDK versions (e.g.
        //     dotnet.runtime.<old-hash>.js) accumulate alongside the current
        //     build's output. The S3 sync uploads all of them, leaving orphan
        //     hash-suffixed files in the bucket. blazor.boot.json only references
        //     the current set so the orphans are harmless at the framework level,
        //     but they bloat the bucket and add ambiguity for users debugging
        //     deploy state. Wipe the publish dir for a clean rebuild.
        var preCleanPublishBase = Path.Combine(webappFolder, projectFolder, "bin", "Release");
        if (Directory.Exists(preCleanPublishBase))
        {
            foreach (var tfmDir in Directory.GetDirectories(preCleanPublishBase))
            {
                var stalePublish = Path.Combine(tfmDir, "publish");
                if (Directory.Exists(stalePublish))
                {
                    try
                    {
                        Directory.Delete(stalePublish, recursive: true);
                        Console.WriteLine($"  Cleaned stale publish output: {stalePublish}");
                    }
                    catch (Exception ex)
                    {
                        // Non-fatal: a locked file (e.g. an editor with the
                        // file open) shouldn't abort the deploy. The build
                        // will overwrite what it can; orphans may persist.
                        Console.WriteLine($"  Warning: could not clean {stalePublish}: {ex.Message}");
                    }
                }
            }
        }

        // 2b. dotnet publish (quiet — only show errors)
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
    /// Syncs the source folder to s3://{bucketName}/wwwroot with three passes
    /// that apply appropriate Cache-Control headers per file category:
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
    ///
    /// Passes 2 and 3 use `aws s3 cp --metadata-directive REPLACE` with
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

        // 2c — *.js override, excluding the two JS manifests (handled by Pass 3).
        await RunAsync("aws",
            $"s3 cp {frameworkRoot} --recursive --quiet --region {region} {profileArg} " +
            $"--metadata-directive REPLACE {immutableCache} " +
            $"--content-type \"application/javascript\" " +
            $"--exclude \"*\" --include \"*.js\" " +
            $"--exclude \"blazor.webassembly.js\" " +
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
        // _framework/dotnet.js is a NON-fingerprinted entry-point that changes
        // between SDK builds. It MUST be a manifest. Pre-fix it was getting
        // immutable cache from Pass 2c (the *.js bulk pass), so returning users
        // were stuck on stale dotnet.js for up to a year — the runtime/native
        // sister files are fingerprinted and updated freely on each deploy, so
        // the user ended up loading a new dotnet.runtime.<hash>.js paired with
        // an old dotnet.js, producing the MONO_WASM "version mismatch" warning
        // and an "Could not find 'checkIfLoaded'" boot failure. Adding it here.
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
            // Each manifest may exist in three forms in the bucket:
            //   path           — uncompressed (served when client lacks
            //                    Accept-Encoding: br/gzip; rare on modern web)
            //   path + ".br"   — Brotli (Chrome/Firefox/Safari/Edge default)
            //   path + ".gz"   — Gzip fallback
            // CloudFront content-negotiates between these by Accept-Encoding.
            // If we set no-cache only on the uncompressed variant, modern
            // browsers (which always negotiate br) would still receive the
            // compressed variant with whatever Cache-Control Pass 2 set —
            // typically `immutable`. That's the bug we just hit on prod.
            // Apply no-cache to all three variants and tag the compressed
            // ones with Content-Encoding so CloudFront serves them correctly.
            //
            // RunSilentAsync — some files may not exist (no-compression
            // builds, static sites without blazor.boot.json, etc.). That's
            // fine; missing-source 404s from the cp are swallowed.
            //
            // no-cache + must-revalidate (WITHOUT no-store) means the
            // browser revalidates on every request AND may store the
            // response. The "store" bit matters because our client-side
            // recovery script uses `fetch(url, {cache: 'reload'})` to
            // force-update stale cache entries — if the response had
            // no-store, the browser would discard it and the stale entry
            // would persist.
            var variants = new (string Suffix, string Encoding)[]
            {
                ("",    null),
                (".br", "br"),
                (".gz", "gzip"),
            };

            foreach (var (suffix, encoding) in variants)
            {
                var encodingArg = encoding == null
                    ? ""
                    : $"--content-encoding \"{encoding}\" ";

                await RunSilentAsync("aws",
                    $"s3 cp \"{s3Root}/{path}{suffix}\" \"{s3Root}/{path}{suffix}\" " +
                    $"--quiet --region {region} {profileArg} " +
                    $"--metadata-directive REPLACE " +
                    $"--cache-control \"no-cache, must-revalidate\" " +
                    $"--content-type \"{contentType}\" " +
                    encodingArg);
            }
        }
    }

    /// <summary>
    /// Finds the most recently modified publish/wwwroot directory under the Release build output.
    /// Searches across all framework target directories (e.g., net9.0/).
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
