using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.FargateAlb;

/// <summary>
/// AWS ECS-specific compute environment outputs.
/// </summary>
public class AwsFargateAlbComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // AWS-specific
    public required Output<string> ClusterArn { get; init; }
    public required Output<string> CloudMapNamespaceId { get; init; }
    public required Output<string> CloudMapNamespaceArn { get; init; }
}
