namespace Lz.Core.Interfaces;

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
    /// Create a Tailscale subnet router component (EC2 ASG).
    /// Returns null if VPN is not used or the platform doesn't support it.
    /// </summary>
    ITailscaleComponent? CreateTailscale();

    /// <summary>
    /// Post-deploy actions to run after Pulumi up for the foundation phase.
    /// Returns null if no post-deploy actions are needed.
    /// </summary>
    IPostDeployAction? GetFoundationPostDeployAction();

    /// <summary>
    /// Post-deploy action to configure Tailscale devices (approve routes,
    /// disable key expiry, configure split DNS) after subnet routers are deployed.
    /// The system definition is used to derive VPN-only domains for split DNS
    /// from services with IngressType.Internal.
    /// Returns null if the platform doesn't support Tailscale or it's not configured.
    /// </summary>
    IPostDeployAction? GetTailscalePostDeployAction(Definitions.SystemDefinition? system = null);

    /// <summary>
    /// Get a key manager that ensures valid Tailscale auth keys exist in Secrets Manager.
    /// Creates auth keys via the Tailscale API if missing or expired.
    /// Returns null if the platform doesn't support Tailscale.
    /// </summary>
    ITailscaleKeyManager? GetTailscaleKeyManager();

    /// <summary>
    /// Get a tenant Keycloak seeder that seeds per-tenant realms via the shared
    /// Keycloak Admin API. Returns null if the platform doesn't support Keycloak.
    /// </summary>
    ITenantKeycloakSeeder? GetTenantKeycloakSeeder();

    /// <summary>
    /// Post-deploy action for building/pushing Docker images and scaling
    /// foundation-level services (e.g., LiveKit SFU).
    /// Returns null if no foundation services with Docker builds exist.
    /// </summary>
    IPostDeployAction? GetFoundationServiceDeployAction(
        Definitions.SystemDefinition system);

    /// <summary>
    /// Post-deploy action for building/pushing Docker images and scaling ECS services.
    /// Called with a specific list of services during tenant deployment.
    /// tenantKey is used to locate the tenant-specific config file for baking into Docker images.
    /// Returns null if no post-deploy actions are needed.
    /// </summary>
    IPostDeployAction? GetServiceDeployAction(
        Definitions.SystemDefinition system,
        IReadOnlyList<Definitions.ServiceDefinition> services,
        string? tenantKey = null,
        Config.TenantConfig? tenantConfig = null);

    /// <summary>
    /// Create a platform-specific transition checker for evaluating gates
    /// between deployment steps (e.g., checking Secrets Manager entries).
    /// </summary>
    ITransitionChecker CreateTransitionChecker();

    /// <summary>
    /// Create a gate-checker component that deploys a Lambda (or equivalent)
    /// for verifying EFS/database data from within the VPC.
    /// Returns null if the platform doesn't support Lambda-based gate checks (e.g., Azure).
    /// </summary>
    IGateCheckerComponent? CreateGateChecker();

    /// <summary>
    /// Get a config init runner that creates tenant databases, app users,
    /// and writes SmartStore config files (Settings.txt, usersettings.json) to EFS.
    /// Uses the gate-checker Lambda for VPC operations.
    /// Returns null if the platform doesn't support config initialization.
    /// </summary>
    IConfigInitRunner? GetConfigInitRunner();

    /// <summary>
    /// Get a post-seed runner that re-writes SmartStore config files after the seed
    /// process, which may overwrite Settings.txt with source-environment values.
    /// Returns null if the platform doesn't support post-seed configuration.
    /// </summary>
    IPostSeedRunner? GetPostSeedRunner();

    /// <summary>
    /// Get an admin setup runner that creates a SmartStore InternalAdmin customer
    /// with Administrators role and generates WebApi API credentials.
    /// Returns null if the platform doesn't support admin setup.
    /// </summary>
    IAdminSetupRunner? GetAdminSetupRunner();

    /// <summary>
    /// Create a seed task component that deploys an ECS task definition, ECR repository,
    /// and IAM roles for the seeder container (EFS + RDS + S3).
    /// Returns null if the platform doesn't support seed tasks or SeedData is not configured.
    /// </summary>
    ISeedTaskComponent? CreateSeedTask();

    /// <summary>
    /// Create a shared S3 seed data bucket with cross-account access policy.
    /// The bucket is created in the shared account and grants access to trusted environment accounts.
    /// Returns the bucket name. Returns null if the platform doesn't support S3.
    /// </summary>
    string? CreateSeedBucket(Config.SharedConfig sharedConfig, string systemKey);

    /// <summary>
    /// Look up existing foundation resources (created by deployfoundation) using
    /// platform-specific data-source queries. Returns typed output interfaces so
    /// tenant components can use them without re-creating foundation resources.
    /// </summary>
    (Outputs.INetworkOutputs Network,
     Outputs.IComputeEnvironmentOutputs Compute,
     Outputs.IDatabaseOutputs Database,
     Outputs.IFileStorageOutputs FileStorage) LookupFoundation(Config.SystemConfig config);
}
