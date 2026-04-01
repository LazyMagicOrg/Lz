using Lz.Core.Config;
using Lz.Core.Orchestration;

namespace Lz.Core.Definitions;

public abstract class SystemDefinition
{
    public List<ServiceDefinition> Services { get; } = new();
    public AuthDefinition? Auth { get; private set; }
    public bool UsesVpn { get; private set; }

    /// <summary>
    /// Transition gates checked after foundation Step 1 (Pulumi up),
    /// before Step 2 (post-deploy). Use for prerequisites that must be
    /// in place before Keycloak seeding runs (e.g., config files, SES).
    /// </summary>
    public List<TransitionRequirement> FoundationInfraGates { get; } = new();

    /// <summary>
    /// Transition gates checked after foundation Step 2 (post-deploy),
    /// before Step 3 (second Pulumi up). Use for prerequisites that
    /// depend on Step 2 output (e.g., tailscale-auth-key).
    /// </summary>
    public List<TransitionRequirement> FoundationGates { get; } = new();

    /// <summary>
    /// Foundation-level services — shared across all tenants.
    /// Deployed during deployfoundation, not per-tenant.
    /// </summary>
    public IReadOnlyList<ServiceDefinition> FoundationLayerServices
        => Services.Where(s => s.Layer == ServiceLayer.Foundation).ToList();

    /// <summary>
    /// Services in the Service layer — deployed first within each tenant.
    /// Must be running and configured before host-layer services.
    /// </summary>
    public IReadOnlyList<ServiceDefinition> ServiceLayerServices
        => Services.Where(s => s.Layer == ServiceLayer.Service).ToList();

    /// <summary>
    /// Services in the Host layer — deployed after service-layer gates pass.
    /// Depend on service-layer services being operational and configured.
    /// </summary>
    public IReadOnlyList<ServiceDefinition> HostLayerServices
        => Services.Where(s => s.Layer == ServiceLayer.Host).ToList();

    public abstract void Define(SystemConfig config);

    protected ServiceDefinition AddService(string name, ServiceDefinition def)
    {
        def.Name = name;
        Services.Add(def);
        return def;
    }

    protected ServiceDefinition? GetService(string name)
        => Services.FirstOrDefault(s => s.Name == name);

    protected void UseKeycloak(string[] realms)
    {
        Auth = new AuthDefinition
        {
            Provider = "keycloak",
            Realms = realms.ToList()
        };
    }

    protected void UseTailscale()
    {
        UsesVpn = true;
    }
}

public class AuthDefinition
{
    public string Provider { get; set; } = string.Empty;
    public List<string> Realms { get; set; } = new();
}
