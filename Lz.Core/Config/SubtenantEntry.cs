namespace Lz.Core.Config;

public class SubtenantEntry
{
    /// <summary>
    /// Leftmost DNS label of the subtenant's first-level subdomain under the
    /// tenant's <c>RootDomain</c>. The full host is constructed as
    /// <c>{SubDomain}.{RootDomain}</c> at consumption sites — never spell
    /// the FQDN out here.
    /// <para>
    /// Optional. Defaults to the subtenant key when empty/omitted. In nearly
    /// every case the subtenant key already IS the desired label (cerulean,
    /// walv, free, …) so this field can be dropped entirely from the YAML.
    /// Only set it when the subtenant key and the customer-facing label
    /// must differ.
    /// </para>
    /// <para>
    /// Validation (<see cref="ConfigValidator"/>): when non-empty must be a
    /// single DNS label (1–63 chars, alphanumeric + hyphen, starting and
    /// ending alphanumeric, no dots). The previous schema accepted full
    /// FQDNs (<c>cerulean.eventitdev.click</c>) and validated that they
    /// were first-level under <c>RootDomain</c>; that was redundant — the
    /// tenant config already supplies <c>RootDomain</c>, so duplicating it
    /// per subtenant just invited drift between the two.
    /// </para>
    /// </summary>
    public string SubDomain { get; set; } = string.Empty;
    public BehaviorsConfig? Behaviors { get; set; }
}
