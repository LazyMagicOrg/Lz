using System.Text.Json;
using System.Text.RegularExpressions;
using Lz.Core.Config;

namespace Lz.Aws.VectorStore;

/// <summary>
/// Pure decisions behind the aoss vector-store provisioning: name derivation +
/// validation and the three aoss policy documents. SDK/Pulumi-free so the
/// branching is unit-testable — <c>AwsVectorStoreComponent</c> and the tenant
/// service component only translate these into resources.
/// </summary>
public static class VectorStorePolicy
{
    // aoss collection/policy names: 3-32 chars, lowercase letters/digits/hyphens,
    // starting with a letter.
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{2,31}$", RegexOptions.Compiled);

    /// <summary>
    /// The collection name: the configured override, or the
    /// <c>{SystemKey}-{Environment}-match</c> convention (e.g. scu-dev-match).
    /// </summary>
    public static string CollectionName(SystemConfig config)
    {
        var name = config.VectorStore?.CollectionName;
        if (string.IsNullOrWhiteSpace(name))
            name = $"{config.SystemKey}-{config.Environment}-match";
        return ValidateName(name);
    }

    /// <summary>
    /// Name of the per-tenant data-access policy granting the tenant service
    /// role (aoss allows several data-access policies per collection, so each
    /// tenant grants its own role without mutating the foundation's policy).
    /// </summary>
    public static string TenantAccessPolicyName(string collectionName, string tenantKey)
        => ValidateName($"{collectionName}-{tenantKey}");

    /// <summary>Throws when <paramref name="name"/> violates the aoss naming rules; returns it otherwise.</summary>
    public static string ValidateName(string name)
    {
        if (!NamePattern.IsMatch(name))
            throw new InvalidOperationException(
                $"aoss name '{name}' is invalid: 3-32 chars, lowercase letters/digits/hyphens, " +
                "starting with a letter. Set VectorStore.CollectionName explicitly if the " +
                "derived {SystemKey}-{Environment}-match form breaks these rules.");
        return name;
    }

    /// <summary>Encryption security policy: AWS-owned key for the collection. (A single JSON object.)</summary>
    public static string EncryptionPolicyJson(string collection) =>
        JsonSerializer.Serialize(new
        {
            Rules = new object[]
            {
                new { ResourceType = "collection", Resource = new[] { $"collection/{collection}" } },
            },
            AWSOwnedKey = true,
        });

    /// <summary>
    /// Network security policy: public endpoint (auth stays SigV4 + IAM — public
    /// here means reachable, not anonymous). (A JSON array of rule blocks.)
    /// </summary>
    public static string NetworkPolicyJson(string collection) =>
        JsonSerializer.Serialize(new object[]
        {
            new
            {
                Rules = new object[]
                {
                    new { ResourceType = "collection", Resource = new[] { $"collection/{collection}" } },
                    new { ResourceType = "dashboard", Resource = new[] { $"collection/{collection}" } },
                },
                AllowFromPublic = true,
            },
        });

    /// <summary>
    /// Data-access policy granting <paramref name="principalArns"/> full data-plane
    /// access (collection ops + index CRUD/search — the index bootstrapper also
    /// needs pipeline creation, which rides the same permission). (A JSON array.)
    /// </summary>
    public static string DataAccessPolicyJson(string collection, IEnumerable<string> principalArns)
    {
        var principals = principalArns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToArray();
        if (principals.Length == 0)
            throw new InvalidOperationException(
                $"aoss data-access policy for '{collection}' requires at least one principal ARN.");

        return JsonSerializer.Serialize(new object[]
        {
            new
            {
                Rules = new object[]
                {
                    new
                    {
                        ResourceType = "collection",
                        Resource = new[] { $"collection/{collection}" },
                        Permission = new[] { "aoss:*" },
                    },
                    new
                    {
                        ResourceType = "index",
                        Resource = new[] { $"index/{collection}/*" },
                        Permission = new[] { "aoss:*" },
                    },
                },
                Principal = principals,
            },
        });
    }
}
