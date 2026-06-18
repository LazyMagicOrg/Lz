using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Shared;

/// <summary>
/// AWS gate-checker Lambda outputs.
/// </summary>
public class AwsGateCheckerOutputs : IGateCheckerOutputs
{
    public required Output<string> FunctionName { get; init; }

    // AWS-specific
    public required Output<string> FunctionArn { get; init; }
}
