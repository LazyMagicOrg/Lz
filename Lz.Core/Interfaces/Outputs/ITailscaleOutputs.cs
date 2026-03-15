using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

/// <summary>
/// Outputs from the Tailscale subnet router deployment.
/// </summary>
public interface ITailscaleOutputs
{
    Output<string> AutoScalingGroupId { get; }
}
