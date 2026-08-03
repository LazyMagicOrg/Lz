using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Ecs;
using Lz.Aws.Interfaces;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Platform factory for AWS AppRunner topology.
/// Creates AppRunner-specific component implementations.
/// Uses DynamoDB (not RDS), Cognito (not Keycloak), S3 (not EFS).
/// Reuses SES email component from ECS topology.
/// </summary>
public class AwsAppRunnerPlatformFactory : IAwsPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsAppRunnerPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    public virtual ISystemNetworkComponent CreateNetwork()
        => new AwsAppRunnerNetworkComponent();

    public virtual IDatabaseComponent CreateDatabase()
        => new AwsAppRunnerDynamoDbComponent();

    public virtual IFileStorageComponent CreateFileStorage()
        => new AwsAppRunnerFileStorageComponent();

    public virtual IComputeEnvironmentComponent CreateComputeEnvironment()
        => new AwsAppRunnerComputeComponent();

    public virtual IServiceComponent CreateService()
        => new AwsAppRunnerServiceComponent(_config);

    public virtual IAuthServiceComponent CreateAuthService()
        => new AwsAppRunnerCognitoComponent();

    public virtual IEmailComponent CreateEmail()
        => new AwsSesComponent();

    public virtual ITenantCdnComponent CreateTenantCdn()
        => new AwsAppRunnerCloudFrontComponent();

    public virtual ITenantDataComponent CreateTenantData()
        => new AwsAppRunnerTenantDataComponent();

    public virtual ITenantServiceComponent CreateTenantService()
        => new AwsAppRunnerTenantServiceComponent();
    public virtual void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network, ICdnOutputs? cdn = null) { }
    public virtual Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig) => Task.CompletedTask;

    // AppRunner doesn't use Tailscale VPN
    public virtual ITailscaleComponent? CreateTailscale() => null;

    // Foundation post-deploy: create system-level DynamoDB table
    public virtual IPostDeployAction? GetFoundationPostDeployAction()
        => new Lz.Aws.EcsExpress.AwsEcsExpressFoundationPostDeployAction(_config);
    // deploysystem-phase hook: ensure the {SystemKey} system table (idempotent).
    public virtual IPostDeployAction? GetSystemPostDeployAction()
        => new Lz.Aws.EcsExpress.AwsEcsExpressFoundationPostDeployAction(_config);

    // No Tailscale
    public virtual IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null) => null;
    public virtual ITailscaleKeyManager? GetTailscaleKeyManager() => null;

    // No Keycloak — uses Cognito
    public virtual ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;

    public virtual IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system)
    {
        var foundationServices = system.FoundationLayerServices;
        if (foundationServices.Count == 0 || !foundationServices.Any(s => s.Docker != null))
            return null;
        return new AwsAppRunnerPostDeployAction(_config, system, foundationServices);
    }

    public virtual IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsAppRunnerPostDeployAction(_config, system, services, tenantKey, tenantConfig);

    public virtual ITransitionChecker CreateTransitionChecker()
        => new AwsAppRunnerTransitionChecker(_config);

    // No Lambda gate-checker needed (no EFS/RDS to verify)
    public virtual IGateCheckerComponent? CreateGateChecker() => null;

    // No config init needed (no EFS config files, no RDS tenant databases)
    public virtual IConfigInitRunner? GetConfigInitRunner() => null;

    // No post-seed runner (no seed process for DynamoDB)
    public virtual IPostSeedRunner? GetPostSeedRunner() => null;

    // No admin setup runner (handled differently for Cognito)
    public virtual IAdminSetupRunner? GetAdminSetupRunner() => null;

    // No seed task (no EFS/RDS seed infrastructure)
    public virtual ISeedTaskComponent? CreateSeedTask() => null;

    // No shared seed bucket needed
    public virtual string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public virtual (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsAppRunnerFoundationLookup.Lookup(config);
}
