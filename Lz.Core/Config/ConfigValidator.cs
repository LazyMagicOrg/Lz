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

        // Digest pinning + untagged expiry, with no durable build tag, is the one
        // combination that deletes the very digest a pinned task definition names. The
        // untagged rule expires by push age, and once :latest moves on, yesterday's pinned
        // digest is untagged and on that clock — so the revision you would roll back to
        // becomes unpullable, and ECS reports CannotPullContainerError rather than anything
        // naming the cause.
        //
        // THREE-way on purpose. Absent Hygiene is fine: EcrDeployer only writes a lifecycle
        // policy when one of its fields is set, so a system with no Hygiene block expires
        // nothing and pinned digests persist. Erroring on absence would block the opt-in for
        // every workspace that has not adopted Hygiene.
        // Versioning without a noncurrent-expiry window is unbounded growth: the console
        // bundles (hundreds of objects) are republished in full on every deploy, and each
        // deploy would leave a whole superseded bundle behind, forever. Same shape as the
        // ECR rule below — a protection that costs without bound unless paired with its cap.
        if (config.Durability?.BucketVersioning == true
            && config.Hygiene?.S3NoncurrentVersionExpirationDays == null)
        {
            errors.Add(
                "Durability.BucketVersioning is on but Hygiene.S3NoncurrentVersionExpirationDays is not " +
                "set. Versioned content buckets are republished in full on every deploy, so without an " +
                "expiry window every deploy leaves a whole superseded bundle behind, forever. Set " +
                "S3NoncurrentVersionExpirationDays (that number is the rollback window), or turn " +
                "BucketVersioning off.");
        }

        if (config.Rollback?.PinImageDigest == true
            && config.Hygiene?.EcrUntaggedImageRetentionDays != null
            && config.Hygiene?.EcrBuildTagRetentionCount == null)
        {
            errors.Add(
                "Rollback.PinImageDigest is on and Hygiene.EcrUntaggedImageRetentionDays is set, " +
                "but Hygiene.EcrBuildTagRetentionCount is not. That combination expires the " +
                "digests pinned task definitions name: once :latest moves, the previously-pinned " +
                "digest is untagged and the untagged rule deletes it on its push-age clock, so a " +
                "rollback target becomes unpullable. Set EcrBuildTagRetentionCount so every push " +
                "also carries a durable b- tag (a tagged image is never selected by the untagged " +
                "rule), or remove EcrUntaggedImageRetentionDays.");
        }

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

        // Subtenant SubDomain (when non-empty) is the leftmost DNS label of
        // the first-level subdomain under RootDomain. The full host is
        // constructed at consumption sites as {SubDomain}.{RootDomain}.
        //
        // Only first-level subdomains are supported — the tenant
        // distribution's TLS cert is {RootDomain} + wildcard `*.{RootDomain}`,
        // which doesn't cover deeper labels (team-a.cerulean.{root} would
        // need a separate cert and distribution-level changes). Enforcing
        // a single-label format here makes that constraint impossible to
        // violate — there's no way to spell a multi-label name in a single-
        // label field.
        if (config.Subtenants != null)
        {
            foreach (var (key, entry) in config.Subtenants)
            {
                if (!string.IsNullOrWhiteSpace(entry.SubDomain)
                    && !_dnsLabelPattern.IsMatch(entry.SubDomain))
                    errors.Add(
                        $"Subtenants[{key}].SubDomain ('{entry.SubDomain}') is not a valid " +
                        $"single DNS label. Expected 1-63 chars, alphanumeric or hyphens, " +
                        $"starting and ending alphanumeric, no dots. The previous schema " +
                        $"accepted FQDNs like '{key}.{config.RootDomain}'; new schema is " +
                        $"just the leftmost label (e.g. '{key}'), and the FQDN is built " +
                        $"from RootDomain at consumption time. Omit this field entirely " +
                        $"when the subtenant key already matches the desired label.");

                // LogoUrl: when non-empty, must be either a host-rooted
                // path (starts with "/" but not "//") or an absolute
                // https URL. Anything else is suspicious enough to flag —
                // the value is rendered as <img src="…"> on the central
                // venues page, so a malformed URL becomes a broken image.
                if (!string.IsNullOrWhiteSpace(entry.LogoUrl))
                {
                    var u = entry.LogoUrl.Trim();
                    var isHostRooted = u.StartsWith("/", StringComparison.Ordinal)
                                    && !u.StartsWith("//", StringComparison.Ordinal);
                    var isHttps = u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    if (!isHostRooted && !isHttps)
                        errors.Add(
                            $"Subtenants[{key}].LogoUrl ('{entry.LogoUrl}') must be a " +
                            $"host-rooted path (e.g. '/subtenancy/.../logo.png') or an " +
                            $"absolute https URL.");
                }

                // VenuesLinkMode: case-insensitive enum-like string.
                // Reject typos at config load — a misspelled "ap" or
                // "lannding" would otherwise silently fall through to
                // "app" behavior at the renderer and the operator
                // wouldn't notice they're not getting the dual-button
                // layout they configured.
                if (!string.IsNullOrWhiteSpace(entry.VenuesLinkMode))
                {
                    var m = entry.VenuesLinkMode.Trim();
                    if (!m.Equals("app", StringComparison.OrdinalIgnoreCase)
                        && !m.Equals("landing", StringComparison.OrdinalIgnoreCase)
                        && !m.Equals("both", StringComparison.OrdinalIgnoreCase))
                        errors.Add(
                            $"Subtenants[{key}].VenuesLinkMode ('{entry.VenuesLinkMode}') " +
                            $"must be one of 'app' (default — straight to /), 'landing' " +
                            $"(to /explore/home/), or 'both' (two buttons).");
                }
            }
        }

        ThrowIfErrors(errors, "TenantConfig", sourceFile);
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
