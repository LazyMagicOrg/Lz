using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// DynamoDB-based database outputs for the AppRunner topology.
/// DynamoDB uses IAM auth (no port/secrets like RDS).
/// </summary>
public class AwsAppRunnerDatabaseOutputs : IDatabaseOutputs
{
    public required Output<string> Endpoint { get; init; }
    public required Output<int> Port { get; init; }
    public required Output<string> AdminSecretId { get; init; }

    // AppRunner-specific — DynamoDB table ARNs for IAM policy
    public required Output<string> TableArnPrefix { get; init; }
}
