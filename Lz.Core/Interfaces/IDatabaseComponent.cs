using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IDatabaseComponent
{
    IDatabaseOutputs Deploy(SystemConfig config, INetworkOutputs network);
}
