using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface IServiceOutputs
{
    Output<string> ServiceId { get; }
    Output<string> Endpoint { get; }
}
