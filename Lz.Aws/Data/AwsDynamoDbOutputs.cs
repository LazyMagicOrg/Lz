using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// DynamoDB-based database outputs for the Cognito+DynamoDB topologies.
/// DynamoDB uses IAM auth (no port/secrets like RDS).
/// </summary>
public class AwsDynamoDbOutputs : IDatabaseOutputs
{
    public required Output<string> Endpoint { get; init; }
    public required Output<int> Port { get; init; }
    public required Output<string> AdminSecretId { get; init; }

    // DynamoDB table ARNs for IAM policy
    public required Output<string> TableArnPrefix { get; init; }
}
