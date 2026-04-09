using System.Collections.Immutable;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// AWS AppRunner network outputs.
/// No VPC — AppRunner is fully serverless. Only Route 53 and ACM cert
/// are needed for domain management and CloudFront HTTPS.
/// INetworkOutputs fields that don't apply return empty stubs.
/// </summary>
public class AwsAppRunnerNetworkOutputs : INetworkOutputs
{
    // INetworkOutputs — stubs (no VPC in AppRunner topology)
    public required Output<string> NetworkId { get; init; }
    public required Output<ImmutableArray<string>> PrivateSubnetIds { get; init; }
    public required Output<ImmutableArray<string>> PublicSubnetIds { get; init; }
    public required Output<string> PrivateDnsZoneId { get; init; }
    public required Output<string> PublicDnsZoneId { get; init; }

    // AppRunner-specific — only what's actually needed
    public required Output<string> CertificateArn { get; init; }
}
