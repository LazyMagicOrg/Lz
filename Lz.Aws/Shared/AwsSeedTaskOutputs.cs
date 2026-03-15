using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Shared;

/// <summary>
/// AWS ECS seed task outputs.
/// </summary>
public class AwsSeedTaskOutputs : ISeedTaskOutputs
{
    public required Output<string> TaskFamily { get; init; }
    public required Output<string> EcrRepositoryUrl { get; init; }

    // AWS-specific
    public required Output<string> TaskDefinitionArn { get; init; }
    public required Output<string> TaskRoleArn { get; init; }
    public required Output<string> ExecutionRoleArn { get; init; }
}
