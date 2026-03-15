using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IComputeEnvironmentComponent
{
    IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network);
}
