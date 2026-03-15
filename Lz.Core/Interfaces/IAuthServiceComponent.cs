using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IAuthServiceComponent
{
    IServiceOutputs Deploy(SystemConfig config, INetworkOutputs network, IComputeEnvironmentOutputs compute, IDatabaseOutputs database, IFileStorageOutputs fileStorage, bool enableAdminBlocking);
}
