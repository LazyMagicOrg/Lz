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

    // Private-networking hardening (opt-in). Carried here because the CDN
    // component receives only compute outputs, never network outputs. Default
    // null/false keeps non-opt-in siblings unchanged.
    /// <summary>ARN of the ingress ALB (internal when PrivateNetworking). Feeds the CloudFront VpcOrigin.</summary>
    public Output<string>? AlbArn { get; init; }
    /// <summary>True when the topology built private subnets + an internal ALB (opt-in).</summary>
    public bool PrivateNetworking { get; init; }
}
