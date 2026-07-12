namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// Safety gate for the teardown–redeploy lifecycle test: the drill may ONLY
/// run against a dev environment — never test, never prod. Pure and
/// side-effect-free so it can be unit-tested in the default fast suite.
///
/// Three signals must ALL agree (see TEARDOWN-REDEPLOY-PUNCHLIST.md §B0):
///   1. the resolved environment is exactly "dev";
///   2. the systemconfig consumed is systemconfig.{sk}.dev.yaml (the
///      filename-derived environment token is "dev");
///   3. the working-copy path contains no "_Test_"/"_Prod_" segment.
///
/// Violations are returned (all of them, each naming its signal) rather
/// than thrown, so the caller can Assert.Fail with the full list — an
/// attempted run in test/prod is operator error and must be loud.
/// </summary>
public static class DevEnvironmentGuard
{
    public static IReadOnlyList<string> Violations(
        string? resolvedEnvironment,
        string? systemConfigFileName,
        string? workingCopyPath)
    {
        var violations = new List<string>();

        // Signal 1 — resolved environment (lz folder-hierarchy auto-detect
        // or explicit --env) must be exactly "dev".
        if (!string.Equals(resolvedEnvironment?.Trim(), "dev", StringComparison.OrdinalIgnoreCase))
            violations.Add(
                $"signal 1 (resolved environment): expected 'dev', got " +
                $"'{resolvedEnvironment ?? "<null>"}'");

        // Signal 2 — the systemconfig filename must carry the dev token:
        // systemconfig.{sk}.dev.yaml (identity fields are filename-derived,
        // so the filename IS the environment of record).
        var envToken = EnvironmentTokenFromFileName(systemConfigFileName);
        if (!string.Equals(envToken, "dev", StringComparison.OrdinalIgnoreCase))
            violations.Add(
                $"signal 2 (systemconfig filename): expected environment token 'dev' in " +
                $"'{systemConfigFileName ?? "<null>"}', got '{envToken ?? "<none>"}'");

        // Signal 3 — the working copy must not be a _Test_/_Prod_ checkout,
        // regardless of what --env claims.
        if (string.IsNullOrWhiteSpace(workingCopyPath))
        {
            violations.Add("signal 3 (working-copy path): path is null or empty");
        }
        else
        {
            foreach (var marker in new[] { "_Test_", "_Prod_" })
                if (workingCopyPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    violations.Add(
                        $"signal 3 (working-copy path): '{workingCopyPath}' contains " +
                        $"the {marker} marker — this is not a dev working copy");
        }

        return violations;
    }

    /// <summary>
    /// Extract the environment token from a systemconfig file name:
    /// "systemconfig.{sk}.{env}.yaml" → "{env}". Returns null for anything
    /// that doesn't match that exact shape.
    /// </summary>
    internal static string? EnvironmentTokenFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName);
        var parts = name.Split('.');
        if (parts.Length != 4) return null;
        if (!string.Equals(parts[0], "systemconfig", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(parts[3], "yaml", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[2];
    }
}
