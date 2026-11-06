using System.Text.RegularExpressions;

namespace Lz.Core.Config;

/// <summary>
/// ONE ENVIRONMENT'S CONFIG, LOADED AS A DEPLOY LOADS IT, AND NOTHING ELSE (DecoupledCd.md P3 C, owner 2026-11-06): every
/// <c>systemconfig.*.{env}.yaml</c> in a directory, and each system's <c>tenantconfig.{sk}.*.{env}.yaml</c> with its subtenants,
/// through <see cref="ConfigLoader"/> - its validation, the build-side file, the platform's checks. No AWS call is made, so it
/// runs anywhere: <c>lz validateconfig</c>, and Gate 1's check of the config bundle in the tooling image at the Lz version the
/// bundle pins, which catches a config that version refuses before it is recorded.
/// </summary>
public static class ConfigValidation
{
    /// <summary>What was loaded, one line a file, in order; throws what the loader throws for the first file it refuses.</summary>
    public static IReadOnlyList<string> ValidateEnvironment(string directory, string environment)
    {
        var systems = Directory.GetFiles(directory, $"systemconfig.*.{environment}.yaml").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (systems.Count == 0)
            throw new FileNotFoundException($"no systemconfig.*.{environment}.yaml in {directory}");

        var loaded = new List<string>();
        foreach (var path in systems)
        {
            var system = ConfigLoader.LoadSystemConfig(path);
            loaded.Add(Path.GetFileName(path));
            foreach (var tenantPath in Directory.GetFiles(directory, $"tenantconfig.{system.SystemKey}.*.{environment}.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                var tenant = ConfigLoader.LoadTenantConfig(tenantPath);
                var subtenants = tenant.Subtenants?.Count ?? 0;
                loaded.Add($"{Path.GetFileName(tenantPath)} ({subtenants} subtenant{(subtenants == 1 ? "" : "s")})");
            }
        }
        return loaded;
    }

    /// <summary>The environments a directory holds a system config for, in order: what Gate 1 validates.</summary>
    public static IReadOnlyList<string> EnvironmentsIn(string directory)
        => Directory.GetFiles(directory, "systemconfig.*.*.yaml")
            .Select(f => Regex.Match(Path.GetFileName(f), @"^systemconfig\.[^.]+\.([^.]+)\.yaml$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
}
