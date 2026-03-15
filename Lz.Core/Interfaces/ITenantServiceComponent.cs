using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface ITenantServiceComponent
{
    IServiceOutputs Deploy(string serviceName, ServiceDefinition definition, TenantConfig tenantConfig, INetworkOutputs network, IComputeEnvironmentOutputs compute, IDatabaseOutputs database, ITenantDataOutputs tenantData);
}
