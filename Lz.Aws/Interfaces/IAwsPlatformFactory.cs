using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;

namespace Lz.Aws.Interfaces;

/// <summary>
/// AWS-specific extension of <see cref="IPlatformFactory"/>. Carries methods
/// that return AWS-named capability interfaces (Tailscale VPN, Keycloak realm
/// seeder, Lambda gate checker, S3 seed bucket) which don't belong on the
/// platform-neutral core factory. AWS orchestration code casts the received
/// <see cref="IPlatformFactory"/> to this type when it needs those capabilities.
/// </summary>
public interface IAwsPlatformFactory : IPlatformFactory
{
    /// <summary>
    /// Create a Tailscale subnet-router component (EC2 ASG).
    /// Returns null if VPN is not configured for this topology.
    /// </summary>
    ITailscaleComponent? CreateTailscale();

    /// <summary>
    /// Update Tailscale split DNS to include a tenant's domains for VPN
    /// access. Adds entries for <c>shop.{RootDomain}</c> (and LegacyDomains)
    /// so VPN users resolve tenant services via VPC DNS → internal ALB.
    /// No-op if Tailscale is not configured.
    /// </summary>
    Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig);

    /// <summary>
    /// Post-deploy action to configure Tailscale devices (approve routes,
    /// disable key expiry, configure split DNS) after subnet routers are
    /// deployed. Returns null if the topology doesn't use Tailscale.
    /// </summary>
    IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null);

    /// <summary>
    /// Get a key manager that ensures valid Tailscale auth keys exist in
    /// Secrets Manager. Creates auth keys via the Tailscale API if missing
    /// or expired. Returns null if the topology doesn't use Tailscale.
    /// </summary>
    ITailscaleKeyManager? GetTailscaleKeyManager();

    /// <summary>
    /// Get a tenant realm seeder that seeds per-tenant Keycloak realms via
    /// the shared Keycloak Admin API. Returns null if the topology has no
    /// centralized Keycloak.
    /// </summary>
    ITenantKeycloakSeeder? GetTenantKeycloakSeeder();

    /// <summary>
    /// Create a gate-checker component that deploys a Lambda for verifying
    /// EFS/database state from within the VPC. Returns null if the topology
    /// doesn't use in-VPC gate checks (e.g. AppRunner).
    /// </summary>
    IGateCheckerComponent? CreateGateChecker();

    /// <summary>
    /// Create a shared S3 seed-data bucket with cross-account access policy.
    /// The bucket is created in the shared account and grants access to
    /// trusted environment accounts. Returns the bucket name. Returns null
    /// if the topology doesn't use cross-account seed data.
    /// </summary>
    string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey);
}
