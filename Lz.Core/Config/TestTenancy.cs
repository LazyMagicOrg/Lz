namespace Lz.Core.Config;

/// <summary>
/// The tenancy a system's AWS-backed test and dev tooling addresses, and the SINGLE place the
/// identifiers for it are derived.
///
/// <para>WHY THIS TYPE EXISTS. The tenancy is two different renderings of the same three keys, and
/// lz is what builds the resources both name (the DynamoDB grants in
/// <c>Lz.Aws/Compute/*/Aws*TenantServiceComponent.cs</c> authorize <c>{sk}_{tk}</c> and
/// <c>{sk}_{tk}_*</c>):</para>
/// <code>
///     table / CallerInfo.DefaultDB   {sk}_{tk}_{stk}
///     CallerInfo.TenantId            {sk}-{tk}-{stk}
/// </code>
/// <para>Every copy of that derivation is a copy that can drift from what lz provisions. A SystemKey
/// rename once left five of a consuming system's test suites holding a literal table name that no
/// longer existed; the DynamoDB failure surfaced as "caller identity could not be resolved",
/// naming nothing close to the cause.</para>
///
/// <para>NOTHING HERE IS INFERRED. <see cref="TryResolve"/> fails rather than guessing a tenant, and
/// every caller is expected to propagate that failure as a skip or a non-zero exit. This matters
/// most for the destructive callers: the teardown drill interpolates the tenant key into
/// <c>lz destroytenant</c>, and the AWS test tier writes to a live table. Inferring a tenant — from
/// "whichever tenantconfig happens to be the only one on disk", say — is how tooling ends up
/// addressing the wrong tenant's data while looking like it worked.</para>
/// </summary>
/// <param name="SystemKey">e.g. "scu" — from the systemconfig filename, not a field in the file.</param>
/// <param name="TenantKey">e.g. "mp" — systemconfig's <c>TestTenant</c>.</param>
/// <param name="SubtenantKey">e.g. "match"; null when no <c>TestSubtenant</c> is configured.</param>
public sealed record TestTenancy(string SystemKey, string TenantKey, string? SubtenantKey)
{
    /// <summary>The tenant-level table, <c>{sk}_{tk}</c> — e.g. <c>scu_mp</c>.</summary>
    public string TenantTable => $"{SystemKey}_{TenantKey}";

    /// <summary>
    /// The table this tenancy addresses: the subtenant table when one is configured, else the
    /// tenant table. This is what belongs in <c>CallerInfo.DefaultDB</c>.
    /// </summary>
    public string Table => SubtenantKey is null ? TenantTable : $"{TenantTable}_{SubtenantKey}";

    /// <summary>
    /// The <c>CallerInfo.TenantId</c> rendering — the SAME keys joined with '-' rather than '_'
    /// (e.g. <c>scu-mp-match</c>). Both forms must move together; deriving them here is what
    /// guarantees they cannot disagree.
    /// </summary>
    public string TenantId => SubtenantKey is null
        ? $"{SystemKey}-{TenantKey}"
        : $"{SystemKey}-{TenantKey}-{SubtenantKey}";

    /// <summary>
    /// Resolves the tenancy from a loaded systemconfig, or explains why it cannot.
    /// </summary>
    /// <param name="config">A systemconfig loaded by <c>ConfigLoader</c> (SystemKey/Environment
    /// already populated from the filename).</param>
    /// <param name="tenancy">The resolved tenancy, or null.</param>
    /// <param name="reason">Empty on success; otherwise an actionable message naming the file to
    /// edit and the line to add. Callers surface this verbatim — a caller that swallows it turns a
    /// one-line config fix into a debugging session.</param>
    /// <returns>True when <c>TestTenant</c> is set.</returns>
    public static bool TryResolve(SystemConfig config, out TestTenancy? tenancy, out string reason)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.TestTenant))
        {
            tenancy = null;
            reason =
                $"'TestTenant' is not set in systemconfig.{config.SystemKey}.{config.Environment}.yaml. " +
                "Tooling that reads this addresses REAL deployed resources, so the tenancy is named " +
                "explicitly rather than guessed - add e.g. 'TestTenant: mp' " +
                "(and optionally 'TestSubtenant: match').";
            return false;
        }

        tenancy = new TestTenancy(
            config.SystemKey,
            config.TestTenant.Trim(),
            string.IsNullOrWhiteSpace(config.TestSubtenant) ? null : config.TestSubtenant.Trim());
        reason = "";
        return true;
    }
}
