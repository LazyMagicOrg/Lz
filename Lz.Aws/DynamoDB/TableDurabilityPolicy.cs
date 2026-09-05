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
    /// Create-time decision for the Cognito custom-auth VENDOR CREDENTIAL table
    /// (<c>{poolPrefix}-vendor-creds</c>), which holds vendor API-key hashes.
    /// <para>
    /// Same opt-in as <see cref="ForVaultTable"/> and for the same reason, but the
    /// stakes are higher and less obvious. That table's rows are HAND-SEEDED by
    /// <c>provisionvendor</c>, are stored nowhere else (a rotation overwrites the
    /// hash in place and nothing retains the prior one), and — unlike the vault
    /// table, which is created imperatively — it is a PULUMI resource, so an
    /// ordinary replace of the Cognito component destroys it. Deletion protection
    /// is what turns that silent destruction into a loud failure; PITR is what
    /// makes it recoverable.
    /// </para>
    /// <para>
    /// A null config (section omitted) yields <see cref="TableDurabilityDecision.None"/>,
    /// so a system that has not opted in gets a byte-identical plan.
    /// </para>
    /// </summary>
    public static TableDurabilityDecision ForVendorCredTable(DurabilityConfig? durability)
        => durability is null
            ? TableDurabilityDecision.None
            : new TableDurabilityDecision(durability.DeletionProtection, durability.PointInTimeRecovery);

    /// <summary>
    /// Create/ensure-time decision for the SYSTEM table (<c>{sk}</c>) — the
    /// foundation-level app table, same LazyMagic PK/SK envelope as the vault table.
    /// Takes both protections, for the reason row counts do not settle: this is
    /// system-level data of record, and PITR coverage is **not retroactive** — the
    /// restorable window starts when it is enabled — so enabling it while the table
    /// is empty is strictly better than enabling it once it matters.
    /// </summary>
    public static TableDurabilityDecision ForSystemTable(DurabilityConfig? durability)
        => durability is null
            ? TableDurabilityDecision.None
            : new TableDurabilityDecision(durability.DeletionProtection, durability.PointInTimeRecovery);

    /// <summary>
    /// Create/ensure-time decision for the TENANT table (<c>{sk}_{tk}</c>). Same class
    /// and same protections as <see cref="ForSystemTable"/>: it holds the tenant-user
    /// roster that binds Cognito identities to tenant roles, and that binding lives
    /// nowhere else — Cognito holds the identity, this table holds the membership.
    /// </summary>
    public static TableDurabilityDecision ForTenantTable(DurabilityConfig? durability)
        => durability is null
            ? TableDurabilityDecision.None
            : new TableDurabilityDecision(durability.DeletionProtection, durability.PointInTimeRecovery);

    /// <summary>
    /// Create/ensure-time decision for a BFF SESSION table (<c>{sk}_{tk}_bff</c>,
    /// <c>_cbff</c>, <c>_abff</c>) — the id/sk session store behind the cookie-to-bearer
    /// middleware.
    /// <para>
    /// **Deliberately divergent: deletion protection YES, point-in-time recovery NO**,
    /// and the asymmetry is the point rather than an oversight. Deletion protection is
    /// free and guards a real failure — dropping the table signs out every session and
    /// breaks the BFF until a redeploy recreates it. PITR is withheld because there is
    /// nothing to recover *to*: rows are session state regenerated by re-login, and TTL
    /// is deleting from these tables continuously by design, so a restore would
    /// resurrect sessions the system had deliberately expired.
    /// </para>
    /// <para>
    /// This is why the decision is a named function per table class rather than one
    /// config pair applied uniformly: PITR on a session table is cost without a
    /// recovery story, and that judgement belongs in code where it can be read.
    /// </para>
    /// </summary>
    public static TableDurabilityDecision ForBffSessionTable(DurabilityConfig? durability)
        => durability is null
            ? TableDurabilityDecision.None
            : new TableDurabilityDecision(durability.DeletionProtection, PointInTimeRecovery: false);

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
