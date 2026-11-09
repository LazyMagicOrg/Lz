namespace Lz.Aws.Pipeline;

/// <summary>
/// THE HOLD, BY KIND (DecoupledCd.md V-15; Docs specs/Platform/PerKindHold.md). One parameter per environment,
/// <c>/{sk}/{env}/pipeline/hold</c>, read before every automated start: by dev's start function and by each promotion poller.
/// The pipeline promotes two kinds that share no member - SETS (the image, the clients, the config and tooling) and SITES (the
/// website) - and the hold may stop either alone:
///
/// <list type="table">
/// <item><term><c>off</c></term><description>nothing held</description></item>
/// <item><term><c>sets</c></term><description>sets held; sites move</description></item>
/// <item><term><c>sites</c></term><description>sites held; sets move</description></item>
/// <item><term><c>on</c>, missing, or anything else</term><description>both held (fail closed, as before the kinds)</description></item>
/// </list>
///
/// Trimmed and case-sensitive, as the <c>off</c> check always was. Lz from before the kinds reads <c>sets</c> and <c>sites</c>
/// as "not off", so it holds both: a new value written ahead of the new functions stops more than meant, never less.
/// </summary>
public readonly record struct Hold(bool Sets, bool Sites)
{
    public const string Off = "off", On = "on", SetsValue = "sets", SitesValue = "sites";

    /// <summary>The kind a build record's class belongs to: <c>site</c> is a site, every other class is a set's member.</summary>
    public const string SiteClass = "site";

    public static Hold Parse(string? value) => value?.Trim() switch
    {
        Off => new(false, false),
        SetsValue => new(true, false),
        SitesValue => new(false, true),
        _ => new(true, true),
    };

    public bool All => Sets && Sites;
    public bool None => !Sets && !Sites;

    public bool Holds(bool site) => site ? Sites : Sets;

    /// <summary>Whether a build record of this class is held (dev's start function).</summary>
    public bool HoldsClass(string recordClass) => Holds(string.Equals(recordClass, SiteClass, StringComparison.Ordinal));

    /// <summary>The kinds held, as the poller logs them: <c>set</c>, <c>site</c>.</summary>
    public IReadOnlyList<string> Kinds => (Sets, Sites) switch
    {
        (true, true) => new[] { "set", "site" },
        (true, false) => new[] { "set" },
        (false, true) => new[] { "site" },
        _ => Array.Empty<string>(),
    };
}
