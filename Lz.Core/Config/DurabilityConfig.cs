namespace Lz.Core.Config;

/// <summary>
/// Durability protections for the per-subtenant DynamoDB table (the vault/PII
/// table — the LazyMagic PK/SK envelope that holds a subtenant's surrogate-link
/// and party rows). Maps to the "Durability:" section in
/// systemconfig.{systemkey}.{env}.yaml.
///
/// <para>ABSENT = OFF, DELIBERATELY. When the section is omitted (or a flag is
/// false) NOTHING is applied: the CreateTable request leaves
/// <c>DeletionProtectionEnabled</c> unset and no
/// <c>UpdateContinuousBackups</c> call is made, so the emitted table is
/// byte-for-byte identical to a pre-durability deploy. This is what keeps
/// systems that don't opt in (e.g. MagicPets) unchanged — the flags gate
/// EXTRA calls, they never alter the baseline request. Scope is the subtenant
/// vault table only; the system/tenant/BFF-session tables are untouched.</para>
///
/// <para>Point-in-time recovery is the recovery net that makes the deliberate
/// <c>--force-delete-protected</c> teardown path acceptable — a forced delete
/// of a protected table is still restorable from a continuous backup.
/// Encryption at rest with a customer-managed key is a SEPARATE concern and is
/// intentionally NOT modelled here.</para>
/// </summary>
public class DurabilityConfig
{
    /// <summary>
    /// Enable DynamoDB deletion protection on the subtenant table. Default
    /// <c>false</c>. When true, the table cannot be deleted until protection is
    /// explicitly disabled — so <c>lz destroysubtenant</c> REFUSES a protected
    /// table unless the operator passes <c>--force-delete-protected</c> (which
    /// disables protection, then deletes).
    /// </summary>
    public bool DeletionProtection { get; set; } = false;

    /// <summary>
    /// Enable DynamoDB point-in-time recovery (continuous backups) on the
    /// subtenant table. Default <c>false</c>. Applied via a post-create
    /// <c>UpdateContinuousBackups</c> call (PITR is not a CreateTable field),
    /// and re-asserted idempotently on subsequent ensures.
    /// </summary>
    public bool PointInTimeRecovery { get; set; } = false;
}
