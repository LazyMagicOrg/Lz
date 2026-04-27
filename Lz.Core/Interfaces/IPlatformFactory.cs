namespace Lz.Core.Interfaces;

/// <summary>
/// Platform-neutral factory for deployment components. Platform-specific
/// capabilities that don't fit a shape-named contract (VPN subnet routers,
/// central-auth realm seeders, in-network gate checkers, cross-account
/// seed-bucket provisioning, etc.) live on platform-extended interfaces in
/// the platform library — e.g. <c>Lz.Aws.Interfaces.IAwsPlatformFactory</c> —
/// and callers cast the received factory to that type when they need them.
/// </summary>
public interface IPlatformFactory
{
    ISystemNetworkComponent CreateNetwork();
    IDatabaseComponent CreateDatabase();
    IFileStorageComponent CreateFileStorage();
    IComputeEnvironmentComponent CreateComputeEnvironment();
    IServiceComponent CreateService();
    IAuthServiceComponent CreateAuthService();
    IEmailComponent CreateEmail();
    ITenantCdnComponent CreateTenantCdn();
    ITenantDataComponent CreateTenantData();
    ITenantServiceComponent CreateTenantService();

    /// <summary>
    /// Deploy per-tenant DNS records and the load-balancer certificate for SNI.
    /// Each tenant gets its own certificate for RootDomain + LegacyDomains,
    /// attached to the shared load-balancer listeners, plus origin DNS records.
    /// </summary>
    void DeployTenantDnsAndCert(Config.TenantConfig tenantConfig, Outputs.INetworkOutputs network, Outputs.ICdnOutputs? cdn = null);

    /// <summary>
    /// Pre-deploy cleanup before the foundation Pulumi up.
    /// Handles platform-specific resource cleanup that Pulumi can't manage
    /// (e.g., clearing records from a DNS zone before it can be replaced).
    /// </summary>
    Task CleanupBeforeFoundationAsync() => Task.CompletedTask;

    /// <summary>
    /// Post-deploy actions to run after Pulumi up for the foundation phase.
    /// Returns null if no post-deploy actions are needed.
    /// </summary>
    IPostDeployAction? GetFoundationPostDeployAction();

    /// <summary>
    /// Post-deploy action for building/pushing container images and scaling
    /// foundation-level services (shared across tenants).
    /// Returns null if no foundation services with container builds exist.
    /// </summary>
    IPostDeployAction? GetFoundationServiceDeployAction(
        Definitions.SystemDefinition system);

    /// <summary>
    /// Post-deploy action for building/pushing container images and scaling services.
    /// Called with a specific list of services during tenant deployment.
    /// tenantKey is used to locate the tenant-specific config file for baking
    /// into container images.
    /// Returns null if no post-deploy actions are needed.
    /// </summary>
    IPostDeployAction? GetServiceDeployAction(
        Definitions.SystemDefinition system,
        IReadOnlyList<Definitions.ServiceDefinition> services,
        string? tenantKey = null,
        Config.TenantConfig? tenantConfig = null);

    /// <summary>
    /// Create a platform-specific transition checker for evaluating gates
    /// between deployment steps (e.g., checking secret-store entries).
    /// </summary>
    ITransitionChecker CreateTransitionChecker();

    /// <summary>
    /// Get a config init runner that creates tenant databases, app users,
    /// and writes any per-tenant config files the application layer expects
    /// on shared file storage. Returns null if the platform doesn't support
    /// config initialization.
    /// </summary>
    IConfigInitRunner? GetConfigInitRunner();

    /// <summary>
    /// Get a post-seed runner that re-writes application config files after
    /// the seed process, which may overwrite them with source-environment values.
    /// Returns null if the platform doesn't support post-seed configuration.
    /// </summary>
    IPostSeedRunner? GetPostSeedRunner();

    /// <summary>
    /// Get an admin setup runner that creates the application's internal
    /// administrator account and generates API credentials.
    /// Returns null if the platform doesn't support admin setup.
    /// </summary>
    IAdminSetupRunner? GetAdminSetupRunner();

    /// <summary>
    /// Create a seed task component that deploys the container task,
    /// container image repository, and IAM roles for the seeder container
    /// (file storage + database + object storage).
    /// Returns null if the platform doesn't support seed tasks or SeedData is
    /// not configured.
    /// </summary>
    ISeedTaskComponent? CreateSeedTask();

    /// <summary>
    /// Look up existing foundation resources (created by deploysystem) using
    /// platform-specific data-source queries. Returns typed output interfaces so
    /// tenant components can use them without re-creating foundation resources.
    /// </summary>
    (Outputs.INetworkOutputs Network,
     Outputs.IComputeEnvironmentOutputs Compute,
     Outputs.IDatabaseOutputs Database,
     Outputs.IFileStorageOutputs FileStorage) LookupFoundation(Config.SystemConfig config);
}
