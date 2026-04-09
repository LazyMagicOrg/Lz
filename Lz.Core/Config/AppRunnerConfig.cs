namespace Lz.Core.Config;

/// <summary>
/// AppRunner deployment configuration section — shared between systemconfig and tenantconfig.
/// Maps to the "AppRunner:" section in YAML.
/// </summary>
public class AppRunnerConfig
{
    /// <summary>CPU units (256, 512, 1024, 2048, 4096).</summary>
    public int Cpu { get; set; } = 1024;

    /// <summary>Memory in MB (512, 1024, 2048, 3072, 4096, 6144, 8192, 10240, 12288).</summary>
    public int Memory { get; set; } = 2048;

    /// <summary>Maximum concurrent requests per instance before scaling.</summary>
    public int MaxConcurrency { get; set; } = 100;

    /// <summary>Minimum number of instances.</summary>
    public int MinSize { get; set; } = 1;

    /// <summary>Maximum number of instances.</summary>
    public int MaxSize { get; set; } = 1;

    /// <summary>Container port.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Health check path.</summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>Log retention in days.</summary>
    public int LogRetentionDays { get; set; } = 3;
}
