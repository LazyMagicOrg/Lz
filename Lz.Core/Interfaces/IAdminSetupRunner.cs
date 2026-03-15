namespace Lz.Core.Interfaces;

/// <summary>
/// Creates a SmartStore InternalAdmin customer with Administrators role and
/// generates WebApi API credentials (PublicKey/SecretKey). Stores all
/// credentials in the tenant secret in Secrets Manager.
/// Runs after database seeding, before AppHost deployment.
/// </summary>
public interface IAdminSetupRunner
{
    /// <summary>
    /// Create InternalAdmin customer and WebApi credentials in the SmartStore database.
    /// Idempotent — safe to re-run; existing records and secrets are preserved.
    /// </summary>
    /// <param name="tenantKey">Tenant key (e.g., "monro").</param>
    /// <param name="dbName">SmartStore database name (e.g., "med_monro_dev_smartstore").</param>
    /// <returns>True if setup succeeded.</returns>
    Task<bool> RunSetupAdminAsync(string tenantKey, string dbName);
}
