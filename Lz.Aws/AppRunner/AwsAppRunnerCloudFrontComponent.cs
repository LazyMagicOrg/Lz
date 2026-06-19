using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.CloudFront;
using Pulumi.Aws.CloudFront.Inputs;
using Pulumi.Aws.Route53;
using Pulumi.Aws.Route53.Inputs;
using Pulumi.Aws.S3;
using Pulumi.Aws.S3.Inputs;
using Lz.Aws.Ecs;

namespace Lz.Aws.AppRunner;

/// <summary>
/// CloudFront + S3 + KeyValueStore component for AppRunner topology.
/// Creates a per-tenant CloudFront distribution with:
///   - CloudFront Functions for dynamic subtenant routing via KVS
///   - S3 origin for WASM/asset serving (OAC)
///   - AppRunner origin for API requests
///   - Route 53 alias records (root, wildcard, subtenants)
///   - KeyValueStore entries for each domain/subdomain
///
/// The CloudFront Function (RequestFunction) dynamically rewrites S3 origins
/// based on KVS config, enabling multi-tenant routing within a single distribution.
/// </summary>
public class AwsAppRunnerCloudFrontComponent : ComponentResource, ITenantCdnComponent
{
    public AwsAppRunnerCloudFrontComponent()
        : base("lz:aws:AppRunnerCloudFront", "cdn", ResourceArgs.Empty, null)
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
        var apexExternal = tenantConfig.ApexHostedExternally;
        var cdn = tenantConfig.CDN ?? new CdnConfig();

        // =====================================================================
        // ACM CERTIFICATE (us-east-1 — required by CloudFront)
        // =====================================================================

        var usEast1 = new Provider($"{prefix}-us-east-1", new ProviderArgs
        {
            Region = "us-east-1",
        }, new CustomResourceOptions { Parent = this });

