using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Lambda;
using Lz.Aws.Tailscale;

namespace Lz.Aws.Ecs;



/// <summary>
/// Platform factory for AWS ECS + ALB topology.
/// Creates AWS-specific component implementations.
/// </summary>
public class AwsEcsPlatformFactory : IPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsEcsPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    public ISystemNetworkComponent CreateNetwork()
        => new AwsEcsNetworkComponent();
    public IDatabaseComponent CreateDatabase()
        => new AwsRdsComponent();
    public IFileStorageComponent CreateFileStorage()
        => new AwsEfsComponent();
    public IComputeEnvironmentComponent CreateComputeEnvironment()
        => new AwsEcsClusterComponent();
    public IServiceComponent CreateService()
        => new AwsEcsServiceComponent(_config);
    public IAuthServiceComponent CreateAuthService()
        => new AwsKeycloakEcsComponent();
    public IEmailComponent CreateEmail()
        => new AwsSesComponent();
    public ITenantCdnComponent CreateTenantCdn()
        => new AwsCloudFrontComponent();
    public ITenantDataComponent CreateTenantData()
        => new AwsTenantDataComponent();
    public ITenantServiceComponent CreateTenantService()
        => new AwsEcsTenantServiceComponent();

    public ITailscaleComponent? CreateTailscale()
        => new AwsTailscaleAsgComponent();

    public IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsFoundationPostDeployAction(_config);

    public IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null)
        => new AwsTailscalePostDeployAction(_config, system);

    public ITailscaleKeyManager? GetTailscaleKeyManager()
        => new AwsTailscalePostDeployAction(_config);

    public ITenantKeycloakSeeder? GetTenantKeycloakSeeder()
        => new AwsTenantKeycloakSeeder(_config);

    public IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsServicesPostDeployAction(_config, system, services, tenantKey, tenantConfig);

    public IConfigInitRunner? GetConfigInitRunner()
        => new AwsLambdaConfigInitRunner(_config);

    public IPostSeedRunner? GetPostSeedRunner()
        => new AwsLambdaPostSeedRunner(_config);

    public IAdminSetupRunner? GetAdminSetupRunner()
        => new AwsLambdaAdminSetupRunner(_config);

    public ITransitionChecker CreateTransitionChecker()
        => new AwsTransitionChecker(_config);

    public IGateCheckerComponent? CreateGateChecker()
        => new AwsGateCheckerLambdaComponent();

    public ISeedTaskComponent? CreateSeedTask()
        => _config.SeedData != null ? new AwsSeedTaskComponent() : null;

    public (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsFoundationLookup.Lookup(config);
}
