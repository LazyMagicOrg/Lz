using Lz.Core.Orchestration;

namespace Lz.Core.Interfaces;

/// <summary>
/// Platform-specific implementation for checking transition requirements.
/// Each platform (AWS, Azure) implements the actual checks against its
/// services (Secrets Manager, EFS, etc.).
/// </summary>
public interface ITransitionChecker
{
    /// <summary>
    /// Check whether a single transition requirement is met.
    /// </summary>
    /// <param name="requirement">The requirement to check.</param>
    /// <param name="systemKey">The system key for template token replacement.</param>
    /// <param name="tenantKey">Optional tenant key for template token replacement.</param>
    /// <returns>True if the requirement is satisfied.</returns>
    Task<bool> CheckAsync(TransitionRequirement requirement, string systemKey, string? tenantKey = null);
}
