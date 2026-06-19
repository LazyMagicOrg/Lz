using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Core.Config;

/// <summary>
/// Discovers, parses, and loads configuration files.
/// SystemKey, TenantKey, and Environment are derived from filenames per convention:
///   sharedconfig.yaml
///   systemconfig.{systemkey}.{env}.yaml
///   tenantconfig.{systemkey}.{tenantkey}.{env}.yaml
///   keycloakconfig.{systemkey}.{env}.yaml
///   credsconfig.{systemkey}.{env}.yaml
/// </summary>
public static class ConfigLoader
{
    // Platform-contributed extensions (e.g. AWS type mappings). See IConfigExtensions.
    // Only extensions whose Platform matches ActivePlatform contribute to the
    // deserializer — prevents silent last-write-wins collisions when more than
    // one platform's extensions are registered in the same process.
    private static readonly List<IConfigExtensions> _extensions = new();
    private static IDeserializer? _deserializer;
    private static string _activePlatform = "aws";

    /// <summary>
    /// The platform whose <see cref="IConfigExtensions"/> contribute type
    /// mappings to the deserializer. Defaults to <c>"aws"</c> and is updated
    /// when a loaded YAML file carries a top-level <c>platform:</c> key that
    /// names a different platform.
    /// </summary>
    public static string ActivePlatform => _activePlatform;

    private static IDeserializer Deserializer => _deserializer ??= BuildDeserializer();

    private static IDeserializer BuildDeserializer()
    {
        var builder = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties();
        foreach (var ext in _extensions)
        {
            if (string.Equals(ext.Platform, _activePlatform, StringComparison.OrdinalIgnoreCase))
                ext.Configure(builder);
        }
        return builder.Build();
    }

    /// <summary>
    /// Register a platform-specific config extension. Platform libraries (Lz.Aws,
    /// Lz.Azure) call this once during host startup, before any config is loaded,
    /// to contribute YAML type mappings for their derived config types. Multiple
    /// platforms' extensions can be safely registered — only the one matching
    /// <see cref="ActivePlatform"/> contributes mappings.
    /// </summary>
    public static void RegisterExtensions(IConfigExtensions extensions)
    {
        if (extensions == null) throw new ArgumentNullException(nameof(extensions));
        _extensions.Add(extensions);
        _deserializer = null; // invalidate so the next load rebuilds with the new mapping
    }

    /// <summary>
    /// Resolve the target environment.
    /// Priority: explicit override → folder hierarchy (_Dev* → dev, _Test* → test, _Prod* → prod).
    /// Lives in Lz.Core so plugins (which only reference Lz.Core / Lz.Aws) can
    /// reuse the same resolution logic the CLI uses.
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
    /// Reset registrations and active platform. Intended for tests that need
    /// a clean slate; not part of the normal runtime contract.
    /// </summary>
    internal static void ResetForTests()
    {
        _extensions.Clear();
        _deserializer = null;
        _activePlatform = "aws";
    }

