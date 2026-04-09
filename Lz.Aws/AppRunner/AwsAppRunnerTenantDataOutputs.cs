using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Tenant data outputs for AppRunner topology.
/// Instead of EFS access points (ECS), this creates S3 buckets for tenant/subtenant assets.
/// </summary>
public class AwsAppRunnerTenantDataOutputs : ITenantDataOutputs
{
    // IFileStorageOutputs — not used for AppRunner but required by interface
    public required Output<string> FileSystemId { get; init; }

    // ITenantDataOutputs — stub values for SmartStore-specific fields
    public required Output<string> TenantSecretId { get; init; }
    public required Output<string> SmartStoreDataAccessPointId { get; init; }
    public required Output<string> SmartStoreConfigAccessPointId { get; init; }
    public required Output<string> SmartStoreDataProtectionAccessPointId { get; init; }
    public required Output<string> AppHostConfigAccessPointId { get; init; }
    public required Output<string> DatabaseName { get; init; }

    // AppRunner-specific — S3 bucket info for tenant assets
    public required Output<string> TenantAssetsBucketName { get; init; }
    public required Output<string> SystemAssetsBucketName { get; init; }
}
