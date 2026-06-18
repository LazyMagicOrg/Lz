using Pulumi;

namespace Lz.Aws.Interfaces.Outputs;

/// <summary>
/// Outputs from the gate-checker Lambda component.
/// Used by the transition checker to invoke the Lambda at gate-check time.
/// </summary>
public interface IGateCheckerOutputs
{
    Output<string> FunctionName { get; }
}
