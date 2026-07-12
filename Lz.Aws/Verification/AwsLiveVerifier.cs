using Amazon;
using Amazon.CertificateManager;
using Amazon.CertificateManager.Model;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.CloudFrontKeyValueStore;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.CognitoIdentity;
using Amazon.CognitoIdentity.Model;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Lz.Core.Config;
using Lz.Core.Definitions;
// Amazon.CloudFront.Model also defines a TenantConfig — ours wins.
using TenantConfig = Lz.Core.Config.TenantConfig;

namespace Lz.Aws.Verification;

/// <summary>
/// Live-AWS interrogation for `lz verify`: enumerates the resources the
/// active topology is EXPECTED to have created — derived purely from
/// config naming conventions, never from Pulumi state (so it works after a
/// destroy, when state is gone) — and checks each against live AWS with
/// read-only Describe/List/Get calls.
///
/// Each expected resource is classified (<see cref="ResourceCategory"/>):
///   Stack      — Pulumi-managed; present while deployed, gone after destroy.
///   Persistent — imperative layer; survives destroy by design.
///
/// Naming conventions are mirrored from the components that create the
/// resources (AwsAppRunnerCognitoComponent, AwsAppRunnerTenantDataComponent,
/// AwsLambdaTenantServiceComponent, AwsEcsExpressCloudFrontComponent,
/// SubtenantBucketManager/Provisioner, EcrDeployer call sites, BffWiring).
/// If a component's naming changes, this catalog must change with it.
///
/// Currently modeled: topology `lambda-cognito-dynamodb` only, and only
/// Host-layer tenant services (MagicPets: apphost). Systems that also deploy
/// ServiceLayer tenant services need those added to the catalog.
/// Read-only guarantee: no call here mutates AWS or Pulumi state.
/// </summary>
public static class AwsLiveVerifier
{
    // Mirrors AwsAppRunnerCognitoComponent.DomainPrefixMap.
    private static readonly Dictionary<string, string> DomainPrefixMap = new()
    {
        ["tenantauth"] = "auth",
        ["plannerauth"] = "auth-planner",
        ["systemauth"] = "auth-system",
    };

    public static bool SupportsTopology(string topology)
        => topology == "lambda-cognito-dynamodb";

