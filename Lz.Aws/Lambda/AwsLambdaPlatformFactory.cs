using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // Reuse no-VPC network, DynamoDB, S3, Cognito, TenantData, compute lookup, transition checker
using Lz.Aws.Ecs;       // Reuse SES
using Lz.Aws.EcsExpress; // Reuse foundation post-deploy (creates the system DynamoDB table)
using Lz.Aws.Interfaces;

namespace Lz.Aws.Lambda;

/// <summary>
/// Platform factory for the lambda-cognito-dynamodb topology — a true serverless
/// variant of ecs-fargate-cognito-dynamodb. The per-tenant container Lambda (the
/// SAME image as Fargate) replaces the ECS task; a CloudFront-fronted Function URL
/// (OAC + AWS_IAM) replaces the ALB. The shared layers — Cognito, DynamoDB, S3, and
/// the CloudFront edge — reuse the same components (and ComponentResource
/// identities) as the Fargate/AppRunner topologies, so a deployed stack can be
/// switched in place between Fargate and Lambda. See Platform/LambdaTopology.md.
/// </summary>
public class AwsLambdaPlatformFactory : IAwsPlatformFactory
{
    private readonly SystemConfig _config;

    // Shared within one deploy: carries the per-tenant Function URL from the
    // tenant-service component to the CDN component. Keeps ITenantCdnComponent
    // unchanged so the other topologies are unaffected.
    private readonly AwsLambdaApiOriginHolder _originHolder = new();

    public AwsLambdaPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    // Lambda-specific components.
    public virtual IComputeEnvironmentComponent CreateComputeEnvironment() => new AwsLambdaComputeComponent();
    public virtual IServiceComponent CreateService() => new AwsLambdaServiceComponent();
    public virtual ITenantServiceComponent CreateTenantService() => new AwsLambdaTenantServiceComponent(_originHolder, _config);
    public virtual ITenantCdnComponent CreateTenantCdn() => new AwsLambdaCloudFrontComponent(_originHolder);

    // Reused from AppRunner (no-VPC network, DynamoDB, S3, Cognito, tenant data).
    public virtual ISystemNetworkComponent CreateNetwork() => new AwsAppRunnerNetworkComponent();
    public virtual IDatabaseComponent CreateDatabase() => new AwsAppRunnerDynamoDbComponent();
    public virtual IFileStorageComponent CreateFileStorage() => new AwsAppRunnerFileStorageComponent();
    public virtual IAuthServiceComponent CreateAuthService() => new AwsAppRunnerCognitoComponent();
    public virtual ITenantDataComponent CreateTenantData() => new AwsAppRunnerTenantDataComponent();
    public virtual IEmailComponent CreateEmail() => new AwsSesComponent();

    public virtual void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network, ICdnOutputs? cdn = null) { }
    public virtual Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig) => Task.CompletedTask;

    // No Tailscale / Keycloak / gate-checker / seed.
    public virtual ITailscaleComponent? CreateTailscale() => null;
    public virtual IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsEcsExpressFoundationPostDeployAction(_config);
    // deploysystem-phase hook: ensure the {SystemKey} system table (idempotent).
    public virtual IPostDeployAction? GetSystemPostDeployAction()
        => new AwsEcsExpressFoundationPostDeployAction(_config);
    public virtual IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null) => null;
    public virtual ITailscaleKeyManager? GetTailscaleKeyManager() => null;
    public virtual ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;
    public virtual IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system) => null;

    // The container image is built/pushed by `lz deploycontainer`; the per-tenant
    // Lambda references {ecr}:latest, so deploycontainer must run before deploytenant.
    // AwsLambdaPostDeployAction does the tenant-phase work: the EcsExpress table
    // creation + apex verification (reused with an empty services list — returning
    // null here once left fresh systems with no tenant/BFF tables and 500ing BFF
    // logins), PLUS a digest-compared UpdateFunctionCode roll of each host-layer
    // function so deploytenant leaves the tenant on current code — the same
    // guarantee the ECS topologies give via their task cycle (Lambda resolves the
    // image digest only at UpdateFunctionCode time; without the roll, a pushed
    // :latest is silently ignored).
    public virtual IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system, IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null, TenantConfig? tenantConfig = null)
        => tenantKey != null
            ? new AwsLambdaPostDeployAction(_config, services, tenantKey, tenantConfig)
            : null;

    public virtual ITransitionChecker CreateTransitionChecker() => new AwsAppRunnerTransitionChecker(_config);
    public virtual IGateCheckerComponent? CreateGateChecker() => null;
    public virtual IConfigInitRunner? GetConfigInitRunner() => null;
    public virtual IPostSeedRunner? GetPostSeedRunner() => null;
    public virtual IAdminSetupRunner? GetAdminSetupRunner() => null;
    public virtual ISeedTaskComponent? CreateSeedTask() => null;
    public virtual string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public virtual (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsAppRunnerFoundationLookup.Lookup(config);
}
