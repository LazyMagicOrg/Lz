using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;
using Pulumi.Aws.SecretsManager;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Per-tenant data component for AppRunner topology.
/// Creates S3 buckets for tenant/subtenant assets and a Secrets Manager secret
/// for tenant-specific credentials and configuration.
/// Replaces EFS access points used in the ECS topology.
/// </summary>
public class AwsAppRunnerTenantDataComponent : ComponentResource, ITenantDataComponent
{
    public AwsAppRunnerTenantDataComponent()
        : base("lz:aws:AppRunnerTenantData", "tenant-data", ResourceArgs.Empty, null)
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
        var suffix = tenantConfig.TenantSuffix;
        var prefix = $"{sk}-{tk}";

        // =====================================================================
        // SECRETS MANAGER — tenant secret for credentials/config
        // =====================================================================

        var secretPrefix = tenantConfig.SecretsManager?.SecretPrefix ?? $"{sk}/{tk}";
        var tenantSecret = new Secret($"{prefix}-secret", new SecretArgs
        {
            Name = secretPrefix,
            Description = $"Tenant credentials for {sk}/{tk} ({env})",
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        new SecretVersion($"{prefix}-secret-init", new SecretVersionArgs
        {
            SecretId = tenantSecret.Id,
            SecretString = "{}",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // S3 BUCKETS — tenant and system assets
        // =====================================================================

        // Bucket naming convention (matches CloudFront Function + LzAws):
        //   System:    {sk}---assets-{ss}                e.g., bcs---assets-4543-a317
        //   Tenant:    {sk}-{tk}--assets-{ts}            e.g., bcs-bcs--assets-4543-a317
        //   Subtenant: {sk}-{tk}-{stk}-assets-{sts}      e.g., bcs-bcs-cerulean-assets-4543-a317

        var systemBucketName = $"{sk}---assets-{suffix}";
        var systemBucket = new BucketV2($"{prefix}-system-assets", new BucketV2Args
        {
            Bucket = systemBucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk, "system-assets"),
        }, new CustomResourceOptions { Parent = this });
        BlockPublicAccess($"{prefix}-system-assets-block", systemBucket);

        var tenantBucketName = $"{sk}-{tk}--assets-{suffix}";
        var tenantBucket = new BucketV2($"{prefix}-tenant-assets", new BucketV2Args
        {
            Bucket = tenantBucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk, "tenant-assets"),
        }, new CustomResourceOptions { Parent = this });
        BlockPublicAccess($"{prefix}-tenant-assets-block", tenantBucket);

        // Subtenant asset buckets
        if (tenantConfig.Subtenants != null)
        {
            foreach (var sub in tenantConfig.Subtenants)
            {
                var stk = sub.Key;
                var subBucketName = $"{sk}-{tk}-{stk}-assets-{suffix}";
                var subBucket = new BucketV2($"{prefix}-{stk}-assets", new BucketV2Args
                {
                    Bucket = subBucketName,
                    ForceDestroy = env == "dev",
                    Tags = Tags(sk, tk, $"{stk}-assets"),
                }, new CustomResourceOptions { Parent = this });
                BlockPublicAccess($"{prefix}-{stk}-assets-block", subBucket);
            }
        }

        // Webapp buckets are created on-demand by `lz deploywebapp` (not here).
        // Webapps can be system-level or tenant-level, so bucket creation
        // belongs in the deploywebapp command which knows the naming convention.

        return new AwsAppRunnerTenantDataOutputs
        {
            // Stub values for SmartStore-specific fields (not used in BCProjects)
            FileSystemId = Output.Create(""),
            TenantSecretId = tenantSecret.Id,
            SmartStoreDataAccessPointId = Output.Create(""),
            SmartStoreConfigAccessPointId = Output.Create(""),
            SmartStoreDataProtectionAccessPointId = Output.Create(""),
            AppHostConfigAccessPointId = Output.Create(""),
            DatabaseName = Output.Create(""),

            // AppRunner-specific
            TenantAssetsBucketName = Output.Create(tenantBucketName),
            SystemAssetsBucketName = Output.Create(systemBucketName),
        };
    }

    private void BlockPublicAccess(string name, BucketV2 bucket)
    {
        new BucketPublicAccessBlock(name, new BucketPublicAccessBlockArgs
        {
            Bucket = bucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        // Allow CloudFront OAC access — SourceAccount so dynamic origin rewriting works
        var accountId = Pulumi.Aws.GetCallerIdentity.Invoke(new Pulumi.Aws.GetCallerIdentityInvokeArgs());
        new BucketPolicy($"{name}-policy", new BucketPolicyArgs
        {
            Bucket = bucket.Id,
            Policy = Pulumi.Output.Tuple(bucket.Arn, accountId.Apply(a => a.AccountId)).Apply(t =>
                $@"{{ ""Version"": ""2012-10-17"", ""Statement"": [{{ ""Sid"": ""AllowCloudFrontRead"", ""Effect"": ""Allow"", ""Principal"": {{ ""Service"": ""cloudfront.amazonaws.com"" }}, ""Action"": ""s3:GetObject"", ""Resource"": ""{t.Item1}/*"", ""Condition"": {{ ""StringEquals"": {{ ""AWS:SourceAccount"": ""{t.Item2}"" }} }} }}] }}"),
        }, new CustomResourceOptions { Parent = this });
    }

    private static InputMap<string> Tags(string sk, string tk, string component) => new()
    {
        { "System", sk },
        { "Tenant", tk },
        { "Component", component },
        { "ManagedBy", "lz-pulumi" },
    };
}
