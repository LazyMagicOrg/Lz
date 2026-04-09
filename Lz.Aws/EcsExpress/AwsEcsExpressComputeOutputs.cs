using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Compute outputs for ECSExpress topology.
/// ECS cluster + Cloud Map namespace + ECR repository.
/// </summary>
public class AwsEcsExpressComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // ECSExpress-specific
    public required Output<string> ClusterArn { get; init; }
    public required Output<string> CloudMapNamespaceId { get; init; }
    public required Output<string> EcrRepositoryUrl { get; init; }
    public required Output<string> EcrRepositoryArn { get; init; }
}
