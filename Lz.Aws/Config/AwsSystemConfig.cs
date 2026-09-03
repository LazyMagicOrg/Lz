using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific extension of <see cref="SystemConfig"/>. Holds fields whose
/// semantics only make sense on AWS: ECS/Fargate sizing, shared-services
/// account identifiers (profile, region, secret/KMS ARNs), and cross-account
/// trust. Materialised from the same systemconfig YAML the user already writes
/// via <see cref="AwsConfigExtensions"/>'s <c>WithTypeMapping&lt;SystemConfig,
/// AwsSystemConfig&gt;()</c>.
/// </summary>
public class AwsSystemConfig : SystemConfig
{
    // Infrastructure sizing. EcsConfig applies to the ecs-fargate-keycloak
    // topology (richer per-service shape); FargateConfig applies to
    // ecs-fargate-cognito-dynamodb (and lambda-cognito-dynamodb).
    public EcsConfig? ECS { get; set; }
    public FargateConfig? Fargate { get; set; }

    // PrivateNetwork — OPT-IN private-subnet hardening for the Fargate
    // (ecs-fargate-cognito-dynamodb) topology (Phase 1, Platform/FargateHardening.md). Absent / Enabled=false
    // ⇒ NOTHING changes; the emitted plan is byte-identical to a public-subnet
    // deploy (the HygieneConfig/DurabilityConfig opt-in-null contract). Read
    // where SystemConfig is in scope via config.Aws().PrivateNetwork.
    public PrivateNetworkConfig? PrivateNetwork { get; set; }

    // Ses — OPT-IN cross-account SES sending. Absent ⇒ no IAM policy and no
    // env vars are emitted, so the plan is byte-identical (the same
    // opt-in-null contract as PrivateNetwork above). Read via config.Aws().Ses.
    public SesConfig? Ses { get; set; }

    // Cross-account shared services. SharedProfile is from YAML
    // (e.g. "monro-shared"); the rest are resolved by the CLI at startup.
    public string? SharedProfile { get; set; }
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }
    public string? SharedRegion { get; set; }

    /// <summary>
    /// The 12-digit AWS account this system deploys into. Declarative, not resolved:
    /// the CLI already knows its account from the profile, so this exists for the
    /// consumers that must know it BEFORE they have credentials — a GitHub Actions
    /// workflow composing a role-to-assume ARN from the checked-out config, for one.
    /// Commands that do hold credentials (bootstrapwebsiteci) compare it to the
    /// caller's account and refuse to run against the wrong one.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// AWS account IDs allowed to read the shared system secret cross-account.
    /// Used to author a resource policy on the shared/system secret.
    /// </summary>
    public List<string> TrustedAccountIds { get; set; } = new();
}
