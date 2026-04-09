using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Stub file storage component for the AppRunner topology.
/// AppRunner doesn't use EFS — all file storage is via S3.
/// This returns empty outputs to satisfy the interface contract.
/// </summary>
public class AwsAppRunnerFileStorageComponent : IFileStorageComponent
{
    public IFileStorageOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        return new AwsAppRunnerFileStorageOutputs
        {
            FileSystemId = Output.Create(""),
        };
    }
}

internal class AwsAppRunnerFileStorageOutputs : IFileStorageOutputs
{
    public required Output<string> FileSystemId { get; init; }
}
