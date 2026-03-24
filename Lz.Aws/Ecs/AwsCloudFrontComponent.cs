using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.Acm.Inputs;
using Pulumi.Aws.CloudFront;
using Pulumi.Aws.CloudFront.Inputs;
using Pulumi.Aws.Route53;
using Pulumi.Aws.Route53.Inputs;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS CloudFront + S3 component for per-tenant CDN distribution.
/// Creates an S3 bucket for static assets (WASM, images), an Origin Access Control (OAC),
/// a CloudFront distribution with behavior routing (/api/* → ALB, default → S3),
/// and Route 53 alias records for the tenant domain.
/// </summary>
public class AwsCloudFrontComponent : ComponentResource, ITenantCdnComponent
{
    public AwsCloudFrontComponent()
        : base("lz:aws:CloudFront", "cdn", ResourceArgs.Empty, null)
    {
    }

    public ICdnOutputs Deploy(
        TenantConfig tenantConfig,
        IComputeEnvironmentOutputs compute)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var prefix = $"{sk}-{tk}";
        var suffix = tenantConfig.TenantSuffix;
        var domain = tenantConfig.RootDomain;
        var cdn = tenantConfig.CDN ?? new CdnConfig();

        // =====================================================================
        // ACM CERTIFICATE (us-east-1 — required by CloudFront)
        // =====================================================================

        var usEast1 = new Provider($"{prefix}-us-east-1", new ProviderArgs
        {
            Region = "us-east-1",
        }, new CustomResourceOptions { Parent = this });

