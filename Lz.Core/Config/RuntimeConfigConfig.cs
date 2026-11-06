using System.Text.RegularExpressions;

namespace Lz.Core.Config;

/// <summary>
/// RUNTIME CONFIG, CLASS 7 (DecoupledCd.md §12 P3 F, P-15): the operator-set values a service loads from SSM under
/// <c>/{SystemKey}/{Environment}/</c>, owned by the config bundle. Each entry of <see cref="Parameters"/> becomes a String
/// parameter in the foundation stack; where the parameter already exists it is imported, so a value set by hand is taken over
/// rather than replaced. A change is a planned update, the refresh before every foundation deploy reverts a hand edit, and a
/// change to a value the service reads only at start (<see cref="ReadAtStart"/>) restarts the system cluster's services.
///
/// <para>NEVER A SECRET, and never what the service writes itself: a SecureString, the BFF key ring (<c>bff/</c>) and the
/// pipeline's own parameters (<c>pipeline/</c>) are refused. A parameter not named here stays as it is, set by hand.</para>
/// </summary>
public class RuntimeConfigConfig
{
    /// <summary>Key, relative to <c>/{SystemKey}/{Environment}/</c> with <c>/</c> between segments, to its value.</summary>
    public Dictionary<string, string>? Parameters { get; set; }

    /// <summary>Key prefixes the service reads once, at start: a change under one restarts the system cluster's services.</summary>
    public List<string>? ReadAtStart { get; set; }

    /// <summary>The prefixes no runtime parameter may sit under: the service's key ring and the pipeline's parameters.</summary>
    public static readonly string[] Reserved = { "bff/", "pipeline/" };

    /// <summary>SSM's limit for a Standard parameter's value.</summary>
    public const int MaxValueLength = 4096;

    private static readonly Regex Key = new(@"^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Every reason this section is not one lz deploys; empty when it is.</summary>
    public static IReadOnlyList<string> Problems(RuntimeConfigConfig? rc)
    {
        var errors = new List<string>();
        if (rc is null) return errors;
        if (rc.Parameters is not { Count: > 0 })
        {
            errors.Add("RuntimeConfig names no Parameters; leave the section out instead.");
            return errors;
        }
        foreach (var (key, value) in rc.Parameters)
        {
            if (!Key.IsMatch(key))
                errors.Add($"RuntimeConfig.Parameters key '{key}' is not segments of letters, digits, '_' and '-' joined by '/'.");
            else if (Reserved.FirstOrDefault(r => (key + "/").StartsWith(r, StringComparison.OrdinalIgnoreCase)) is { } reserved)
                errors.Add($"RuntimeConfig.Parameters key '{key}' is under {reserved}, which the service or the pipeline writes; it is never config.");
            if (string.IsNullOrEmpty(value))
                errors.Add($"RuntimeConfig.Parameters '{key}' has no value; SSM stores none, so leave the key out.");
            else if (value.Length > MaxValueLength)
                errors.Add($"RuntimeConfig.Parameters '{key}' is {value.Length} characters; a Standard parameter holds {MaxValueLength}.");
        }
        foreach (var prefix in rc.ReadAtStart ?? new List<string>())
        {
            if (!Key.IsMatch(prefix.TrimEnd('/')))
                errors.Add($"RuntimeConfig.ReadAtStart '{prefix}' is not a key prefix.");
            else if (!rc.Parameters.Keys.Any(k => IsUnder(k, prefix)))
                errors.Add($"RuntimeConfig.ReadAtStart '{prefix}' names no parameter of RuntimeConfig.Parameters, so it restarts nothing.");
        }
        return errors;
    }

    /// <summary>Whether <paramref name="key"/> is the prefix itself or under it, segment by segment.</summary>
    public static bool IsUnder(string key, string prefix)
    {
        var p = prefix.TrimEnd('/');
        return string.Equals(key, p, StringComparison.Ordinal) || key.StartsWith(p + "/", StringComparison.Ordinal);
    }
}
