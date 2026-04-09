using System.Collections.Immutable;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Network outputs for ECSExpress topology.
/// Minimal VPC with public subnets only + ALB. No private subnets, no NAT.
/// </summary>
public class AwsEcsExpressNetworkOutputs : INetworkOutputs
{
    public required Output<string> NetworkId { get; init; }
    public required Output<ImmutableArray<string>> PrivateSubnetIds { get; init; }
    public required Output<ImmutableArray<string>> PublicSubnetIds { get; init; }
    public required Output<string> PrivateDnsZoneId { get; init; }
    public required Output<string> PublicDnsZoneId { get; init; }

    // ECSExpress-specific
    public required Output<string> AlbArn { get; init; }
    public required Output<string> AlbDns { get; init; }
    public required Output<string> AlbZoneId { get; init; }
    public required Output<string> HttpsListenerArn { get; init; }
    public required Output<string> AlbSecurityGroupId { get; init; }
    public required Output<string> EcsTaskSecurityGroupId { get; init; }
    public required Output<string> CertificateArn { get; init; }
}
