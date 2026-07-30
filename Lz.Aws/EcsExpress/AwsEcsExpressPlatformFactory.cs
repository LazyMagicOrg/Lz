using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // Reuse DynamoDB, FileStorage, Cognito, TenantData, TransitionChecker
using Lz.Aws.Ecs;       // Reuse SES
using Lz.Aws.Interfaces;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Platform factory for ECSExpress topology.
/// ECS Fargate in public subnets (no NAT) + DynamoDB + Cognito + CloudFront KVS.
/// Reuses data/auth components from AppRunner topology.
/// </summary>
public class AwsEcsExpressPlatformFactory : IAwsPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsEcsExpressPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    // ECSExpress-specific components
    public virtual ISystemNetworkComponent CreateNetwork() => new AwsEcsExpressNetworkComponent();
    public virtual IComputeEnvironmentComponent CreateComputeEnvironment() => new AwsEcsExpressComputeComponent();
    public virtual ITenantServiceComponent CreateTenantService() => new AwsEcsExpressTenantServiceComponent();
    public virtual ITenantCdnComponent CreateTenantCdn() => new AwsEcsExpressCloudFrontComponent();
    public virtual void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network, ICdnOutputs? cdn = null) { }
    public virtual Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig) => Task.CompletedTask;

    // Reused from AppRunner topology (DynamoDB, S3/Secrets, Cognito, stub FileStorage)
    public virtual IDatabaseComponent CreateDatabase() => new AwsAppRunnerDynamoDbComponent();
    public virtual IFileStorageComponent CreateFileStorage() => new AwsAppRunnerFileStorageComponent();
    public virtual IAuthServiceComponent CreateAuthService() => new AwsAppRunnerCognitoComponent();
    public virtual ITenantDataComponent CreateTenantData() => new AwsAppRunnerTenantDataComponent();
    public virtual IEmailComponent CreateEmail() => new AwsSesComponent();
    public virtual IServiceComponent CreateService() => new AwsAppRunnerServiceComponent(_config);

    // No Tailscale, no Keycloak
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

    public virtual IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsEcsExpressPostDeployAction(_config, services, tenantKey, tenantConfig);

    public virtual ITransitionChecker CreateTransitionChecker()
        => new AwsAppRunnerTransitionChecker(_config);

    // No Lambda gate-checker, no seed tasks, no config init
    public virtual IGateCheckerComponent? CreateGateChecker() => null;
    public virtual IConfigInitRunner? GetConfigInitRunner() => null;
    public virtual IPostSeedRunner? GetPostSeedRunner() => null;
    public virtual IAdminSetupRunner? GetAdminSetupRunner() => null;
    public virtual ISeedTaskComponent? CreateSeedTask() => null;
    public virtual string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public virtual (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsEcsExpressFoundationLookup.Lookup(config);
}
