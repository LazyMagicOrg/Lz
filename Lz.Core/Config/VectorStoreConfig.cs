namespace Lz.Core.Config;

/// <summary>
/// Vector-store provisioning for the semantic-matching backend — an OpenSearch
/// Serverless (aoss) collection. Maps to the "VectorStore:" section in
/// systemconfig.{systemkey}.{env}.yaml.
///
/// <para>ABSENT = OFF, DELIBERATELY. When the section is omitted NOTHING
/// aoss-related is provisioned and no OpenSearch env var or IAM statement
/// reaches the tenant service — the emitted stacks are byte-identical to a
/// pre-feature deploy. This is what keeps systems that don't opt in
/// (e.g. MagicPets) unchanged.</para>
///
/// <para>When present, <c>lz deploysystem</c> creates in the FOUNDATION Pulumi
/// stack (see <c>AwsVectorStoreComponent</c>): an encryption security policy
/// (AWS-owned key), a public SigV4 network policy, (when <see cref="ScaleToZero"/>)
/// a NEXTGEN collection group with min 0 OCU — idle ≈ $0, billing stops after a
/// ~10-minute idle tail, wake-up is a 10–30 s first-query latency — capped at
/// the Max*Ocu ceilings, the collection itself, and a data-access policy for
/// <see cref="DataAccessPrincipals"/>. The collection endpoint is exported as
/// <c>vectorStoreEndpoint</c> and injected into the tenant service
/// (<c>OpenSearch__Endpoint</c>), whose execution role also receives
/// <c>aoss:APIAccessAll</c> plus its own per-tenant data-access policy.</para>
///
/// <para>Provisioned in the foundation ON PURPOSE: the original collection was
/// created out-of-band and was silently left behind when the system moved to a
/// new AWS account. Owned by the deploy, that cannot repeat. The collection is
/// Pulumi-managed, so <c>lz destroysystem</c> deletes it — acceptable because
/// vectors are derived data (re-embed via the loader's reindex).</para>
///
/// <para>API constraints encoded here: NEXTGEN requires StandbyReplicas=ENABLED
/// (the API rejects DISABLED — standby is moot at scale-to-zero), and aoss
/// names are 3–32 chars of lowercase letters/digits/hyphens.</para>
/// </summary>
public class VectorStoreConfig
{
    /// <summary>
    /// Collection name. Empty (the default) derives
    /// <c>{SystemKey}-{Environment}-match</c> (e.g. <c>scu-dev-match</c>).
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Collection type: <c>VECTORSEARCH</c> (default), <c>SEARCH</c>, or
    /// <c>TIMESERIES</c>.
    /// </summary>
    public string Type { get; set; } = "VECTORSEARCH";

    /// <summary>
    /// Create a NEXTGEN scale-to-zero collection group (min 0 OCU) and place
    /// the collection in it. Default <c>true</c>. When false a classic
    /// collection is created instead — which carries a permanent OCU floor
    /// (real monthly cost while idle).
    /// </summary>
    public bool ScaleToZero { get; set; } = true;

    /// <summary>Per-group max indexing OCUs — the indexing cost ceiling. Default 2.</summary>
    public int MaxIndexingOcu { get; set; } = 2;

    /// <summary>Per-group max search OCUs — the search cost ceiling. Default 2.</summary>
    public int MaxSearchOcu { get; set; } = 2;

    /// <summary>
    /// Extra IAM principal ARNs granted data access to the collection (e.g. the
    /// dev SSO role that runs tests and the product loader). The tenant service
    /// execution role is granted automatically by the tenant stack and does NOT
    /// belong in this list.
    /// </summary>
    public List<string> DataAccessPrincipals { get; set; } = new();
}
