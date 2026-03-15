using Lz.Core.Config;

namespace Lz.Core.Interfaces;

/// <summary>
/// Seeds per-tenant Keycloak realms (adminsauth + usersauth) via the
/// Keycloak Admin REST API on the shared-services Keycloak instance.
/// </summary>
public interface ITenantKeycloakSeeder
{
    /// <summary>
    /// Seed Keycloak with the given configuration.
    /// Connects to the shared Keycloak via CentralAuthDomain (reachable via VPN),
    /// authenticates with admin creds from the shared/system secret,
    /// and seeds realms/clients/roles/groups.
    /// </summary>
    Task SeedAsync(KeycloakSeedConfig seedConfig, string tenantKey);
}
