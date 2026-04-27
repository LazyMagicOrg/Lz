using System.CommandLine;
using Lz.Core.Definitions;

namespace Lz.Core.Plugin;

/// <summary>
/// Contract for system-specific plugin assemblies.
/// The plugin provides a SystemDefinition (topology/services/gates) and
/// optionally registers additional CLI commands (e.g., seed export/import).
/// </summary>
public interface ILzPlugin
{
    /// <summary>
    /// Create the system definition that describes the deployment topology.
    /// </summary>
    SystemDefinition CreateSystemDefinition();

    /// <summary>
    /// Register plugin-specific CLI commands on the root command.
    /// Core commands (deploy, destroy, status) are provided by the tool.
    /// </summary>
    void RegisterCommands(RootCommand root);

    /// <summary>
    /// Contribute or override platform topology descriptors. Called once at
    /// CLI startup, before any factory is resolved. Typical uses:
    /// <list type="bullet">
    ///   <item>Add a brand-new topology (e.g. <c>AwsTopologies.Register(newTopology)</c>).</item>
    ///   <item>Derive a variant of a built-in topology
    ///     (<c>AwsTopologies.DeriveFrom(AwsTopologies.EcsFargateCognitoDynamodb, ...)</c>)
    ///     that wires plugin-specific component implementations.</item>
    ///   <item>Override a built-in topology by name (with
    ///     <c>allowOverride: true</c>) when a system intentionally replaces
    ///     what an existing topology name means for itself.</item>
    /// </list>
    /// Default implementation is a no-op so existing plugins keep working
    /// unchanged.
    /// </summary>
    void RegisterTopologies() { }

    /// <summary>
    /// Refresh per-tenant runtime state that isn't managed by Pulumi —
    /// typically per-subtenant KVS entries, config-map updates, or anything
    /// else the application layer reads at runtime that depends on per-
    /// subtenant data.
    /// <para>
    /// Invoked by <c>lz deploysubtenants</c> after the tool has provisioned
    /// imperative infrastructure (DynamoDB tables, S3 buckets), and also
    /// available to plugins that want to hook the same refresh into their
    /// own commands. Default implementation is a no-op — systems that don't
    /// need runtime refresh just inherit it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Contract — additive-only.</b> Implementations must never enumerate
    /// the target namespace and delete keys absent from the config. Subtenants
    /// can be created pragmatically at runtime outside <c>subtenantconfig</c>;
    /// a reconciliation pass that removed "unknown" entries would wipe those
    /// programmatic subtenants. Writes that overwrite existing keys the plugin
    /// manages are fine; sweeps that remove state the plugin didn't write are
    /// not. Use targeted removal hooks (invoked from destroy-path commands)
    /// for deliberate cleanup.
    /// </remarks>
    Task RefreshTenantRuntimeAsync(
        Config.SystemConfig systemConfig,
        Config.TenantConfig tenantConfig) => Task.CompletedTask;
}
