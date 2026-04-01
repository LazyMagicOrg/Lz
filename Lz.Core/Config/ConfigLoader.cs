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
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Keycloak config YAML uses camelCase naming
    private static readonly IDeserializer CamelCaseDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load a SharedConfig from a specific file path.
    /// There is a single shared-services account, so the filename is simply sharedconfig.yaml.
    /// </summary>
    public static SharedConfig LoadSharedConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
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
        var config = Deserializer.Deserialize<TenantConfig>(yaml) ?? new TenantConfig();
        config.SystemKey = systemKey;
        config.TenantKey = tenantKey;
        config.Environment = environment;
        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
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
    /// Load a KeycloakSeedConfig from a specific file path.
    /// Uses camelCase YAML naming convention (different from systemconfig).
    /// </summary>
    public static KeycloakSeedConfig LoadKeycloakSeedConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return CamelCaseDeserializer.Deserialize<KeycloakSeedConfig>(yaml) ?? new KeycloakSeedConfig();
    }

    /// <summary>
    /// Discover a keycloakconfig file by convention: keycloakconfig.{systemKey}.{env}.yaml.
    /// Returns null if no matching file is found (seeding is optional).
    /// </summary>
    public static KeycloakSeedConfig? DiscoverKeycloakSeedConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var expectedFilename = $"keycloakconfig.{systemKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, expectedFilename);
        if (filePath == null) return null;
        return LoadKeycloakSeedConfig(filePath);
    }

    /// <summary>
    /// Discover a per-tenant keycloakconfig file with template fallback.
    /// Resolution order:
    ///   1. keycloakconfig.{systemKey}.{tenantKey}.{env}.yaml (tenant-specific override)
    ///   2. keycloakconfig.system.tenant.{env}.yaml (template with &lt;&lt;placeholder&gt;&gt; replacements)
    /// Returns null if neither file is found (seeding is optional).
    /// </summary>
    public static KeycloakSeedConfig? DiscoverTenantKeycloakSeedConfig(
        string systemKey, string tenantKey, string environment,
        TenantConfig tenantConfig,
        Dictionary<string, string?> smtpSecrets,
        string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();

        // 1. Look for tenant-specific override
        var specificFilename = $"keycloakconfig.{systemKey}.{tenantKey}.{environment}.yaml";
        var specificPath = DiscoverConfigFile(dir, specificFilename);
        if (specificPath != null)
            return LoadKeycloakSeedConfig(specificPath);

        // 2. Fall back to template
        var templateFilename = $"keycloakconfig.system.tenant.{environment}.yaml";
        var templatePath = DiscoverConfigFile(dir, templateFilename);
        if (templatePath == null)
            return null;

        // Read raw YAML and perform placeholder replacements
        var yaml = File.ReadAllText(templatePath);
        yaml = yaml.Replace("<<system>>", systemKey);
        yaml = yaml.Replace("<<tenant>>", tenantKey);
        yaml = yaml.Replace("<<env>>", environment);
        yaml = yaml.Replace("<<rootdomain>>", tenantConfig.RootDomain);
        yaml = yaml.Replace("<<displayname>>", tenantConfig.DisplayName ?? tenantKey);

        // SMTP secrets from shared/system
        foreach (var (key, value) in smtpSecrets)
            yaml = yaml.Replace($"<<{key}>>", value ?? "");

        return CamelCaseDeserializer.Deserialize<KeycloakSeedConfig>(yaml) ?? new KeycloakSeedConfig();
    }

    /// <summary>
    /// Load a BootstrapCredsConfig from a specific file path.
    /// Uses camelCase YAML naming convention.
    /// </summary>
    public static BootstrapCredsConfig LoadBootstrapCredsConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return CamelCaseDeserializer.Deserialize<BootstrapCredsConfig>(yaml) ?? new BootstrapCredsConfig();
    }

    /// <summary>
    /// Discover a credsconfig file by convention: credsconfig.{systemKey}.{env}.yaml.
    /// Returns null if no matching file is found (bootstrap creds are optional).
    /// </summary>
    public static BootstrapCredsConfig? DiscoverBootstrapCredsConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var expectedFilename = $"credsconfig.{systemKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, expectedFilename);
        if (filePath == null) return null;
        return LoadBootstrapCredsConfig(filePath);
    }

    /// <summary>
    /// Load a ContainerServiceConfig from a specific file path.
    /// </summary>
    public static ContainerServiceConfig LoadContainerServiceConfig(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        var config = Deserializer.Deserialize<ContainerServiceConfig>(yaml) ?? new ContainerServiceConfig();
        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        return config;
    }

    /// <summary>
    /// Discover and load ContainerServiceConfig by convention: servicesconfig.{systemKey}.{env}.yaml.
    /// </summary>
    public static ContainerServiceConfig DiscoverAndLoadContainerServiceConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var pattern = $"servicesconfig.{systemKey}.{environment}.yaml";
        var filePath = DiscoverConfigFile(dir, pattern)
            ?? throw new FileNotFoundException(
                $"Container service config not found: {pattern}. " +
                $"Create {pattern} in the monorepo root.");
        return LoadContainerServiceConfig(filePath);
    }

    /// <summary>
    /// Discover and load foundation ContainerServiceConfig by convention: servicesconfig.foundation.{env}.yaml.
    /// Foundation containers are system-scoped (shared across all tenants).
    /// </summary>
    public static ContainerServiceConfig DiscoverAndLoadFoundationContainerConfig(
        string systemKey, string environment, string? startDirectory = null)
    {
        var dir = startDirectory ?? Directory.GetCurrentDirectory();
        var pattern = $"servicesconfig.foundation.{environment}.yaml";
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
