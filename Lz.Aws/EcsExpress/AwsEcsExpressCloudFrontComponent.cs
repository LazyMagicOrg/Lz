using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.CloudFront;
using Pulumi.Aws.CloudFront.Inputs;
using Pulumi.Aws.Route53.Inputs;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;
using Lz.Aws.Ecs; // AwsCloudFrontOutputs

namespace Lz.Aws.EcsExpress;

/// <summary>
/// CloudFront for ECSExpress topology.
/// Same KVS + CF Functions as AppRunner, but origin is ALB DNS name.
/// </summary>
public class AwsEcsExpressCloudFrontComponent : ComponentResource, ITenantCdnComponent
{
    public AwsEcsExpressCloudFrontComponent()
        : base("lz:aws:EcsExpressCloudFront", "cdn", ResourceArgs.Empty, null)
    {
    }

    public ICdnOutputs Deploy(TenantConfig tenantConfig, IComputeEnvironmentOutputs compute)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var prefix = $"{sk}-{tk}";
        var suffix = tenantConfig.TenantSuffix;
        var domain = tenantConfig.RootDomain;
        var cdn = tenantConfig.CDN ?? new CdnConfig();

        // =====================================================================
        // ACM CERTIFICATE (us-east-1)
        // =====================================================================

        var usEast1 = new Provider($"{prefix}-us-east-1", new ProviderArgs { Region = "us-east-1" },
            new CustomResourceOptions { Parent = this });

