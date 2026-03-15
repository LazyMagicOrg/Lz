using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IEmailComponent
{
    IEmailOutputs Deploy(SystemConfig config, INetworkOutputs network);
}
