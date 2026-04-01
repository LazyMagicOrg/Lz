using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;

namespace Lz.Azure.Stubs;

/// <summary>
/// Stub Azure platform factory. All methods throw NotImplementedException.
/// Azure support is planned for Phase 5 of the implementation.
/// </summary>
public class AzureContainerAppsPlatformFactory : IPlatformFactory
{
    public ISystemNetworkComponent CreateNetwork()
        => throw new NotImplementedException("Azure Container Apps network not yet implemented.");
    public IDatabaseComponent CreateDatabase()
        => throw new NotImplementedException("Azure PostgreSQL not yet implemented.");
    public IFileStorageComponent CreateFileStorage()
        => throw new NotImplementedException("Azure Files not yet implemented.");
    public IComputeEnvironmentComponent CreateComputeEnvironment()
        => throw new NotImplementedException("Azure Container Apps environment not yet implemented.");
    public IServiceComponent CreateService()
        => throw new NotImplementedException("Azure Container App not yet implemented.");
    public IAuthServiceComponent CreateAuthService()
        => throw new NotImplementedException("Azure Keycloak Container App not yet implemented.");
    public IEmailComponent CreateEmail()
        => throw new NotImplementedException("Azure Communication Services not yet implemented.");
    public ITenantCdnComponent CreateTenantCdn()
        => throw new NotImplementedException("Azure Front Door not yet implemented.");
    public ITenantDataComponent CreateTenantData()
        => throw new NotImplementedException("Azure tenant data not yet implemented.");
    public ITenantServiceComponent CreateTenantService()
        => throw new NotImplementedException("Azure tenant service not yet implemented.");

    public ITailscaleComponent? CreateTailscale() => null;

    public IPostDeployAction? GetFoundationPostDeployAction() => null;

    public IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null) => null;

    public ITailscaleKeyManager? GetTailscaleKeyManager() => null;

    public ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;

    public IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system) => null;

    public IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null) => null;

    public ITransitionChecker CreateTransitionChecker()
        => throw new NotImplementedException("Azure transition checker not yet implemented.");

    public IConfigInitRunner? GetConfigInitRunner() => null;

    public IPostSeedRunner? GetPostSeedRunner() => null;

    public IAdminSetupRunner? GetAdminSetupRunner() => null;

    public IGateCheckerComponent? CreateGateChecker() => null;

    public ISeedTaskComponent? CreateSeedTask() => null;

    public string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => throw new NotImplementedException("Azure foundation lookup not yet implemented.");
}
