using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;
using Pulumi.Aws.SecretsManager;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS tenant data component — creates per-tenant EFS access points for
/// SmartStore data, SmartStore config, and AppHost config directories,
/// plus a Secrets Manager secret for tenant-scoped credentials.
/// </summary>
public class AwsTenantDataComponent : ComponentResource, ITenantDataComponent
{
    public AwsTenantDataComponent()
        : base("lz:aws:TenantData", "tenantdata", ResourceArgs.Empty, null)
    {
    }

    public ITenantDataOutputs Deploy(
        TenantConfig tenantConfig,
        IFileStorageOutputs systemFileStorage,
        IDatabaseOutputs database)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var prefix = $"{sk}-{tk}";
        var awsFileStorage = (Shared.AwsFileStorageOutputs)systemFileStorage;
        var ecs = tenantConfig.ECS ?? new EcsConfig();

        // =====================================================================
        // EFS ACCESS POINTS
        // =====================================================================

        // SmartStore data: product images, media, exports, etc.
        var ssDataPath = ecs.EfsSmartStoreDataPath ?? $"/{sk}-{tk}-{env}/smartstore-data";
        var ssDataAp = CreateAccessPoint($"{prefix}-ss-data", awsFileStorage.FileSystemId, ssDataPath);

        // SmartStore config: appsettings overrides, theme configs
        var ssConfigPath = ecs.EfsSmartStoreConfigPath ?? $"/{sk}-{tk}-{env}/smartstore-config";
        var ssConfigAp = CreateAccessPoint($"{prefix}-ss-config", awsFileStorage.FileSystemId, ssConfigPath);

        // SmartStore DataProtection keys: persisted across container restarts
        var ssDpPath = ecs.EfsSmartStoreDataProtectionPath ?? $"/{sk}-{tk}-{env}/smartstore-dataprotection";
        var ssDpAp = CreateAccessPoint($"{prefix}-ss-dp", awsFileStorage.FileSystemId, ssDpPath);

        // AppHost config: per-tenant runtime configuration
        var ahConfigPath = ecs.EfsAppHostConfigPath ?? $"/{sk}-{tk}-{env}/apphost-config";
        var ahConfigAp = CreateAccessPoint($"{prefix}-ah-config", awsFileStorage.FileSystemId, ahConfigPath);

        // =====================================================================
        // TENANT SECRET
        // =====================================================================

        var tenantSecret = new Secret($"{prefix}-tenant-secret", new SecretArgs
        {
            Name = $"{sk}/{tk}",
            Description = $"Tenant credentials for {sk}/{tk} ({env})",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions
        {
            Parent = this,
            RetainOnDelete = true, // Always retain — avoids AWS scheduled-deletion conflicts on recreate
            Protect = env is "prod" or "staging",
        });

        // Seed with empty JSON — post-deploy actions populate the actual values
        var tenantSecretVersion = new SecretVersion($"{prefix}-tenant-secret-version", new SecretVersionArgs
        {
            SecretId = tenantSecret.Id,
            SecretString = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["tenant-key"] = tk,
                ["environment"] = env,
            }),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // DATABASE NAME (convention-based, actual DB created by post-deploy)
        // =====================================================================

        var dbName = ecs.DatabaseName ?? $"{sk}_{tk}_{env}_smartstore";

        return new AwsTenantDataOutputs(
            fileSystemId: awsFileStorage.FileSystemId,
            tenantSecretId: tenantSecret.Id,
            smartStoreDataAccessPointId: ssDataAp.Id,
            smartStoreConfigAccessPointId: ssConfigAp.Id,
            smartStoreDataProtectionAccessPointId: ssDpAp.Id,
            appHostConfigAccessPointId: ahConfigAp.Id,
            databaseName: Output.Create(dbName));
    }

    private AccessPoint CreateAccessPoint(string name, Output<string> fileSystemId, string path)
    {
        return new AccessPoint($"{name}-ap", new AccessPointArgs
        {
            FileSystemId = fileSystemId,
            PosixUser = new AccessPointPosixUserArgs
            {
                Uid = 1000,
                Gid = 1000,
            },
            RootDirectory = new AccessPointRootDirectoryArgs
            {
                Path = path,
                CreationInfo = new AccessPointRootDirectoryCreationInfoArgs
                {
                    OwnerUid = 1000,
                    OwnerGid = 1000,
                    Permissions = "755",
                },
            },
            Tags = new InputMap<string>
            {
                { "Name", name },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });
    }

    private static InputMap<string> Tags(string systemKey, string tenantKey) => new()
    {
        { "System", systemKey },
        { "Tenant", tenantKey },
        { "Component", "tenant-data" },
        { "ManagedBy", "lz-pulumi" },
    };
}

internal class AwsTenantDataOutputs : ITenantDataOutputs
{
    public Output<string> FileSystemId { get; }
    public Output<string> TenantSecretId { get; }
    public Output<string> SmartStoreDataAccessPointId { get; }
    public Output<string> SmartStoreConfigAccessPointId { get; }
    public Output<string> SmartStoreDataProtectionAccessPointId { get; }
    public Output<string> AppHostConfigAccessPointId { get; }
    public Output<string> DatabaseName { get; }

    public AwsTenantDataOutputs(
        Output<string> fileSystemId,
        Output<string> tenantSecretId,
        Output<string> smartStoreDataAccessPointId,
        Output<string> smartStoreConfigAccessPointId,
        Output<string> smartStoreDataProtectionAccessPointId,
        Output<string> appHostConfigAccessPointId,
        Output<string> databaseName)
    {
        FileSystemId = fileSystemId;
        TenantSecretId = tenantSecretId;
        SmartStoreDataAccessPointId = smartStoreDataAccessPointId;
        SmartStoreConfigAccessPointId = smartStoreConfigAccessPointId;
        SmartStoreDataProtectionAccessPointId = smartStoreDataProtectionAccessPointId;
        AppHostConfigAccessPointId = appHostConfigAccessPointId;
        DatabaseName = databaseName;
    }
}
