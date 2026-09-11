namespace Lz.Core.Config;

/// <summary>
/// Opt-in decoupled-CD pipeline. Maps to the "Pipeline:" section in
/// systemconfig.{systemkey}.{env}.yaml. Design: Docs/specs/DecoupledCd.md.
///
/// <para>WHAT IT IS FOR. Today every class this system deploys reaches AWS because a person ran an
/// <c>lz</c> command from a workstation holding administrator credentials. The pipeline moves the
/// ACT of deploying inside the target account and reduces GitHub to something that can only ADD an
/// artifact to a store — never alter one, never deploy one. This block is what turns that on, per
/// environment, so dev can deploy automatically while prod requires a human inside the account.</para>
///
/// <para>ABSENT = OFF, DELIBERATELY, and this is the contract the five sibling workspaces run on.
/// When the section is omitted NOTHING changes: no pipeline resources, no gated branch taken, and
/// the emitted plan is byte-for-byte what a pre-Pipeline deploy produced. The same rule the other
/// opt-in blocks follow (<see cref="DurabilityConfig"/>, <see cref="HygieneConfig"/>,
/// <see cref="RollbackConfig"/>): the flags gate EXTRA behaviour and never alter the baseline.
/// DecoupledCd.md is explicit that this is "not a courtesy here; it is the compatibility guarantee
/// the sibling systems run on", which is why it is pinned by a test rather than by a comment.</para>
///
/// <para>IT DOES NOT IMPLY <see cref="RollbackConfig.PinImageDigest"/>, and that is a deliberate
/// refusal rather than an omission. The pipeline deploys class 1 by digest, and the mechanism for
/// that ALREADY EXISTS under <c>Rollback</c> — so the tempting move is to have
/// <c>Pipeline.Enabled</c> silently turn it on. That would make one opt-in block change another
/// block's emitted plan invisibly, in a design whose whole legibility rests on "absent means
/// byte-identical". Instead the validator REFUSES the combination and names the flag to add. Two
/// explicit flags beat one flag with a hidden second effect.</para>
///
/// <para>NOTHING HERE IS BUILT YET. This is the schema and its validator — P0 of DecoupledCd's
/// punchlist — landing before the state machine, stores and roles so that later work has a gate to
/// hang from. A config that sets <c>Enabled: true</c> today gets a validated block that no
/// component reads.</para>
/// </summary>
public class PipelineConfig
{
    /// <summary>
    /// Turn the pipeline on for this environment. Default <c>false</c>.
    ///
    /// <para>Present-but-false is meaningful and supported: it documents that the environment has
    /// considered the pipeline and is not using it, which reads differently in a diff from the
    /// section being missing. Both produce the identical plan.</para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The account holding the registry and the artifact, build-record and request stores. Twelve
    /// digits. Optional: when null the artifact origin is THIS account, which is the arrangement
    /// until the separate build account exists (DecoupledCd.md section 11.1 is still OPEN, so this
    /// deliberately has no default pointing anywhere).
    /// </summary>
    public string? ArtifactAccountId { get; set; }

    /// <summary>
    /// Which artifact classes this environment accepts. Required when <see cref="Enabled"/>.
    ///
    /// <para>SIX NAMES COVER THE EIGHT CLASSES of DecoupledCd.md section 3, because classes 5, 6
    /// and 7 — edge functions and KVS routing, the Pulumi stacks, and the SSM runtime config — are
    /// all built from the root repo and travel as one config bundle:</para>
    ///
    /// <list type="bullet">
    ///   <item><c>image</c> — class 1, the service container image</item>
    ///   <item><c>client</c> — class 2, the Blazor client bundles</item>
    ///   <item><c>site</c> — class 3, the public and review site bundles</item>
    ///   <item><c>assets</c> — class 4, the tenancy asset trees</item>
    ///   <item><c>config</c> — classes 5–7, the config bundle</item>
    ///   <item><c>tooling</c> — class 8, the deployer image itself</item>
    /// </list>
    ///
    /// <para>It is an allowlist, so an environment accepts exactly what is named: a build record
    /// whose class is absent from this list is refused at Verify (section 4.4 step 1) rather than
    /// ignored. A typo therefore fails closed — it removes a class rather than adding one — but the
    /// validator rejects unknown names anyway, because "the deploy silently stopped covering the
    /// client bundles" is not a failure anyone would look for.</para>
    /// </summary>
    public List<string>? Classes { get; set; }

