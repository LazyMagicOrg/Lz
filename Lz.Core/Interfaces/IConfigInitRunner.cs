namespace Lz.Core.Interfaces;

/// <summary>
/// Initializes tenant config: creates the tenant database and app user on RDS,
/// stores credentials in Secrets Manager, and writes SmartStore config files
/// (Settings.txt, usersettings.json) to EFS.
/// </summary>
public interface IConfigInitRunner
{
    /// <summary>
    /// Run config initialization for a tenant.
    /// </summary>
    /// <param name="tenantKey">Tenant key (e.g., "meadows").</param>
    /// <param name="dbName">Tenant database name (e.g., "med_meadows_dev_smartstore").</param>
    /// <param name="appUser">Database app user name (e.g., "med_meadows_app").</param>
    /// <param name="appVersion">SmartStore app version string (default: "6.3.0.0").</param>
    /// <param name="userSettings">Optional dictionary to write as usersettings.json (e.g., SmartStore section from tenant config).</param>
    /// <param name="platformDatabaseName">Optional platform database name (e.g., "med_meadows_dev_platform"). If provided, creates the database and app user.</param>
    /// <param name="mediaBucket">Optional S3 media bucket name. Used when <paramref name="mediaStorage"/> is "s3" to seed media into S3.</param>
    /// <param name="mediaStorage">Media storage backend: "s3" seeds media into S3 and activates the Smartstore.AmazonS3 provider; "filesystem" (default) leaves media on EFS.</param>
    /// <returns>True if initialization succeeded.</returns>
    Task<bool> RunInitConfigAsync(string tenantKey, string dbName, string appUser,
        string appVersion = "6.3.0.0", Dictionary<string, object>? userSettings = null,
        string? platformDatabaseName = null,
        string? mediaBucket = null, string? mediaStorage = null);
}
