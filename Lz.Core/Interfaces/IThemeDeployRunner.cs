namespace Lz.Core.Interfaces;

/// <summary>
/// Deploys Keycloak theme files to EFS by uploading a tarball to S3 and
/// invoking the gate-checker Lambda to extract it to the theme directory.
/// </summary>
public interface IThemeDeployRunner
{
    /// <summary>
    /// Deploy a Keycloak theme from a local directory to EFS.
    /// </summary>
    /// <param name="themeName">Theme name (e.g., "harmova") — becomes the directory name under /opt/keycloak/themes/.</param>
    /// <param name="themeSourcePath">Path to the local theme directory containing login/, account/, email/ subdirectories.</param>
    /// <returns>True if deployment succeeded.</returns>
    Task<bool> DeployThemeAsync(string themeName, string themeSourcePath);
}
