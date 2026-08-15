using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

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
