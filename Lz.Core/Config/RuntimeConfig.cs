namespace Lz.Core.Config;

/// <summary>
/// Runtime configuration sections consumed by running application containers.
/// These sections appear in both systemconfig and tenantconfig YAML files.
/// Tenant values override system defaults when present.
/// </summary>
public class SecretsManagerConfig
{
    public string SecretPrefix { get; set; } = string.Empty;
    public bool VerboseLogging { get; set; }
}

public class IntegrationsConfig
{
    public Dictionary<string, IntegrationServiceConfig> Services { get; set; } = new();
}

public class IntegrationServiceConfig
{
    public string Deployment { get; set; } = string.Empty;     // "cloud", "local", "all"
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? DockerName { get; set; }
    public string Scheme { get; set; } = "https";
    public string? Description { get; set; }
    public List<string> Modules { get; set; } = new();
}

public class AuthConfigEntry
{
    public string? HostedUIDomain { get; set; }
    public string? MetadataUrl { get; set; }
    public string? ClientId { get; set; }
    public bool ValidateAudience { get; set; }
}

public class RequestRewriterConfig
{
    public bool LogRewrites { get; set; }
    public bool VerboseLogging { get; set; }
    public bool PreserveOriginalPath { get; set; } = true;
    public List<RewriteRule> Rules { get; set; } = new();
}

public class RewriteRule
{
    public string Name { get; set; } = string.Empty;
    public string MatchPrefix { get; set; } = string.Empty;
    public string ReplaceWith { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public bool StopOnMatch { get; set; }
}

public class RequestLoggingConfig
{
    public List<string> ExcludedPaths { get; set; } = new();
}

public class VerboseLoggingConfig
{
    public bool VerboseLogging { get; set; }
}
