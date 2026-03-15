using Lz.Core.Interfaces.Outputs;
using Pulumi;

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
