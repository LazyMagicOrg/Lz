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
    ///   <item><description><see cref="WebAppBehavior.AuthConfig"/>:
    ///     <c>null</c> → inherit from the next-higher level; empty string
    ///     <c>""</c> → explicit-public override (overrides a parent-level
    ///     pool name to no-auth); non-empty pool name → gated by that
    ///     pool.</description></item>
    ///   <item><description><see cref="WebAppBehavior.AppName"/> + the
    ///     resolved <see cref="ResolvedWebApp.Level"/>: see the rule below.
    ///     Level tracks which cascade tier owns the bucket holding the WASM
    ///     bundle; BCPlugin uses it to pick the suffix token (<c>{ss}</c> /
    ///     <c>{ts}</c> / <c>{sts}</c>) and the matching bucket name.</description></item>
    /// </list>
    /// AppName + Level rule:
    /// <list type="bullet">
    ///   <item><description>Empty AppName → inherit AppName + Level from
    ///     parent. Idiomatic "I'm only overriding AuthConfig" entry.</description></item>
    ///   <item><description>Non-empty AppName matching the parent's →
    ///     restating for clarity is a no-op for ownership. Level stays at
    ///     the parent's level. This makes AppName re-declaration harmless
    ///     at every override site.</description></item>
    ///   <item><description>Non-empty AppName differing from the parent's
    ///     → child OWNS this path; Level promotes to the child's tier.
    ///     This is the escape hatch for "subtenant has its own bundle for
    ///     the same path".</description></item>
    ///   <item><description>First introduction of a path (no parent entry)
    ///     → Level is the introducing tier.</description></item>
    /// </list>
    /// History: an earlier rule promoted Level on every non-empty AppName,
    /// which forced users to omit AppName at override sites to avoid
    /// pointing the routing tuple at a non-existent per-subtenant bucket.
    /// The current rule decouples re-declaration from ownership — restating
    /// is harmless, only changing matters. See the unit tests in
    /// <c>ConfigMergerTests.ResolveWebApps_*</c>.
    /// <para>
    /// After full resolution, an <see cref="ResolvedWebApp.AuthConfig"/> of
    /// <c>null</c> or empty means "public" (no edge gate, no WASM-side login
    /// flow); a non-empty value names the pool that authenticates the app.
    /// </para>
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

                // AppName + Level resolution. Level tracks which cascade tier
                // OWNS the bucket holding the WASM bundle — BCPlugin uses it
                // to pick the suffix token ({ss} / {ts} / {sts}) and the
                // matching bucket name (bcs---webapp-eventit-{ss},
                // bcs-bcs--webapp-eventit-{ts}, etc.).
                //
                // Rule:
                //   - empty AppName  → inherit AppName + Level from parent
                //                      (idiomatic "I'm only overriding
                //                      AuthConfig" entry)
                //   - non-empty AppName matching parent → keep parent's Level
                //                      (restating the same app for clarity is
                //                      a no-op for ownership)
                //   - non-empty AppName differing from parent → child OWNS
                //                      this path; promote Level to current
                //   - first introduction of the path (prev == null) → Level
                //                      is the current level (whoever declared
                //                      the path first owns it)
                //
                // Why not "always promote on non-empty AppName"? That conflates
                // re-declaration with ownership. A subtenant overriding only
                // AuthConfig but restating AppName for self-documentation
                // would erroneously promote the bucket lookup to a per-
                // subtenant bucket that doesn't exist. The previous schema
                // forced users to OMIT AppName at the override level to avoid
                // this — error-prone. With the rule below, restating is
                // harmless and changing is meaningful.
                int appLevel;
                string appName;
                if (string.IsNullOrEmpty(w.AppName))
                {
                    appName = prev?.AppName ?? string.Empty;
                    appLevel = prev?.Level ?? level;
                }
                else if (prev != null && string.Equals(w.AppName, prev.AppName, StringComparison.Ordinal))
                {
                    appName = prev.AppName;
                    appLevel = prev.Level;
                }
                else
                {
                    appName = w.AppName;
                    appLevel = level;
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
