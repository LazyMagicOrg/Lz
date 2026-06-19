using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific extension of <see cref="SystemConfig"/>. Holds fields whose
/// semantics only make sense on AWS: ECS/AppRunner sizing, shared-services
/// account identifiers (profile, region, secret/KMS ARNs), and cross-account
/// trust. Materialised from the same systemconfig YAML the user already writes
/// via <see cref="AwsConfigExtensions"/>'s <c>WithTypeMapping&lt;SystemConfig,
/// AwsSystemConfig&gt;()</c>.
/// </summary>
public class AwsSystemConfig : SystemConfig
{
    // Infrastructure sizing. EcsConfig applies to the ecs-fargate-keycloak
    // topology (richer per-service shape); AppRunnerConfig applies to the
    // apprunner topology; FargateConfig applies to ecs-fargate-cognito-dynamodb.
    public EcsConfig? ECS { get; set; }
    public AppRunnerConfig? AppRunner { get; set; }
    public FargateConfig? Fargate { get; set; }

    // Cross-account shared services. SharedProfile is from YAML
    // (e.g. "monro-shared"); the rest are resolved by the CLI at startup.
    public string? SharedProfile { get; set; }
    public string? SharedSecretArn { get; set; }
    public string? SharedKmsKeyArn { get; set; }
    public string? SharedRegion { get; set; }

    /// <summary>
    /// AWS account IDs allowed to read the shared system secret cross-account.
    /// Used to author a resource policy on the shared/system secret.
    /// </summary>
    public List<string> TrustedAccountIds { get; set; } = new();
}
