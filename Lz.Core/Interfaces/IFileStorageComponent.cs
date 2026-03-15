using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IFileStorageComponent
{
    IFileStorageOutputs Deploy(SystemConfig config, INetworkOutputs network);
}
