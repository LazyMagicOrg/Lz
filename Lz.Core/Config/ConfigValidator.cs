using System.Text.RegularExpressions;

namespace Lz.Core.Config;

/// <summary>
/// Validates configuration objects after deserialization.
/// Catches missing required fields early with clear error messages,
/// rather than failing deep inside Pulumi resource creation.
/// </summary>
public static class ConfigValidator
{
    private static readonly Regex _dnsLabelPattern = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] _validPriceClasses =
        { "PriceClass_All", "PriceClass_100", "PriceClass_200" };

    /// <summary>
    /// Validate a SharedConfig has all required deployment fields.
    /// </summary>
    public static void Validate(SharedConfig config, string sourceFile)
    {
        var errors = new List<string>();

        RequireNonEmpty(errors, nameof(config.Profile), config.Profile);
        RequireNonEmpty(errors, nameof(config.Region), config.Region);
        RequireNonEmpty(errors, nameof(config.Domain), config.Domain);
        RequireNonEmpty(errors, nameof(config.VpcCidr), config.VpcCidr);
        RequireNonEmpty(errors, nameof(config.SharedSuffix), config.SharedSuffix);

        ThrowIfErrors(errors, "SharedConfig", sourceFile);
    }

    /// <summary>
    /// Validate a SystemConfig has all required deployment fields.
    /// </summary>
    public static void Validate(SystemConfig config, string sourceFile)
    {
        var errors = new List<string>();

        RequireNonEmpty(errors, nameof(config.SystemKey), config.SystemKey);
        RequireNonEmpty(errors, nameof(config.Environment), config.Environment);
        RequireNonEmpty(errors, nameof(config.Profile), config.Profile);
        RequireNonEmpty(errors, nameof(config.Region), config.Region);
        RequireNonEmpty(errors, nameof(config.SystemSuffix), config.SystemSuffix);

        if (config.CDN != null) ValidateCdn(config.CDN, errors);

        // Topology-specific prerequisites (e.g., VpcCidr for topologies with a
        // private network) live in the platform library's topology descriptor
        // and are invoked by the CLI via ValidateTopologyConfig before factory
        // construction — not here.

        ThrowIfErrors(errors, "SystemConfig", sourceFile);
    }

    /// <summary>
    /// Validate a TenantConfig has all required deployment fields.
    /// </summary>
    public static void Validate(TenantConfig config, string sourceFile)
    {
        var errors = new List<string>();

        RequireNonEmpty(errors, nameof(config.SystemKey), config.SystemKey);
        RequireNonEmpty(errors, nameof(config.TenantKey), config.TenantKey);
        RequireNonEmpty(errors, nameof(config.Environment), config.Environment);
        RequireNonEmpty(errors, nameof(config.RootDomain), config.RootDomain);

        if (!string.IsNullOrEmpty(config.RootDomain))
            ValidateFqdn(config.RootDomain, "RootDomain", errors);

        if (config.LegacyDomains != null)
        {
            for (int i = 0; i < config.LegacyDomains.Count; i++)
                ValidateFqdn(config.LegacyDomains[i], $"LegacyDomains[{i}]", errors);
        }

        if (config.CDN != null) ValidateCdn(config.CDN, errors);

        // Subtenant domains must be exactly one label above the tenant's
        // RootDomain. The tenant distribution's TLS cert is {RootDomain} +
        // wildcard `*.{RootDomain}` — only first-level subdomains are covered.
        // Deeper subdomains (e.g. team-a.cerulean.example.com) would need a
        // separate cert and distribution-level changes, which this topology
        // doesn't support via the deploysubtenants fast path.
        if (config.Subtenants != null && !string.IsNullOrEmpty(config.RootDomain))
        {
            foreach (var (key, entry) in config.Subtenants)
            {
                if (string.IsNullOrWhiteSpace(entry.SubDomain)) continue;
                if (!IsFirstLevelSubdomainOf(entry.SubDomain, config.RootDomain))
                    errors.Add(
                        $"Subtenants[{key}].SubDomain ('{entry.SubDomain}') is not a first-level " +
                        $"subdomain of the tenant's RootDomain ('{config.RootDomain}'). " +
                        $"Expected exactly one label above the root (e.g. '{key}.{config.RootDomain}').");
            }
        }

        ThrowIfErrors(errors, "TenantConfig", sourceFile);
    }

    /// <summary>
    /// True if <paramref name="sub"/> is exactly one DNS label above
    /// <paramref name="root"/>. Comparison is case-insensitive; both arguments
    /// are treated as bare domain names (no scheme or path).
    /// </summary>
    private static bool IsFirstLevelSubdomainOf(string sub, string root)
    {
        var subLabels = sub.Trim().TrimEnd('.').Split('.');
        var rootLabels = root.Trim().TrimEnd('.').Split('.');
        if (subLabels.Length != rootLabels.Length + 1) return false;
        for (int i = 0; i < rootLabels.Length; i++)
        {
            if (!string.Equals(subLabels[i + 1], rootLabels[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static void ValidateFqdn(string domain, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        var trimmed = domain.Trim().TrimEnd('.');
        if (trimmed.Length > 253)
            errors.Add($"{fieldName} '{domain}' exceeds 253 chars (DNS FQDN limit).");

        foreach (var label in trimmed.Split('.'))
        {
            if (!_dnsLabelPattern.IsMatch(label))
            {
                errors.Add(
                    $"{fieldName} '{domain}' contains invalid DNS label '{label}'. " +
                    "Each label must be 1-63 chars, alphanumeric or hyphens, " +
                    "starting and ending alphanumeric.");
                break;
            }
        }
    }

    private static void ValidateCdn(CdnConfig cdn, List<string> errors)
    {
        if (string.IsNullOrEmpty(cdn.PriceClass)) return;
        if (!_validPriceClasses.Contains(cdn.PriceClass))
            errors.Add(
                $"CDN.PriceClass '{cdn.PriceClass}' is invalid. " +
                $"Allowed: {string.Join(", ", _validPriceClasses)}.");
    }

    private static void RequireNonEmpty(List<string> errors, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(fieldName);
    }

    private static void ThrowIfErrors(List<string> errors, string configType, string sourceFile)
    {
        if (errors.Count == 0) return;

        var fields = string.Join(", ", errors);
        throw new InvalidOperationException(
            $"{configType} validation failed — missing required field(s): {fields}. " +
            $"Source: {sourceFile}");
    }
}
