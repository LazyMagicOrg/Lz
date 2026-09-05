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
    ///
    /// <para><b>Deliberately does not use <c>Path.GetFileName</c>.</b> Its separator set is
    /// platform-dependent — on Windows it cuts at <c>\</c>, <c>/</c> and the volume <c>:</c>; on
    /// Linux only at <c>/</c> — which made this guard's verdict depend on the host. A Windows-shaped
    /// path handed to it on Linux returned the whole string, so
    /// <c>C:\dir\systemconfig.med.test.yaml</c> parsed to null instead of "test" (found by running
    /// this suite on an ubuntu runner, 2026-09-05). Normalising the separator first, exactly as
    /// <c>CloneReposLogic.IsSafeRelativePath</c> does, makes the answer identical everywhere.</para>
    ///
    /// <para><b>Failure stays CLOSED, and the volume separator is deliberately not handled.</b>
    /// Anything this does not recognise returns null, which signal 2 treats as a violation. That is
    /// why a drive-RELATIVE name like <c>C:systemconfig.scu.dev.yaml</c> is left to parse as null
    /// rather than being cut at the colon: cutting there would let an odd name such as
    /// <c>weird:systemconfig.x.dev.yaml</c> resolve to "dev" and PASS the gate. For a guard whose
    /// whole job is refusing a non-dev run, an unparseable name must refuse, never admit.</para>
    ///
    /// <para><b>Both decisions are pinned, including on a Windows runner.</b> Verified by mutation
    /// on 2026-09-05, after an earlier draft of this remark claimed the opposite and was wrong.
    /// Reverting to <c>Path.GetFileName</c> fails two tests on Windows: it cuts at the volume
    /// separator, so <c>C:systemconfig.scu.dev.yaml</c> resolves to "dev" and PASSES the gate —
    /// precisely the fail-open this refuses — and it does not trim, so a padded name parses to null.
    /// Widening the cut to include <c>:</c> fails the two fail-closed cases. So the Windows gate
    /// does catch a regression here, by a different route than the Linux bug that prompted it.</para>
    /// </summary>
    internal static string? EnvironmentTokenFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var name = fileName.Trim().Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var parts = name.Split('.');
        if (parts.Length != 4) return null;
        if (!string.Equals(parts[0], "systemconfig", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(parts[3], "yaml", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[2];
    }
}
