using Lz.Aws.Interfaces.Outputs;
using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Aws.Interfaces;

/// <summary>
/// Deploys a gate-checker function (e.g., Lambda) that can verify
/// EFS data existence and database table existence from within the VPC.
/// </summary>
public interface IGateCheckerComponent
{
    IGateCheckerOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage);
}
