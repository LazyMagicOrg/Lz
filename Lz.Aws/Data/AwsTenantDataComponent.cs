using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;
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
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// Per-tenant data component for the Cognito+DynamoDB topologies.
/// Creates S3 buckets for tenant/subtenant assets and a Secrets Manager secret
/// for tenant-specific credentials and configuration.
/// Replaces EFS access points used in the ECS topology.
/// </summary>
public class AwsTenantDataComponent : ComponentResource, ITenantDataComponent
{
    public AwsTenantDataComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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
        // RecoveryWindowInDays = 0 on non-prod skips the 7–30-day AWS
        // deletion-window tombstone, so `destroytenant` followed by
        // `deploytenant` with the same name doesn't hit
        // "InvalidRequestException: already scheduled for deletion".
        // Prod/staging keep the default 30-day window as a safety net.
        var recoveryWindow = env is "prod" or "staging" ? 30 : 0;
        var tenantSecret = new Secret($"{prefix}-secret", new SecretArgs
        {
            Name = secretPrefix,
            Description = $"Tenant credentials for {sk}/{tk} ({env})",
            RecoveryWindowInDays = recoveryWindow,
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // Seed the secret body. Default is the historical empty document "{}".
        // When the BFF is enabled for this tenant (additive, opt-in), seed the
        // confidential client id/secret as JSON keys so the BFF server can read
        // them via its secret-based path. The id/secret are pulled from the
        // FOUNDATION stack outputs (the Cognito pools are system-scoped) via a
        // StackReference — created ONLY on the BFF path, so a non-BFF tenant
        // still seeds "{}" exactly as before.
        Input<string> secretBody = "{}";
        if (BffWiring.IsEnabled(tenantConfig))
        {
            var pool = BffWiring.ResolvePool(tenantConfig);
            var foundation = new BffStackOutputs(tenantConfig, this);
            secretBody = Output.Tuple(foundation.ClientId(pool), foundation.ClientSecret(pool))
                .Apply(t => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["BffClientId"] = t.Item1,
                    ["BffClientSecret"] = t.Item2,
                }));
        }

        new SecretVersion($"{prefix}-secret-init", new SecretVersionArgs
        {
            SecretId = tenantSecret.Id,
            SecretString = secretBody,
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

        // Subtenant asset buckets are NOT provisioned via Pulumi. They are
        // created imperatively by SubtenantBucketManager — invoked by
        // `lz deploytenant` post-deploy and by `lz deploysubtenants`. This
        // decoupling lets operators add subtenants without a Pulumi run.
        // See Design/Subtenants.md for the split.

        // Webapp buckets are created on-demand by `lz deploywebapp` (not here).
        // Webapps can be system-level or tenant-level, so bucket creation
        // belongs in the deploywebapp command which knows the naming convention.

        return new AwsTenantDataOutputs
        {
            // Stub values for SmartStore-specific fields (not used in BCProjects)
            FileSystemId = Output.Create(""),
            TenantSecretId = tenantSecret.Id,
            SmartStoreDataAccessPointId = Output.Create(""),
            SmartStoreConfigAccessPointId = Output.Create(""),
            SmartStoreDataProtectionAccessPointId = Output.Create(""),
            AppHostConfigAccessPointId = Output.Create(""),
            DatabaseName = Output.Create(""),

            // S3-lineage-specific
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
