using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

/// <summary>
/// Outputs from the seed task component.
/// Used by the seed runner to launch ECS tasks for export/import operations.
/// </summary>
public interface ISeedTaskOutputs
{
    Output<string> TaskFamily { get; }
    Output<string> EcrRepositoryUrl { get; }
}