        var cert = new Certificate($"{prefix}-cdn-cert", new CertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = { $"*.{domain}" },
            ValidationMethod = "DNS",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        // DNS validation records
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = domain });

        var validationRecord = new Pulumi.Aws.Route53.Record($"{prefix}-cdn-cert-validation", new Pulumi.Aws.Route53.RecordArgs
        {
            ZoneId = publicZone.Apply(z => z.ZoneId),
            Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
            Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
            Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
            Ttl = 300,
            AllowOverwrite = true,
        }, new CustomResourceOptions { Parent = this });

        var certValidation = new CertificateValidation($"{prefix}-cdn-cert-validated",
            new CertificateValidationArgs
            {
                CertificateArn = cert.Arn,
                ValidationRecordFqdns = { validationRecord.Fqdn },
            }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        var certificateId = certValidation.CertificateArn;

        // =====================================================================
        // S3 BUCKET FOR STATIC ASSETS
        // =====================================================================

        var bucketName = $"{prefix}-{suffix}-{env}-assets";
        var assetsBucket = new BucketV2($"{prefix}-assets-bucket", new BucketV2Args
        {
            Bucket = bucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this });

        // Block all public access — CloudFront uses OAC
        var publicAccessBlock = new BucketPublicAccessBlock($"{prefix}-assets-block", new BucketPublicAccessBlockArgs
        {
            Bucket = assetsBucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ORIGIN ACCESS CONTROL (OAC)
        // =====================================================================

        var oac = new OriginAccessControl($"{prefix}-oac", new OriginAccessControlArgs
        {
            Name = $"{prefix}-oac",
            Description = $"OAC for {domain} assets",
            OriginAccessControlOriginType = "s3",
            SigningBehavior = "always",
            SigningProtocol = "sigv4",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // CLOUDFRONT DISTRIBUTION
        // =====================================================================

        // Collect aliases: root domain + wildcard + subtenant domains
        var aliases = new InputList<string> { domain, $"*.{domain}" };
        if (tenantConfig.Subtenants != null)
        {
            foreach (var sub in tenantConfig.Subtenants)
            {
                if (!string.IsNullOrEmpty(sub.Value.SubDomain))
                    aliases.Add(sub.Value.SubDomain);
            }
        }

        var distribution = new Distribution($"{prefix}-cf-dist", new DistributionArgs
        {
            Enabled = true,
            IsIpv6Enabled = true,
            Comment = $"{sk}/{tk} CDN ({env})",
            DefaultRootObject = cdn.DefaultRootObject ?? "app/index.html",
            PriceClass = cdn.PriceClass ?? "PriceClass_100",
            Aliases = aliases,

            // S3 origin for static assets + ALB origin for API/media paths
            // When PrivateLink is unavailable (cross-region), a separate origin
            // routes auth paths directly to the shared Keycloak public ALB.
            Origins = BuildOrigins(tenantConfig, assetsBucket, oac, domain),

            // Default behavior → S3 (WASM app)
            DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
            {
                TargetOriginId = "s3-assets",
                ViewerProtocolPolicy = "redirect-to-https",
                AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                CachedMethods = { "GET", "HEAD" },
                Compress = true,
                CachePolicyId = "658327ea-f89d-4fab-a63d-7e88639e58f6", // CachingOptimized
            },

            // Auth behaviors → Keycloak (via PrivateLink through ALB, or direct to shared ALB)
            // Same-region: /realms/* etc. go through tenant ALB → PrivateLink → shared Keycloak
            // Cross-region: /realms/* etc. go directly to shared public ALB (CloudFront is global)
            // AllViewer origin request policy forwards the Host header (e.g., harmova.life)
            // so Keycloak uses the tenant domain as the token issuer.
            OrderedCacheBehaviors =
            {
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/realms/*",
                    TargetOriginId = AuthOriginId(tenantConfig),
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/resources/*",
                    TargetOriginId = AuthOriginId(tenantConfig),
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/js/*",
                    TargetOriginId = AuthOriginId(tenantConfig),
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                // SmartStore media → ALB origin (product images, thumbnails, etc.)
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/media/*",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = env == "dev"
                        ? "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"        // CachingDisabled (dev)
                        : "658327ea-f89d-4fab-a63d-7e88639e58f6",        // CachingOptimized
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                // AppHost API behaviors → ALB origin
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/api/*",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/AppApi/*",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/config",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/health",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
            },

            // SPA fallback: return index.html for missing S3 objects so Blazor
            // client-side routing handles paths like /authentication/login-callback
            CustomErrorResponses =
            {
                new DistributionCustomErrorResponseArgs
                {
                    ErrorCode = 403,
                    ResponseCode = 200,
                    ResponsePagePath = "/index.html",
                    ErrorCachingMinTtl = 10,
                },
                new DistributionCustomErrorResponseArgs
                {
                    ErrorCode = 404,
                    ResponseCode = 200,
                    ResponsePagePath = "/index.html",
                    ErrorCachingMinTtl = 10,
                },
            },

            ViewerCertificate = new DistributionViewerCertificateArgs
            {
                AcmCertificateArn = certificateId,
                SslSupportMethod = "sni-only",
                MinimumProtocolVersion = "TLSv1.2_2021",
            },

            Restrictions = new DistributionRestrictionsArgs
            {
                GeoRestriction = new DistributionRestrictionsGeoRestrictionArgs
                {
                    RestrictionType = "none",
                },
            },

            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // S3 BUCKET POLICY — allow CloudFront OAC access
        // =====================================================================

        var bucketPolicy = new BucketPolicy($"{prefix}-assets-policy", new BucketPolicyArgs
        {
            Bucket = assetsBucket.Id,
            Policy = Output.Tuple(assetsBucket.Arn, distribution.Arn).Apply(t =>
                $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Sid"": ""AllowCloudFrontServicePrincipal"",
                        ""Effect"": ""Allow"",
                        ""Principal"": {{
                            ""Service"": ""cloudfront.amazonaws.com""
                        }},
                        ""Action"": ""s3:GetObject"",
                        ""Resource"": ""{t.Item1}/*"",
                        ""Condition"": {{
                            ""StringEquals"": {{
                                ""AWS:SourceArn"": ""{t.Item2}""
                            }}
                        }}
                    }}]
                }}"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ROUTE 53 ALIAS RECORDS
        // =====================================================================

        // Reuse the public hosted zone lookup from cert validation
        {
            var zoneId = publicZone.Apply(z => z.ZoneId);

            // Root domain → CloudFront (overrides any prior ALB record from foundation)
            var rootAlias = new Record($"{prefix}-cf-alias", new RecordArgs
            {
                ZoneId = zoneId,
                Name = domain,
                Type = "A",
                AllowOverwrite = true,
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = distribution.DomainName,
                        ZoneId = distribution.HostedZoneId,
                        EvaluateTargetHealth = false,
                    },
                },
            }, new CustomResourceOptions { Parent = this });

            // Wildcard → CloudFront (ensures browsers using public DNS can still
            // resolve subdomains like shop.{domain} that also have private-zone records)
            new Record($"{prefix}-cf-alias-wildcard", new RecordArgs
            {
                ZoneId = zoneId,
                Name = $"*.{domain}",
                Type = "A",
                AllowOverwrite = true,
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = distribution.DomainName,
                        ZoneId = distribution.HostedZoneId,
                        EvaluateTargetHealth = false,
                    },
                },
            }, new CustomResourceOptions { Parent = this });

            // Subtenant domains → same CloudFront distribution
            if (tenantConfig.Subtenants != null)
            {
                foreach (var sub in tenantConfig.Subtenants)
                {
                    if (!string.IsNullOrEmpty(sub.Value.SubDomain))
                    {
                        new Record($"{prefix}-cf-alias-{sub.Key}", new RecordArgs
                        {
                            ZoneId = zoneId,
                            Name = sub.Value.SubDomain,
                            Type = "A",
                            Aliases =
                            {
                                new RecordAliasArgs
                                {
                                    Name = distribution.DomainName,
                                    ZoneId = distribution.HostedZoneId,
                                    EvaluateTargetHealth = false,
                                },
                            },
                        }, new CustomResourceOptions { Parent = this });
                    }
                }
            }
        }

        return new AwsCloudFrontOutputs(
            distributionId: distribution.Id,
            domainName: distribution.DomainName,
            assetsBucketId: assetsBucket.Id);
    }

    /// <summary>
    /// Returns the origin ID for auth behaviors (/realms/*, /resources/*, /js/*).
    /// Same-region (PrivateLink available): routes through tenant ALB.
    /// Cross-region: routes directly to shared Keycloak public ALB.
    /// </summary>
    private static string AuthOriginId(TenantConfig tenantConfig)
        => tenantConfig.UsePrivateLink ? "alb-origin" : "shared-auth";

    /// <summary>
    /// Builds the list of CloudFront origins. Always includes S3 + ALB.
    /// For cross-region deployments (no PrivateLink), adds a direct origin
    /// to the shared Keycloak public ALB so CloudFront can route auth paths
    /// across regions without PrivateLink.
    /// </summary>
    private static InputList<DistributionOriginArgs> BuildOrigins(
        TenantConfig tenantConfig,
        BucketV2 assetsBucket,
        OriginAccessControl oac,
        string domain)
    {
        var origins = new InputList<DistributionOriginArgs>
        {
            new DistributionOriginArgs
            {
                OriginId = "s3-assets",
                DomainName = assetsBucket.BucketRegionalDomainName,
                OriginAccessControlId = oac.Id,
                OriginPath = "/wwwroot",
            },
            new DistributionOriginArgs
            {
                OriginId = "alb-origin",
                DomainName = $"origin.{domain}",
                CustomOriginConfig = new DistributionOriginCustomOriginConfigArgs
                {
                    HttpPort = 80,
                    HttpsPort = 443,
                    OriginProtocolPolicy = "https-only",
                    OriginSslProtocols = { "TLSv1.2" },
                },
            },
        };

        // Cross-region: add direct origin to shared Keycloak public ALB.
        // CloudFront connects via TLS to CentralAuthDomain (e.g., auth.monroadmin.click).
        // The AllViewer origin request policy forwards the viewer's Host header
        // (e.g., harmova.life), so Keycloak uses the tenant domain as the token issuer.
        // The shared ALB's path-only /realms/* rule (priority 12) matches any Host.
        if (!tenantConfig.UsePrivateLink && !string.IsNullOrEmpty(tenantConfig.CentralAuthDomain))
        {
            origins.Add(new DistributionOriginArgs
            {
                OriginId = "shared-auth",
                DomainName = tenantConfig.CentralAuthDomain,
                CustomOriginConfig = new DistributionOriginCustomOriginConfigArgs
                {
                    HttpPort = 80,
                    HttpsPort = 443,
                    OriginProtocolPolicy = "https-only",
                    OriginSslProtocols = { "TLSv1.2" },
                },
            });
        }

        return origins;
    }

    private static InputMap<string> Tags(string systemKey, string tenantKey) => new()
    {
        { "System", systemKey },
        { "Tenant", tenantKey },
        { "Component", "cdn" },
        { "ManagedBy", "lz-pulumi" },
    };
}

internal class AwsCloudFrontOutputs : ICdnOutputs
{
    public Output<string> DistributionId { get; }
    public Output<string> DomainName { get; }
    public Output<string> AssetsBucketId { get; }

    public AwsCloudFrontOutputs(
        Output<string> distributionId,
        Output<string> domainName,
        Output<string> assetsBucketId)
    {
        DistributionId = distributionId;
        DomainName = domainName;
        AssetsBucketId = assetsBucketId;
    }
}
