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
    /// Resolve ECS config for a tenant deployment.
    /// Uses tenant ECS values where specified, falls back to system defaults.
    /// </summary>
    public static EcsConfig GetEffectiveEcsConfig(SystemConfig system, TenantConfig tenant)
    {
        var systemEcs = system.ECS ?? new EcsConfig();
        var tenantEcs = tenant.ECS;
        if (tenantEcs == null) return systemEcs;

        return new EcsConfig
        {
            SmartStoreCpu = tenantEcs.SmartStoreCpu > 0 ? tenantEcs.SmartStoreCpu : systemEcs.SmartStoreCpu,
            SmartStoreMemory = tenantEcs.SmartStoreMemory > 0 ? tenantEcs.SmartStoreMemory : systemEcs.SmartStoreMemory,
            AppHostCpu = tenantEcs.AppHostCpu > 0 ? tenantEcs.AppHostCpu : systemEcs.AppHostCpu,
            AppHostMemory = tenantEcs.AppHostMemory > 0 ? tenantEcs.AppHostMemory : systemEcs.AppHostMemory,
            ServiceDesiredCount = tenantEcs.ServiceDesiredCount > 0 ? tenantEcs.ServiceDesiredCount : systemEcs.ServiceDesiredCount,
            LogRetentionDays = tenantEcs.LogRetentionDays > 0 ? tenantEcs.LogRetentionDays : systemEcs.LogRetentionDays,
            EnableEfsMountInstance = tenantEcs.EnableEfsMountInstance ?? systemEcs.EnableEfsMountInstance,
            SmartStoreVpnAccess = systemEcs.SmartStoreVpnAccess, // system-level only
            SmartStoreImage = tenantEcs.SmartStoreImage ?? systemEcs.SmartStoreImage,
            AppHostImage = tenantEcs.AppHostImage ?? systemEcs.AppHostImage,
            // Per-tenant isolation fields
            EfsSmartStoreDataPath = tenantEcs.EfsSmartStoreDataPath,
            EfsSmartStoreConfigPath = tenantEcs.EfsSmartStoreConfigPath,
            EfsSmartStoreDataProtectionPath = tenantEcs.EfsSmartStoreDataProtectionPath,
            EfsAppHostConfigPath = tenantEcs.EfsAppHostConfigPath,
            DatabaseName = tenantEcs.DatabaseName,
            SmartStoreServiceDiscoveryName = tenantEcs.SmartStoreServiceDiscoveryName,
            AppHostServiceDiscoveryName = tenantEcs.AppHostServiceDiscoveryName,
            ListenerPriorities = tenantEcs.ListenerPriorities,
        };
    }

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
    /// Merge runtime AuthConfigs — tenant overrides system.
    /// </summary>
    public static Dictionary<string, AuthConfigEntry>? GetEffectiveAuthConfigs(SystemConfig system, TenantConfig tenant)
        => tenant.AuthConfigs ?? system.AuthConfigs;

    /// <summary>
    /// Merge runtime RequestRewriter — tenant overrides system.
    /// </summary>
    public static RequestRewriterConfig? GetEffectiveRequestRewriter(SystemConfig system, TenantConfig tenant)
        => tenant.RequestRewriter ?? system.RequestRewriter;
}
