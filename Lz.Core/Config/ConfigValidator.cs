namespace Lz.Core.Config;

/// <summary>
/// Validates configuration objects after deserialization.
/// Catches missing required fields early with clear error messages,
/// rather than failing deep inside Pulumi resource creation.
/// </summary>
public static class ConfigValidator
{
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
        RequireNonEmpty(errors, nameof(config.SystemDomain), config.SystemDomain);
        RequireNonEmpty(errors, nameof(config.VpcCidr), config.VpcCidr);
        RequireNonEmpty(errors, nameof(config.SystemSuffix), config.SystemSuffix);
        RequireNonEmpty(errors, nameof(config.CentralAuthDomain), config.CentralAuthDomain);

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

        ThrowIfErrors(errors, "TenantConfig", sourceFile);
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
