using Lz.Core.Config;

namespace Lz.Aws.DynamoDB;

/// <summary>
/// What durability protections to apply to a subtenant table, derived purely
/// from config. SDK-free on purpose: the imperative
/// <see cref="DynamoDbTableCreator"/> translates a decision into CreateTable /
/// UpdateTable / UpdateContinuousBackups calls, but the DECISION is a pure
/// function so it is unit-testable without AWS.
/// </summary>
public readonly record struct TableDurabilityDecision(bool DeletionProtection, bool PointInTimeRecovery)
{
    /// <summary>Apply nothing — the byte-identical, no-opt-in baseline.</summary>
    public static readonly TableDurabilityDecision None = new(false, false);

    /// <summary>True when at least one protection is requested (nothing to do otherwise).</summary>
    public bool Any => DeletionProtection || PointInTimeRecovery;
}

/// <summary>What a subtenant-table teardown should do, given live state + operator intent.</summary>
public enum TableTeardownAction
{
    /// <summary>Table is unprotected — delete it directly (today's behaviour).</summary>
    Delete,

    /// <summary>Table is protected AND the operator forced it — disable protection, then delete.</summary>
    DisableProtectionThenDelete,

    /// <summary>Table is protected and no force flag — REFUSE (leave the table intact).</summary>
    Refuse,
}

/// <summary>
/// The pure durability decisions for the per-subtenant vault/PII table. Both the
/// create-time protections and the teardown action are decided here so the
/// imperative provisioner stays a thin translation layer and the branching is
/// covered by fast pure tests.
/// </summary>
public static class TableDurabilityPolicy
{
    /// <summary>
    /// Create/ensure-time decision for the subtenant vault table. A null config
    /// (section omitted) yields <see cref="TableDurabilityDecision.None"/> — no
    /// protection, byte-identical to a pre-durability deploy.
    /// </summary>
    public static TableDurabilityDecision ForVaultTable(DurabilityConfig? durability)
        => durability is null
            ? TableDurabilityDecision.None
            : new TableDurabilityDecision(durability.DeletionProtection, durability.PointInTimeRecovery);

    /// <summary>
    /// Teardown decision for the subtenant vault table.
    /// <list type="bullet">
    ///   <item>Unprotected → <see cref="TableTeardownAction.Delete"/> (force flag is irrelevant).</item>
    ///   <item>Protected + not forced → <see cref="TableTeardownAction.Refuse"/>.</item>
    ///   <item>Protected + forced → <see cref="TableTeardownAction.DisableProtectionThenDelete"/>.</item>
    /// </list>
    /// A protected table is NEVER deleted silently: protection only means
    /// something if teardown refuses it absent an explicit, separate opt-in.
    /// </summary>
    public static TableTeardownAction DecideTeardown(bool tableIsProtected, bool forceDeleteProtected)
        => !tableIsProtected ? TableTeardownAction.Delete
         : forceDeleteProtected ? TableTeardownAction.DisableProtectionThenDelete
         : TableTeardownAction.Refuse;
}
