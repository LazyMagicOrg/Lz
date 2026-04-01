using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Lambda;
using Lz.Aws.Tailscale;

namespace Lz.Aws.Ecs;



/// <summary>
/// Platform factory for AWS ECS + ALB topology.
/// Creates AWS-specific component implementations.
/// </summary>
public class AwsEcsPlatformFactory : IPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsEcsPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    public ISystemNetworkComponent CreateNetwork()
        => new AwsEcsNetworkComponent();
    public IDatabaseComponent CreateDatabase()
        => new AwsRdsComponent();
    public IFileStorageComponent CreateFileStorage()
        => new AwsEfsComponent();
    public IComputeEnvironmentComponent CreateComputeEnvironment()
        => new AwsEcsClusterComponent();
    public IServiceComponent CreateService()
        => new AwsEcsServiceComponent(_config);
    public IAuthServiceComponent CreateAuthService()
        => new AwsKeycloakEcsComponent();
    public IEmailComponent CreateEmail()
        => new AwsSesComponent();
    public ITenantCdnComponent CreateTenantCdn()
        => new AwsCloudFrontComponent();
    public ITenantDataComponent CreateTenantData()
        => new AwsTenantDataComponent();
    public ITenantServiceComponent CreateTenantService()
        => new AwsEcsTenantServiceComponent();

    public ITailscaleComponent? CreateTailscale()
        => new AwsTailscaleAsgComponent();

    public IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsFoundationPostDeployAction(_config);

    public IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null)
        => new AwsTailscalePostDeployAction(_config, system);

    public ITailscaleKeyManager? GetTailscaleKeyManager()
        => new AwsTailscalePostDeployAction(_config);

    public ITenantKeycloakSeeder? GetTenantKeycloakSeeder()
        => new AwsTenantKeycloakSeeder(_config);

    public IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system)
    {
        var foundationServices = system.FoundationLayerServices;
        if (foundationServices.Count == 0 || !foundationServices.Any(s => s.Docker != null))
            return null;
        return new AwsServicesPostDeployAction(_config, system, foundationServices);
    }

    public IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsServicesPostDeployAction(_config, system, services, tenantKey, tenantConfig);

    public IConfigInitRunner? GetConfigInitRunner()
        => new AwsLambdaConfigInitRunner(_config);

    public IPostSeedRunner? GetPostSeedRunner()
        => new AwsLambdaPostSeedRunner(_config);

    public IAdminSetupRunner? GetAdminSetupRunner()
        => new AwsLambdaAdminSetupRunner(_config);

    public ITransitionChecker CreateTransitionChecker()
        => new AwsTransitionChecker(_config);

    public IGateCheckerComponent? CreateGateChecker()
        => new AwsGateCheckerLambdaComponent();

    public ISeedTaskComponent? CreateSeedTask()
        => _config.SeedData != null ? new AwsSeedTaskComponent() : null;

    public string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey)
    {
        var bucketName = sharedConfig.SeedData?.Bucket
            ?? $"{systemKey}--seeddata-{sharedConfig.SharedSuffix}";
        var region = sharedConfig.SeedData?.Region ?? sharedConfig.Region;

        var bucket = new Pulumi.Aws.S3.BucketV2($"{systemKey}-seeddata-bucket", new Pulumi.Aws.S3.BucketV2Args
        {
            Bucket = bucketName,
            Tags =
            {
                { "Name", bucketName },
                { "System", systemKey },
                { "ManagedBy", "lz-pulumi" },
                { "Purpose", "seed-data" },
            },
        });

        // Block public access
        new Pulumi.Aws.S3.BucketPublicAccessBlock($"{systemKey}-seeddata-block", new Pulumi.Aws.S3.BucketPublicAccessBlockArgs
        {
            Bucket = bucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        });

        // Enable versioning for safety
        new Pulumi.Aws.S3.BucketVersioningV2($"{systemKey}-seeddata-versioning", new Pulumi.Aws.S3.BucketVersioningV2Args
        {
            Bucket = bucket.Id,
            VersioningConfiguration = new Pulumi.Aws.S3.Inputs.BucketVersioningV2VersioningConfigurationArgs
            {
                Status = "Enabled",
            },
        });

        // Enable SSE-S3 encryption
        new Pulumi.Aws.S3.BucketServerSideEncryptionConfigurationV2($"{systemKey}-seeddata-sse",
            new Pulumi.Aws.S3.BucketServerSideEncryptionConfigurationV2Args
        {
            Bucket = bucket.Id,
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
        });

        // Cross-account bucket policy — grant trusted accounts access
        if (sharedConfig.TrustedAccountIds.Count > 0)
        {
            var principals = string.Join(",",
                sharedConfig.TrustedAccountIds.Select(id => $"\"arn:aws:iam::{id}:root\""));

            new Pulumi.Aws.S3.BucketPolicy($"{systemKey}-seeddata-policy", new Pulumi.Aws.S3.BucketPolicyArgs
            {
                Bucket = bucket.Id,
                Policy = bucket.Arn.Apply(arn => $@"{{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{{
    ""Sid"": ""AllowTrustedAccountAccess"",
    ""Effect"": ""Allow"",
    ""Principal"": {{
      ""AWS"": [{principals}]
    }},
    ""Action"": [
      ""s3:GetObject"",
      ""s3:PutObject"",
      ""s3:ListBucket"",
      ""s3:GetBucketLocation"",
      ""s3:DeleteObject""
    ],
    ""Resource"": [
      ""{arn}"",
      ""{arn}/*""
    ]
  }}]
}}"),
            });
        }

        return bucketName;
    }

    public (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsFoundationLookup.Lookup(config);
}
