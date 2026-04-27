using System.Diagnostics;
using System.Text.Json;

namespace Lz.Core.Docker;

/// <summary>
/// Synchronizes NuGet packages from the local cache into a DockerPackages folder
/// inside the Docker build context. This allows Docker builds to resolve packages
/// that are only available from local NuGet sources (not nuget.org).
///
/// Workflow:
///   1. Run dotnet restore on the host to populate the global NuGet cache
///   2. Parse project.assets.json to discover all resolved packages
///   3. Copy each .nupkg from the global cache into Context/DockerPackages/
/// </summary>
public static class DockerPackageSyncer
{
    /// <summary>
    /// Restore the project and sync all resolved NuGet packages into
    /// <paramref name="contextPath"/>/DockerPackages.
    /// </summary>
    public static async Task SyncAsync(string contextPath, string projectRelativePath)
    {
        var projectFullPath = Path.GetFullPath(Path.Combine(contextPath, projectRelativePath));
        if (!File.Exists(projectFullPath))
            throw new FileNotFoundException(
                $"Cannot sync packages — project file not found: {projectFullPath}");

        var dockerPkgDir = Path.Combine(contextPath, "DockerPackages");

        // Clean and recreate
        if (Directory.Exists(dockerPkgDir))
            Directory.Delete(dockerPkgDir, recursive: true);
        Directory.CreateDirectory(dockerPkgDir);

        Console.WriteLine("Syncing NuGet packages for Docker build...");

        // 1. dotnet restore on the host (uses all configured NuGet sources)
        Console.WriteLine("  Running dotnet restore...");
        await RunDotnetRestoreAsync(projectFullPath);

        // 2. Locate project.assets.json
        var projectDir = Path.GetDirectoryName(projectFullPath)!;
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            throw new FileNotFoundException(
                $"project.assets.json not found after restore: {assetsPath}");

        // 3. Discover NuGet global-packages folder
        var globalPackagesDir = await GetGlobalPackagesDirAsync();
        Console.WriteLine($"  NuGet cache: {globalPackagesDir}");

        // 4. Parse packages from project.assets.json
        var packages = ParsePackagesFromAssets(assetsPath);
        Console.WriteLine($"  Found {packages.Count} packages");

        // 5. Copy .nupkg files
        int copied = 0, notFound = 0;
        foreach (var (name, version) in packages)
        {
            var lowerName = name.ToLowerInvariant();
            var lowerVersion = version.ToLowerInvariant();

            // NuGet global cache layout: {cache}/{lowercase-name}/{lowercase-version}/{lowercase-name}.{lowercase-version}.nupkg
            var nupkgPath = Path.Combine(
                globalPackagesDir, lowerName, lowerVersion, $"{lowerName}.{lowerVersion}.nupkg");

            if (File.Exists(nupkgPath))
            {
                var dest = Path.Combine(dockerPkgDir, $"{lowerName}.{lowerVersion}.nupkg");
                File.Copy(nupkgPath, dest, overwrite: true);
                copied++;
            }
            else
            {
                // Not critical — may be available from nuget.org during Docker build
                notFound++;
            }
        }

        Console.WriteLine($"  Copied {copied} packages to DockerPackages/");
        if (notFound > 0)
            Console.WriteLine($"  {notFound} packages not in local cache (will restore from nuget.org in Docker)");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task RunDotnetRestoreAsync(string projectPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{projectPath}\" --verbosity quiet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet restore");

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet restore failed (exit {process.ExitCode}):\n{stderr}");
    }

    private static async Task<string> GetGlobalPackagesDirAsync()
    {
        var psi = new ProcessStartInfo("dotnet", "nuget locals global-packages --list")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to query NuGet cache location");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Output: "global-packages: C:\Users\...\.nuget\packages\"
        var prefix = "global-packages:";
        var line = stdout.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Could not determine NuGet global-packages path from: {stdout}");

        return line[prefix.Length..].Trim();
    }

    /// <summary>
    /// Parse the "libraries" section of project.assets.json to collect all
    /// NuGet package references (excluding project references).
    /// </summary>
    private static List<(string Name, string Version)> ParsePackagesFromAssets(string assetsPath)
    {
        using var stream = File.OpenRead(assetsPath);
        using var doc = JsonDocument.Parse(stream);

        var packages = new List<(string, string)>();
        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
            return packages;

        foreach (var lib in libraries.EnumerateObject())
        {
            // Only NuGet packages, not project references
            if (lib.Value.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString()?.Equals("package", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Key format: "PackageName/Version"
                var parts = lib.Name.Split('/', 2);
                if (parts.Length == 2)
                    packages.Add((parts[0], parts[1]));
            }
        }

        return packages;
    }
}
