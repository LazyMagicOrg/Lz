namespace Lz.Aws.Interfaces;

/// <summary>
/// Manages Tailscale auth key and SSH key lifecycle — ensures valid keys
/// exist in Secrets Manager before ASG instances boot.
/// </summary>
public interface ITailscaleKeyManager
{
    /// <summary>
    /// Ensure the Tailscale API access key is present in the system secret
    /// ({systemkey}/system, or shared/system on cross-account systems) BEFORE any
    /// auth-key work. No-op when already stored. Otherwise the value is taken from
    /// <paramref name="cliKey"/> (the --tailscale-key flag); if that is empty and a
    /// console is attached it is prompted for (masked); with no console attached it
    /// throws, instructing the caller to pass --tailscale-key. Creates the secret
    /// if it does not yet exist.
    /// </summary>
    Task EnsureApiKeySeededAsync(string? cliKey);

    /// <summary>
    /// Ensure a valid Tailscale auth key exists in the shared secret.
    /// If the existing key is missing, expired, or revoked, creates a new
    /// one via the Tailscale API and writes it to Secrets Manager.
    /// </summary>
    Task EnsureAuthKeyAsync();

    /// <summary>
    /// Ensure an SSH key pair exists in the shared secret for SFTP access
    /// to the Tailscale router instances. Generates a new RSA 4096-bit key
    /// pair if one doesn't exist. The private key can be downloaded from
    /// Secrets Manager for use with WinSCP/PuTTY.
    /// </summary>
    Task EnsureSshKeyAsync();
}
