using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface IComputeEnvironmentOutputs
{
    Output<string> ClusterId { get; }
    Output<string> PublicIngressEndpoint { get; }
    Output<string> InternalIngressEndpoint { get; }
}
