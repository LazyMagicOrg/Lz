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

    public void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network)
        => new AwsTenantDnsAndCertComponent().Deploy(tenantConfig, network);

    public ITailscaleComponent? CreateTailscale()
        => new AwsTailscaleAsgComponent();

    public async Task CleanupBeforeFoundationAsync()
    {
        // Clean up stale records from the old private zone if its name changed.
        // Pulumi can't delete a Route53 zone that has non-NS/SOA records created
        // by other stacks (e.g., tenant stack records like shop.{domain}).
        var expectedZoneName = $"{_config.SystemKey}.private";
        await AwsPrivateZoneCleanup.CleanupStalePrivateZoneAsync(
            _config.SystemKey, expectedZoneName, _config.Profile, _config.Region);
    }

    public IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsFoundationPostDeployAction(_config);

    public async Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig)
    {
        // Retrieve Tailscale API key from shared/system secret
        string? apiKey = null;
        try
        {
            var profile = _config.SharedProfile ?? _config.Profile;
            var region = _config.SharedRegion ?? _config.Region;
            var entries = await AwsAccountResolver.ReadSecretEntriesAsync(
                profile, region, "shared/system", "tailscale-api-key");
            entries.TryGetValue("tailscale-api-key", out apiKey);
        }
        catch { /* No Tailscale — skip silently */ }

        if (string.IsNullOrEmpty(apiKey))
            return;

        var vpcDnsResolver = Tailscale.AwsTailscalePostDeployAction.CalculateVpcDnsResolver(_config.VpcCidr);

        var domains = new List<string> { tenantConfig.RootDomain };
        if (tenantConfig.LegacyDomains != null)
            domains.AddRange(tenantConfig.LegacyDomains);

        // Add specific subdomains, NOT the entire domain. Routing the whole domain
        // through VPC DNS would intercept apex/wildcard queries (monrotest.click,
        // www.monrotest.click) and break public CloudFront access from VPN clients.
        // Only shop.{domain} and auth.{domain} need VPC DNS resolution.
        var splitDnsEntries = new Dictionary<string, string[]>();
        foreach (var d in domains)
        {
            splitDnsEntries[$"shop.{d}"] = [vpcDnsResolver];
            splitDnsEntries[$"auth.{d}"] = [vpcDnsResolver];
        }

        Console.WriteLine("Updating Tailscale split DNS for tenant domains...");
        foreach (var (domain, resolvers) in splitDnsEntries)
            Console.WriteLine($"  {domain} → {resolvers[0]}");

        using var client = new Tailscale.TailscaleApiClient(apiKey);
        await client.SetSplitDnsAsync(splitDnsEntries);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  Tenant split DNS updated.");
        Console.ResetColor();
    }

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
