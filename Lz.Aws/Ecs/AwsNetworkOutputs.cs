using System.Collections.Immutable;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS-specific network outputs extending the cloud-agnostic INetworkOutputs.
/// Other AWS components can safely cast INetworkOutputs to this type since
/// the factory guarantees all components are from the same platform.
/// </summary>
public class AwsNetworkOutputs : INetworkOutputs
{
    // INetworkOutputs implementation (cloud-agnostic)
    public required Output<string> NetworkId { get; init; }
    public required Output<ImmutableArray<string>> PrivateSubnetIds { get; init; }
    public required Output<ImmutableArray<string>> PublicSubnetIds { get; init; }
    public required Output<string> PrivateDnsZoneId { get; init; }
    public required Output<string> PublicDnsZoneId { get; init; }

    // AWS-specific — ALBs
    public required Output<string> PublicAlbArn { get; init; }
    public required Output<string> InternalAlbArn { get; init; }
    public required Output<string> PublicAlbDns { get; init; }
    public required Output<string> PublicAlbZoneId { get; init; }
    public required Output<string> InternalAlbDns { get; init; }
    public required Output<string> InternalAlbZoneId { get; init; }
    public required Output<string> HttpsListenerArn { get; init; }
    public required Output<string> InternalHttpsListenerArn { get; init; }

    // AWS-specific — Security Groups
    public required Output<string> EcsPublicSecurityGroupId { get; init; }
    public required Output<string> EcsPrivateSecurityGroupId { get; init; }
    public required Output<string> AlbSecurityGroupId { get; init; }
    public required Output<string> InternalAlbSecurityGroupId { get; init; }
    public required Output<string> RdsSecurityGroupId { get; init; }
    public required Output<string> EfsSecurityGroupId { get; init; }
    public required Output<string> TailscaleSecurityGroupId { get; init; }

    // AWS-specific — Certificate
    public required Output<string> CertificateArn { get; init; }

    // AWS-specific — NAT Gateway (used for DependsOn by components needing outbound internet)
    public required Output<string> NatGatewayId { get; init; }

    // AWS-specific — NLB (for UDP media traffic, e.g., LiveKit)
    // Not required — may not exist in deployments without UDP services.
    public Output<string>? NlbArn { get; init; }
    public Output<string>? NlbDns { get; init; }
    public Output<string>? NlbZoneId { get; init; }
    public Output<string> NlbTcpTargetGroupArn { get; init; } = Output.Create("");
    public Output<string> NlbUdpTargetGroupArn { get; init; } = Output.Create("");
}
