using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface ITenantDataOutputs : IFileStorageOutputs
{
    Output<string> TenantSecretId { get; }
    Output<string> SmartStoreDataAccessPointId { get; }
    Output<string> SmartStoreConfigAccessPointId { get; }
    Output<string> SmartStoreDataProtectionAccessPointId { get; }
    Output<string> AppHostConfigAccessPointId { get; }
    Output<string> DatabaseName { get; }
}
