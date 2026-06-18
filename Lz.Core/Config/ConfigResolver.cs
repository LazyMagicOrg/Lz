namespace Lz.Core.Config;

/// <summary>
/// Resolves environment, system configs, and tenant configs using smart defaults.
/// Priority: explicit override → folder hierarchy heuristic → file discovery.
///
/// Sibling to <see cref="ConfigLoader"/>: Loader parses a single known path,
/// Resolver picks which path(s) to load by walking the working directory and
/// applying the filename conventions in <see cref="ConfigLoader"/>'s docs.
///
/// Lives in Lz.Core (not Lz.Cli) so plugins can call the same discovery logic
/// the CLI uses without taking a tool-package dependency.
/// </summary>
public static class ConfigResolver
{
    /// <summary>
    /// Resolve the target environment.
    /// Priority: override → folder hierarchy (_Dev* → dev, _Test* → test, _Prod* → prod).
    /// Delegates to <see cref="ConfigLoader.ResolveEnvironment"/>.
    /// </summary>
    public static string ResolveEnvironment(string? envOverride = null)
        => ConfigLoader.ResolveEnvironment(envOverride);

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
