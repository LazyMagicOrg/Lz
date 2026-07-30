using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Secrets;

/// <summary>
/// Pure decisions behind the required-secrets pre-flight (see
/// <see cref="RequiredSecretConfig"/>): name expansion, <c>--secret</c> argument
/// parsing, missing-key detection, and JSON merging. SDK-free so every branch is
/// unit-testable — <see cref="AwsSecretsEnsurer"/> only translates these into
/// Secrets Manager calls.
/// </summary>
public static class SecretsPlanner
{
    /// <summary>Expand the {SecretPrefix} token in a required-secret name.</summary>
    public static string ExpandName(string name, SystemConfig config)
    {
        if (!name.Contains("{SecretPrefix}"))
            return name;
        var prefix = config.SecretsManager?.SecretPrefix;
        if (string.IsNullOrEmpty(prefix))
            throw new InvalidOperationException(
                $"RequiredSecrets name '{name}' uses {{SecretPrefix}} but " +
                "SecretsManager.SecretPrefix is not set in systemconfig.");
        return name.Replace("{SecretPrefix}", prefix);
    }

    /// <summary>
    /// Parse repeatable <c>--secret "&lt;name&gt;:&lt;key&gt;=&lt;value&gt;"</c> arguments.
    /// The name may contain '/'; the value may contain ':' and '=' (split points
    /// are the FIRST ':' and the first '=' after it).
    /// </summary>
    public static IReadOnlyDictionary<(string Name, string Key), string> ParseSecretArgs(
        IEnumerable<string>? args)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var arg in args ?? Enumerable.Empty<string>())
        {
            var colon = arg.IndexOf(':');
            var rest = colon >= 0 ? arg[(colon + 1)..] : string.Empty;
            var eq = rest.IndexOf('=');
            if (colon <= 0 || eq <= 0 || eq == rest.Length - 1 && rest[..eq].Length == 0)
                throw new ArgumentException(
                    $"Malformed --secret value '{arg}'. Expected <name>:<key>=<value>, " +
                    "e.g. --secret \"scu/icecat:ApiToken=abc123\".");
            var name = arg[..colon];
            var key = rest[..eq];
            var value = rest[(eq + 1)..];
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key) || value.Length == 0)
                throw new ArgumentException(
                    $"Malformed --secret value '{arg}'. Expected <name>:<key>=<value> with " +
                    "a non-empty name, key and value.");
            result[(name, key)] = value;
        }
        return result;
    }

    /// <summary>
    /// The required keys that are absent (or empty) in <paramref name="secretJson"/>.
    /// Null JSON (secret does not exist) → every key is missing. A secret that
    /// exists but is not a JSON object is an error, never silently overwritten.
    /// </summary>
    public static IReadOnlyList<string> MissingKeys(string? secretJson, IReadOnlyList<string> keys)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
            return keys;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(secretJson); }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "Secret exists but its value is not valid JSON — refusing to overwrite it. " +
                "Fix or delete the secret manually.");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    "Secret exists but its value is not a JSON object — refusing to overwrite it. " +
                    "Fix or delete the secret manually.");
            return keys.Where(k =>
                    !doc.RootElement.TryGetProperty(k, out var v)
                    || v.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(v.GetString()))
                .ToList();
        }
    }

    /// <summary>
    /// Merge <paramref name="newValues"/> into the existing JSON object (null →
    /// a fresh object), preserving keys not being set.
    /// </summary>
    public static string MergeSecretJson(
        string? existingJson, IReadOnlyDictionary<string, string> newValues)
    {
        var merged = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            using var doc = JsonDocument.Parse(existingJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                merged[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }
        foreach (var (key, value) in newValues)
            merged[key] = value;
        return JsonSerializer.Serialize(merged);
    }
}
