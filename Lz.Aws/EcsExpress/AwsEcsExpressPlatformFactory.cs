using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // Reuse DynamoDB, FileStorage, Cognito, TenantData, TransitionChecker
using Lz.Aws.Ecs;       // Reuse SES

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Platform factory for ECSExpress topology.
/// ECS Fargate in public subnets (no NAT) + DynamoDB + Cognito + CloudFront KVS.
/// Reuses data/auth components from AppRunner topology.
/// </summary>
public class AwsEcsExpressPlatformFactory : IPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsEcsExpressPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    // ECSExpress-specific components
    public ISystemNetworkComponent CreateNetwork() => new AwsEcsExpressNetworkComponent();
    public IComputeEnvironmentComponent CreateComputeEnvironment() => new AwsEcsExpressComputeComponent();
    public ITenantServiceComponent CreateTenantService() => new AwsEcsExpressTenantServiceComponent();
    public ITenantCdnComponent CreateTenantCdn() => new AwsEcsExpressCloudFrontComponent();
    public void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network, ICdnOutputs? cdn = null) { }
    public Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig) => Task.CompletedTask;

    // Reused from AppRunner topology (DynamoDB, S3/Secrets, Cognito, stub FileStorage)
    public IDatabaseComponent CreateDatabase() => new AwsAppRunnerDynamoDbComponent();
    public IFileStorageComponent CreateFileStorage() => new AwsAppRunnerFileStorageComponent();
    public IAuthServiceComponent CreateAuthService() => new AwsAppRunnerCognitoComponent();
    public ITenantDataComponent CreateTenantData() => new AwsAppRunnerTenantDataComponent();
    public IEmailComponent CreateEmail() => new AwsSesComponent();
    public IServiceComponent CreateService() => new AwsAppRunnerServiceComponent(_config);

    // No Tailscale, no Keycloak
    public ITailscaleComponent? CreateTailscale() => null;
    public IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsEcsExpressFoundationPostDeployAction(_config);
    public IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null) => null;
    public ITailscaleKeyManager? GetTailscaleKeyManager() => null;
    public ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;

    public IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system) => null;

    public IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsEcsExpressPostDeployAction(_config, services, tenantKey, tenantConfig);

    public ITransitionChecker CreateTransitionChecker()
        => new AwsAppRunnerTransitionChecker(_config);

    // No Lambda gate-checker, no seed tasks, no config init
    public IGateCheckerComponent? CreateGateChecker() => null;
    public IConfigInitRunner? GetConfigInitRunner() => null;
    public IPostSeedRunner? GetPostSeedRunner() => null;
    public IAdminSetupRunner? GetAdminSetupRunner() => null;
    public ISeedTaskComponent? CreateSeedTask() => null;
    public string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsEcsExpressFoundationLookup.Lookup(config);
}
