using Lz.Core.Config;
using Lz.Core.Keycloak;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Aws.Config;

/// <summary>
/// AWS/Keycloak-specific config loader. Keycloak and bootstrap-creds YAML
/// files use camelCase naming (unlike systemconfig/tenantconfig which are
/// PascalCase), so they get their own deserializer. Moved here so Lz.Core
/// doesn't need to reference Keycloak-shaped types.
/// </summary>
public static class AwsKeycloakConfigLoader
{
    private static readonly IDeserializer CamelCaseDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load a KeycloakSeedConfig from a specific file path.
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
        var filePath = ConfigLoader.DiscoverConfigFile(dir, expectedFilename);
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
        var specificPath = ConfigLoader.DiscoverConfigFile(dir, specificFilename);
        if (specificPath != null)
            return LoadKeycloakSeedConfig(specificPath);

        // 2. Fall back to template
        var templateFilename = $"keycloakconfig.system.tenant.{environment}.yaml";
        var templatePath = ConfigLoader.DiscoverConfigFile(dir, templateFilename);
        if (templatePath == null)
            return null;

        // Read raw YAML and perform placeholder replacements
        var yaml = File.ReadAllText(templatePath);
        yaml = yaml.Replace("<<system>>", systemKey);
        yaml = yaml.Replace("<<tenant>>", tenantKey);
        yaml = yaml.Replace("<<env>>", environment);
        yaml = yaml.Replace("<<rootdomain>>", tenantConfig.RootDomain);

        // Legacy domain: substitute if present, remove lines if not
        var legacyDomain = tenantConfig.LegacyDomains?.FirstOrDefault();
        if (!string.IsNullOrEmpty(legacyDomain))
        {
            yaml = yaml.Replace("<<legacydomain>>", legacyDomain);
        }
        else
        {
            // Remove entire lines containing the placeholder
            yaml = string.Join("\n",
                yaml.Split('\n').Where(line => !line.Contains("<<legacydomain>>")));
        }

        yaml = yaml.Replace("<<displayname>>", tenantConfig.DisplayName ?? tenantKey);

        // SMTP secrets from shared/system
        foreach (var (key, value) in smtpSecrets)
            yaml = yaml.Replace($"<<{key}>>", value ?? "");

        return CamelCaseDeserializer.Deserialize<KeycloakSeedConfig>(yaml) ?? new KeycloakSeedConfig();
    }

    /// <summary>
    /// Load a BootstrapCredsConfig from a specific file path.
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
        var filePath = ConfigLoader.DiscoverConfigFile(dir, expectedFilename);
        if (filePath == null) return null;
        return LoadBootstrapCredsConfig(filePath);
    }
}
