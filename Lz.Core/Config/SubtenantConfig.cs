namespace Lz.Core.Config;

/// <summary>
/// Per-tenant subtenant declarations. Loaded from
/// <c>subtenantconfig.{systemkey}.{tenantkey}.{env}.yaml</c> when present.
/// SystemKey, TenantKey, and Environment are derived from the filename.
/// <para>
/// If a sibling subtenantconfig file is present, <see cref="ConfigLoader"/>
/// hydrates <see cref="TenantConfig.Subtenants"/> from it. This lets systems
/// manage subtenants in their own file without touching tenantconfig, and
/// enables the fast path <c>lz deploysubtenants</c> for adding subtenants
/// without a full <c>deploytenant</c> Pulumi run.
/// </para>
/// <para>
/// Legacy behaviour: if no subtenantconfig file is present, subtenants may
/// be declared inline in tenantconfig as before. Having both is rejected
/// at load time to avoid ambiguity.
/// </para>
/// </summary>
public class SubtenantConfig
{
    // Derived from filename
    public string SystemKey { get; set; } = string.Empty;
    public string TenantKey { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Subtenants keyed by subtenant-key (e.g. <c>cerulean</c>). Each entry
    /// carries a <c>SubDomain</c> (must be first-level under the tenant's
    /// <c>RootDomain</c>) and optional <c>Behaviors</c> overrides.
    /// </summary>
    public Dictionary<string, SubtenantEntry> Subtenants { get; set; } = new();
}
