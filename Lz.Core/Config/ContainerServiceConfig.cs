namespace Lz.Core.Config;

/// <summary>
/// Configuration for building and pushing container images to a registry.
/// Maps to servicesconfig.{systemkey}.{env}.yaml files.
/// </summary>
public class ContainerServiceConfig
{
    /// <summary>
    /// Map of service name → container build definition.
    /// Keys should match the service names used in deploytenant
    /// (e.g., "smartstore", "apphost").
    /// </summary>
    public Dictionary<string, ContainerDefinition> Containers { get; set; } = new();

    /// <summary>
    /// Directory containing the config file. Set by ConfigLoader after loading.
    /// Used to resolve relative paths in ContainerDefinition.
    /// </summary>
    public string ConfigDirectory { get; set; } = "";
}

/// <summary>
/// Docker build definition for a single container/service.
/// </summary>
public class ContainerDefinition
{
    /// <summary>
    /// Docker build context directory, relative to the config file's location.
    /// </summary>
    public string Context { get; set; } = "";

    /// <summary>
    /// Dockerfile path, relative to Context.
    /// </summary>
    public string Dockerfile { get; set; } = "Dockerfile";

    /// <summary>
    /// Optional build arguments passed as --build-arg to docker build.
    /// </summary>
    public Dictionary<string, string>? BuildArgs { get; set; }
}
