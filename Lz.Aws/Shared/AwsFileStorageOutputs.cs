using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Shared;

/// <summary>
/// AWS EFS-specific file storage outputs.
/// </summary>
public class AwsFileStorageOutputs : IFileStorageOutputs
{
    public required Output<string> FileSystemId { get; init; }

    // AWS-specific
    public required Output<string> FileSystemArn { get; init; }
    public required Output<string> KeycloakThemeAccessPointId { get; init; }
}
