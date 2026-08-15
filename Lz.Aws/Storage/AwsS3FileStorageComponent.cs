using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Storage;

/// <summary>
/// Stub file storage component for the S3 topologies —
/// no EFS; all file storage is via S3.
/// This returns empty outputs to satisfy the interface contract.
/// </summary>
public class AwsS3FileStorageComponent : IFileStorageComponent
{
    public IFileStorageOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        return new AwsS3FileStorageOutputs
        {
            FileSystemId = Output.Create(""),
        };
    }
}

internal class AwsS3FileStorageOutputs : IFileStorageOutputs
{
    public required Output<string> FileSystemId { get; init; }
}
