using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// Tenant data outputs for the Cognito+DynamoDB topologies.
/// Instead of EFS access points (ECS), this creates S3 buckets for tenant/subtenant assets.
/// </summary>
public class AwsTenantDataOutputs : ITenantDataOutputs
{
    // IFileStorageOutputs — not used (no EFS) but required by the interface
    public required Output<string> FileSystemId { get; init; }

    // ITenantDataOutputs — stub values for SmartStore-specific fields
    public required Output<string> TenantSecretId { get; init; }
    public required Output<string> SmartStoreDataAccessPointId { get; init; }
    public required Output<string> SmartStoreConfigAccessPointId { get; init; }
    public required Output<string> SmartStoreDataProtectionAccessPointId { get; init; }
    public required Output<string> AppHostConfigAccessPointId { get; init; }
    public required Output<string> DatabaseName { get; init; }

    // S3 bucket info for tenant assets
    public required Output<string> TenantAssetsBucketName { get; init; }
    public required Output<string> SystemAssetsBucketName { get; init; }
}
