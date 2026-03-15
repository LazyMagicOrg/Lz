using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

public interface ITenantDataComponent
{
    ITenantDataOutputs Deploy(TenantConfig tenantConfig, IFileStorageOutputs systemFileStorage, IDatabaseOutputs database);
}
