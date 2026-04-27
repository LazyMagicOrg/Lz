namespace Lz.Core.Config;

/// <summary>
/// Merges system defaults with tenant overrides.
/// When a tenant config does not specify a value, the system config value applies.
/// </summary>
public static class ConfigMerger
{
    /// <summary>
    /// Resolve the effective Profile for a tenant deployment.
    /// Tenant can override system-level profile.
    /// </summary>
    public static string GetEffectiveProfile(SystemConfig system, TenantConfig tenant)
        => tenant.Profile ?? system.Profile;

    /// <summary>
    /// Resolve the effective Region for a tenant deployment.
    /// Tenant can override system-level region.
    /// </summary>
    public static string GetEffectiveRegion(SystemConfig system, TenantConfig tenant)
        => tenant.Region ?? system.Region;

    /// <summary>
    /// Resolve CDN config for a tenant deployment.
    /// Tenant can override system CDN settings.
    /// </summary>
    public static CdnConfig GetEffectiveCdnConfig(SystemConfig system, TenantConfig tenant)
    {
        var systemCdn = system.CDN ?? new CdnConfig();
        var tenantCdn = tenant.CDN;
        if (tenantCdn == null) return systemCdn;

        return new CdnConfig
        {
            PriceClass = !string.IsNullOrEmpty(tenantCdn.PriceClass) ? tenantCdn.PriceClass : systemCdn.PriceClass,
            DefaultRootObject = !string.IsNullOrEmpty(tenantCdn.DefaultRootObject) ? tenantCdn.DefaultRootObject : systemCdn.DefaultRootObject,
        };
    }

    /// <summary>
    /// Merge runtime SecretsManager config — tenant overrides system.
    /// </summary>
    public static SecretsManagerConfig GetEffectiveSecretsManager(SystemConfig system, TenantConfig tenant)
        => tenant.SecretsManager ?? system.SecretsManager ?? new SecretsManagerConfig();

    /// <summary>
    /// Merge runtime Integrations — tenant overrides system.
    /// When present, the entire tenant Integrations block replaces the system block.
    /// </summary>
    public static IntegrationsConfig? GetEffectiveIntegrations(SystemConfig system, TenantConfig tenant)
        => tenant.Integrations ?? system.Integrations;

    /// <summary>
    /// Merge runtime RequestRewriter — tenant overrides system.
    /// </summary>
    public static RequestRewriterConfig? GetEffectiveRequestRewriter(SystemConfig system, TenantConfig tenant)
        => tenant.RequestRewriter ?? system.RequestRewriter;

    /// <summary>
    /// Resolve the <c>Behaviors.WebApps</c> cascade for a single host, applying
    /// system → tenant → subtenant override semantics keyed by <c>Path</c>.
    /// </summary>
    /// <remarks>
    /// Per-field semantics on tenant/subtenant overrides:
    /// <list type="bullet">
    ///   <item><description><see cref="WebAppBehavior.AppName"/> empty → inherit
    ///     from the next-higher level. Non-empty → override.</description></item>
    ///   <item><description><see cref="WebAppBehavior.AuthConfig"/>:
    ///     <c>null</c> → inherit; empty string <c>""</c> → explicit public
    ///     (overrides a parent-level pool name to no-auth); non-empty pool
    ///     name → gated by that pool.</description></item>
    /// </list>
    /// After full resolution, an <see cref="ResolvedWebApp.AuthConfig"/> of
    /// <c>null</c> or empty means "public" (no edge gate, no WASM-side login
    /// flow); a non-empty value names the pool that authenticates the app.
    /// </remarks>
    /// <param name="system">System config — provides defaults.</param>
    /// <param name="tenant">Tenant config — may override system per Path.</param>
    /// <param name="subtenantBehaviors">Per-subtenant Behaviors block (from
    /// <c>SubtenantEntry.Behaviors</c>), or <c>null</c> if the subtenant
    /// inherits tenant + system. May override tenant per Path.</param>
    public static IReadOnlyList<ResolvedWebApp> ResolveWebApps(
        SystemConfig system,
        TenantConfig? tenant,
        BehaviorsConfig? subtenantBehaviors)
    {
        var byPath = new Dictionary<string, ResolvedWebApp>(StringComparer.Ordinal);

        ApplyLevel(system.Behaviors?.WebApps, level: 0);
        ApplyLevel(tenant?.Behaviors?.WebApps, level: 1);
        ApplyLevel(subtenantBehaviors?.WebApps, level: 2);

        return byPath.Values.ToList();

        void ApplyLevel(List<WebAppBehavior>? webApps, int level)
        {
            if (webApps is null) return;
            foreach (var w in webApps)
            {
                if (string.IsNullOrEmpty(w.Path)) continue; // skip malformed
                var prev = byPath.TryGetValue(w.Path, out var existing) ? existing : null;

                // AppName: empty inherits from parent level; non-empty overrides.
                // Level tracks where AppName came from — that determines which
                // bucket-suffix token (system/tenant/subtenant) BCPlugin emits
                // in the routing tuple. A child level overriding only AuthConfig
                // (no AppName) leaves Level unchanged so the bucket reference
                // stays at the level that owns the WASM bundle.
                int appLevel;
                string appName;
                if (!string.IsNullOrEmpty(w.AppName))
                {
                    appName = w.AppName;
                    appLevel = level;
                }
                else
                {
                    appName = prev?.AppName ?? string.Empty;
                    appLevel = prev?.Level ?? level;
                }

                // AuthConfig: null inherits from parent level; "" or non-empty
                // overrides (including override-to-public via "").
                var authConfig = w.AuthConfig is not null
                    ? w.AuthConfig
                    : prev?.AuthConfig;

                byPath[w.Path] = new ResolvedWebApp(w.Path, appName, authConfig, appLevel);
            }
        }
    }
}

/// <summary>
/// A <see cref="WebAppBehavior"/> after the system → tenant → subtenant
/// cascade has been resolved. <see cref="Level"/> records where the
/// <see cref="AppName"/> originated (0=system, 1=tenant, 2=subtenant) — used
/// by callers (BCPlugin) to pick the right bucket-suffix token (e.g. {ss},
/// {ts}, {sts}) for routing tuples. Child-level overrides that only change
/// <see cref="AuthConfig"/> leave <see cref="Level"/> unchanged.
/// </summary>
public sealed record ResolvedWebApp(
    string Path,
    string AppName,
    string? AuthConfig,
    int Level);