        // When the apex is hosted externally the cert is wildcard-only;
        // *.{domain} does not cover the bare apex, and the distribution
        // alias list + apex Route 53 record below move in lockstep.
        var certArgs = new CertificateArgs
        {
            DomainName = apexExternal ? $"*.{domain}" : domain,
            ValidationMethod = "DNS",
            Tags = Tags(sk, tk),
        };
        if (!apexExternal)
            certArgs.SubjectAlternativeNames.Add($"*.{domain}");
        var cert = new Certificate($"{prefix}-cdn-cert", certArgs,
            new CustomResourceOptions { Parent = this, Provider = usEast1 });

        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = domain });

        var validationRecord = new Pulumi.Aws.Route53.Record($"{prefix}-cdn-cert-validation",
            new Pulumi.Aws.Route53.RecordArgs
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

        // =====================================================================
        // CLOUDFRONT KEYVALUESTORE
        // =====================================================================

        var kvs = new KeyValueStore($"{prefix}-kvs", new KeyValueStoreArgs
        {
            Name = $"{prefix}-kvs",
            Comment = $"Config for {sk}/{tk} ({env})",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // S3 BUCKET FOR DEFAULT ORIGIN (assets bucket)
        // =====================================================================

        var bucketName = $"{prefix}-{suffix}-{env}-assets";
        var assetsBucket = new BucketV2($"{prefix}-assets-bucket", new BucketV2Args
        {
            Bucket = bucketName,
            ForceDestroy = env == "dev",
            Tags = Tags(sk, tk),
        }, new CustomResourceOptions { Parent = this });

        new BucketPublicAccessBlock($"{prefix}-assets-block", new BucketPublicAccessBlockArgs
        {
            Bucket = assetsBucket.Id,
            BlockPublicAcls = true,
            BlockPublicPolicy = true,
            IgnorePublicAcls = true,
            RestrictPublicBuckets = true,
        }, new CustomResourceOptions { Parent = this });

        // OAC
        var oac = new OriginAccessControl($"{prefix}-oac", new OriginAccessControlArgs
        {
            Name = $"{prefix}-oac",
            Description = $"OAC for {domain} assets",
            OriginAccessControlOriginType = "s3",
            SigningBehavior = "always",
            SigningProtocol = "sigv4",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // CLOUDFRONT FUNCTIONS — loaded from CloudFront/ directory
        // =====================================================================

        var authConfigFn = CreateFunctionFromFile(
            prefix, "authconfig", "CFAuthConfig.js",
            $"Auth config for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required CloudFront function not found: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFAuthConfig.js")}");

        var requestFn = CreateFunctionFromFile(
            prefix, "request", "CFRequest.js",
            $"Request routing for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required CloudFront function not found: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFRequest.js")}");

        // =====================================================================
        // ALIASES
        // =====================================================================

        // Wildcard covers every first-level subtenant domain
        // (e.g. cerulean.{domain}). Subtenants are NOT enumerated here — adding
        // one requires no CloudFront change. SubDomain is a single DNS label
        // (validated by ConfigValidator); the FQDN is built at consumption
        // time as {SubDomain}.{RootDomain}, so first-level is structurally
        // guaranteed by the schema. The apex is included only when lz owns
        // it (ApexHostedExternally == false) — it must match the cert above.
        var aliases = apexExternal
            ? new InputList<string> { $"*.{domain}" }
            : new InputList<string> { domain, $"*.{domain}" };

        // =====================================================================
        // CLOUDFRONT DISTRIBUTION
        // =====================================================================

        var distribution = new Distribution($"{prefix}-cf-dist", new DistributionArgs
        {
            Enabled = true,
            IsIpv6Enabled = true,
            Comment = $"{sk}/{tk} CDN ({env})",
            DefaultRootObject = cdn.DefaultRootObject ?? "app/index.html",
            PriceClass = cdn.PriceClass ?? "PriceClass_100",
            Aliases = aliases,

            Origins =
            {
                // S3 origin for static assets (WASM apps, tenant assets)
                new DistributionOriginArgs
                {
                    OriginId = "s3-assets",
                    DomainName = assetsBucket.BucketRegionalDomainName,
                    OriginAccessControlId = oac.Id,
                },
            },

            // Default behavior — RequestFunction rewrites origin dynamically
            DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
            {
                TargetOriginId = "s3-assets",
                ViewerProtocolPolicy = "redirect-to-https",
                AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                CachedMethods = { "GET", "HEAD" },
                Compress = true,
                CachePolicyId = env == "dev"
                    ? "4135ea2d-6df8-44a3-9df3-4b5a84be39ad"  // CachingDisabled (dev)
                    : "658327ea-f89d-4fab-a63d-7e88639e58f6",  // CachingOptimized
                FunctionAssociations =
                {
                    new DistributionDefaultCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-request",
                        FunctionArn = requestFn.Arn,
                    },
                },
            },

            // /config behavior — serves auth config from KVS
            OrderedCacheBehaviors =
            {
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/config",
                    TargetOriginId = "s3-assets",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = false,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad", // CachingDisabled
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request",
                            FunctionArn = authConfigFn.Arn,
                        },
                    },
                },
            },

            // SPA fallback: return index.html for missing S3 objects
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
                AcmCertificateArn = certValidation.CertificateArn,
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

        new BucketPolicy($"{prefix}-assets-policy", new BucketPolicyArgs
        {
            Bucket = assetsBucket.Id,
            Policy = Output.Tuple(assetsBucket.Arn, distribution.Arn).Apply(t =>
                $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Sid"": ""AllowCloudFrontServicePrincipal"",
                        ""Effect"": ""Allow"",
                        ""Principal"": {{ ""Service"": ""cloudfront.amazonaws.com"" }},
                        ""Action"": ""s3:GetObject"",
                        ""Resource"": ""{t.Item1}/*"",
                        ""Condition"": {{ ""StringEquals"": {{ ""AWS:SourceArn"": ""{t.Item2}"" }} }}
                    }}]
                }}"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ROUTE 53 ALIAS RECORDS
        // =====================================================================

        var zoneId = publicZone.Apply(z => z.ZoneId);

        // Root domain — created only when lz owns the apex. When
        // ApexHostedExternally is true the apex record is left untouched;
        // it belongs to whatever external host serves the bare domain.
        if (!apexExternal)
        {
            new Pulumi.Aws.Route53.Record($"{prefix}-cf-alias", new Pulumi.Aws.Route53.RecordArgs
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
        }

        // Wildcard
        new Pulumi.Aws.Route53.Record($"{prefix}-cf-alias-wildcard", new Pulumi.Aws.Route53.RecordArgs
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

        // Per-subtenant Route 53 records are NOT created here. The wildcard
        // A-alias (`*.{domain}` → distribution) created above covers every
        // first-level subtenant domain. Adding a subtenant requires no DNS
        // change. SubDomain is a single DNS label (see ConfigValidator);
        // multi-label values are unrepresentable.

        return new AwsCloudFrontOutputs(
            distributionId: distribution.Id,
            domainName: distribution.DomainName,
            webappBucketId: assetsBucket.Id,
            exploreBucketId: assetsBucket.Id);
    }

    /// <summary>
    /// Creates a CloudFront Function by reading JS source from the CloudFront/ directory.
    /// Returns null if the file doesn't exist (graceful fallback).
    /// </summary>
    private Pulumi.Aws.CloudFront.Function? CreateFunctionFromFile(
        string prefix, string name, string jsFileName,
        string comment, string configDirectory, Output<string> kvsArn)
    {
        var jsPath = Path.Combine(configDirectory, "CloudFront", jsFileName);
        if (!File.Exists(jsPath))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: CloudFront function not found at {jsPath} — skipping {name} function.");
            Console.ResetColor();
            return null;
        }

        // Read, substitute ${KvsArn}, minify (safe mode — strips comments +
        // collapses whitespace, keeps identifiers), and validate against the
        // 10 KB CloudFront Functions limit.
        var jsCode = kvsArn.Apply(arn =>
            Lz.Aws.Shared.CfFunctionCodePrep.PrepareAndValidate(
                jsPath, jsFileName, ("${KvsArn}", arn)));

        return new Pulumi.Aws.CloudFront.Function(
            $"{prefix}-{name}-fn", new FunctionArgs
            {
                Runtime = "cloudfront-js-2.0",
                Code = jsCode,
                Comment = comment,
                Publish = true,
                KeyValueStoreAssociations = { kvsArn },
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
