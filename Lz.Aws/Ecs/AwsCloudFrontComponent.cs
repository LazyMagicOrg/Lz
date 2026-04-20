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

        // Collect all domains: root + legacy
        var allDomains = new List<string> { domain };
        if (tenantConfig.LegacyDomains != null)
            allDomains.AddRange(tenantConfig.LegacyDomains);

        // Look up Route53 hosted zones for all domains
        var zonesByDomain = new Dictionary<string, Output<string>>();
        foreach (var d in allDomains)
        {
            var zone = Pulumi.Aws.Route53.GetZone.Invoke(
                new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = d });
            zonesByDomain[d] = zone.Apply(z => z.ZoneId);
        }

        // Build SANs: wildcard for each domain
        var sans = new InputList<string>();
        foreach (var d in allDomains)
        {
            sans.Add($"*.{d}");
            if (d != domain) // primary domain is DomainName, not a SAN
                sans.Add(d);
        }

        var cert = new Certificate($"{prefix}-cdn-cert", new CertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = sans,
            ValidationMethod = "DNS",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        // DNS validation records — one per unique base domain
        // Use stable resource names: original name for primary domain, slug-based for legacy
        var validationFqdns = new InputList<string>();

        // Primary domain validation (keep original resource name for backward compat)
        var primaryValidationRecord = new Pulumi.Aws.Route53.Record(
            $"{prefix}-cdn-cert-validation",
            new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = zonesByDomain[domain],
                Name = cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == domain || o.DomainName == $"*.{domain}").ResourceRecordName!),
                Type = cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == domain || o.DomainName == $"*.{domain}").ResourceRecordType!),
                Records = { cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == domain || o.DomainName == $"*.{domain}").ResourceRecordValue!) },
                Ttl = 300,
                AllowOverwrite = true,
            }, new CustomResourceOptions { Parent = this });
        validationFqdns.Add(primaryValidationRecord.Fqdn);

        // Legacy domain validation records
        if (tenantConfig.LegacyDomains != null)
        {
            foreach (var legacy in tenantConfig.LegacyDomains)
            {
                var slug = legacy.Replace(".", "-");
                var legacyValRecord = new Pulumi.Aws.Route53.Record(
                    $"{prefix}-cdn-cert-val-{slug}",
                    new Pulumi.Aws.Route53.RecordArgs
                    {
                        ZoneId = zonesByDomain[legacy],
                        Name = cert.DomainValidationOptions.Apply(opts =>
                            opts.First(o => o.DomainName == legacy || o.DomainName == $"*.{legacy}").ResourceRecordName!),
                        Type = cert.DomainValidationOptions.Apply(opts =>
                            opts.First(o => o.DomainName == legacy || o.DomainName == $"*.{legacy}").ResourceRecordType!),
                        Records = { cert.DomainValidationOptions.Apply(opts =>
                            opts.First(o => o.DomainName == legacy || o.DomainName == $"*.{legacy}").ResourceRecordValue!) },
                        Ttl = 300,
                        AllowOverwrite = true,
                    }, new CustomResourceOptions { Parent = this });
                validationFqdns.Add(legacyValRecord.Fqdn);
            }
        }

        var certValidation = new CertificateValidation($"{prefix}-cdn-cert-validated",
            new CertificateValidationArgs
            {
                CertificateArn = cert.Arn,
                ValidationRecordFqdns = validationFqdns,
            }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

        var certificateId = certValidation.CertificateArn;

        // =====================================================================
        // S3 BUCKETS — webapp, explore, park
        // =====================================================================
        // Naming: {sk}-{tk}-{stk}-webapp-{name}-{suffix}
        // With empty subtenantkey, double dash: med-monro--webapp-storeapp-4085-b82b

        var stk = ""; // subtenantkey — empty for now (double dash in bucket name)
        var webappBucketName = $"{prefix}-{stk}-webapp-storeapp-{suffix}";
        var exploreBucketName = $"{prefix}-{stk}-webapp-explore-{suffix}";
        var parkBucketName = $"{prefix}-{stk}-webapp-park-{suffix}";

        var webappBucket = CreateBucket(prefix, "webapp", webappBucketName, env, sk, tk);
        var exploreBucket = CreateBucket(prefix, "explore", exploreBucketName, env, sk, tk);
        var parkBucket = CreateBucket(prefix, "park", parkBucketName, env, sk, tk);

        // =====================================================================
        // CLOUDFRONT KEYVALUESTORE
        // =====================================================================
        // Used by the viewer-request function to check park state and backend status.
        // Keys: "parked" (true/false), "backend-status" (ready/deploying)
        // Managed imperatively by `lz park` and `lz unpark`.

        var kvs = new KeyValueStore($"{prefix}-kvs", new KeyValueStoreArgs
        {
            Name = $"{prefix}-kvs",
            Comment = $"Config for {sk}/{tk} ({env})",
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

        // Collect aliases: root domain + wildcard + legacy domains + subtenant domains
        var aliases = new InputList<string> { domain, $"*.{domain}" };
        if (tenantConfig.LegacyDomains != null)
        {
            foreach (var legacy in tenantConfig.LegacyDomains)
            {
                aliases.Add(legacy);
                aliases.Add($"*.{legacy}");
            }
        }
        if (tenantConfig.Subtenants != null)
        {
            foreach (var sub in tenantConfig.Subtenants)
            {
                if (!string.IsNullOrEmpty(sub.Value.SubDomain))
                    aliases.Add(sub.Value.SubDomain);
            }
        }

        // =====================================================================
        // CLOUDFRONT FUNCTION — viewer-request for region detection
        // =====================================================================
        // Intercepts default behavior (S3/WASM) requests. If the user has no
        // region-pref cookie and no ?region= param, redirects to
        // /AppApi/util/detect-region to geo-detect before the WASM app loads.

        var viewerRequestFn = CreateViewerRequestFunction(
            prefix, domain, tenantConfig, kvs.Arn,
            exploreBucket.BucketRegionalDomainName,
            parkBucket.BucketRegionalDomainName);
        var viewerResponseFn = CreateViewerResponseFunction(prefix, tenantConfig.ConfigDirectory);
        var exploreRewriteFn = CreateExploreRewriteFunction(prefix, tenantConfig.ConfigDirectory);

        // Build default-behavior function associations: viewer-request (auth gate,
        // park, region detection) + optional viewer-response (e.g. inject
        // Service-Worker-Allowed header for /service-worker.js). Both are
        // attached to the default behavior which targets the webapp bucket.
        var defaultFunctionAssociations = new List<DistributionDefaultCacheBehaviorFunctionAssociationArgs>();
        if (viewerRequestFn != null)
        {
            defaultFunctionAssociations.Add(new DistributionDefaultCacheBehaviorFunctionAssociationArgs
            {
                EventType = "viewer-request",
                FunctionArn = viewerRequestFn.Arn,
            });
        }
        if (viewerResponseFn != null)
        {
            defaultFunctionAssociations.Add(new DistributionDefaultCacheBehaviorFunctionAssociationArgs
            {
                EventType = "viewer-response",
                FunctionArn = viewerResponseFn.Arn,
            });
        }

        var distribution = new Distribution($"{prefix}-cf-dist", new DistributionArgs
        {
            Enabled = true,
            IsIpv6Enabled = true,
            Comment = $"{sk}/{tk} CDN ({env})",
            DefaultRootObject = cdn.DefaultRootObject ?? "app/index.html",
            PriceClass = cdn.PriceClass ?? "PriceClass_100",
            Aliases = aliases,

            // S3 origins for webapp/explore/park + ALB origin for API/media paths
            // When PrivateLink is unavailable (cross-region), a separate origin
            // routes auth paths directly to the shared Keycloak public ALB.
            Origins = BuildOrigins(tenantConfig, webappBucket, exploreBucket, parkBucket, oac, domain),

            // Default behavior → S3 webapp bucket (CF function switches origin for park/landing)
            DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
            {
                TargetOriginId = "s3-webapp",
                ViewerProtocolPolicy = "redirect-to-https",
                AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                CachedMethods = { "GET", "HEAD" },
                Compress = true,
                CachePolicyId = env == "dev"
                    ? "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"        // CachingDisabled (dev)
                    : "658327ea-f89d-4fab-a63d-7e88639e58f6",        // CachingOptimized
                FunctionAssociations = defaultFunctionAssociations.ToArray(),
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
                    TargetOriginId = "shared-auth",
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
                    TargetOriginId = "shared-auth",
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
                    TargetOriginId = "shared-auth",
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
                    OriginRequestPolicyId = "33f36d7e-f396-46d9-90e0-52428a34d9dc", // AllViewerAndCloudFrontHeaders-2022-06
                },
                // Staging explore pages (non-crawlable review) → ALB origin (SmartStore serves from EFS)
                // Always CachingDisabled so reviewers see latest generated content immediately
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/exploredev/*",
                    TargetOriginId = "alb-origin",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad",        // CachingDisabled always
                    OriginRequestPolicyId = "216adef6-5c7f-47e4-b989-5492eafa07d3", // AllViewer
                },
                // Published explore pages (crawlable) → S3 explore origin
                // Uses a dedicated rewrite function for clean directory-style URLs
                // No SPA fallback — missing pages should 404, not return Blazor index.html
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/explore/*",
                    TargetOriginId = "s3-explore",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = env == "dev"
                        ? "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"        // CachingDisabled (dev)
                        : "658327ea-f89d-4fab-a63d-7e88639e58f6",        // CachingOptimized
                    FunctionAssociations = exploreRewriteFn != null
                        ? new[]
                        {
                            new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                            {
                                EventType = "viewer-request",
                                FunctionArn = exploreRewriteFn.Arn,
                            },
                        }
                        : Array.Empty<DistributionOrderedCacheBehaviorFunctionAssociationArgs>(),
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
        // S3 BUCKET POLICIES — allow CloudFront OAC access to all three buckets
        // =====================================================================

        CreateBucketPolicy(prefix, "webapp", webappBucket, distribution);
        CreateBucketPolicy(prefix, "explore", exploreBucket, distribution);
        CreateBucketPolicy(prefix, "park", parkBucket, distribution);

        // DNS records are managed by AwsTenantDnsAndCertComponent (single owner
        // for all tenant DNS records). This avoids resource identity conflicts when
        // domains switch between root and legacy roles during domain transitions.

        return new AwsCloudFrontOutputs(
            distributionId: distribution.Id,
            domainName: distribution.DomainName,
            webappBucketId: webappBucket.Id,
            exploreBucketId: exploreBucket.Id);
    }

    /// <summary>
    /// Builds the list of CloudFront origins: S3 webapp/explore/park, ALB (API),
    /// and shared-auth (Keycloak on shared public ALB).
    /// </summary>
    private static InputList<DistributionOriginArgs> BuildOrigins(
        TenantConfig tenantConfig,
        BucketV2 webappBucket,
        BucketV2 exploreBucket,
        BucketV2 parkBucket,
        OriginAccessControl oac,
        string domain)
    {
        var origins = new InputList<DistributionOriginArgs>
        {
            new DistributionOriginArgs
            {
                OriginId = "s3-webapp",
                DomainName = webappBucket.BucketRegionalDomainName,
                OriginAccessControlId = oac.Id,
                OriginPath = "/wwwroot",
            },
            new DistributionOriginArgs
            {
                OriginId = "s3-explore",
                DomainName = exploreBucket.BucketRegionalDomainName,
                OriginAccessControlId = oac.Id,
                OriginPath = "/wwwroot",
            },
            new DistributionOriginArgs
            {
                OriginId = "s3-park",
                DomainName = parkBucket.BucketRegionalDomainName,
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

        // Shared-auth origin — routes /realms/*, /resources/*, /js/* directly
        // to the shared Keycloak public ALB (CentralAuthDomain).
        // The AllViewer origin request policy forwards the viewer's Host header
        // (e.g., harmova.life), so Keycloak uses the tenant domain as the token issuer.
        // The shared ALB's path-only /realms/* rule (priority 12) matches any Host.
        if (!string.IsNullOrEmpty(tenantConfig.CentralAuthDomain))
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

    /// <summary>
    /// Creates a CloudFront Function for viewer-request region detection.
    /// Reads CFViewerRequest.js from the repo's CloudFront/ directory,
    /// replaces ${RootDomainParameter} with the tenant's root domain.
    /// Returns null if the JS file doesn't exist (graceful fallback).
    /// </summary>
    private Pulumi.Aws.CloudFront.Function? CreateViewerRequestFunction(
        string prefix, string domain, TenantConfig tenantConfig, Output<string> kvsArn,
        Output<string> exploreBucketDomain, Output<string> parkBucketDomain)
    {
        var jsPath = Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFViewerRequest.js");
        if (!File.Exists(jsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: CloudFront function not found at {jsPath} — skipping viewer-request function.");
            Console.ResetColor();
            return null;
        }

        // Build legacy domains JSON array for injection into the CloudFront function
        var legacyDomainsJson = tenantConfig.LegacyDomains?.Count > 0
            ? "[" + string.Join(",", tenantConfig.LegacyDomains.Select(d => $"\"{d}\"")) + "]"
            : "[]";

        // Replace template placeholders with actual values.
        // ${KvsId} uses UUID extracted from ARN — cf.kvs() requires UUID, not full ARN.
        // ${ExploreBucketDomain} and ${ParkBucketDomain} are S3 regional domain names
        // used by cf.updateRequestOrigin() to switch origins dynamically.
        var jsCode = Output.Tuple(kvsArn, exploreBucketDomain, parkBucketDomain).Apply(t =>
        {
            var kvsUuid = t.Item1.Contains('/') ? t.Item1.Split('/').Last() : t.Item1;
            var code = File.ReadAllText(jsPath)
                .Replace("${RootDomainParameter}", domain)
                .Replace("${LegacyDomainsJson}", legacyDomainsJson)
                .Replace("${KvsId}", kvsUuid)
                .Replace("${ExploreBucketDomain}", t.Item2)
                .Replace("${ParkBucketDomain}", t.Item3);

            var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(code);
            const int maxBytes = 10240; // CloudFront Functions limit: 10 KB
            if (sizeBytes > maxBytes)
                throw new InvalidOperationException(
                    $"CloudFront function 'CFViewerRequest.js' is {sizeBytes:N0} bytes — exceeds {maxBytes:N0} byte limit.");
            return code;
        });

        var comment = tenantConfig.LegacyDomains?.Count > 0
            ? $"Region detection + legacy redirect + park for {domain}"
            : $"Region detection + park for {domain}";

        return new Pulumi.Aws.CloudFront.Function(
            $"{prefix}-viewer-request", new FunctionArgs
            {
                Runtime = "cloudfront-js-2.0",
                Code = jsCode,
                Comment = comment,
                Publish = true,
                KeyValueStoreAssociations = { kvsArn },
            }, new CustomResourceOptions { Parent = this });
    }

    private Pulumi.Aws.CloudFront.Function? CreateViewerResponseFunction(
        string prefix, string configDirectory)
    {
        var jsPath = Path.Combine(configDirectory, "CloudFront", "CFViewerResponse.js");
        if (!File.Exists(jsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Info: CloudFront viewer-response function not found at {jsPath} — skipping response header injection.");
            Console.ResetColor();
            return null;
        }

        var jsCode = File.ReadAllText(jsPath);
        var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(jsCode);
        const int maxBytes = 10240; // CloudFront Functions limit: 10 KB
        if (sizeBytes > maxBytes)
            throw new InvalidOperationException(
                $"CloudFront function 'CFViewerResponse.js' is {sizeBytes:N0} bytes — exceeds {maxBytes:N0} byte limit.");

        return new Pulumi.Aws.CloudFront.Function(
            $"{prefix}-viewer-response", new FunctionArgs
            {
                Runtime = "cloudfront-js-2.0",
                Code = jsCode,
                Comment = $"Response header injection for {prefix}",
                Publish = true,
            }, new CustomResourceOptions { Parent = this });
    }

    private Pulumi.Aws.CloudFront.Function? CreateExploreRewriteFunction(
        string prefix, string configDirectory)
    {
        var jsPath = Path.Combine(configDirectory, "CloudFront", "CFExploreRewrite.js");
        if (!File.Exists(jsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Info: Explore rewrite function not found at {jsPath} — /explore/* will not rewrite to index.html.");
            Console.ResetColor();
            return null;
        }

        var jsCode = File.ReadAllText(jsPath);
        return new Pulumi.Aws.CloudFront.Function(
            $"{prefix}-explore-rewrite", new FunctionArgs
            {
                Runtime = "cloudfront-js-2.0",
                Code = jsCode,
                Comment = $"Explore pages index.html rewrite for {prefix}",
                Publish = true,
            }, new CustomResourceOptions { Parent = this });
    }

    /// <summary>
    /// Creates an S3 bucket with public access blocked (CloudFront uses OAC).
    /// </summary>
    private BucketV2 CreateBucket(string prefix, string label, string bucketName, string env, string sk, string tk)
    {
        var bucket = new BucketV2($"{prefix}-{label}-bucket", new BucketV2Args
        {
            Bucket = bucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this });

        new BucketPublicAccessBlock($"{prefix}-{label}-block", new BucketPublicAccessBlockArgs
        {
            Bucket = bucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        return bucket;
    }

    /// <summary>
    /// Creates an S3 bucket policy allowing CloudFront OAC access.
    /// </summary>
    private void CreateBucketPolicy(string prefix, string label, BucketV2 bucket, Distribution distribution)
    {
        new BucketPolicy($"{prefix}-{label}-policy", new BucketPolicyArgs
        {
            Bucket = bucket.Id,
            Policy = Output.Tuple(bucket.Arn, distribution.Arn).Apply(t =>
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
    public Output<string> WebappBucketId { get; }
    public Output<string> ExploreBucketId { get; }

    public AwsCloudFrontOutputs(
        Output<string> distributionId,
        Output<string> domainName,
        Output<string> webappBucketId,
        Output<string> exploreBucketId)
    {
        DistributionId = distributionId;
        DomainName = domainName;
        WebappBucketId = webappBucketId;
        ExploreBucketId = exploreBucketId;
    }
}
