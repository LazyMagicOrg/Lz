namespace Lz.Core.Orchestration;

/// <summary>
/// Determines how a transition requirement is checked.
/// </summary>
public enum TransitionCheckType
{
    /// <summary>Check for a non-empty key in a Secrets Manager JSON secret.</summary>
    SecretEntry,

    /// <summary>Check for data on EFS via the gate-checker Lambda.</summary>
    EfsData,

    /// <summary>Check for a Pulumi stack output from a prior phase.</summary>
    StackOutput,

    /// <summary>Delegate to a custom check function.</summary>
    Custom,

    /// <summary>Check for database tables/data via the gate-checker Lambda.</summary>
    DatabaseData
}

/// <summary>
/// A gate between deployment steps. When not met, the deployment stops
/// with a message telling the user what manual action is needed.
/// The user performs the action and re-runs the same command.
/// Pulumi idempotency ensures already-created resources are untouched.
/// </summary>
public class TransitionRequirement
{
    /// <summary>Machine-readable name, e.g., "tailscale-auth-key".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable message displayed when the gate is not met.
    /// Should describe what manual action is needed.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>How to check whether this requirement is satisfied.</summary>
    public TransitionCheckType CheckType { get; set; }

    /// <summary>
    /// Check-type-specific target:
    /// - SecretEntry: the JSON key name to look for in the secret
    /// - EfsData: the EFS path to check for files (e.g., "/{SK}-{TK}-{env}/smartstore-data")
    /// - DatabaseData: the database name to check for tables (e.g., "{SK}-{TK}-{env}-smartstore")
    /// - StackOutput: the output key name
    /// - Custom: ignored (use CustomCheck delegate)
    /// Supports template tokens: {SK} = SystemKey, {TK} = TenantKey, {env} = Environment.
    /// </summary>
    public string CheckTarget { get; set; } = string.Empty;

    /// <summary>
    /// For SecretEntry checks: the Secrets Manager secret name.
    /// Supports template tokens: {SK} = SystemKey, {TK} = TenantKey, {env} = Environment.
    /// </summary>
    public string? SecretName { get; set; }

    /// <summary>
    /// Optional AWS profile override for cross-account SecretEntry checks.
    /// When set, the gate checker uses this profile instead of the system's own profile.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Optional AWS region override for cross-account checks.
    /// When set, the gate checker uses this region instead of the system's own region.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Whether this requirement is only relevant on first deploy.
    /// Once satisfied, it typically stays satisfied on subsequent deploys.
    /// </summary>
    public bool IsOneTime { get; set; }

    /// <summary>
    /// Optional delegate for Custom check type.
    /// Returns true if the requirement is met.
    /// </summary>
    public Func<Task<bool>>? CustomCheck { get; set; }
}
