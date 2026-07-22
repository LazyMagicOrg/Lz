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

    /// <summary>
    /// Identifies the dynamic application origin (the API host) that the
    /// distribution proxies <c>/*Api/*</c> and <c>/auth/*</c> to.
    /// </summary>
    protected sealed class ApiOriginSpec
    {
        /// <summary>The <c>OriginId</c> that the API/auth ordered behaviors target.</summary>
        public required string OriginId { get; init; }
        /// <summary>The distribution origin definition for the API host.</summary>
        public required DistributionOriginArgs Origin { get; init; }
    }

    /// <summary>
    /// Builds the application/API origin. Base implementation = the ECSExpress
    /// ALB, reached via the stable <c>origin.{domain}</c> Route 53 alias. The
    /// Lambda topology overrides this to target the Function URL through a
    /// Lambda-type OAC. The base output is kept byte-identical to the previous
    /// inline definition so the live distribution's config — and therefore its
    /// in-place updatability — is unchanged.
    /// </summary>
    protected virtual ApiOriginSpec BuildApiOrigin(
        string prefix, string domain, IComputeEnvironmentOutputs compute)
        => new()
        {
            OriginId = "alb-origin",
            Origin = new DistributionOriginArgs
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
        };

    /// <summary>
    /// Hook invoked after the distribution is created so a topology can grant
    /// CloudFront access to its API origin — e.g. the Lambda topology adds the
    /// <c>aws:lambda:Permission</c> that lets this distribution invoke the
    /// Function URL via OAC, scoped to the distribution ARN. Base = no-op
    /// (the ECSExpress ALB origin is publicly reachable).
    /// </summary>
    protected virtual void ConfigureApiOriginAccess(
        string prefix, Distribution distribution, IComputeEnvironmentOutputs compute)
    {
    }

    /// <summary>
    /// Hook for a topology to append extra ordered cache behaviors just before the
    /// distribution is created — e.g. a server-rendered commerce path that needs a
    /// cookie-forwarding / no-cache cache behavior (a policy bound at deploy time that
    /// the runtime CFRequest function cannot set). Base returns none, so an existing
    /// distribution's ordered-behaviors list — and therefore its config — is byte-for-byte
    /// identical. Behaviors are appended after any /bff and /cbff behaviors; CloudFront
    /// evaluates ordered behaviors first-match, so give an appended pattern a prefix that
    /// does not overlap an earlier one.
    /// </summary>
    protected virtual IEnumerable<DistributionOrderedCacheBehaviorArgs> BuildExtraBehaviors(
        TenantConfig tenantConfig, ApiOriginSpec apiOrigin, Function requestFn, Function responseFn)
        => Array.Empty<DistributionOrderedCacheBehaviorArgs>();

    public ICdnOutputs Deploy(TenantConfig tenantConfig, IComputeEnvironmentOutputs compute)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var prefix = $"{sk}-{tk}";
        var suffix = tenantConfig.TenantSuffix;
        var domain = tenantConfig.RootDomain;
        var cdn = tenantConfig.CDN ?? new CdnConfig();

        // Topology-overridable API origin (base = ALB via origin.{domain}).
        var apiOrigin = BuildApiOrigin(prefix, domain, compute);

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

        // Explore static-site routing — split from CFRequest.js to keep
        // the main request function under the 10 KB CloudFront limit.
        // Handles /explore* (bare-prefix redirects + S3 origin rewrite to
        // the per-subtenant explore bucket).
        var exploreFn = CreateFunctionFromFile(prefix, "explore", "CFExplore.js",
            $"Explore static-site routing for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFExplore.js")}");

        // Auth routing — /auth/{pool}/... (OIDC façade) and /authentication/...
        // (login/logout flow + WASM passthrough). Split from CFRequest.js to
        // consolidate auth-flow routing in one auditable place and to give
        // CFRequest more headroom under the 10 KB CloudFront limit.
        var authFn = CreateFunctionFromFile(prefix, "auth", "CFAuth.js",
            $"Auth routing for {domain}", tenantConfig.ConfigDirectory, kvs.Arn)
            ?? throw new FileNotFoundException(
                $"Required: {Path.Combine(tenantConfig.ConfigDirectory, "CloudFront", "CFAuth.js")}");

        // Auth callback function — no KVS needed, just redirects based on state param
        var authCallbackFn = CreateSimpleFunctionFromFile(prefix, "auth-callback", "CFAuthCallback.js",
            $"OAuth callback redirect for {domain}", tenantConfig.ConfigDirectory);

        // Viewer-response CORS function. CFRequest.js handles OPTIONS
        // preflights; this handles the simple-GET case (browser skips
        // preflight, response just needs Access-Control-Allow-Origin).
        //
        // The allowlist is operator-controlled via tenantconfig.CDN.Cors:
        //   AllowLocalhostDev: true     → echo http(s)://localhost(:port)?
        //   AllowedOrigins: [...]       → echo exact-match strings
        // Both default to false/empty; prod with no Cors block echoes
        // nothing. Values are baked in here at deploy time via string
        // substitution — no live KVS lookup per response.
        var corsCfg = (tenantConfig.CDN ?? new CdnConfig()).Cors ?? new CorsConfig();
        var allowLocalhostJs = corsCfg.AllowLocalhostDev ? "true" : "false";
        var allowedOriginsJson = System.Text.Json.JsonSerializer.Serialize(
            corsCfg.AllowedOrigins ?? new List<string>());

        var responseFnPath = Path.Combine(
            tenantConfig.ConfigDirectory, "CloudFront", "CFResponse.js");
        if (!File.Exists(responseFnPath))
            throw new FileNotFoundException($"Required: {responseFnPath}");
        var responseFnCode = Lz.Aws.Shared.CfFunctionCodePrep.PrepareAndValidate(
            responseFnPath, "CFResponse.js",
            ("__ALLOW_LOCALHOST_DEV__", allowLocalhostJs),
            ("__ALLOWED_ORIGINS_JSON__", allowedOriginsJson));
        var responseFn = new Pulumi.Aws.CloudFront.Function($"{prefix}-response-fn",
            new FunctionArgs
            {
                Runtime = "cloudfront-js-2.0",
                Code = responseFnCode,
                Comment = $"CORS response headers for {domain}",
                Publish = true,
            }, new CustomResourceOptions { Parent = this });

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

        // S3-level CORS configuration. The CFResponse CloudFront Function
        // adds Access-Control-Allow-Origin to *successful* origin responses
        // routed through the default '/*' behavior — but CloudFront's
        // CustomErrorResponses flow can short-circuit viewer-response on
        // origin 4xx errors (the SPA-fallback /index.html recursion fails
        // when CFRequest's dynamic origin rewrite picks a bucket without
        // /index.html, falling back to the raw S3 error which never gets
        // CORS injected). Configuring CORS at the S3 level means S3 itself
        // emits Access-Control-Allow-Origin on its responses including
        // 403/404 — CloudFront passes those headers through. Net effect:
        // browsers see clean 4xx responses with CORS, the WASM client's
        // fetch sees the 4xx instead of a CORS-blocked failure, and JS-
        // side fall-through (e.g. subtenancy overlay missing → fall back
        // to system default) works as designed.
        var s3CorsOrigins = new List<string>();
        if (corsCfg.AllowLocalhostDev)
        {
            s3CorsOrigins.Add("http://localhost:*");
            s3CorsOrigins.Add("https://localhost:*");
        }
        if (corsCfg.AllowedOrigins != null)
            s3CorsOrigins.AddRange(corsCfg.AllowedOrigins);
        if (s3CorsOrigins.Count > 0)
        {
            new Pulumi.Aws.S3.BucketCorsConfigurationV2($"{prefix}-assets-cors",
                new Pulumi.Aws.S3.BucketCorsConfigurationV2Args
                {
                    Bucket = assetsBucket.Id,
                    CorsRules =
                    {
                        new Pulumi.Aws.S3.Inputs.BucketCorsConfigurationV2CorsRuleArgs
                        {
                            AllowedHeaders = { "*" },
                            AllowedMethods = { "GET", "HEAD" },
                            AllowedOrigins = s3CorsOrigins.ToArray(),
                            ExposeHeaders = { "ETag" },
                            MaxAgeSeconds = 3000,
                        }
                    }
                }, new CustomResourceOptions { Parent = this });
        }

        var oac = new OriginAccessControl($"{prefix}-oac", new OriginAccessControlArgs
        {
            Name = $"{prefix}-oac", Description = $"OAC for {domain}",
            OriginAccessControlOriginType = "s3", SigningBehavior = "always", SigningProtocol = "sigv4",
        }, new CustomResourceOptions { Parent = this });

        // ─────────────────────────────────────────────────────────────────────
        // CACHE POLICY — host-keyed
        // ─────────────────────────────────────────────────────────────────────
        // Mirrors the AWS-managed CachingOptimized policy (gzip+brotli, MaxTtl
        // 1 year) but adds the x-custom-cache-key header to the cache key.
        // CFRequest.js and CFExplore.js compute that header as
        // {bucket}-{originPath}-{request.uri} so the cache key is 1:1 with the
        // resolved S3 response (host-disambiguated through the bucket name,
        // which contains the tenant/subtenant keys via the KVS lookup).
        // CFAuthConfig sets it as 'config-{host}' on its inline response.
        //
        // Without a custom policy, CloudFront's default cache key is
        // (URI, query string) — Host is NOT included — and any function-driven
        // origin rewrite collapses cross-host onto a single entry. That is
        // the cross-tenant cache poisoning that bit on test the first time
        // CachingOptimized went live.
        //
        // Wired into the default (/*) behavior plus /explore* and /venues*.
        // /config stays on CachingDisabled today; the function emits the same
        // header anyway so the migration to this policy is a one-line config
        // change when desired.
        var hostKeyedCachePolicy = new CachePolicy($"{prefix}-cache-host-keyed",
            new CachePolicyArgs
            {
                Name = $"{prefix}-cache-host-keyed-{env}",
                Comment = "Cache key includes x-custom-cache-key header so " +
                          "function-driven per-host origin rewrites do not collide.",
                DefaultTtl = 86400,
                MinTtl = 1,
                MaxTtl = 31536000,
                ParametersInCacheKeyAndForwardedToOrigin =
                    new CachePolicyParametersInCacheKeyAndForwardedToOriginArgs
                    {
                        EnableAcceptEncodingGzip = true,
                        EnableAcceptEncodingBrotli = true,
                        HeadersConfig =
                            new CachePolicyParametersInCacheKeyAndForwardedToOriginHeadersConfigArgs
                            {
                                HeaderBehavior = "whitelist",
                                Headers =
                                    new CachePolicyParametersInCacheKeyAndForwardedToOriginHeadersConfigHeadersArgs
                                    {
                                        Items = { "x-custom-cache-key" },
                                    },
                            },
                        QueryStringsConfig =
                            new CachePolicyParametersInCacheKeyAndForwardedToOriginQueryStringsConfigArgs
                            {
                                QueryStringBehavior = "none",
                            },
                        CookiesConfig =
                            new CachePolicyParametersInCacheKeyAndForwardedToOriginCookiesConfigArgs
                            {
                                CookieBehavior = "none",
                            },
                    },
            }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ALIASES
        // =====================================================================

        // Apex + wildcard cover every first-level subtenant domain; subtenants
        // are not enumerated here. SubDomain is a single DNS label (see
        // ConfigValidator); the FQDN is {SubDomain}.{RootDomain}, so
        // first-level is structurally guaranteed by the schema.
        var aliases = new InputList<string> { domain, $"*.{domain}" };

        // =====================================================================
        // CLOUDFRONT DISTRIBUTION — ALB origin + S3 origin
        // =====================================================================

        var distributionArgs = new DistributionArgs
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
                apiOrigin.Origin,
            },
            DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
            {
                TargetOriginId = "s3-assets",
                ViewerProtocolPolicy = "redirect-to-https",
                AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                CachedMethods = { "GET", "HEAD" },
                Compress = true,
                // ALL envs (incl. dev) use the host-keyed policy declared
                // above — does NOT use the AWS-managed CachingOptimized
                // because that policy keys only on (URI, query string) and
                // CFRequest's per-host bucket rewrites would poison the cache
                // cross-tenant. The function emits x-custom-cache-key for
                // every webapp/asset response and this policy includes that
                // header in the key. Dev was previously CachingDisabled for
                // fast iteration; flipped ON to exercise edge caching + the
                // .br/.gz pre-compressed asset path (so dev now needs a
                // CloudFront invalidation to see redeploys).
                CachePolicyId = hostKeyedCachePolicy.Id,
                FunctionAssociations =
                {
                    new DistributionDefaultCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-request", FunctionArn = requestFn.Arn,
                    },
                    // viewer-response: echoes CORS headers for localhost
                    // origins so VS-hosted local WASM apps can fetch
                    // cloud assets (system + tenant + subtenant + framework).
                    new DistributionDefaultCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-response", FunctionArn = responseFn.Arn,
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
                    TargetOriginId = apiOrigin.OriginId,
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
                        // CORS for localhost dev — same rationale as default behavior.
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-response", FunctionArn = responseFn.Arn,
                        },
                    },
                },
                // Explore static-site behavior — /explore*
                // CFExplore.js short-circuits bare-prefix redirects and does
                // dynamic S3 origin rewrite to the per-subtenant explore
                // bucket. Pattern matches /explore, /explore/, /explore/home,
                // /explore/{slug}/... — caveat: also matches /exploration,
                // which we don't have today; if such a path is ever added,
                // either rename or move it to its own behavior ahead of this.
                // Static content benefits from edge caching in non-dev envs.
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/explore*",
                    TargetOriginId = "s3-assets", // placeholder; CFExplore overrides
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    // Host-keyed in non-dev so per-subtenant explore buckets
                    // don't poison cross-tenant. CFExplore emits
                    // x-custom-cache-key with the resolved bucket+path+uri.
                    // Host-keyed in ALL envs (incl. dev) so per-subtenant
                    // explore/venues buckets don't poison cross-tenant.
                    // CFExplore emits x-custom-cache-key with the resolved
                    // bucket+path+uri. Dev was CachingDisabled for fast
                    // iteration; flipped ON alongside the default behavior to
                    // enable edge caching + compression (dev now needs a
                    // CloudFront invalidation to see redeploys).
                    CachePolicyId = hostKeyedCachePolicy.Id,
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = exploreFn.Arn,
                        },
                    },
                },
                // Venues static-site behavior — /venues*
                // Same CFExplore.js function (it's generic over staticsite
                // path now — matches the longest-prefix `staticsite` tuple
                // in KVS). The /venues/ static site is tenant-level (lives
                // in Tenancies/{tk}/staticsite/public/venues/), so the
                // `StaticSites: /venues/` entry in tenantconfig cascades
                // into every per-host KVS entry and CFExplore resolves it
                // to the same `bcs-bcs--webapp-venues-{ts}` bucket on
                // every request, regardless of which subtenant subdomain
                // serves the request. Pattern caveat same as /explore* —
                // /venues* also matches hypothetical /venuesabc paths.
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/venues*",
                    TargetOriginId = "s3-assets", // placeholder; CFExplore overrides
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    // Host-keyed for parity with /explore* even though
                    // /venues/ resolves to the same tenant-level bucket on
                    // every host today. Future-safe: if /venues/ ever
                    // diverges per host (tenant-specific listings, A/B
                    // testing), this is the right key shape already.
                    // Host-keyed in ALL envs (incl. dev) so per-subtenant
                    // explore/venues buckets don't poison cross-tenant.
                    // CFExplore emits x-custom-cache-key with the resolved
                    // bucket+path+uri. Dev was CachingDisabled for fast
                    // iteration; flipped ON alongside the default behavior to
                    // enable edge caching + compression (dev now needs a
                    // CloudFront invalidation to see redeploys).
                    CachePolicyId = hostKeyedCachePolicy.Id,
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = exploreFn.Arn,
                        },
                    },
                },
                // OIDC façade behavior — /auth/{pool}/...
                // CFAuth.js dispatches by sub-path:
                //   /.well-known/openid-configuration → 200 with synthetic discovery doc
                //   /.well-known/jwks.json            → origin rewrite to cognito-idp.{region}.amazonaws.com
                //   /oauth2/{token,userInfo,revoke}   → origin rewrite to auth-{pool}.{domain}
                //   /oauth2/authorize, /logout        → 302 to Cognito Hosted UI
                // CachingDisabled is mandatory — token/userInfo responses MUST NOT
                // be cached at the edge. AllViewerExceptHostHeader rewrites Host to
                // the dynamically-selected origin (Cognito validates Host strictly).
                // TargetOriginId is a placeholder; cf.updateRequestOrigin overrides
                // it for proxied sub-paths and the inline-200/302 sub-paths never
                // reach an origin.
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/auth/*",
                    TargetOriginId = apiOrigin.OriginId,
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
                            EventType = "viewer-request", FunctionArn = authFn.Arn,
                        },
                        // CORS for localhost dev — same rationale as default + /*Api/*.
                        // CFAuth proxies /auth/{pool}/oauth2/{token,userInfo,revoke}
                        // upstream to Cognito; the WASM OIDC client at localhost
                        // exchanges auth codes for tokens here, and needs
                        // Access-Control-Allow-Origin on the response from
                        // upstream. Function-generated 200/302 sub-paths
                        // (.well-known/openid-configuration, authorize, logout)
                        // bypass viewer-response and add CORS inline in CFAuth.js.
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-response", FunctionArn = responseFn.Arn,
                        },
                    },
                },
                // Authentication-flow behavior — /authentication/...
                // CFAuth.js handles login/logout intercepts and passes through
                // the rest to the root webapp's S3 bucket via dynamic origin
                // rewrite. Cache policy matches /auth/* (CachingDisabled), but
                // the ORIGIN-REQUEST policy must differ: this behavior's target
                // is an S3 origin, and CloudFront rejects CreateDistribution
                // when an S3 origin uses AllViewerExceptHostHeader — only
                // CORS-CustomOrigin / CORS-S3Origin / UserAgentRefererHeaders
                // are legal there. The live distribution predated this behavior
                // config, so the 400 (InvalidArgument: "The parameter Origin S3
                // Origins can only use the following managed request policies…")
                // only surfaced on a FROM-SCRATCH deploytenant — found by the
                // teardown-redeploy drill, 2026-07-12. S3 serves static SPA
                // files here and needs no viewer headers; CORS-S3Origin is the
                // appropriate legal policy.
                new DistributionOrderedCacheBehaviorArgs
                {
                    PathPattern = "/authentication/*",
                    TargetOriginId = "s3-assets",
                    ViewerProtocolPolicy = "redirect-to-https",
                    AllowedMethods = { "GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE" },
                    CachedMethods = { "GET", "HEAD" },
                    Compress = true,
                    CachePolicyId = "4135ea2d-6df8-44a3-9df3-4b5a84be39ad", // CachingDisabled
                    OriginRequestPolicyId = "88a5eaf4-2fd4-4709-b370-b4c650ea3fcf", // CORS-S3Origin (S3-origin-legal)
                    FunctionAssociations =
                    {
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-request", FunctionArn = authFn.Arn,
                        },
                        // CORS for localhost dev — same rationale as /auth/*.
                        new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                        {
                            EventType = "viewer-response", FunctionArn = responseFn.Arn,
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
        };

        // BFF behavior — routes /bff/* to the API origin (the container).
        // ADDED ONLY when the BFF is enabled for this tenant, so a non-BFF
        // distribution's ordered-behaviors list (and therefore its config) is
        // byte-for-byte identical to today. The Backend-For-Frontend auth
        // endpoints (/bff/login, /callback, /user, /logout, /ws-token —
        // MultiTenantAuth.md §8.3) live in AppHost, so this mirrors the /*Api/*
        // behavior: same API origin, CachingDisabled (auth responses MUST NOT be
        // edge-cached), AllViewerExceptHostHeader origin-request policy, and the
        // same CFRequest viewer-request function that injects the lz-config /
        // lz-tenantid tenancy headers (its "api" branch).
        //
        // NOTE: deliberately NOT /auth/* — that path is the CFAuth.js OIDC façade
        // (a different function) and never reaches the container. /bff/* is a
        // distinct pattern, so there is no collision with CFAuth.
        if (tenantConfig.BffEnabled == true)
        {
            distributionArgs.OrderedCacheBehaviors.Add(new DistributionOrderedCacheBehaviorArgs
            {
                PathPattern = "/bff/*",
                TargetOriginId = apiOrigin.OriginId,
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
                    // CORS for localhost dev — same rationale as /*Api/*.
                    new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-response", FunctionArn = responseFn.Arn,
                    },
                },
            });
        }

        // SECOND BFF pool — routes /cbff/* (consumerauth) to the API origin, mirroring /bff/*.
        // ADDED ONLY when the tenant wires the consumerauth instance (BffConsumerAuthEnabled), so
        // tenants without it keep a byte-for-byte identical behaviors list.
        if (tenantConfig.BffConsumerAuthEnabled == true)
        {
            distributionArgs.OrderedCacheBehaviors.Add(new DistributionOrderedCacheBehaviorArgs
            {
                PathPattern = "/cbff/*",
                TargetOriginId = apiOrigin.OriginId,
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
                    new DistributionOrderedCacheBehaviorFunctionAssociationArgs
                    {
                        EventType = "viewer-response", FunctionArn = responseFn.Arn,
                    },
                },
            });
        }

        // Topology hook: append extra ordered behaviors (e.g. a commerce path with a
        // cookie-forwarding cache policy). Base = none → byte-identical for existing tenants.
        foreach (var extra in BuildExtraBehaviors(tenantConfig, apiOrigin, requestFn, responseFn))
            distributionArgs.OrderedCacheBehaviors.Add(extra);

        var distribution = new Distribution($"{prefix}-cf-dist", distributionArgs,
            new CustomResourceOptions { Parent = this });

        // Topology hook: grant CloudFront access to the API origin (Lambda adds
        // the Function-URL invoke permission scoped to this distribution; the
        // ECSExpress ALB origin is public so the base implementation is a no-op).
        ConfigureApiOriginAccess(prefix, distribution, compute);

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

        // Route 53 aliases — apex + wildcard only. The wildcard covers every
        // first-level subtenant domain, so subtenants are not enumerated here.
        var zoneId = publicZone.Apply(z => z.ZoneId);
        CreateAliasRecord($"{prefix}-cf-alias", zoneId, domain, distribution);
        CreateAliasRecord($"{prefix}-cf-alias-wildcard", zoneId, $"*.{domain}", distribution);

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

        var jsCode = Lz.Aws.Shared.CfFunctionCodePrep.PrepareAndValidate(jsPath, jsFileName);

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

        var jsCode = kvsArn.Apply(arn =>
            Lz.Aws.Shared.CfFunctionCodePrep.PrepareAndValidate(
                jsPath, jsFileName, ("${KvsArn}", arn)));

        return new Pulumi.Aws.CloudFront.Function($"{prefix}-{name}-fn", new FunctionArgs
        {
            Runtime = "cloudfront-js-2.0", Code = jsCode,
            Comment = comment, Publish = true, KeyValueStoreAssociations = { kvsArn },
        }, new CustomResourceOptions { Parent = this });
    }
}
