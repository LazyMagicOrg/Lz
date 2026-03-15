using Pulumi;
using System.Collections.Immutable;

namespace Lz.Core.Interfaces.Outputs;

public interface INetworkOutputs
{
    Output<string> NetworkId { get; }
    Output<ImmutableArray<string>> PrivateSubnetIds { get; }
    Output<ImmutableArray<string>> PublicSubnetIds { get; }
    Output<string> PrivateDnsZoneId { get; }
    Output<string> PublicDnsZoneId { get; }
}
