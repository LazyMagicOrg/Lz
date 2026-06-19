using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// AWS AppRunner-specific compute environment outputs.
/// AppRunner doesn't have a cluster — compute is per-service.
/// ECR repos are per-tenant and imperatively created by
/// <c>lz deploycontainer</c>; they aren't surfaced here.
/// </summary>
public class AwsAppRunnerComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // AWS AppRunner-specific
    public required Output<string> AutoScalingConfigArn { get; init; }
}
