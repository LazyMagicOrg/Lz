namespace Lz.Core.Config;

/// <summary>
/// One secret that must exist (with the listed JSON keys) before the system can
/// deploy. Maps to the "RequiredSecrets:" list in systemconfig.{sk}.{env}.yaml.
///
/// <para>ABSENT = OFF: when the list is omitted, <c>lz deploysystem</c> checks
/// nothing and behaves exactly as before. When present, deploysystem verifies
/// each entry BEFORE any deploy step: a missing secret (or missing/empty key) is
/// filled from <c>--secret "&lt;name&gt;:&lt;key&gt;=&lt;value&gt;"</c> command-line values
/// (for scripted runs) or an interactive hidden prompt, then created/completed
/// in Secrets Manager. With no console and no supplied value, the deploy fails
/// fast with instructions. Values never live in config or the repo.</para>
/// </summary>
public class RequiredSecretConfig
{
    /// <summary>
    /// Secrets Manager secret name. The token <c>{SecretPrefix}</c> expands to
    /// <c>SecretsManager.SecretPrefix</c> (e.g. "{SecretPrefix}/icecat" →
    /// "scu/icecat").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON keys the secret must contain with non-empty values.</summary>
    public List<string> Keys { get; set; } = new();

    /// <summary>Secret description, applied when the secret is first created.</summary>
    public string Description { get; set; } = string.Empty;
}
