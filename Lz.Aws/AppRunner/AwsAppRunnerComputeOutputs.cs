using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// AWS AppRunner-specific compute environment outputs.
/// AppRunner doesn't have a cluster — compute is per-service.
/// We track the ECR repository and auto-scaling config here.
/// </summary>
public class AwsAppRunnerComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // AWS AppRunner-specific
    public required Output<string> AutoScalingConfigArn { get; init; }
    public required Output<string> EcrRepositoryUrl { get; init; }
    public required Output<string> EcrRepositoryArn { get; init; }
}