    public PipelineRegistryConfig? Registry { get; set; }
    public PipelineApprovalConfig? Approval { get; set; }
    public PipelineReconcilerConfig? Reconciler { get; set; }
    public PipelineScanConfig? Scan { get; set; }

    /// <summary>
    /// Test tiers the deployer runs after a rollout, by Category name (e.g. <c>Aws</c>,
    /// <c>E2E</c>). Empty or null runs none.
    ///
    /// <para>DecoupledCd.md section 10 expects this to be empty in prod — a post-deploy suite that
    /// writes to a production store is a data-integrity problem, not a safety net. That is a policy
    /// this validator deliberately does NOT enforce: it is the operator's call per environment, and
    /// a validator that refused it would be encoding a Scutara decision into a library every
    /// sibling system shares.</para>
    /// </summary>
    public List<string>? PostDeployTests { get; set; }

    /// <summary>The class names <see cref="Classes"/> accepts. See that property for the mapping.</summary>
    public static readonly string[] KnownClasses =
        { "image", "client", "site", "assets", "config", "tooling" };
}

/// <summary>Registry hardening for the pipeline. See DecoupledCd.md sections 4.1 and 8.</summary>
public class PipelineRegistryConfig
{
    /// <summary>
    /// <c>neutral</c> for an environment-independent repository name, or <c>environment</c> (the
    /// default) to keep today's <c>{sk}-{suffix}-{env}-{tk}-{svc}</c>.
    ///
    /// <para>Neutral naming is what lets one built image be replicated between accounts and stay
    /// the same image: an image promoted from dev to prod cannot carry <c>-dev-</c> in its
    /// repository name and still be the artifact that was tested.</para>
    /// </summary>
    public string? RepositoryNaming { get; set; }

    /// <summary>
    /// Refuse to overwrite an existing tag. Default <c>null</c> — leave the repository's current
    /// setting alone, which is the byte-identical baseline.
    /// </summary>
    public bool? TagImmutability { get; set; }

    /// <summary>The values <see cref="RepositoryNaming"/> accepts.</summary>
    public static readonly string[] KnownNamings = { "neutral", "environment" };
}

/// <summary>The human gate in front of a deploy. See DecoupledCd.md section 4.5.</summary>
public class PipelineApprovalConfig
{
    /// <summary>
    /// Require a human inside the account to approve before the deploy proceeds. Prod: true.
    /// Dev: false, which is the point of having this per environment.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// How long the execution waits for that human before failing. Default 86400 (one day).
    ///
    /// <para>It is a HEARTBEAT rather than a deadline: the wait fails only if nothing is heard
    /// within the window, so this is "how long may an approval request sit unanswered", not "how
    /// long may the whole deploy take".</para>
    /// </summary>
    public int? HeartbeatSeconds { get; set; }

    /// <summary>
    /// SNS topic the approval request is published to. REQUIRED when <see cref="Required"/> is
    /// true — an approval nobody is told about is not a gate, it is a deploy that waits a day and
    /// then fails. The validator refuses the combination.
    /// </summary>
    public string? NotifyTopicArn { get; set; }

    /// <summary>
    /// Fail the execution when the Pulumi plan contains a replace or delete, unless the deploy
    /// request carries an explicit override. Default <c>false</c>.
    /// </summary>
    public bool RefuseDestructivePlan { get; set; }
}

/// <summary>The sweep that catches what events lost. See DecoupledCd.md section 5.3.</summary>
public class PipelineReconcilerConfig
{
    /// <summary>
    /// How often to enumerate the build-record store looking for records no execution handled.
    /// Default 15.
    ///
    /// <para>This exists because event delivery is best-effort: S3 and EventBridge both drop
    /// deliveries under documented conditions, so the record store is the system of record and the
    /// reconciler is what makes that true in practice rather than in principle.</para>
    /// </summary>
    public int? IntervalMinutes { get; set; }
}

/// <summary>Image-scan policy. See DecoupledCd.md sections 4.4 and 11.3.</summary>
public class PipelineScanConfig
{
    /// <summary>
    /// Severities that block a deploy, e.g. <c>[CRITICAL]</c>. Empty or null blocks on none.
    ///
    /// <para>A severity key is absent from ECR's counts when its count is zero, so "not present"
    /// means none found — not "not scanned". A scan that has not completed yet is a bounded wait,
    /// never a pass.</para>
    /// </summary>
    public List<string>? BlockOn { get; set; }

    /// <summary>The severities <see cref="BlockOn"/> accepts, as ECR reports them.</summary>
    public static readonly string[] KnownSeverities =
        { "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFORMATIONAL", "UNDEFINED" };
}
