using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// Validate Cognito pool hardening config at load time. Cognito rejects
/// invalid values at UpdateUserPool time, so catching them early gives a
/// precise config-file error rather than a Pulumi apply failure deep in
/// the component.
/// </summary>
/// <remarks>
/// Called from <c>AwsTopologies.RequireAuthConfigs</c> for Cognito topologies.
/// The same checks used to live inline in <c>AwsAppRunnerCognitoComponent</c>;
/// keeping them here lets the CLI surface them before any AWS call is made.
/// </remarks>
public static class AwsAuthValidator
{
    // Pulumi/Terraform AWS provider accepts OFF | ON | OPTIONAL (not the
    // AWS-SDK-native enum name REQUIRED — the TF provider normalizes it to ON).
    private static readonly string[] _validMfa = { "OFF", "ON", "OPTIONAL" };
    private static readonly string[] _validAsm = { "OFF", "AUDIT", "ENFORCED" };
    private static readonly string[] _validTiers = { "LITE", "ESSENTIALS", "PLUS" };

    public static void Validate(SystemConfig config, List<string> errs)
    {
        if (config.AuthConfigs is null) return;

        foreach (var (poolName, entry) in config.AuthConfigs)
        {
            if (entry is AwsAuthConfigEntry aws)
            {
                ValidatePool(poolName, aws, errs);
            }
            else
            {
                errs.Add(
                    $"AuthConfigs['{poolName}'] did not resolve to AwsAuthConfigEntry " +
                    $"(got '{entry.GetType().Name}'). Platform is 'aws' but AWS config " +
                    "extensions were not registered at YAML load time — this is a tool " +
                    "packaging bug. Verify Lz.Aws is referenced and AwsConfigExtensions " +
                    "was loaded before the systemconfig was parsed.");
            }
        }

        ValidateWebAppAuthConfigs(config, errs);
    }

    /// <summary>
    /// For every system-level <c>Behaviors.WebApps[]</c> entry whose
    /// <c>AuthConfig</c> is non-empty, verify the named pool exists in
    /// <c>AuthConfigs</c>. <c>null</c> / empty means "public" (no auth gate)
    /// and is left alone.
    /// </summary>
    /// <remarks>
    /// Tenant- and subtenant-level overrides aren't visible from a SystemConfig
    /// alone, so they're validated when the cascade is resolved at deploy
    /// time (see BCPlugin).
    /// </remarks>
    private static void ValidateWebAppAuthConfigs(SystemConfig config, List<string> errs)
    {
        var webApps = config.Behaviors?.WebApps;
        if (webApps is null || webApps.Count == 0) return;

        var poolNames = new HashSet<string>(
            config.AuthConfigs?.Keys ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < webApps.Count; i++)
        {
            var app = webApps[i];
            if (string.IsNullOrEmpty(app.AuthConfig)) continue; // public — fine
            if (!poolNames.Contains(app.AuthConfig))
            {
                var available = poolNames.Count > 0
                    ? string.Join(", ", poolNames)
                    : "(none declared)";
                errs.Add(
                    $"Behaviors.WebApps[{i}] (Path='{app.Path}', AppName='{app.AppName}') " +
                    $"references AuthConfig '{app.AuthConfig}' which is not declared in " +
                    $"AuthConfigs. Available pools: {available}. Either remove the " +
                    "AuthConfig (= public access) or declare the pool in AuthConfigs.");
            }
        }
    }

    private static void ValidatePool(string poolName, AwsAuthConfigEntry pool, List<string> errs)
    {
        if (!_validMfa.Any(v => v.Equals(pool.MfaConfiguration, StringComparison.OrdinalIgnoreCase)))
            errs.Add(
                $"AuthConfigs['{poolName}'].MfaConfiguration '{pool.MfaConfiguration}' is invalid. " +
                $"Allowed: {string.Join(", ", _validMfa)}.");

        if (!_validAsm.Any(v => v.Equals(pool.AdvancedSecurityMode, StringComparison.OrdinalIgnoreCase)))
            errs.Add(
                $"AuthConfigs['{poolName}'].AdvancedSecurityMode '{pool.AdvancedSecurityMode}' is invalid. " +
                $"Allowed: {string.Join(", ", _validAsm)}.");

        // Feature tier — optional; when present it must be a real tier, and it must
        // not contradict AdvancedSecurityMode (threat protection is PLUS-only —
        // Cognito would reject the combination mid-deploy with a far worse error).
        if (!string.IsNullOrEmpty(pool.UserPoolTier))
        {
            if (!_validTiers.Any(v => v.Equals(pool.UserPoolTier, StringComparison.OrdinalIgnoreCase)))
                errs.Add(
                    $"AuthConfigs['{poolName}'].UserPoolTier '{pool.UserPoolTier}' is invalid. " +
                    $"Allowed: {string.Join(", ", _validTiers)} (or omit to leave the tier unmanaged).");
            else if (!pool.UserPoolTier.Equals("PLUS", StringComparison.OrdinalIgnoreCase)
                && !pool.AdvancedSecurityMode.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                errs.Add(
                    $"AuthConfigs['{poolName}'] sets UserPoolTier={pool.UserPoolTier} with " +
                    $"AdvancedSecurityMode={pool.AdvancedSecurityMode}, but threat protection requires the " +
                    "PLUS tier. Either set AdvancedSecurityMode: OFF or UserPoolTier: PLUS (or omit the tier).");
        }

        if (pool.PasswordMinLength < 6 || pool.PasswordMinLength > 99)
            errs.Add(
                $"AuthConfigs['{poolName}'].PasswordMinLength {pool.PasswordMinLength} is out of range. " +
                "Cognito requires a value between 6 and 99.");

        if (pool.SmsMfa && !pool.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            errs.Add(
                $"AuthConfigs['{poolName}'] requests SmsMfa=true, but SMS MFA requires an SNS " +
                "caller role which the tool does not currently provision. Use SoftwareTokenMfa=true (TOTP) instead.");

        if (!pool.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase)
            && !pool.SoftwareTokenMfa
            && !pool.SmsMfa)
            errs.Add(
                $"AuthConfigs['{poolName}'] has MfaConfiguration={pool.MfaConfiguration} but no MFA " +
                "factor enabled. Set SoftwareTokenMfa=true (or SmsMfa=true once SMS is supported).");

        if (pool.Groups != null)
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pool.Groups.Count; i++)
            {
                var name = pool.Groups[i].Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    errs.Add($"AuthConfigs['{poolName}'].Groups[{i}].Name must not be empty.");
                    continue;
                }
                if (name.Length > 128)
                    errs.Add(
                        $"AuthConfigs['{poolName}'].Groups[{i}].Name '{name}' exceeds " +
                        "Cognito's 128-char group-name limit.");
                if (!seenNames.Add(name))
                    errs.Add(
                        $"AuthConfigs['{poolName}'].Groups[{i}].Name '{name}' is a duplicate. " +
                        "Cognito rejects duplicate group names within a pool (case-insensitive).");
            }
        }
    }
}
