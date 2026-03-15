namespace Lz.Core.Interfaces;

/// <summary>
/// Runs post-seed configuration after the tenant database has been seeded.
/// Re-writes SmartStore config files (Settings.txt, usersettings.json) to EFS
/// with correct credentials for this environment, replacing any source-environment
/// values that the seed process may have overwritten.
/// </summary>
public interface IPostSeedRunner
{
    /// <summary>
    /// Run post-seed config for a tenant.
    /// </summary>
    /// <param name="tenantKey">Tenant key (e.g., "monro").</param>
    /// <param name="dbName">Tenant database name (e.g., "med_monro_dev_smartstore").</param>
    /// <param name="appUser">Database app user name (e.g., "med_monro_app").</param>
    /// <param name="appVersion">SmartStore app version string (default: "6.3.0.0").</param>
    /// <param name="userSettings">Optional dictionary to write as usersettings.json (e.g., SmartStore section from tenant config).</param>
    /// <returns>True if post-seed config succeeded.</returns>
    Task<bool> RunPostSeedAsync(string tenantKey, string dbName, string appUser,
        string appVersion = "6.3.0.0", Dictionary<string, object>? userSettings = null);
}
