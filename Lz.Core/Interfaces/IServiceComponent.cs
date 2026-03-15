using Lz.Core.Definitions;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface IServiceComponent
{
    IServiceOutputs Deploy(string serviceName, ServiceDefinition definition, INetworkOutputs network, IComputeEnvironmentOutputs compute, IDatabaseOutputs database, IFileStorageOutputs? fileStorage);
}
