using System.Collections.Immutable;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Network outputs for ECSExpress topology.
/// Minimal VPC with public subnets only + ALB. No private subnets, no NAT.
/// </summary>
public class AwsEcsExpressNetworkOutputs : INetworkOutputs, IPrivateNetworkOutputs
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

    // Private-network (opt-in) — reflected from config.Aws().PrivateNetwork by the
    // network component / foundation lookup. Non-required with neutral defaults so
    // OFF deploys (and every existing construction site) are unaffected. Downstream
    // components read PrivateNetworking to branch (private placement / VPC origin)
    // without re-reading SystemConfig (which they never receive). PrivateSubnetIds
    // (above) carries the real ids when on; NatGatewayId lets components DependsOn
    // outbound egress.
    public bool PrivateNetworking { get; init; }
    public Output<string> NatGatewayId { get; init; } = Output.Create("");

    // Phase 2 Tailscale opt-in. Real SG id when PrivateNetwork.Tailscale is on;
    // "" otherwise. Consumed by AwsTailscaleAsgComponent (launch-template SG) via
    // the shared IPrivateNetworkOutputs contract.
    public Output<string> TailscaleSecurityGroupId { get; init; } = Output.Create("");
}
