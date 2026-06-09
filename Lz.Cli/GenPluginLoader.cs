using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Lz.Gen;

namespace Lz.Cli;

/// <summary>
/// Discovers and loads the gen plugin assembly — a system-specific DLL that
/// registers custom directive and artifact types with <see cref="GenExtensions"/>.
/// Parallels <see cref="PluginLoader"/> for the deployment plugin (Deploy.dll),
/// but is entirely independent: a system can ship one, the other, or both.
///
/// Resolution order (same as PluginLoader):
///   1. lz.json marker file (searched upward from cwd) — "genPlugin" field
///   2. Convention: Generate/bin/{Debug|Release}/&lt;tfm&gt;/Generate.dll
///      (searched upward from cwd; tfm discovered dynamically)
///
/// Returns null if neither is found — <c>lz gen</c> still works with only
/// built-in directives/artifacts.
/// </summary>
public static class GenPluginLoader
{
    private const string MarkerFileName = "lz.json";
    private const string ConventionFolder = "Generate";
    private const string ConventionDll = "Generate.dll";

    /// <summary>
    /// Discover and load the gen plugin assembly, find and instantiate ILzGenPlugin.
    /// Returns null if no gen plugin is found (built-in gen still works without one).
    /// </summary>
    public static ILzGenPlugin? LoadGenPlugin(string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();

        // 1. Try lz.json marker file (genPlugin field)
        var dllPath = DiscoverViaMarkerFile(dir);

        // 2. Fall back to convention: Generate/bin/.../Generate.dll
        dllPath ??= DiscoverViaConvention(dir);

        if (dllPath == null)
            return null;

        return LoadGenPluginFromDll(dllPath);
    }

    /// <summary>
    /// Search upward for lz.json and resolve the gen plugin DLL path from its
    /// <c>genPlugin</c> field. The <c>plugin</c> field is still consumed by
    /// <see cref="PluginLoader"/> for the deployment plugin; they're independent.
    /// </summary>
    private static string? DiscoverViaMarkerFile(string startDir)
    {
        var markerPath = DiscoverFileUpward(startDir, MarkerFileName);
        if (markerPath == null)
            return null;

        var markerDir = Path.GetDirectoryName(markerPath)!;
        var json = File.ReadAllText(markerPath);
        var marker = JsonSerializer.Deserialize<LzMarker>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // genPlugin is optional — absence just means "no gen plugin configured".
        if (string.IsNullOrWhiteSpace(marker?.GenPlugin))
            return null;

        var dllPath = ResolvePluginPath(markerDir, marker.GenPlugin);

        if (dllPath == null)
            throw new FileNotFoundException(
                $"Gen plugin DLL not found: '{marker.GenPlugin}' (resolved from '{markerDir}'). Build the Generate project first.");

        return dllPath;
    }

    /// <summary>
    /// Search upward for a Generate/ folder containing the built gen plugin DLL.
    /// Probes both Debug and Release configurations, discovering the target
    /// framework directory dynamically so the same code path works for
    /// net8.0, net9.0, net10.0, and onwards.
    /// </summary>
    private static string? DiscoverViaConvention(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var generateDir = Path.Combine(dir.FullName, ConventionFolder);
            if (Directory.Exists(generateDir))
            {
                var debugPath = FindBuiltDll(Path.Combine(generateDir, "bin", "Debug"));
                if (debugPath != null) return debugPath;

                var releasePath = FindBuiltDll(Path.Combine(generateDir, "bin", "Release"));
                if (releasePath != null) return releasePath;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Enumerate target-framework subdirectories under <paramref name="configDir"/>
    /// (e.g. <c>bin/Debug/{net8.0,net10.0,...}</c>) and pick the highest TFM
    /// containing the convention DLL. Newer TFMs sort lexicographically last
    /// in the framework-moniker convention, which gives the right "prefer
    /// newest" behavior for net8 → net10 and beyond.
    /// </summary>
    private static string? FindBuiltDll(string configDir)
    {
        if (!Directory.Exists(configDir)) return null;
        string? best = null;
        string? bestTfm = null;
        foreach (var tfmDir in Directory.EnumerateDirectories(configDir))
        {
            var tfm = Path.GetFileName(tfmDir);
            var candidate = Path.Combine(tfmDir, ConventionDll);
            if (!File.Exists(candidate)) continue;
            if (bestTfm == null || string.Compare(tfm, bestTfm, StringComparison.OrdinalIgnoreCase) > 0)
            {
                best = candidate;
                bestTfm = tfm;
            }
        }
        return best;
    }

    /// <summary>
    /// Load the gen plugin assembly, register its dependency resolver, and instantiate ILzGenPlugin.
    /// </summary>
    private static ILzGenPlugin LoadGenPluginFromDll(string dllPath)
    {
        // Register dependency resolver for plugin-specific dependencies
        // (e.g., additional NSwag extensions or Roslyn analyzers the plugin uses).
        // Coexists fine with the Deploy plugin's resolver — both handlers run and
        // the first one that returns non-null wins.
        var resolver = new AssemblyDependencyResolver(dllPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = resolver.ResolveAssemblyToPath(name);
            return path != null ? context.LoadFromAssemblyPath(path) : null;
        };

        var assembly = Assembly.LoadFrom(dllPath);
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(ILzGenPlugin).IsAssignableFrom(t)
                                 && !t.IsAbstract && !t.IsInterface);

        if (pluginType == null)
            throw new InvalidOperationException(
                $"No ILzGenPlugin implementation found in '{dllPath}'.");

        return (ILzGenPlugin)Activator.CreateInstance(pluginType)!;
    }

    /// <summary>
    /// Resolve the plugin DLL path, probing Debug/Release configurations if needed.
    /// </summary>
    private static string? ResolvePluginPath(string markerDir, string relativePath)
    {
        var exact = Path.GetFullPath(Path.Combine(markerDir, relativePath));
        if (File.Exists(exact))
            return exact;

        var altPath = relativePath.Contains("bin/Debug/") || relativePath.Contains("bin\\Debug\\")
            ? relativePath.Replace("bin/Debug/", "bin/Release/").Replace("bin\\Debug\\", "bin\\Release\\")
            : relativePath.Contains("bin/Release/") || relativePath.Contains("bin\\Release\\")
                ? relativePath.Replace("bin/Release/", "bin/Debug/").Replace("bin\\Release\\", "bin\\Debug\\")
                : null;

        if (altPath != null)
        {
            var alt = Path.GetFullPath(Path.Combine(markerDir, altPath));
            if (File.Exists(alt))
                return alt;
        }

        return null;
    }

    private static string? DiscoverFileUpward(string startDir, string fileName)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// lz.json schema: <c>plugin</c> points at the Deploy DLL (consumed by
    /// <see cref="PluginLoader"/>); <c>genPlugin</c> points at the Generate DLL
    /// (consumed here). Either, both, or neither may be present.
    /// </summary>
    private class LzMarker
    {
        public string? Plugin { get; set; }
        public string? GenPlugin { get; set; }
    }
}