        var cert = new Certificate($"{prefix}-cdn-cert", new CertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = { $"*.{domain}" },
            ValidationMethod = "DNS",
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = domain });

        var validationRecord = new Pulumi.Aws.Route53.Record($"{prefix}-cdn-cert-validation",
            new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = publicZone.Apply(z => z.ZoneId),
                Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
                Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
                Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
                Ttl = 300, AllowOverwrite = true,
            }, new CustomResourceOptions { Parent = this });

        var certValidation = new CertificateValidation($"{prefix}-cdn-cert-validated",
            new CertificateValidationArgs
            {
                CertificateArn = cert.Arn,
                ValidationRecordFqdns = { validationRecord.Fqdn },
            }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        // =====================================================================
        // KVS + CF FUNCTIONS
        // =====================================================================

        var kvs = new KeyValueStore($"{prefix}-kvs", new KeyValueStoreArgs
        {
            Name = $"{prefix}-kvs",
            Comment = $"Config for {sk}/{tk} ({env})",
        }, new CustomResourceOptions { Parent = this });

        var authConfigFn = CreateFunctionFromFile(prefix, "authconfig", "CFAuthConfig.js",
            $"Auth config for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFAuthConfig.js")}");

        var requestFn = CreateFunctionFromFile(prefix, "request", "CFRequest.js",
            $"Request routing for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFRequest.js")}");

        // Auth callback function — no KVS needed, just redirects based on state param
        var authCallbackFn = CreateSimpleFunctionFromFile(prefix, "auth-callback", "CFAuthCallback.js",
            $"OAuth callback redirect for {domain}", tenantConfig.ConfigDirectory);

        // =====================================================================
        // S3 BUCKET + OAC
        // =====================================================================

        var bucketName = $"{prefix}-{suffix}-{env}-assets";
        var assetsBucket = new BucketV2($"{prefix}-assets-bucket", new BucketV2Args
        {
            Bucket = bucketName, ForceDestroy = env == "dev",
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        new BucketPublicAccessBlock($"{prefix}-assets-block", new BucketPublicAccessBlockArgs
        {
            Bucket = assetsBucket.Id, BlockPublicAcls = true, BlockPublicPolicy = true,
            IgnorePublicAcls = true, RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        var oac = new OriginAccessControl($"{prefix}-oac", new OriginAccessControlArgs
        {
            Name = $"{prefix}-oac", Description = $"OAC for {domain}",
            OriginAccessControlOriginType = "s3", SigningBehavior = "always", SigningProtocol = "sigv4",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ALIASES
        // =====================================================================

        var aliases = new InputList<string> { domain, $"*.{domain}" };
        if (tenantConfig.Subtenants != null)
            foreach (var sub in tenantConfig.Subtenants)
                if (!string.IsNullOrEmpty(sub.Value.SubDomain))
                    aliases.Add(sub.Value.SubDomain);

        // =====================================================================
        // CLOUDFRONT DISTRIBUTION — ALB origin + S3 origin
        // =====================================================================

        var distribution = new Distribution($"{prefix}-cf-dist", new DistributionArgs
        {
            Enabled = true, IsIpv6Enabled = true,
            Comment = $"{sk}/{tk} CDN ({env})",
            DefaultRootObject = cdn.DefaultRootObject ?? "app/index.html",
            PriceClass = cdn.PriceClass ?? "PriceClass_100",
            Aliases = aliases,
            Origins =
            {
                new DistributionOriginArgs
                {
                    OriginId = "s3-assets",
                    DomainName = assetsBucket.BucketRegionalDomainName,
                    OriginAccessControlId = oac.Id,
                },
                new DistributionOriginArgs
                {
                    OriginId = "alb-origin",
                    DomainName = $"origin.{domain}",
                    CustomOriginConfig = new DistributionOriginCustomOriginConfigArgs
                    {
                        HttpPort = 80, HttpsPort = 443,
                        OriginProtocolPolicy = "https-only",
                        OriginSslProtocols = { "TLSv1.2" },
                    },
                },
            },
            DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
            {
                TargetOriginId = "s3-assets",
                ViewerProtocolPolicy = "redirect-to-https",
                AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                CachedMethods = { "GET", "HEAD" },
                Compress = true,
                CachePolicyId = env == "dev"
                    ? "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"
                    : "658327ea-f89d-4fab-a63d-7e88639e58f6",
                FunctionAssociations =
                {
                    new DistributionDefaultCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-request", FunctionArn = requestFn.Arn,
                    },
                },
            },
            OrderedCacheBehaviors =
            {
                // OAuth callback — CFAuthCallback.js redirects to subtenant
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/oauth2/callback",
                    TargetOriginId = "s3-assets", // Won't reach S3 — function returns 302
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad", // CachingDisabled
                    FunctionAssociations = authCallbackFn != null ? new[]
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = authCallbackFn.Arn,
                        },
                    } : Array.Empty<DistributionOrderedCacheBehaviorFunctionAssociationArgs>(),
                },
                // OAuth logout callback — same redirect logic
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/oauth2/logout-callback",
                    TargetOriginId = "s3-assets",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",
                    FunctionAssociations = authCallbackFn != null ? new[]
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = authCallbackFn.Arn,
                        },
                    } : Array.Empty<DistributionOrderedCacheBehaviorFunctionAssociationArgs>(),
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/config",
                    TargetOriginId = "s3-assets",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = authConfigFn.Arn,
                        },
                    },
                },
                // API behavior — routes /*Api/* through CFRequest.js to ALB origin
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/*Api/*",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "https-only",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad", // CachingDisabled
                    OriginRequestPolicyId = "b689b0a8-53d0-40ab-baf2-68738e2966ac", // AllViewerExceptHostHeader
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = requestFn.Arn,
                        },
                    },
                },
            },
            CustomErrorResponses =
            {
                new DistributionCustomErrorResponseArgs { ErrorCode = 403, ResponseCode = 200, ResponsePagePath = "/index.html", ErrorCachingMinTtl = 10 },
                new DistributionCustomErrorResponseArgs { ErrorCode = 404, ResponseCode = 200, ResponsePagePath = "/index.html", ErrorCachingMinTtl = 10 },
            },
            ViewerCertificate = new DistributionViewerCertificateArgs
            {
                AcmCertificateArn = certValidation.CertificateArn,
                SslSupportMethod = "sni-only", MinimumProtocolVersion = "TLSv1.2_2021",
            },
            Restrictions = new DistributionRestrictionsArgs
            {
                GeoRestriction = new DistributionRestrictionsGeoRestrictionArgs { RestrictionType = "none" },
            },
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // S3 bucket policy for OAC
        // Use SourceAccount (not SourceArn) so dynamic origin rewriting works —
        // CFRequest.js switches origins to different buckets at runtime
        var accountId = Pulumi.Aws.GetCallerIdentity.Invoke(new Pulumi.Aws.GetCallerIdentityInvokeArgs());
        new BucketPolicy($"{prefix}-assets-policy", new BucketPolicyArgs
        {
            Bucket = assetsBucket.Id,
            Policy = Output.Tuple(assetsBucket.Arn, accountId.Apply(a => a.AccountId)).Apply(t =>
                $@"{{ ""Version"": ""2012-10-17"", ""Statement"": [{{ ""Sid"": ""AllowCloudFrontRead"", ""Effect"": ""Allow"", ""Principal"": {{ ""Service"": ""cloudfront.amazonaws.com"" }}, ""Action"": ""s3:GetObject"", ""Resource"": ""{t.Item1}/*"", ""Condition"": {{ ""StringEquals"": {{ ""AWS:SourceAccount"": ""{t.Item2}"" }} }} }}] }}"),
        }, new CustomResourceOptions { Parent = this });

        // Route 53 aliases
        var zoneId = publicZone.Apply(z => z.ZoneId);
        CreateAliasRecord($"{prefix}-cf-alias", zoneId, domain, distribution);
        CreateAliasRecord($"{prefix}-cf-alias-wildcard", zoneId, $"*.{domain}", distribution);
        if (tenantConfig.Subtenants != null)
            foreach (var sub in tenantConfig.Subtenants)
                if (!string.IsNullOrEmpty(sub.Value.SubDomain))
                    CreateAliasRecord($"{prefix}-cf-alias-{sub.Key}", zoneId, sub.Value.SubDomain, distribution);

        return new AwsCloudFrontOutputs(distribution.Id, distribution.DomainName, assetsBucket.Id, assetsBucket.Id);
    }

    /// <summary>
    /// Creates a simple CloudFront Function (no KVS association) from a JS file.
    /// </summary>
    private Pulumi.Aws.CloudFront.Function? CreateSimpleFunctionFromFile(
        string prefix, string name, string jsFileName, string comment, string configDirectory)
    {
        var jsPath = Path.Combine(configDirectory, "CloudFront", jsFileName);
        if (!File.Exists(jsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: {jsFileName} not found — OAuth callback redirect disabled.");
            Console.ResetColor();
            return null;
        }

        var jsCode = File.ReadAllText(jsPath);
        var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(jsCode);
        const int maxBytes = 10240;
        if (sizeBytes > maxBytes)
            throw new InvalidOperationException(
                $"CloudFront function '{jsFileName}' is {sizeBytes:N0} bytes — exceeds {maxBytes:N0} byte limit.");
        Console.WriteLine($"  CF function {jsFileName}: {sizeBytes:N0} / {maxBytes:N0} bytes");

        return new Pulumi.Aws.CloudFront.Function($"{prefix}-{name}-fn", new FunctionArgs
        {
            Runtime = "cloudfront-js-2.0",
            Code = jsCode,
            Comment = comment,
            Publish = true,
        }, new CustomResourceOptions { Parent = this });
    }

    private void CreateAliasRecord(string name, Output<string> zoneId, string recordName, Distribution dist)
    {
        new Pulumi.Aws.Route53.Record(name, new Pulumi.Aws.Route53.RecordArgs
        {
            ZoneId = zoneId, Name = recordName, Type = "A", AllowOverwrite = true,
            Aliases = { new RecordAliasArgs { Name = dist.DomainName, ZoneId = dist.HostedZoneId, EvaluateTargetHealth = false } },
        }, new CustomResourceOptions { Parent = this });
    }

    private Pulumi.Aws.CloudFront.Function? CreateFunctionFromFile(
        string prefix, string name, string jsFileName, string comment, string configDirectory, Output<string> kvsArn)
    {
        var jsPath = Path.Combine(configDirectory, "CloudFront", jsFileName);
        if (!File.Exists(jsPath)) return null;

        // Replace ${KvsArn} placeholder with actual KVS ARN (similar to SAM !Sub)
        var jsCode = kvsArn.Apply(arn =>
        {
            var code = File.ReadAllText(jsPath).Replace("${KvsArn}", arn);
            var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(code);
            const int maxBytes = 10240; // CloudFront Functions limit: 10 KB
            if (sizeBytes > maxBytes)
                throw new InvalidOperationException(
                    $"CloudFront function '{jsFileName}' is {sizeBytes:N0} bytes — exceeds {maxBytes:N0} byte limit. Remove comments or refactor.");
            Console.WriteLine($"  CF function {jsFileName}: {sizeBytes:N0} / {maxBytes:N0} bytes");
            return code;
        });

        return new Pulumi.Aws.CloudFront.Function($"{prefix}-{name}-fn", new FunctionArgs
        {
            Runtime = "cloudfront-js-2.0", Code = jsCode,
            Comment = comment, Publish = true, KeyValueStoreAssociations = { kvsArn },
        }, new CustomResourceOptions { Parent = this });
    }
}
