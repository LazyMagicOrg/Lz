namespace Lz.Aws.Config;

/// <summary>
/// Preflight validation for <see cref="FargateConfig"/>. Catches AWS Fargate
/// CPU/memory pair violations and obvious misconfigurations before Pulumi
/// surfaces them as opaque apply-time errors.
/// </summary>
/// <remarks>
/// CPU/memory pair matrix is the Linux/x86 matrix documented at
/// https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-cpu-memory-error.html.
/// ARM/Graviton has a tighter subset; we validate the superset here since
/// the tool currently provisions x86 task definitions.
/// </remarks>
public static class FargateValidator
{
    // CPU unit => allowed memory range. Step=0 means the only allowed values
    // are in _cpu256Memories below (the 256 row is discrete, not a range).
    private static readonly Dictionary<int, (int Min, int Max, int Step)> _pairs = new()
    {
        [256]   = (512,      2048, 0),
        [512]   = (1024,     4096, 1024),
        [1024]  = (2048,     8192, 1024),
        [2048]  = (4096,    16384, 1024),
        [4096]  = (8192,    30720, 1024),
        [8192]  = (16384,   61440, 4096),
        [16384] = (32768,  122880, 8192),
    };

    private static readonly int[] _cpu256Memories = { 512, 1024, 2048 };

    // CloudWatch Logs accepts only specific retention values. Anything else is
    // rejected at PutRetentionPolicy time with a cryptic API error.
    // https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_PutRetentionPolicy.html
    private static readonly int[] _validLogRetentionDays =
    {
        1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180,
        365, 400, 545, 731, 1827, 2192, 2557, 2922, 3288, 3653,
    };

    // Fargate hard limit is 5000 tasks/service (Sept 2023), but realistic
    // config values are O(10). Cap well below the API limit so YAML typos
    // like DesiredCount=10000 fail at load, not at quota-exceeded time.
    private const int _desiredCountSanityCap = 1000;

    public static void Validate(FargateConfig cfg, List<string> errs, string context)
    {
        if (!_pairs.TryGetValue(cfg.Cpu, out var range))
        {
            errs.Add($"{context}: Fargate Cpu={cfg.Cpu} is not a valid AWS Fargate CPU unit. " +
                     $"Allowed: {string.Join(", ", _pairs.Keys)}.");
        }
        else
        {
            var memOk = cfg.Cpu == 256
                ? _cpu256Memories.Contains(cfg.Memory)
                : cfg.Memory >= range.Min
                  && cfg.Memory <= range.Max
                  && (cfg.Memory - range.Min) % range.Step == 0;

            if (!memOk)
            {
                var allowed = cfg.Cpu == 256
                    ? string.Join(", ", _cpu256Memories)
                    : $"{range.Min}-{range.Max} MB in {range.Step} MB increments";
                errs.Add($"{context}: Fargate Memory={cfg.Memory} MB is not valid for Cpu={cfg.Cpu}. " +
                         $"Allowed: {allowed}.");
            }
        }

        if (cfg.Port < 1 || cfg.Port > 65535)
            errs.Add($"{context}: Fargate Port={cfg.Port} out of range (1-65535).");

        if (string.IsNullOrWhiteSpace(cfg.HealthCheckPath) || !cfg.HealthCheckPath.StartsWith('/'))
            errs.Add($"{context}: Fargate HealthCheckPath must start with '/' (got '{cfg.HealthCheckPath}').");

        if (cfg.DesiredCount < 0)
            errs.Add($"{context}: Fargate DesiredCount must be >= 0 (got {cfg.DesiredCount}).");
        else if (cfg.DesiredCount > _desiredCountSanityCap)
            errs.Add($"{context}: Fargate DesiredCount={cfg.DesiredCount} exceeds sanity cap of " +
                     $"{_desiredCountSanityCap}. If this is intentional, raise the cap in FargateValidator.");

        if (!_validLogRetentionDays.Contains(cfg.LogRetentionDays))
            errs.Add($"{context}: Fargate LogRetentionDays={cfg.LogRetentionDays} is not a valid " +
                     $"CloudWatch retention value. Allowed: {string.Join(", ", _validLogRetentionDays)}.");
    }
}
