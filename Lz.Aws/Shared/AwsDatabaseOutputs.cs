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
/// AWS RDS-specific database outputs.
/// </summary>
public class AwsDatabaseOutputs : IDatabaseOutputs
{
    public required Output<string> Endpoint { get; init; }
    public required Output<int> Port { get; init; }
    public required Output<string> AdminSecretId { get; init; }

    // AWS-specific
    public required Output<string> DbInstanceIdentifier { get; init; }
    public required Output<string> MasterSecretArn { get; init; }
    public required Output<string> SystemSecretArn { get; init; }
    public required Output<string> DbSubnetGroupName { get; init; }
    public required Output<string> InitTaskFamily { get; init; }
}