    /// <summary>
    /// Run every check for the system + the given tenants. Results are
    /// returned in catalog order (foundation, then per-tenant stack, then
    /// persistent layer).
    /// </summary>
    public static async Task<List<ResourceCheckResult>> VerifyAsync(
        SystemConfig config,
        IReadOnlyList<(string TenantKey, TenantConfig Config)> tenants,
        SystemDefinition system,
        CancellationToken ct)
    {
        if (!SupportsTopology(config.Topology))
            throw new NotSupportedException(
                $"lz verify models topology 'lambda-cognito-dynamodb' only; " +
                $"'{config.Topology}' is not yet cataloged. Add its resource " +
                "conventions to AwsLiveVerifier before verifying it.");

        var ctx = new VerifyContext(config, system);
        var checks = new List<Func<Task<ResourceCheckResult>>>();

        AddFoundationChecks(checks, ctx);
        foreach (var (tk, tc) in tenants)
        {
            // Tenant checks run under the TENANT-effective profile/region —
            // TenantConfig.Profile/Region overrides are honored by every
            // resource-creating path (deploycontainer, deploytenant pre-flight,
            // deploywebapp, deploystaticsite), so verify must look in the same
            // account/region those paths wrote to.
            var tctx = ctx.ForTenant(tc);
            AddTenantStackChecks(checks, tctx, tk, tc);
            AddTenantPersistentChecks(checks, tctx, tk, tc);
        }
        AddSystemPersistentChecks(checks, ctx);

        // Checks are independent read-only calls; run them concurrently but
        // keep result order deterministic (catalog order, not finish order).
        var tasks = checks.Select(c => c()).ToList();
        await Task.WhenAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    // =====================================================================
    // Catalog — foundation stack (lzm-dev)
    // =====================================================================

    private static void AddFoundationChecks(
        List<Func<Task<ResourceCheckResult>>> checks, VerifyContext ctx)
    {
        var (sk, env, ss) = (ctx.Config.SystemKey, ctx.Config.Environment, ctx.Config.SystemSuffix);
        var domain = ctx.Config.SystemDomain;
        var authTypes = ctx.Config.AuthConfigs?.Keys.ToList() ?? new List<string>();

        foreach (var auth in authTypes)
        {
            // AwsAppRunnerCognitoComponent: Name = {sk}-{suffix}-{env}-{authType}
            var poolName = $"{sk}-{ss}-{env}-{auth}";
            checks.Add(() => ctx.CheckUserPool(poolName));

            // Custom domain {prefix}.{SystemDomain} (tenantauth → "auth").
            var domainPrefix = DomainPrefixMap.GetValueOrDefault(auth, $"auth-{auth}");
            var customDomain = $"{domainPrefix}.{domain}";
            checks.Add(() => ctx.CheckUserPoolDomain(customDomain));

            // Per-pool us-east-1 ACM cert backing the custom domain.
            checks.Add(() => ctx.CheckAcmCert(customDomain, usEast1: true,
                kind: "acm-cert (cognito domain, us-east-1)"));

            // Identity pool {sk}_{suffix}_{env}_{authType}
            checks.Add(() => ctx.CheckIdentityPool($"{sk}_{ss}_{env}_{auth}"));

            // LogGroup /aws/cognito/{sk}-{env}-{authType}
            checks.Add(() => ctx.CheckLogGroup(
                $"/aws/cognito/{sk}-{env}-{auth}", ResourceCategory.Stack));
        }

        // Regional ACM cert for the system domain (AwsAppRunnerNetworkComponent).
        checks.Add(() => ctx.CheckAcmCert(domain, usEast1: false,
            kind: "acm-cert (regional)"));
    }

    // =====================================================================
    // Catalog — tenant stack (lzm-mp-dev)
    // =====================================================================

    private static void AddTenantStackChecks(
        List<Func<Task<ResourceCheckResult>>> checks, VerifyContext ctx,
        string tk, TenantConfig tc)
    {
        var (sk, env) = (ctx.Config.SystemKey, ctx.Config.Environment);
        var ts = tc.TenantSuffix;
        var rootDomain = tc.RootDomain;
        var prefix = $"{sk}-{tk}";

        foreach (var svc in ctx.System.HostLayerServices)
        {
            // AwsLambdaTenantServiceComponent: function {sk}-{tk}-{svc}
            checks.Add(() => ctx.CheckLambdaFunction($"{prefix}-{svc.Name}"));
            checks.Add(() => ctx.CheckLambdaFunctionUrl($"{prefix}-{svc.Name}"));
            // Exec role: Pulumi logical name {sk}-{tk}-{svc}-exec with NO explicit
            // RoleName, so AWS auto-names it {sk}-{tk}-{svc}-exec-{suffix} — prefix match.
            checks.Add(() => ctx.CheckIamRoleByPrefix($"{prefix}-{svc.Name}-exec"));
        }

        // AwsEcsExpressCloudFrontComponent (Lambda CDN subclasses it):
        checks.Add(() => ctx.CheckDistributionByAlias(rootDomain));
        // One check per edge function (set mirrors the component; the physical
        // names are Pulumi auto-names, {sk}-{tk}-{name}-fn-{suffix}). Individual
        // checks make BOTH --expect verdicts correct on a partial set — a single
        // aggregated any-match check cannot.
        foreach (var fn in new[] { "request", "authconfig", "explore", "auth", "auth-callback", "response" })
            checks.Add(() => ctx.CheckCloudFrontFunction($"{prefix}-{fn}-fn"));
        checks.Add(() => ctx.CheckKeyValueStore($"{prefix}-kvs"));
        checks.Add(() => ctx.CheckOriginAccessControl($"{prefix}-oac"));
        checks.Add(() => ctx.CheckCachePolicy($"{prefix}-cache-host-keyed-{env}"));

        // AwsAppRunnerTenantDataComponent: tenant secret — SecretPrefix or {sk}/{tk};
        // RecoveryWindowInDays=0 on non-prod means NO tombstone should ever linger.
        var secretName = tc.SecretsManager?.SecretPrefix ?? $"{sk}/{tk}";
        checks.Add(() => ctx.CheckSecret(secretName));

        // ForceDestroy=dev assets buckets (content + bucket die with the stack).
        checks.Add(() => ctx.CheckBucket($"{sk}---assets-{ts}", ResourceCategory.Stack,
            "s3-bucket (system assets)"));
        checks.Add(() => ctx.CheckBucket($"{sk}-{tk}--assets-{ts}", ResourceCategory.Stack,
            "s3-bucket (tenant assets)"));
        checks.Add(() => ctx.CheckBucket($"{prefix}-{ts}-{env}-assets", ResourceCategory.Stack,
            "s3-bucket (cdn assets)"));

        // Route53: apex + wildcard A-aliases → the distribution (stack-owned).
        checks.Add(() => ctx.CheckRoute53Record(rootDomain, rootDomain, "A",
            "route53-record (apex A)"));
        checks.Add(() => ctx.CheckRoute53Record(rootDomain, $"*.{rootDomain}", "A",
            "route53-record (wildcard A)"));

        // us-east-1 ACM cert for the CDN (apex + wildcard SANs).
        checks.Add(() => ctx.CheckAcmCert(rootDomain, usEast1: true,
            kind: "acm-cert (cdn, us-east-1)"));
    }

    // =====================================================================
    // Catalog — persistent layer (survives destroy)
    // =====================================================================

    private static void AddTenantPersistentChecks(
        List<Func<Task<ResourceCheckResult>>> checks, VerifyContext ctx,
        string tk, TenantConfig tc)
    {
        var (sk, env, ss) = (ctx.Config.SystemKey, ctx.Config.Environment, ctx.Config.SystemSuffix);
        var ts = tc.TenantSuffix;
        var prefix = $"{sk}-{tk}";

        foreach (var svc in ctx.System.HostLayerServices)
        {
            // ECR repo {sk}-{TenantSuffix}-{env}-{tk}-{svc} + :latest image
            // (deploytenant's pre-flight gate requires the image — redeploy-critical).
            checks.Add(() => ctx.CheckEcrRepoAndLatest($"{sk}-{ts}-{env}-{tk}-{svc.Name}"));

            // Runtime-created Lambda log group (never in Pulumi state).
            checks.Add(() => ctx.CheckLogGroup(
                $"/aws/lambda/{prefix}-{svc.Name}", ResourceCategory.Persistent));
        }

        // DynamoDB (DynamoDbTableCreator: "Tables are persistent — not deleted on destroy.")
        checks.Add(() => ctx.CheckDynamoTable($"{sk}_{tk}", "table (tenant data)"));
        checks.Add(() => ctx.CheckDynamoTable($"{sk}_{tk}_bff", "table (bff sessions)"));
        if (tc.BffConsumerAuthEnabled == true)
            checks.Add(() => ctx.CheckDynamoTable($"{sk}_{tk}_cbff", "table (cbff sessions)"));

        foreach (var stk in tc.Subtenants?.Keys ?? Enumerable.Empty<string>())
        {
            checks.Add(() => ctx.CheckDynamoTable($"{sk}_{tk}_{stk}", "table (subtenant)"));
            // SubtenantProvisioner uses the SYSTEM suffix for subtenant buckets.
            checks.Add(() => ctx.CheckBucket(
                Shared.SubtenantBucketManager.BucketName(sk, tk, stk, ss),
                ResourceCategory.Persistent, "s3-bucket (subtenant assets)"));
        }

        // Static-site buckets (lz deploystaticsite / deployassets): tenant-level
        // {sk}-{tk}--webapp-{app}-{ts} + per-subtenant {sk}-{tk}-{stk}-webapp-{app}-{ts}.
        foreach (var app in StaticSiteAppNames(tc))
        {
            checks.Add(() => ctx.CheckBucket($"{prefix}--webapp-{app}-{ts}",
                ResourceCategory.Persistent, "s3-bucket (static site)"));
            foreach (var stk in tc.Subtenants?.Keys ?? Enumerable.Empty<string>())
                checks.Add(() => ctx.CheckBucket($"{prefix}-{stk}-webapp-{app}-{ts}",
                    ResourceCategory.Persistent, "s3-bucket (static site, subtenant)"));
        }

        // Hosted zone for the tenant root domain (pre-existing, never lz-managed).
        checks.Add(() => ctx.CheckHostedZone(tc.RootDomain));
    }

    private static void AddSystemPersistentChecks(
        List<Func<Task<ResourceCheckResult>>> checks, VerifyContext ctx)
    {
        var (sk, env, ss) = (ctx.Config.SystemKey, ctx.Config.Environment, ctx.Config.SystemSuffix);

        // System DynamoDB table {sk}. NOTE: on the lambda topology no lz command
        // recreates this table (foundation post-deploy only runs via the shared
        // layer) — its survival is load-bearing.
        checks.Add(() => ctx.CheckDynamoTable(sk, "table (system — never recreated by lz)"));

        // System-wide webapp buckets {sk}---webapp-{app}-{ss} (lz deploywebapp,
        // non-central-auth topologies), one per distinct WebApp behavior.
        foreach (var app in WebAppNames(ctx.Config))
            checks.Add(() => ctx.CheckBucket($"{sk}---webapp-{app}-{ss}",
                ResourceCategory.Persistent, "s3-bucket (webapp)"));

        // BFF Data Protection key ring (written by the app at runtime — BffWiring).
        checks.Add(() => ctx.CheckSsmParameter($"/{sk}/{env}/bff/dataprotection"));

        // Pulumi state backend (AwsStateBootstrapper) — always generated config.
        if (ctx.Config.State is { } state)
        {
            checks.Add(() => ctx.CheckBucket(
                AwsStateBootstrapper.ParseBucketName(state.Backend),
                ResourceCategory.Persistent, "s3-bucket (pulumi state)"));
            checks.Add(() => ctx.CheckKmsAlias(
                AwsStateBootstrapper.ParseKmsAlias(state.SecretsProvider)));
        }
    }

    private static IEnumerable<string> WebAppNames(SystemConfig config)
        => (config.Behaviors?.WebApps ?? new List<WebAppBehavior>())
            .Select(w => w.AppName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.ToLowerInvariant())
            .Distinct();

    private static IEnumerable<string> StaticSiteAppNames(TenantConfig tc)
        => (tc.Behaviors?.StaticSites ?? new List<StaticSiteBehavior>())
            .Select(s => s.AppName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!.ToLowerInvariant())
            .Distinct();

    // =====================================================================
    // Check implementations
    // =====================================================================

    private sealed class VerifyContext
    {
        public SystemConfig Config { get; }
        public SystemDefinition System { get; }

        private readonly AWSCredentials? _creds;
        private readonly RegionEndpoint _region;
        private static readonly RegionEndpoint UsEast1 = RegionEndpoint.USEast1;

        public VerifyContext(SystemConfig config, SystemDefinition system)
            : this(config, system, config.Profile, config.Region) { }

        private VerifyContext(
            SystemConfig config, SystemDefinition system, string? profile, string region)
        {
            Config = config;
            System = system;
            _region = RegionEndpoint.GetBySystemName(region);

            if (!string.IsNullOrEmpty(profile))
            {
                var chain = new CredentialProfileStoreChain();
                if (!chain.TryGetAWSCredentials(profile, out var credentials))
                    // A configured-but-unresolvable profile must be an ERROR, not a
                    // silent fall-through to the default chain — verify verdicts
                    // against the wrong AWS account are worse than no verdict.
                    // (global:: — Amazon.CloudWatchLogs.Model also has this type name.)
                    throw new global::System.InvalidOperationException(
                        $"AWS profile '{profile}' could not be resolved from the " +
                        "credential store. Refusing to fall back to the default " +
                        "credential chain — verify would interrogate whatever " +
                        "account that chain points at.");
                _creds = credentials;
            }
        }

        /// <summary>
        /// Context for one tenant, honoring TenantConfig.Profile/Region overrides
        /// exactly like the resource-creating paths do (ConfigMerger semantics).
        /// </summary>
        public VerifyContext ForTenant(TenantConfig tc)
            => new(Config, System,
                ConfigMerger.GetEffectiveProfile(Config, tc),
                ConfigMerger.GetEffectiveRegion(Config, tc));

        private T Client<T>(Func<AWSCredentials, RegionEndpoint, T> withCreds,
            Func<RegionEndpoint, T> withDefault, bool usEast1 = false)
        {
            var region = usEast1 ? UsEast1 : _region;
            return _creds != null ? withCreds(_creds, region) : withDefault(region);
        }

        private static async Task<ResourceCheckResult> Run(
            ResourceCategory category, string service, string kind, string name,
            Func<Task<(ResourceState State, string? Detail)>> probe)
        {
            try
            {
                var (state, detail) = await probe();
                return new ResourceCheckResult(category, service, kind, name, state, detail);
            }
            catch (Exception ex)
            {
                return new ResourceCheckResult(category, service, kind, name,
                    ResourceState.Error, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- S3 ----------------------------------------------------------

        public Task<ResourceCheckResult> CheckBucket(
            string name, ResourceCategory category, string kind) =>
            Run(category, "s3", kind, name, async () =>
            {
                using var s3 = Client(
                    (c, r) => new AmazonS3Client(c, r), r => new AmazonS3Client(r));
                try
                {
                    await s3.GetBucketLocationAsync(name);
                    return (ResourceState.Present, null);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == global::System.Net.HttpStatusCode.NotFound)
                {
                    return (ResourceState.Absent, null);
                }
            });

        // ---- DynamoDB ----------------------------------------------------

        public Task<ResourceCheckResult> CheckDynamoTable(string name, string kind) =>
            Run(ResourceCategory.Persistent, "dynamodb", kind, name, async () =>
            {
                using var ddb = Client(
                    (c, r) => new AmazonDynamoDBClient(c, r), r => new AmazonDynamoDBClient(r));
                try
                {
                    var resp = await ddb.DescribeTableAsync(name);
                    return (ResourceState.Present, $"status={resp.Table.TableStatus}");
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });

        // ---- Lambda ------------------------------------------------------

        public Task<ResourceCheckResult> CheckLambdaFunction(string name) =>
            Run(ResourceCategory.Stack, "lambda", "function", name, async () =>
            {
                using var lambda = Client(
                    (c, r) => new AmazonLambdaClient(c, r), r => new AmazonLambdaClient(r));
                try
                {
                    var resp = await lambda.GetFunctionAsync(
                        new Amazon.Lambda.Model.GetFunctionRequest { FunctionName = name });
                    return (ResourceState.Present, $"state={resp.Configuration.State}");
                }
                catch (Amazon.Lambda.Model.ResourceNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });

        public Task<ResourceCheckResult> CheckLambdaFunctionUrl(string functionName) =>
            Run(ResourceCategory.Stack, "lambda", "function-url", functionName, async () =>
            {
                using var lambda = Client(
                    (c, r) => new AmazonLambdaClient(c, r), r => new AmazonLambdaClient(r));
                try
                {
                    var resp = await lambda.GetFunctionUrlConfigAsync(
                        new GetFunctionUrlConfigRequest { FunctionName = functionName });
                    return (ResourceState.Present, resp.FunctionUrl);
                }
                catch (Amazon.Lambda.Model.ResourceNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });

        // ---- IAM ---------------------------------------------------------

        /// <summary>
        /// Roles created without an explicit RoleName get Pulumi's auto-name
        /// (logical name + random suffix) — match by prefix.
        /// </summary>
        public Task<ResourceCheckResult> CheckIamRoleByPrefix(string prefix) =>
            Run(ResourceCategory.Stack, "iam", "role", $"{prefix}*", async () =>
            {
                using var iam = Client(
                    (c, r) => new AmazonIdentityManagementServiceClient(c, r),
                    r => new AmazonIdentityManagementServiceClient(r));
                var found = new List<string>();
                string? marker = null;
                do
                {
                    var resp = await iam.ListRolesAsync(new ListRolesRequest { Marker = marker });
                    found.AddRange((resp.Roles ?? new List<Role>())
                        .Select(r => r.RoleName)
                        .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)));
                    marker = resp.IsTruncated == true ? resp.Marker : null;
                } while (marker != null);
                return found.Count > 0
                    ? (ResourceState.Present, string.Join(", ", found))
                    : (ResourceState.Absent, null);
            });

        // ---- CloudFront (global control plane) ----------------------------

        public Task<ResourceCheckResult> CheckDistributionByAlias(string alias) =>
            Run(ResourceCategory.Stack, "cloudfront", "distribution", alias, async () =>
            {
                using var cf = Client(
                    (c, r) => new AmazonCloudFrontClient(c, r),
                    r => new AmazonCloudFrontClient(r), usEast1: true);
                string? marker = null;
                do
                {
                    var resp = await cf.ListDistributionsAsync(
                        new ListDistributionsRequest { Marker = marker });
                    var hit = resp.DistributionList.Items?.FirstOrDefault(d =>
                        d.Aliases?.Items?.Contains(alias) == true);
                    if (hit != null)
                        return (ResourceState.Present,
                            $"id={hit.Id} status={hit.Status} enabled={hit.Enabled}");
                    marker = resp.DistributionList.IsTruncated == true
                        ? resp.DistributionList.NextMarker : null;
                } while (marker != null);
                return (ResourceState.Absent, null);
            });

        /// <summary>
        /// One CloudFront Function, matched by its auto-name prefix
        /// ({sk}-{tk}-{name}-fn → "{sk}-{tk}-{name}-fn-{suffix}"). Exact-prefix
        /// plus separator so "…-auth-fn" never matches "…-auth-callback-fn…".
        /// </summary>
        public Task<ResourceCheckResult> CheckCloudFrontFunction(string namePrefix) =>
            Run(ResourceCategory.Stack, "cloudfront", "function", $"{namePrefix}*", async () =>
            {
                using var cf = Client(
                    (c, r) => new AmazonCloudFrontClient(c, r),
                    r => new AmazonCloudFrontClient(r), usEast1: true);
                var found = new List<string>();
                string? marker = null;
                do
                {
                    var resp = await cf.ListFunctionsAsync(
                        new Amazon.CloudFront.Model.ListFunctionsRequest { Marker = marker });
                    found.AddRange((resp.FunctionList.Items ?? new List<FunctionSummary>())
                        .Select(f => f.Name)
                        .Where(n => n == namePrefix
                                    || n.StartsWith(namePrefix + "-", StringComparison.Ordinal)));
                    marker = resp.FunctionList.NextMarker;
                } while (marker != null);
                // ListFunctions reports DEVELOPMENT and LIVE stages — dedup names.
                var names = found.Distinct().ToList();
                return names.Count > 0
                    ? (ResourceState.Present, string.Join(", ", names))
                    : (ResourceState.Absent, null);
            });

        public Task<ResourceCheckResult> CheckKeyValueStore(string name) =>
            Run(ResourceCategory.Stack, "cloudfront", "key-value-store", name, async () =>
            {
                using var cf = Client(
                    (c, r) => new AmazonCloudFrontClient(c, r),
                    r => new AmazonCloudFrontClient(r), usEast1: true);
                try
                {
                    await cf.DescribeKeyValueStoreAsync(
                        new DescribeKeyValueStoreRequest { Name = name });
                    return (ResourceState.Present, null);
                }
                catch (EntityNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });

        public Task<ResourceCheckResult> CheckOriginAccessControl(string name) =>
            Run(ResourceCategory.Stack, "cloudfront", "origin-access-control", name, async () =>
            {
                using var cf = Client(
                    (c, r) => new AmazonCloudFrontClient(c, r),
                    r => new AmazonCloudFrontClient(r), usEast1: true);
                string? marker = null;
                do
                {
                    var resp = await cf.ListOriginAccessControlsAsync(
                        new ListOriginAccessControlsRequest { Marker = marker });
                    if (resp.OriginAccessControlList.Items?.Any(o => o.Name == name) == true)
                        return (ResourceState.Present, null);
                    marker = resp.OriginAccessControlList.IsTruncated == true
                        ? resp.OriginAccessControlList.NextMarker : null;
                } while (marker != null);
                return (ResourceState.Absent, null);
            });

        public Task<ResourceCheckResult> CheckCachePolicy(string name) =>
            Run(ResourceCategory.Stack, "cloudfront", "cache-policy", name, async () =>
            {
                using var cf = Client(
                    (c, r) => new AmazonCloudFrontClient(c, r),
                    r => new AmazonCloudFrontClient(r), usEast1: true);
                string? marker = null;
                do
                {
                    var resp = await cf.ListCachePoliciesAsync(new ListCachePoliciesRequest
                    {
                        Type = CachePolicyType.Custom,
                        Marker = marker,
                    });
                    if (resp.CachePolicyList.Items?.Any(
                            p => p.CachePolicy.CachePolicyConfig.Name == name) == true)
                        return (ResourceState.Present, null);
                    marker = resp.CachePolicyList.NextMarker;
                } while (marker != null);
                return (ResourceState.Absent, null);
            });

        // ---- Cognito -----------------------------------------------------

        public Task<ResourceCheckResult> CheckUserPool(string name) =>
            Run(ResourceCategory.Stack, "cognito-idp", "user-pool", name, async () =>
            {
                using var idp = Client(
                    (c, r) => new AmazonCognitoIdentityProviderClient(c, r),
                    r => new AmazonCognitoIdentityProviderClient(r));
                string? token = null;
                do
                {
                    var resp = await idp.ListUserPoolsAsync(new ListUserPoolsRequest
                    {
                        MaxResults = 60,
                        NextToken = token,
                    });
                    var hit = resp.UserPools?.FirstOrDefault(p => p.Name == name);
                    if (hit != null)
                        return (ResourceState.Present, $"id={hit.Id}");
                    token = resp.NextToken;
                } while (token != null);
                return (ResourceState.Absent, null);
            });

        public Task<ResourceCheckResult> CheckUserPoolDomain(string domain) =>
            Run(ResourceCategory.Stack, "cognito-idp", "user-pool-domain", domain, async () =>
            {
                using var idp = Client(
                    (c, r) => new AmazonCognitoIdentityProviderClient(c, r),
                    r => new AmazonCognitoIdentityProviderClient(r));
                var resp = await idp.DescribeUserPoolDomainAsync(
                    new DescribeUserPoolDomainRequest { Domain = domain });
                // Absent domains come back as an empty description, not an exception.
                return string.IsNullOrEmpty(resp.DomainDescription?.Domain)
                    ? (ResourceState.Absent, null)
                    : (ResourceState.Present, $"status={resp.DomainDescription.Status}");
            });

        public Task<ResourceCheckResult> CheckIdentityPool(string name) =>
            Run(ResourceCategory.Stack, "cognito-identity", "identity-pool", name, async () =>
            {
                using var ci = Client(
                    (c, r) => new AmazonCognitoIdentityClient(c, r),
                    r => new AmazonCognitoIdentityClient(r));
                string? token = null;
                do
                {
                    var resp = await ci.ListIdentityPoolsAsync(new ListIdentityPoolsRequest
                    {
                        MaxResults = 60,
                        NextToken = token,
                    });
                    if (resp.IdentityPools?.Any(p => p.IdentityPoolName == name) == true)
                        return (ResourceState.Present, null);
                    token = resp.NextToken;
                } while (token != null);
                return (ResourceState.Absent, null);
            });

        // ---- CloudWatch Logs ----------------------------------------------

        public Task<ResourceCheckResult> CheckLogGroup(string name, ResourceCategory category) =>
            Run(category, "logs", "log-group", name, async () =>
            {
                using var logs = Client(
                    (c, r) => new AmazonCloudWatchLogsClient(c, r),
                    r => new AmazonCloudWatchLogsClient(r));
                var resp = await logs.DescribeLogGroupsAsync(new DescribeLogGroupsRequest
                {
                    LogGroupNamePrefix = name,
                });
                return resp.LogGroups?.Any(g => g.LogGroupName == name) == true
                    ? (ResourceState.Present, null)
                    : (ResourceState.Absent, null);
            });

        // ---- Secrets Manager ----------------------------------------------

        public Task<ResourceCheckResult> CheckSecret(string name) =>
            Run(ResourceCategory.Stack, "secretsmanager", "secret", name, async () =>
            {
                using var sm = Client(
                    (c, r) => new AmazonSecretsManagerClient(c, r),
                    r => new AmazonSecretsManagerClient(r));
                try
                {
                    var resp = await sm.DescribeSecretAsync(
                        new DescribeSecretRequest { SecretId = name });
                    // A tombstoned secret blocks redeploy with the same name —
                    // exactly what RecoveryWindowInDays=0 is meant to prevent.
                    return resp.DeletedDate.HasValue
                        ? (ResourceState.ScheduledForDeletion,
                           $"deletedDate={resp.DeletedDate:o}")
                        : (ResourceState.Present, null);
                }
                catch (Amazon.SecretsManager.Model.ResourceNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });

        // ---- Route53 ------------------------------------------------------

        public Task<ResourceCheckResult> CheckHostedZone(string domain) =>
            Run(ResourceCategory.Persistent, "route53", "hosted-zone", domain, async () =>
            {
                var zone = await FindZoneAsync(domain);
                return zone != null
                    ? (ResourceState.Present, $"id={zone.Id}")
                    : (ResourceState.Absent, null);
            });

        public Task<ResourceCheckResult> CheckRoute53Record(
            string zoneDomain, string recordName, string type, string kind) =>
            Run(ResourceCategory.Stack, "route53", kind, recordName, async () =>
            {
                var zone = await FindZoneAsync(zoneDomain);
                if (zone == null)
                    return (ResourceState.Absent, "hosted zone not found");

                using var r53 = Client(
                    (c, r) => new AmazonRoute53Client(c, r), r => new AmazonRoute53Client(r));
                // Route53 encodes '*' as \052 in record names.
                var wire = recordName.Replace("*", "\\052").TrimEnd('.') + ".";
                var resp = await r53.ListResourceRecordSetsAsync(new ListResourceRecordSetsRequest
                {
                    HostedZoneId = zone.Id,
                    StartRecordName = recordName,
                    StartRecordType = type,
                    MaxItems = "1",
                });
                var hit = resp.ResourceRecordSets?.FirstOrDefault(r =>
                    r.Name == wire && r.Type?.Value == type);
                return hit != null
                    ? (ResourceState.Present,
                       hit.AliasTarget != null ? $"alias={hit.AliasTarget.DNSName}" : null)
                    : (ResourceState.Absent, null);
            });

        private async Task<HostedZone?> FindZoneAsync(string domain)
        {
            using var r53 = Client(
                (c, r) => new AmazonRoute53Client(c, r), r => new AmazonRoute53Client(r));
            var resp = await r53.ListHostedZonesByNameAsync(new ListHostedZonesByNameRequest
            {
                DNSName = domain,
            });
            return resp.HostedZones?.FirstOrDefault(z =>
                z.Name == domain.TrimEnd('.') + "." && z.Config?.PrivateZone != true);
        }

        // ---- ACM ----------------------------------------------------------

        public Task<ResourceCheckResult> CheckAcmCert(string domain, bool usEast1, string kind) =>
            Run(ResourceCategory.Stack, "acm", kind, domain, async () =>
            {
                using var acm = Client(
                    (c, r) => new AmazonCertificateManagerClient(c, r),
                    r => new AmazonCertificateManagerClient(r), usEast1);
                var matches = new List<string>();
                string? token = null;
                do
                {
                    var resp = await acm.ListCertificatesAsync(new ListCertificatesRequest
                    {
                        // Default omits PENDING_VALIDATION etc.; we want any lingerers.
                        CertificateStatuses = new List<string>
                        {
                            "PENDING_VALIDATION", "ISSUED", "INACTIVE",
                            "EXPIRED", "VALIDATION_TIMED_OUT", "REVOKED", "FAILED",
                        },
                        NextToken = token,
                    });
                    matches.AddRange((resp.CertificateSummaryList ?? new List<CertificateSummary>())
                        .Where(c => c.DomainName == domain)
                        .Select(c => $"{c.CertificateArn!.Split('/').Last()}({c.Status})"));
                    token = resp.NextToken;
                } while (token != null);
                return matches.Count > 0
                    ? (ResourceState.Present, $"count={matches.Count}: {string.Join(", ", matches)}")
                    : (ResourceState.Absent, null);
            });

        // ---- ECR ----------------------------------------------------------

        public Task<ResourceCheckResult> CheckEcrRepoAndLatest(string repo) =>
            Run(ResourceCategory.Persistent, "ecr", "repository+latest", repo, async () =>
            {
                using var ecr = Client(
                    (c, r) => new AmazonECRClient(c, r), r => new AmazonECRClient(r));
                try
                {
                    await ecr.DescribeRepositoriesAsync(new DescribeRepositoriesRequest
                    {
                        RepositoryNames = new List<string> { repo },
                    });
                }
                catch (RepositoryNotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
                try
                {
                    await ecr.DescribeImagesAsync(new DescribeImagesRequest
                    {
                        RepositoryName = repo,
                        ImageIds = new List<ImageIdentifier> { new() { ImageTag = "latest" } },
                    });
                    return (ResourceState.Present, "repo + :latest image");
                }
                catch (ImageNotFoundException)
                {
                    return (ResourceState.Present,
                        "repo exists but NO :latest image — deploytenant pre-flight will fail");
                }
            });

        // ---- SSM ----------------------------------------------------------

        /// <summary>
        /// The BFF Data Protection path is a PARENT path — the app writes the
        /// actual key ring under it at runtime. Present = the exact parameter
        /// OR any child parameter exists.
        /// </summary>
        public Task<ResourceCheckResult> CheckSsmParameter(string name) =>
            Run(ResourceCategory.Persistent, "ssm", "parameter", name, async () =>
            {
                using var ssm = Client(
                    (c, r) => new AmazonSimpleSystemsManagementClient(c, r),
                    r => new AmazonSimpleSystemsManagementClient(r));
                try
                {
                    await ssm.GetParameterAsync(new GetParameterRequest { Name = name });
                    return (ResourceState.Present, "exact");
                }
                catch (ParameterNotFoundException)
                {
                    var resp = await ssm.GetParametersByPathAsync(new GetParametersByPathRequest
                    {
                        Path = name,
                        Recursive = true,
                        MaxResults = 1,
                    });
                    return resp.Parameters?.Count > 0
                        ? (ResourceState.Present, $"children under {name}")
                        : (ResourceState.Absent, null);
                }
            });

        // ---- KMS ----------------------------------------------------------

        public Task<ResourceCheckResult> CheckKmsAlias(string alias) =>
            Run(ResourceCategory.Persistent, "kms", "key-alias", alias, async () =>
            {
                using var kms = Client(
                    (c, r) => new AmazonKeyManagementServiceClient(c, r),
                    r => new AmazonKeyManagementServiceClient(r));
                try
                {
                    await kms.DescribeKeyAsync(new DescribeKeyRequest { KeyId = alias });
                    return (ResourceState.Present, null);
                }
                catch (Amazon.KeyManagementService.Model.NotFoundException)
                {
                    return (ResourceState.Absent, null);
                }
            });
    }
}
