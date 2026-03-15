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
}
