using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Lz.Core.Config;

namespace Lz.Aws.Secrets;

/// <summary>
/// Imperative pre-flight for systemconfig <c>RequiredSecrets</c> (run by
/// <c>lz deploysystem</c> BEFORE any deploy step, in the AwsStateBootstrapper
/// style): verifies each declared secret exists with all required JSON keys, and
/// creates/completes it from supplied <c>--secret</c> values or an interactive
/// prompt. Fails fast with instructions when a value cannot be resolved — a
/// deploy never proceeds against a half-configured secret store. Secret VALUES
/// are never logged.
/// </summary>
public static class AwsSecretsEnsurer
{
    /// <param name="config">The system config (profile/region/RequiredSecrets/SecretPrefix).</param>
    /// <param name="supplied">Values from --secret args: (secretName, key) → value.</param>
    /// <param name="promptForValue">
    /// Interactive fallback: (secretName, key) → value, or null when no console /
    /// nothing entered. Null disables prompting entirely (scripted contexts).
    /// </param>
    public static async Task EnsureAsync(
        SystemConfig config,
        IReadOnlyDictionary<(string Name, string Key), string> supplied,
        Func<string, string, string?>? promptForValue)
    {
        var required = config.RequiredSecrets;
        if (required is null || required.Count == 0)
            return;

        Console.WriteLine("Checking required secrets...");
        using var client = CreateClient(config.Profile, config.Region);

        foreach (var entry in required)
        {
            var name = SecretsPlanner.ExpandName(entry.Name, config);
            string? existingJson = null;
            var exists = false;
            try
            {
                var current = await client.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = name });
                existingJson = current.SecretString;
                exists = true;
            }
            catch (ResourceNotFoundException)
            {
                // Secret absent — created below once values are resolved.
            }
            catch (InvalidRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Secret '{name}' is in an unusable state (e.g. scheduled for deletion): " +
                    $"{ex.Message}. Restore or fully delete it, then re-run.", ex);
            }

            var missing = SecretsPlanner.MissingKeys(existingJson, entry.Keys);
            if (missing.Count == 0)
            {
                Console.WriteLine($"  '{name}' — OK ({entry.Keys.Count} key(s) present)");
                continue;
            }

            // Resolve each missing key: --secret values win, then the prompt.
            var resolved = new Dictionary<string, string>();
            var unresolved = new List<string>();
            foreach (var key in missing)
            {
                if (supplied.TryGetValue((name, key), out var v) && v.Length > 0)
                    resolved[key] = v;
                else if (promptForValue?.Invoke(name, key) is { Length: > 0 } entered)
                    resolved[key] = entered;
                else
                    unresolved.Add(key);
            }

            if (unresolved.Count > 0)
                throw new InvalidOperationException(
                    $"Required secret '{name}' is missing value(s) for: {string.Join(", ", unresolved)}. " +
                    "Supply them non-interactively with " +
                    $"--secret \"{name}:<key>=<value>\" (repeatable), or run from an " +
                    "interactive terminal to be prompted.");

            var merged = SecretsPlanner.MergeSecretJson(existingJson, resolved);
            if (exists)
            {
                await client.PutSecretValueAsync(new PutSecretValueRequest
                {
                    SecretId = name,
                    SecretString = merged,
                });
                Console.WriteLine($"  '{name}' — updated (added: {string.Join(", ", resolved.Keys)})");
            }
            else
            {
                await client.CreateSecretAsync(new CreateSecretRequest
                {
                    Name = name,
                    Description = entry.Description,
                    SecretString = merged,
                });
                Console.WriteLine($"  '{name}' — created (keys: {string.Join(", ", resolved.Keys)})");
            }
        }
    }

    private static AmazonSecretsManagerClient CreateClient(string profile, string region)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonSecretsManagerClient(credentials, regionEndpoint);
        }
        return new AmazonSecretsManagerClient(regionEndpoint);
    }
}
