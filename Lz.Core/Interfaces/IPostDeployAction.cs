namespace Lz.Core.Interfaces;

/// <summary>
/// Imperative action to run after Pulumi stack.UpAsync() completes.
/// Used for operations that can't be expressed in the Pulumi resource graph
/// (e.g., ECS run-task for DB init, scaling services).
/// </summary>
public interface IPostDeployAction
{
    /// <summary>
    /// Execute the post-deploy action using resolved stack outputs.
    /// </summary>
    /// <param name="outputs">Stack outputs from Pulumi (string keys, resolved values).</param>
    Task ExecuteAsync(IDictionary<string, object> outputs);
}
