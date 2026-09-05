using Lz.Core.Config;

namespace Lz.Aws.Auth;

/// <summary>
/// The pure durability decision for a Cognito user pool, derived purely from config.
/// SDK-free on purpose, exactly like <see cref="Lz.Aws.DynamoDB.TableDurabilityPolicy"/>:
/// the component translates the decision into a <c>UserPoolArgs</c> assignment, but the
/// DECISION is a pure function so it is unit-testable without AWS.
/// <para>
/// Deliberately NOT expressed as a <c>TableDurabilityDecision</c>. That type carries a
/// <c>PointInTimeRecovery</c> flag, and Cognito has no point-in-time-recovery concept at
/// all — reusing it would smuggle a meaningless field into the pool path and invite a
/// future reader to wire it to something.
/// </para>
/// </summary>
public static class CognitoDurabilityPolicy
{
    /// <summary>Cognito's own wire values for the pool's <c>DeletionProtection</c> field.</summary>
    public const string Active = "ACTIVE";

    /// <summary>See <see cref="Active"/>. This is also the service-side default.</summary>
    public const string Inactive = "INACTIVE";

    /// <summary>
    /// What to set <c>UserPoolArgs.DeletionProtection</c> to, or <c>null</c> to leave the
    /// property UNSET.
    /// <list type="bullet">
    ///   <item><c>null</c> when the Durability section is absent — the property is not
    ///     assigned at all, so an un-opted-in system emits the plan it emitted before this
    ///     existed. Assigning <c>"INACTIVE"</c> here instead would be *nearly* the same
    ///     thing (INACTIVE is the service default) but not identically the same, and
    ///     "byte-identical when absent" is the compatibility guarantee six workspaces run
    ///     on — so it is honoured literally rather than approximately.</item>
    ///   <item><c>"INACTIVE"</c> when the section is present and opts out — an explicit
    ///     choice, which is worth stating on the resource.</item>
    ///   <item><c>"ACTIVE"</c> when the section opts in.</item>
    /// </list>
    /// <para>
    /// Returns <see cref="string"/> rather than <see cref="bool"/> because the Pulumi
    /// member is <c>Input&lt;string&gt;</c> over Cognito's two literals, not a flag.
    /// </para>
    /// <para>
    /// One functional consequence to know before opting in: with the pool ACTIVE,
    /// <c>DeleteUserPool</c> is refused by the service, so a <c>pulumi destroy</c> of the
    /// foundation stack FAILS at the pool — and it fails *after* Pulumi has already
    /// deleted everything that depends on it (the domain, the clients, the managed-login
    /// branding), because deletion runs in reverse dependency order. That is protection
    /// working as intended, but it is a half-destroyed stack rather than a clean refusal.
    /// See the teardown note in <c>Docs/reference/operations/runbooks.md</c>.
    /// </para>
    /// </summary>
    public static string? ForUserPool(DurabilityConfig? durability)
        => durability is null
            ? null
            : durability.DeletionProtection ? Active : Inactive;
}
