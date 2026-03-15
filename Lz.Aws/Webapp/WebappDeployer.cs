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

        // 2. dotnet publish
        await RunAsync("dotnet", $"publish \"{csprojPath}\" --configuration Release");

        // 3. Find the most recent publish/wwwroot output
        var publishBasePath = Path.Combine(webappFolder, projectFolder, "bin", "Release");
        var publishPath = FindPublishWwwroot(publishBasePath);

        Console.WriteLine($"  Publish output: {publishPath}");
        Console.WriteLine($"  Target bucket:  {bucketName}");

        // 4. S3 sync
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Syncing to S3...");
        Console.ResetColor();

        var profileArg = string.IsNullOrEmpty(profile) ? "" : $"--profile \"{profile}\"";
        await RunAsync("aws",
            $"s3 sync \"{publishPath}\" \"s3://{bucketName}/wwwroot\" --delete --region {region} {profileArg}");

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
