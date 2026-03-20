namespace Lz.Core.Config;

/// <summary>
/// ECS deployment configuration section — shared between systemconfig and tenantconfig.
/// Maps to the "ECS:" section in YAML.
/// </summary>
public class EcsConfig
{
    // Keycloak (system-level only)
    public int LogRetentionDays { get; set; } = 3;
    public string? KeycloakImageTag { get; set; }
    public int KeycloakCpu { get; set; } = 512;
    public int KeycloakMemory { get; set; } = 1024;

    // Keycloak EFS theme path (system-level only)
    // Must match the theme name used in keycloakthemes/<name>/ and keycloakconfig YAML.
    public string KeycloakThemePath { get; set; } = "/keycloak-themes/meadows-healing";

    // Database (system-level only)
    public string DbEngineVersion { get; set; } = "15";
    public string DbInstanceClass { get; set; } = "db.t4g.micro";
    public int DbAllocatedStorage { get; set; } = 20;
    public bool DbMultiAZ { get; set; }
    public bool DbChangesApplyImmediately { get; set; }

    // Tailscale (system-level only)
    public string TailscaleInstanceType { get; set; } = "t4g.nano";
    public int TailscaleDesiredCapacity { get; set; } = 2;
    public bool? EnableEfsMountInstance { get; set; }

    // VPN access (system-level only)
    public bool SmartStoreVpnAccess { get; set; }

    // Services — shared between system-level and per-tenant
    public string? SmartStoreImage { get; set; }
    public int SmartStoreCpu { get; set; } = 512;
    public int SmartStoreMemory { get; set; } = 1024;
    public string? AppHostImage { get; set; }
    public int AppHostCpu { get; set; } = 256;
    public int AppHostMemory { get; set; } = 512;
    public int ServiceDesiredCount { get; set; } = 1;

    // Per-tenant resource isolation (tenantconfig only)
    public string? EfsSmartStoreDataPath { get; set; }
    public string? EfsSmartStoreConfigPath { get; set; }
    public string? EfsSmartStoreDataProtectionPath { get; set; }
    public string? EfsAppHostConfigPath { get; set; }
    public string? DatabaseName { get; set; }
    public string? SmartStoreServiceDiscoveryName { get; set; }
    public string? AppHostServiceDiscoveryName { get; set; }

    // Per-tenant listener priorities
    public ListenerPrioritiesConfig? ListenerPriorities { get; set; }
}

public class ListenerPrioritiesConfig
{
    public int Auth { get; set; } = 11;
    public int Realms { get; set; } = 13;
    public int InternalAuth { get; set; } = 11;
    public int InternalRealms { get; set; } = 13;
    public int SmartStore { get; set; } = 20;
    public int AppHost { get; set; } = 30;
}
