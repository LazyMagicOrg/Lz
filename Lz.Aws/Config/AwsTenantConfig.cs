using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific extension of <see cref="TenantConfig"/>. Holds fields whose
/// semantics only make sense on AWS: ACM certificate ARN, Route 53 hosted
/// zone ID, cross-account Secrets Manager/KMS ARNs, and ECS/Fargate sizing.
/// </summary>
public class AwsTenantConfig : TenantConfig
{
    /// <summary>ACM certificate ARN for RootDomain + LegacyDomains SNI.</summary>
    public string? AcmCertificateArn { get; set; }

    /// <summary>Route 53 hosted-zone ID for RootDomain.</summary>
    public string? HostedZoneId { get; set; }

    // Cross-account shared services — propagated from AwsSystemConfig at runtime
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }

    // Per-tenant infrastructure overrides. See AwsSystemConfig for topology
    // mapping. Any omitted block falls back to the system-level value.
    public EcsConfig? ECS { get; set; }
    public FargateConfig? Fargate { get; set; }
}
