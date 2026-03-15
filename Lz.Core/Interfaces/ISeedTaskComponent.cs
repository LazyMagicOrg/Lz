using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Core.Interfaces;

/// <summary>
/// Component that deploys infrastructure for seed data operations:
/// ECS task definition, ECR repository, and IAM roles for the
/// seeder container. The seeder mounts EFS, connects to RDS,
/// and transfers data to/from a shared S3 seed bucket.
/// </summary>
public interface ISeedTaskComponent
{
    ISeedTaskOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage);
}
