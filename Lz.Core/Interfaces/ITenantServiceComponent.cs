using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface ITenantServiceComponent
{
    /// <param name="systemConfig">
    /// The system half of the config. Present because per-service settings are documented as
    /// tenant-overrides-system-overrides-default, and without it an implementation can only ever
    /// see the tenant half - which is precisely the bug this parameter was added to fix.
    /// </param>
    IServiceOutputs Deploy(string serviceName, ServiceDefinition definition, SystemConfig systemConfig, TenantConfig tenantConfig, INetworkOutputs network, IComputeEnvironmentOutputs compute, IDatabaseOutputs database, ITenantDataOutputs tenantData);
}
