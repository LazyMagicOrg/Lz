using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Ecs;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Platform factory for AWS AppRunner topology.
/// Creates AppRunner-specific component implementations.
/// Uses DynamoDB (not RDS), Cognito (not Keycloak), S3 (not EFS).
/// Reuses SES email component from ECS topology.
/// </summary>
public class AwsAppRunnerPlatformFactory : IPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsAppRunnerPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    public ISystemNetworkComponent CreateNetwork()
        => new AwsAppRunnerNetworkComponent();

    public IDatabaseComponent CreateDatabase()
        => new AwsAppRunnerDynamoDbComponent();

    public IFileStorageComponent CreateFileStorage()
        => new AwsAppRunnerFileStorageComponent();

    public IComputeEnvironmentComponent CreateComputeEnvironment()
        => new AwsAppRunnerComputeComponent();

    public IServiceComponent CreateService()
        => new AwsAppRunnerServiceComponent(_config);

    public IAuthServiceComponent CreateAuthService()
        => new AwsAppRunnerCognitoComponent();

    public IEmailComponent CreateEmail()
        => new AwsSesComponent();

    public ITenantCdnComponent CreateTenantCdn()
        => new AwsAppRunnerCloudFrontComponent();

    public ITenantDataComponent CreateTenantData()
        => new AwsAppRunnerTenantDataComponent();

    public ITenantServiceComponent CreateTenantService()
        => new AwsAppRunnerTenantServiceComponent();
    public void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network) { }
    public Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig) => Task.CompletedTask;

    // AppRunner doesn't use Tailscale VPN
    public ITailscaleComponent? CreateTailscale() => null;

    // Foundation post-deploy: create system-level DynamoDB table
    public IPostDeployAction? GetFoundationPostDeployAction()
        => new Lz.Aws.EcsExpress.AwsEcsExpressFoundationPostDeployAction(_config);

    // No Tailscale
    public IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null) => null;
    public ITailscaleKeyManager? GetTailscaleKeyManager() => null;

    // No Keycloak — uses Cognito
    public ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;

    public IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system)
    {
        var foundationServices = system.FoundationLayerServices;
        if (foundationServices.Count == 0 || !foundationServices.Any(s => s.Docker != null))
            return null;
        return new AwsAppRunnerPostDeployAction(_config, system, foundationServices);
    }

    public IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsAppRunnerPostDeployAction(_config, system, services, tenantKey, tenantConfig);

    public ITransitionChecker CreateTransitionChecker()
        => new AwsAppRunnerTransitionChecker(_config);

    // No Lambda gate-checker needed (no EFS/RDS to verify)
    public IGateCheckerComponent? CreateGateChecker() => null;

    // No config init needed (no EFS config files, no RDS tenant databases)
    public IConfigInitRunner? GetConfigInitRunner() => null;

    // No post-seed runner (no seed process for DynamoDB)
    public IPostSeedRunner? GetPostSeedRunner() => null;

    // No admin setup runner (handled differently for Cognito)
    public IAdminSetupRunner? GetAdminSetupRunner() => null;

    // No seed task (no EFS/RDS seed infrastructure)
    public ISeedTaskComponent? CreateSeedTask() => null;

    // No shared seed bucket needed
    public string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsAppRunnerFoundationLookup.Lookup(config);
}