    /// <summary>
    /// Explicitly set the active platform. Normally inferred from the YAML
    /// being loaded; this hook is for tests and for callers that know the
    /// target before any file is touched.
    /// </summary>
    public static void SetActivePlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("Platform must not be empty.", nameof(platform));
        var normalized = platform.Trim().ToLowerInvariant();
        if (_activePlatform == normalized) return;
        _activePlatform = normalized;
        _deserializer = null;
    }

    /// <summary>
    /// Scan the YAML for a top-level <c>platform:</c> (or <c>Platform:</c>) key
    /// and update <see cref="ActivePlatform"/> if present and different. Called
    /// before each deserialize so the correct platform's type mappings are
    /// active. Lines inside nested blocks are ignored by requiring the key to
    /// appear at column zero.
    /// </summary>
    private static void DetectPlatformFromYaml(string yaml)
    {
        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            if (!key.Equals("platform", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line.Substring(colon + 1).Trim();
            // strip inline comment + surrounding quotes
            var hash = value.IndexOf('#');
            if (hash >= 0) value = value.Substring(0, hash).Trim();
            value = value.Trim('"', '\'');
            if (!string.IsNullOrEmpty(value))
                SetActivePlatform(value);
            return;
        }
    }

    /// <summary>
    /// Load a SharedConfig from a specific file path.
    /// There is a single shared-services account, so the filename is simply sharedconfig.yaml.
    /// </summary>
    public static SharedConfig LoadSharedConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        DetectPlatformFromYaml(yaml);
        var config = Deserializer.Deserialize<SharedConfig>(yaml) ?? new SharedConfig();
        config.State = GenerateAwsStateConfig("shared", config.SharedSuffix, config.Region);
        ConfigValidator.Validate(config, filePath);
        return config;
    }

    /// <summary>
    /// Discover and load SharedConfig by searching upward from the given directory.
    /// </summary>
    public static SharedConfig DiscoverAndLoadSharedConfig(string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var filePath = DiscoverConfigFile(dir, "sharedconfig.yaml")
            ?? throw new FileNotFoundException(
                $"No sharedconfig.yaml found searching upward from {dir}");
        return LoadSharedConfig(filePath);
    }

    /// <summary>
    /// Load a SystemConfig from a specific file path.
    /// SystemKey and Environment are derived from the filename.
    /// </summary>
    public static SystemConfig LoadSystemConfig(string filePath)
    {
        var (systemKey, environment) = ParseSystemConfigFilename(filePath);
        var yaml = File.ReadAllText(filePath);
        DetectPlatformFromYaml(yaml);
        var config = Deserializer.Deserialize<SystemConfig>(yaml) ?? new SystemConfig();
        config.SystemKey = systemKey;
        config.Environment = environment;
        config.State = GenerateAwsStateConfig($"{systemKey}-{environment}", config.SystemSuffix, config.Region);
        ConfigValidator.Validate(config, filePath);
        return config;
    }

    /// <summary>
    /// Discover and load SystemConfig by searching upward from the given directory
    /// for files matching the pattern systemconfig.*.*.yaml.
    /// </summary>
    public static SystemConfig DiscoverAndLoadSystemConfig(string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var filePath = DiscoverConfigFile(dir, "systemconfig.*.*.yaml")
            ?? throw new FileNotFoundException(
                $"No systemconfig.*.*.yaml found searching upward from {dir}");
        return LoadSystemConfig(filePath);
    }

    /// <summary>
    /// Load a TenantConfig from a specific file path.
    /// SystemKey, TenantKey, and Environment are derived from the filename.
    /// </summary>
    public static TenantConfig LoadTenantConfig(string filePath)
    {
        var (systemKey, tenantKey, environment) = ParseTenantConfigFilename(filePath);
        var yaml = File.ReadAllText(filePath);
        DetectPlatformFromYaml(yaml);
        var config = Deserializer.Deserialize<TenantConfig>(yaml) ?? new TenantConfig();
        config.SystemKey = systemKey;
        config.TenantKey = tenantKey;
        config.Environment = environment;
        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

        // If a sibling subtenantconfig file is present, hydrate Subtenants
        // from it. Having both inline Subtenants: in tenantconfig AND a
        // sibling subtenantconfig is ambiguous — reject.
        var configDirForSiblings = Path.GetDirectoryName(filePath);
        if (configDirForSiblings != null)
        {
            var subtenantFilename = $"subtenantconfig.{systemKey}.{tenantKey}.{environment}.yaml";
            var subtenantPath = Path.Combine(configDirForSiblings, subtenantFilename);
            if (File.Exists(subtenantPath))
            {
                if (config.Subtenants != null && config.Subtenants.Count > 0)
                    throw new InvalidOperationException(
                        $"Tenant '{tenantKey}' ({environment}) has both an inline Subtenants: " +
                        $"block in tenantconfig and a sibling {subtenantFilename} file. " +
                        "Move all subtenants into the subtenantconfig file or delete the file — " +
                        "pick one.");
                var subtenantConfig = LoadSubtenantConfig(subtenantPath);
                config.Subtenants = subtenantConfig.Subtenants;
            }
        }

        ConfigValidator.Validate(config, filePath);

        // Load smartstore.usersettings.json from the same directory if present.
        // This file contains the SmartStore usersettings.json content (ReverseProxy, Serilog, etc.)
        // and is stored separately to avoid the SSM 4096-char parameter limit.
        var configDir = Path.GetDirectoryName(filePath);
        if (configDir != null)
        {
            var userSettingsPath = Path.Combine(configDir, "smartstore.usersettings.json");
            if (File.Exists(userSettingsPath))
            {
                var json = File.ReadAllText(userSettingsPath);
                config.Smartstore = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }
        }

        return config;
    }

    /// <summary>
    /// Discover and load TenantConfig for a specific tenant.
    /// Searches upward from the given directory for tenantconfig.{systemKey}.{tenantKey}.{env}.yaml.
    /// </summary>
    public static TenantConfig DiscoverAndLoadTenantConfig(
        string systemKey, string tenantKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var expectedFilename = $"tenantconfig.{systemKey}.{tenantKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, expectedFilename)
            ?? throw new FileNotFoundException(
                $"Tenant config file '{expectedFilename}' not found searching upward from {dir}");
        return LoadTenantConfig(filePath);
    }

    /// <summary>
    /// Load a SubtenantConfig from a specific file path.
    /// SystemKey, TenantKey, and Environment are derived from the filename.
    /// </summary>
    public static SubtenantConfig LoadSubtenantConfig(string filePath)
    {
        var (systemKey, tenantKey, environment) = ParseSubtenantConfigFilename(filePath);
        var yaml = File.ReadAllText(filePath);
        DetectPlatformFromYaml(yaml);
        var config = Deserializer.Deserialize<SubtenantConfig>(yaml) ?? new SubtenantConfig();
        config.SystemKey = systemKey;
        config.TenantKey = tenantKey;
        config.Environment = environment;
        return config;
    }

    /// <summary>
    /// Discover a subtenantconfig file for the given tenant; returns null if
    /// no sibling file exists (inline Subtenants: in tenantconfig remains
    /// supported for back-compat).
    /// </summary>
    public static SubtenantConfig? DiscoverSubtenantConfig(
        string systemKey, string tenantKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var expectedFilename = $"subtenantconfig.{systemKey}.{tenantKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, expectedFilename);
        return filePath == null ? null : LoadSubtenantConfig(filePath);
    }

    /// <summary>
    /// Load a ContainerServiceConfig from a specific file path.
    /// </summary>
    public static ContainerServiceConfig LoadContainerServiceConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        DetectPlatformFromYaml(yaml);
        var config = Deserializer.Deserialize<ContainerServiceConfig>(yaml) ?? new ContainerServiceConfig();
        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        return config;
    }

    /// <summary>
    /// Discover and load ContainerServiceConfig by convention: containersbuild.{systemKey}.{env}.yaml.
    /// </summary>
    public static ContainerServiceConfig DiscoverAndLoadContainerServiceConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var pattern = $"containersbuild.{systemKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, pattern)
            ?? throw new FileNotFoundException(
                $"Container service config not found: {pattern}. " +
                $"Create {pattern} in the monorepo root.");
        return LoadContainerServiceConfig(filePath);
    }

    /// <summary>
    /// Discover and load foundation ContainerServiceConfig by convention: containersbuild.foundation.{env}.yaml.
    /// Foundation containers are system-scoped (shared across all tenants).
    /// </summary>
    public static ContainerServiceConfig DiscoverAndLoadFoundationContainerConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var pattern = $"containersbuild.foundation.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, pattern);
        if (filePath == null)
        {
            // Foundation config is optional — return empty if not found
            return new ContainerServiceConfig { ConfigDirectory = dir };
        }
        return LoadContainerServiceConfig(filePath);
    }

    /// <summary>
    /// Discover the monorepo root directory (where systemconfig lives).
    /// Searches upward from the current directory for the systemconfig file.
    /// </summary>
    public static string? DiscoverMonorepoRoot(string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var pattern = $"systemconfig.{systemKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, pattern);
        return filePath != null ? Path.GetDirectoryName(filePath) : null;
    }

    /// <summary>
    /// Search upward from startDir for a file matching the given pattern.
    /// Returns the first match or null.
    /// </summary>
    public static string? DiscoverConfigFile(string startDir, string pattern)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var matches = dir.GetFiles(pattern);
            if (matches.Length > 0)
                return matches[0].FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Parse systemconfig.{systemkey}.{env}.yaml → (systemKey, environment)
    /// </summary>
    public static (string SystemKey, string Environment) ParseSystemConfigFilename(string filePath)
    {
        var filename = Path.GetFileNameWithoutExtension(filePath); // remove .yaml
        var parts = filename.Split('.');
        // Expected: systemconfig.{systemkey}.{env}
        if (parts.Length != 3 || !parts[0].Equals("systemconfig", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid systemconfig filename format: '{Path.GetFileName(filePath)}'. " +
                $"Expected: systemconfig.{{systemkey}}.{{env}}.yaml");
        return (parts[1], parts[2]);
    }

    /// <summary>
    /// Parse tenantconfig.{systemkey}.{tenantkey}.{env}.yaml → (systemKey, tenantKey, environment)
    /// </summary>
    public static (string SystemKey, string TenantKey, string Environment) ParseTenantConfigFilename(string filePath)
    {
        var filename = Path.GetFileNameWithoutExtension(filePath); // remove .yaml
        var parts = filename.Split('.');
        // Expected: tenantconfig.{systemkey}.{tenantkey}.{env}
        if (parts.Length != 4 || !parts[0].Equals("tenantconfig", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid tenantconfig filename format: '{Path.GetFileName(filePath)}'. " +
                $"Expected: tenantconfig.{{systemkey}}.{{tenantkey}}.{{env}}.yaml");
        return (parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// Parse subtenantconfig.{systemkey}.{tenantkey}.{env}.yaml → (systemKey, tenantKey, environment)
    /// </summary>
    public static (string SystemKey, string TenantKey, string Environment) ParseSubtenantConfigFilename(string filePath)
    {
        var filename = Path.GetFileNameWithoutExtension(filePath);
        var parts = filename.Split('.');
        if (parts.Length != 4 || !parts[0].Equals("subtenantconfig", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid subtenantconfig filename format: '{Path.GetFileName(filePath)}'. " +
                $"Expected: subtenantconfig.{{systemkey}}.{{tenantkey}}.{{env}}.yaml");
        return (parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// Propagate shared SeedData config to a SystemConfig if the system doesn't define its own.
    /// The bucket name is derived from the systemKey and shared suffix.
    /// Call this after loading both configs.
    /// </summary>
    public static void PropagateSharedSeedData(SystemConfig systemConfig, SharedConfig sharedConfig)
    {
        if (systemConfig.SeedData != null)
            return; // system has explicit SeedData — don't override

        var bucket = sharedConfig.SeedData?.Bucket
            ?? $"{systemConfig.SystemKey}--seeddata-{sharedConfig.SharedSuffix}";
        var region = sharedConfig.SeedData?.Region ?? sharedConfig.Region;

        systemConfig.SeedData = new SeedDataConfig
        {
            Bucket = bucket,
            Region = region,
        };
    }

    /// <summary>
    /// Generate AWS Pulumi state config from a name prefix and suffix.
    /// The suffix is placed at the end of the resource name (not the middle).
    /// e.g., prefix="med-dev", suffix="4498-a704", region="us-west-2"
    ///   → Backend:         s3://med-dev-pulumi-state-4498-a704?region=us-west-2
    ///   → SecretsProvider:  awskms://alias/med-dev-pulumi-key-4498-a704
    /// </summary>
    internal static StateConfig GenerateAwsStateConfig(string prefix, string suffix, string region)
    {
        return new StateConfig
        {
            Backend = $"s3://{prefix}-pulumi-state-{suffix}?region={region}",
            SecretsProvider = $"awskms://alias/{prefix}-pulumi-key-{suffix}",
        };
    }
}
