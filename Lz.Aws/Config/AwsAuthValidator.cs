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
