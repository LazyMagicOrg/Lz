using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Compute outputs for ECSExpress topology. ECS cluster + Cloud Map namespace.
/// ECR repos are per-tenant and imperatively created by
/// <c>lz deploycontainer</c>; they are not surfaced on compute outputs.
/// </summary>
public class AwsEcsExpressComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // ECSExpress-specific
    public required Output<string> ClusterArn { get; init; }
    public required Output<string> CloudMapNamespaceId { get; init; }
}
