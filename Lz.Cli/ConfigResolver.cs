using Lz.Core.Config;

namespace Lz.Cli;

/// <summary>
/// Resolves environment, system configs, and tenant configs using smart defaults.
/// Priority: explicit CLI option → folder hierarchy heuristic → file discovery.
/// </summary>
public static class ConfigResolver
{
    /// <summary>
    /// Resolve the target environment.
    /// Priority: --env override → folder hierarchy (_Dev* → dev, _Test* → test, _Prod* → prod).
    /// </summary>
    public static string ResolveEnvironment(string? envOverride = null)
    {
        if (!string.IsNullOrEmpty(envOverride))
            return envOverride;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var name = dir.Name;
            if (name.StartsWith("_Dev", StringComparison.OrdinalIgnoreCase))
                return "dev";
            if (name.StartsWith("_Test", StringComparison.OrdinalIgnoreCase))
                return "test";
            if (name.StartsWith("_Prod", StringComparison.OrdinalIgnoreCase))
                return "prod";
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cannot determine environment. Use --env or run from a directory under _Dev, _Test, or _Prod.");
    }

    /// <summary>
    /// Resolve system configs for the given environment.
    /// If systemKeyOverride is provided, loads that specific config.
    /// Otherwise, discovers all systemconfig.*.{env}.yaml files.
    /// </summary>
    public static List<SystemConfig> ResolveSystemConfigs(string env, string? systemKeyOverride = null)
    {
        if (!string.IsNullOrEmpty(systemKeyOverride))
        {
            var pattern = $"systemconfig.{systemKeyOverride}.{env}.yaml";
            var path = ConfigLoader.DiscoverConfigFile(Directory.GetCurrentDirectory(), pattern)
                ?? throw new FileNotFoundException(
                    $"Config file '{pattern}' not found searching upward from current directory.");
            return [ConfigLoader.LoadSystemConfig(path)];
        }

        // Discover all systemconfig.*.{env}.yaml files
        var configs = DiscoverAllConfigFiles($"systemconfig.*.{env}.yaml");
        if (configs.Count == 0)
            throw new FileNotFoundException(
                $"No systemconfig.*.{env}.yaml files found searching upward from current directory.");

        return configs.Select(ConfigLoader.LoadSystemConfig).ToList();
    }

    /// <summary>
    /// Resolve tenant configs for the given system key and environment.
    /// If tenantKeyOverride is provided, loads that specific tenant config.
    /// Otherwise, discovers all tenantconfig.{sk}.*.{env}.yaml files.
    /// </summary>
    public static List<(string TenantKey, TenantConfig Config)> ResolveTenantConfigs(
        string systemKey, string env, string? tenantKeyOverride = null)
    {
        if (!string.IsNullOrEmpty(tenantKeyOverride))
        {
            var config = ConfigLoader.DiscoverAndLoadTenantConfig(systemKey, tenantKeyOverride, env);
            return [(tenantKeyOverride, config)];
        }

        // Discover all tenantconfig.{sk}.*.{env}.yaml files
        var pattern = $"tenantconfig.{systemKey}.*.{env}.yaml";
        var paths = DiscoverAllConfigFiles(pattern);
        if (paths.Count == 0)
            throw new FileNotFoundException(
                $"No {pattern} files found searching upward from current directory.");

        return paths.Select(path =>
        {
            var config = ConfigLoader.LoadTenantConfig(path);
            return (config.TenantKey, config);
        }).ToList();
    }

    /// <summary>
    /// Search upward from current directory for a directory containing files
    /// matching the pattern, then return ALL matches in that directory.
    /// </summary>
    private static List<string> DiscoverAllConfigFiles(string pattern)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var matches = dir.GetFiles(pattern);
            if (matches.Length > 0)
                return matches.Select(f => f.FullName).ToList();
            dir = dir.Parent;
        }
        return [];
    }
}
