using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;
using Pulumi.Aws.SecretsManager;
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
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// AWS tenant data component — creates per-tenant EFS access points for
/// SmartStore data, SmartStore config, and AppHost config directories,
/// plus a Secrets Manager secret for tenant-scoped credentials.
/// </summary>
public class AwsFargateAlbTenantDataComponent : ComponentResource, ITenantDataComponent
{
    public AwsFargateAlbTenantDataComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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
        var ecs = tenantConfig.Aws().ECS ?? new EcsConfig();

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
        // MEDIA S3 BUCKET
        // =====================================================================
        // Private per-tenant bucket backing the Smartstore.AmazonS3 media storage
        // provider (product images, downloads, attachments). Accessed directly by
        // the SmartStore ECS task role — not a CloudFront origin, so no OAC or
        // bucket policy. Naming: {sk}-{tk}-{stk}-media--{suffix} (stk empty for now,
        // producing a double dash, consistent with the webapp/explore/park buckets).

        var stk = ""; // subtenantkey — empty for now (double dash in bucket name)
        var mediaBucketName = tenantConfig.MediaBucket
            ?? $"{prefix}-{stk}-media--{tenantConfig.TenantSuffix}";

        var mediaBucket = new Pulumi.Aws.S3.BucketV2($"{prefix}-media-bucket", new Pulumi.Aws.S3.BucketV2Args
        {
            Bucket = mediaBucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this });

        new Pulumi.Aws.S3.BucketPublicAccessBlock($"{prefix}-media-block", new Pulumi.Aws.S3.BucketPublicAccessBlockArgs
        {
            Bucket = mediaBucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        new Pulumi.Aws.S3.BucketServerSideEncryptionConfigurationV2($"{prefix}-media-sse",
            new Pulumi.Aws.S3.BucketServerSideEncryptionConfigurationV2Args
            {
                Bucket = mediaBucket.Id,
                Rules =
                {
                    new Pulumi.Aws.S3.Inputs.BucketServerSideEncryptionConfigurationV2RuleArgs
                    {
                        ApplyServerSideEncryptionByDefault =
                            new Pulumi.Aws.S3.Inputs.BucketServerSideEncryptionConfigurationV2RuleApplyServerSideEncryptionByDefaultArgs
                            {
                                SseAlgorithm = "AES256",
                            },
                    },
                },
            }, new CustomResourceOptions { Parent = this });

        new Pulumi.Aws.S3.BucketVersioningV2($"{prefix}-media-versioning", new Pulumi.Aws.S3.BucketVersioningV2Args
        {
            Bucket = mediaBucket.Id,
            VersioningConfiguration = new Pulumi.Aws.S3.Inputs.BucketVersioningV2VersioningConfigurationArgs
            {
                Status = "Enabled",
            },
        }, new CustomResourceOptions { Parent = this });

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

        // Seed-only secret version. Pulumi WRITES this JSON on initial creation
        // (so the field shape is in place from day one), then NEVER overwrites
        // the secret's contents on subsequent deploys. After first deploy, the
        // secret is "owned" by the operator — populate via
        // `aws secretsmanager put-secret-value` and restart the tenant ECS task
        // to pick up new env vars.
        //
        // IgnoreChanges = { "secretString" } is what enforces the seed-once
        // semantics: without it, every `pulumi up` would diff the literal
        // placeholder dict below against AWS's current encrypted value, see a
        // change, and push the placeholders back — silently destroying any
        // operator-set credentials (which is exactly what bit us once).
        //
        // Consequences operators should know:
        //   - Editing/adding fields in this dict only affects FRESH tenants.
        //     Existing tenants keep whatever secret payload they already have.
        //     If you add a new field here, operators must manually
        //     `put-secret-value` to add it to existing tenants' secrets.
        //   - If a secret is recreated (e.g. deleted in AWS console), this
        //     seed runs again and operator-set values are lost. Don't delete.
        var tenantSecretVersion = new SecretVersion($"{prefix}-tenant-secret-version", new SecretVersionArgs
        {
            SecretId = tenantSecret.Id,
            SecretString = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["tenant-key"] = tk,
                ["environment"] = env,
                // GitHub App credentials for the in-container Hugo build
                // service (Smartstore.ExplorePages.HugoBuildService /
                // StaticSiteRepository). The org disables Deploy Keys by
                // policy, so we authenticate via a GitHub App installed on
                // monromeadows/StaticSite with Contents:Read. The container
                // signs a JWT with the App's RSA private key and exchanges
                // it at /app/installations/{id}/access_tokens for a 1-hour
                // installation token used as Basic-auth credentials on
                // git-over-HTTPS clones. Operators replace the placeholders
                // post-deploy via `aws secretsmanager put-secret-value`,
                // then restart the tenant ECS task to pick up the new
                // LZ_GITHUB_APP_* env vars. The private key value must be
                // the full PEM including the BEGIN/END RSA PRIVATE KEY
                // lines (newlines preserved via JSON `\n` escaping).
                ["github-app-id"] = "PLACEHOLDER_SET_VIA_AWS_CLI",
                ["github-app-installation-id"] = "PLACEHOLDER_SET_VIA_AWS_CLI",
                ["github-app-private-key"] = "PLACEHOLDER_SET_VIA_AWS_CLI",
            }),
        }, new CustomResourceOptions
        {
            Parent = this,
            IgnoreChanges = { "secretString" },
        });

        // =====================================================================
        // DATABASE NAME (convention-based, actual DB created by post-deploy)
        // =====================================================================

        var dbName = ecs.DatabaseName ?? $"{sk}_{tk}_{env}_smartstore";

        return new AwsFargateAlbTenantDataOutputs(
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

internal class AwsFargateAlbTenantDataOutputs : ITenantDataOutputs
{
    public Output<string> FileSystemId { get; }
    public Output<string> TenantSecretId { get; }
    public Output<string> SmartStoreDataAccessPointId { get; }
    public Output<string> SmartStoreConfigAccessPointId { get; }
    public Output<string> SmartStoreDataProtectionAccessPointId { get; }
    public Output<string> AppHostConfigAccessPointId { get; }
    public Output<string> DatabaseName { get; }

    public AwsFargateAlbTenantDataOutputs(
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
