using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Lz.Core.Plugin;

namespace Lz.Cli;

/// <summary>
/// Discovers and loads plugin assemblies.
/// Resolution order:
///   1. lz.json marker file (searched upward from cwd) — explicit plugin path
///   2. Convention: Deploy/bin/{Debug|Release}/net10.0/Deploy.dll (searched upward from cwd)
/// Returns null if neither is found (core commands still work without a plugin).
/// </summary>
public static class PluginLoader
{
    private const string MarkerFileName = "lz.json";
    private const string ConventionFolder = "Deploy";
    private const string ConventionDll = "Deploy.dll";

    /// <summary>
    /// Discover and load the plugin assembly, find and instantiate ILzPlugin.
    /// Returns null if no plugin is found (core commands still work without a plugin).
    /// </summary>
    public static ILzPlugin? LoadPlugin(string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();

        // 1. Try lz.json marker file
        var dllPath = DiscoverViaMarkerFile(dir);

        // 2. Fall back to convention: Deploy/bin/.../Deploy.dll
        dllPath ??= DiscoverViaConvention(dir);

        if (dllPath == null)
            return null;

        return LoadPluginFromDll(dllPath);
    }

    /// <summary>
    /// Search upward for lz.json and resolve the plugin DLL path from it.
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

        if (marker?.Plugin == null)
            throw new InvalidOperationException(
                $"lz.json at '{markerPath}' is missing the 'plugin' field.");

        var dllPath = ResolvePluginPath(markerDir, marker.Plugin);

        if (dllPath == null)
            throw new FileNotFoundException(
                $"Plugin DLL not found: '{marker.Plugin}' (resolved from '{markerDir}'). Build the plugin project first.");

        return dllPath;
    }

    /// <summary>
    /// Search upward for a Deploy/ folder containing the built plugin DLL.
    /// Probes both Debug and Release configurations.
    /// </summary>
    private static string? DiscoverViaConvention(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var deployDir = Path.Combine(dir.FullName, ConventionFolder);
            if (Directory.Exists(deployDir))
            {
                // Probe Debug first, then Release
                var debugPath = Path.Combine(deployDir, "bin", "Debug", "net10.0", ConventionDll);
                if (File.Exists(debugPath))
                    return debugPath;

                var releasePath = Path.Combine(deployDir, "bin", "Release", "net10.0", ConventionDll);
                if (File.Exists(releasePath))
                    return releasePath;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Load the plugin assembly, register its dependency resolver, and instantiate ILzPlugin.
    /// </summary>
    private static ILzPlugin LoadPluginFromDll(string dllPath)
    {
        // Register dependency resolver for plugin-specific dependencies
        // (e.g., AWSSDK.S3 that the plugin needs but the host doesn't bundle)
        var resolver = new AssemblyDependencyResolver(dllPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = resolver.ResolveAssemblyToPath(name);
            return path != null ? context.LoadFromAssemblyPath(path) : null;
        };

        var assembly = Assembly.LoadFrom(dllPath);
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(ILzPlugin).IsAssignableFrom(t)
                                 && !t.IsAbstract && !t.IsInterface);

        if (pluginType == null)
            throw new InvalidOperationException(
                $"No ILzPlugin implementation found in '{dllPath}'.");

        return (ILzPlugin)Activator.CreateInstance(pluginType)!;
    }

    /// <summary>
    /// Resolve the plugin DLL path, probing Debug/Release configurations if needed.
    /// </summary>
    private static string? ResolvePluginPath(string markerDir, string relativePath)
    {
        // Try exact path first
        var exact = Path.GetFullPath(Path.Combine(markerDir, relativePath));
        if (File.Exists(exact))
            return exact;

        // If path contains bin/Debug or bin/Release, probe the other configuration
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

    private class LzMarker
    {
        public string? Plugin { get; set; }
    }
}
