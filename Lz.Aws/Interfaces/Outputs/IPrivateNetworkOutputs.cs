using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Interfaces.Outputs;

/// <summary>
/// Network outputs a topology must expose to host the Tailscale subnet-router
/// ASG: the private subnets it launches into (inherited from INetworkOutputs),
/// the NAT gateway it depends on for egress, and the security group it attaches.
/// Implemented by every AWS network-outputs type that can carry Tailscale
/// (AwsNetworkOutputs, AwsEcsExpressNetworkOutputs) so AwsTailscaleAsgComponent
/// reads them through this contract instead of casting to one concrete type.
/// </summary>
public interface IPrivateNetworkOutputs : INetworkOutputs
{
    Output<string> NatGatewayId { get; }
    Output<string> TailscaleSecurityGroupId { get; }
}
