namespace Lz.Core.Config;

/// <summary>
/// Deserialization model for credsconfig.{systemkey}.{env}.yaml.
/// Contains bootstrap credentials for initial deployment — SMTP passwords,
/// initial Keycloak users, etc. This file should NOT be committed to source control.
/// YAML uses camelCase naming — requires CamelCaseNamingConvention deserializer.
/// </summary>
public class BootstrapCredsConfig
{
    /// <summary>
    /// SMTP password for SES (applied to all realms that have masked passwords).
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// Users to create in Keycloak, keyed by realm name.
    /// </summary>
    public Dictionary<string, List<BootstrapUserConfig>>? KeycloakUsers { get; set; }
}

public class BootstrapUserConfig
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
    public bool Temporary { get; set; }
    public List<string>? Groups { get; set; }
}
