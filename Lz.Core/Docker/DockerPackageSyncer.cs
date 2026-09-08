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
///
/// <para><b>THE CONTAINER'S LANE IS THE WORKSPACE'S LANE, and that is by construction rather than
/// by configuration.</b> Step 1 restores on the host, so it obeys whatever
/// <c>Packages.Local.props</c> says at that moment; step 2 reads the versions that restore actually
/// RESOLVED; step 3 copies exactly those. Switch the workspace with <c>lz packages mode</c> and the
/// next image follows, with no separate switch to forget. The folder is deleted and recreated each
/// run, so nothing survives from a previous lane.</para>
///
/// <para>Worth stating because it is easy to misread: a stale <c>DockerPackages</c> is a folder that
/// this syncer has NOT run over, not evidence that it copies the wrong thing. Building the image
/// directly with <c>docker build</c> - skipping <c>lz deploycontainer</c>, and therefore this - will
/// restore against whatever was left there, which on 2026-09-08 was a set from months earlier.</para>
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

        // Which ids does this workspace BUILD? A first-party package missing from the cache cannot be
        // recovered inside the image - the container's generated nuget.config names only
        // DockerPackages and nuget.org, and nothing first-party is on nuget.org. So that case must
        // fail here, loudly, rather than surface as an NU1102 ten minutes into a docker build.
        var firstParty = FirstPartyIds(contextPath);

        // 5. Copy .nupkg files
        int copied = 0;
        var missing = new List<(string Name, string Version)>();
        var syncedFirstParty = new List<string>();
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
                if (firstParty.Contains(name)) syncedFirstParty.Add($"{name} {version}");
            }
            else
            {
                missing.Add((name, version));
            }
        }

        Console.WriteLine($"  Copied {copied} packages to DockerPackages/");

        // THE PROVENANCE LINE. Without it the image's first-party versions are invisible until
        // something fails, and a lane mix-up looks exactly like a working build.
        if (syncedFirstParty.Count > 0)
        {
            syncedFirstParty.Sort(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"  First-party packages, at the versions the CURRENT LANE resolved:");
            foreach (var p in syncedFirstParty) Console.WriteLine($"    {p}");
        }

        // Compare the ID, not a reconstructed string. Formatting a name and version together and
        // splitting them apart again is a needless way to be wrong about a package id.
        var missingFirstParty = missing
            .Where(m => firstParty.Contains(m.Name))
            .Select(m => $"{m.Name} {m.Version}")
            .ToList();

        if (missingFirstParty.Count > 0)
            throw new InvalidOperationException(
                "these packages are built by this workspace but are not in the NuGet cache, so the " +
                "image cannot restore them - the container sees only DockerPackages and nuget.org, and " +
                "nothing first-party is published to nuget.org:" + Environment.NewLine + "    " +
                string.Join(Environment.NewLine + "    ", missingFirstParty) + Environment.NewLine +
                "  Build the producing repo first, or switch lanes and restore again.");

        var thirdPartyMissing = missing.Count - missingFirstParty.Count;
        if (thirdPartyMissing > 0)
            Console.WriteLine($"  {thirdPartyMissing} third-party package(s) not cached; the image will take them from nuget.org.");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The package ids this workspace BUILDS, read from its local feeds — the same feeds
    /// <c>NuGet.Config</c> declares, so this cannot drift from what restore sees.
    ///
    /// <para>Used only to decide whether a package missing from the cache is recoverable. A
    /// third-party one is: the image can take it from nuget.org. A first-party one is not, and
    /// letting that through produces an <c>NU1102</c> deep inside a docker build instead of a
    /// sentence here naming the repo to build.</para>
    ///
    /// <para>Returns empty when the workspace root cannot be found — outside a workspace there is no
    /// first-party set to protect, and refusing to sync would be worse than syncing without the
    /// check.</para>
    /// </summary>
    private static HashSet<string> FirstPartyIds(string contextPath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = Lz.Core.Repos.RepoDiscovery.FindWorkspaceRoot(contextPath);
            var configPath = Directory.EnumerateFiles(root, "*uget.[Cc]onfig").FirstOrDefault();
            if (configPath is null) return ids;

            var feeds = Lz.Core.PackageLane.LocalFeeds.FromNuGetConfig(File.ReadAllText(configPath));
            foreach (var p in Lz.Core.PackageLane.LocalFeeds.Scan(root, feeds)) ids.Add(p.Id);
        }
        catch
        {
            // Not in a workspace, or the config is unreadable. Fall through with an empty set: the
            // check simply does not apply, which is different from it failing.
        }
        return ids;
    }

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
