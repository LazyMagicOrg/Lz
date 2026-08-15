using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific merges that require AWS-derived config types. Generic merges
/// (Profile, Region, CDN, SecretsManager, Integrations, AuthConfigs,
/// RequestRewriter) stay in <see cref="ConfigMerger"/>.
/// </summary>
public static class AwsConfigMerger
{
    /// <summary>
    /// Resolve ECS config for a tenant deployment: tenant values override
    /// system values field-by-field. If neither system nor tenant carry
    /// AWS-derived config types (shouldn't happen under the AWS platform
    /// but guards against misconfiguration), returns an empty <see cref="EcsConfig"/>.
    /// </summary>
    public static EcsConfig GetEffectiveEcsConfig(SystemConfig system, TenantConfig tenant)
    {
        var systemEcs = (system as AwsSystemConfig)?.ECS ?? new EcsConfig();
        var tenantEcs = (tenant as AwsTenantConfig)?.ECS;
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
    /// Resolve the effective <see cref="FargateConfig"/> for a tenant deployment
    /// under a Fargate-based topology (today: <c>ecs-fargate-cognito-dynamodb</c>).
    /// Resolution order:
    ///   1. <c>tenant.Fargate</c> (explicit per-tenant override)
    ///   2. <c>system.Fargate</c> (system-level setting)
    ///   3. Defaults from a new <see cref="FargateConfig"/>.
    /// (The legacy pre-<c>Fargate</c> YAML sizing fallback was removed in
    /// 0.11.0 with the apprunner-topology retirement.)
    /// </summary>
    public static FargateConfig GetEffectiveFargateConfig(SystemConfig system, TenantConfig tenant)
    {
        var sysAws = system as AwsSystemConfig;
        var tenAws = tenant as AwsTenantConfig;

        var fargate = tenAws?.Fargate ?? sysAws?.Fargate;
        if (fargate != null) return fargate;

        return new FargateConfig();
    }

    /// <summary>
    /// Resolve log retention (days) for a system-scoped AWS resource under a
    /// Fargate-based topology. Checks <c>system.Fargate</c>, then defaults to 3.
    /// Used by cross-topology components like
    /// <see cref="Lz.Aws.Auth.AwsCognitoComponent"/> that are instantiated under
    /// both the <c>ecs-fargate-cognito-dynamodb</c> and
    /// <c>lambda-cognito-dynamodb</c> topologies.
    /// </summary>
    public static int GetEffectiveSystemLogRetentionDays(SystemConfig system)
    {
        var sysAws = system as AwsSystemConfig;
        return sysAws?.Fargate?.LogRetentionDays
            ?? 3;
    }
}
