using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface IDatabaseOutputs
{
    Output<string> Endpoint { get; }
    Output<int> Port { get; }
    Output<string> AdminSecretId { get; }
}
