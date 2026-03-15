using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS ECS-specific compute environment outputs.
/// </summary>
public class AwsComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // AWS-specific
    public required Output<string> ClusterArn { get; init; }
    public required Output<string> CloudMapNamespaceId { get; init; }
    public required Output<string> CloudMapNamespaceArn { get; init; }
}
