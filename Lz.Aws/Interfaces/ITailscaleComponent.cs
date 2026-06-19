using Lz.Aws.Interfaces.Outputs;
using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Aws.Interfaces;

/// <summary>
/// Component that deploys Tailscale subnet router instances.
/// Enables private network access to VPC resources via Tailscale mesh VPN.
/// </summary>
public interface ITailscaleComponent
{
    ITailscaleOutputs Deploy(SystemConfig config, INetworkOutputs network, IFileStorageOutputs fileStorage);
}
