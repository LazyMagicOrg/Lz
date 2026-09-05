namespace Lz.Core.Config;

/// <summary>
/// Opt-in controls that make a container rollback a real operation. Maps to the
/// "Rollback:" section in systemconfig.{systemkey}.{env}.yaml.
///
/// <para>ABSENT = OFF, DELIBERATELY. When the section is omitted (or a flag is false)
/// NOTHING changes: the task definition keeps naming the moving tag, no digest is
/// resolved, no AWS call is made, and the emitted Pulumi plan is byte-for-byte what a
/// pre-Rollback deploy produced. That is what keeps the five sibling workspaces
/// unchanged. Note the flags gate ASSIGNMENT, not just value: assigning an explicit
/// <c>false</c> is not the same plan as not assigning the property at all, the same rule
/// <see cref="DurabilityConfig"/> follows.</para>
///
/// <para>THE TWO FLAGS ARE INDEPENDENT AND BOTH ARE NEEDED, which is the non-obvious
/// part. Pinning a digest without retaining revisions produces correctly-pinned task
/// definitions that a service still cannot be pointed at: Pulumi deregisters the previous
/// revision on every image change, and AWS refuses to update a service to reference an
/// INACTIVE task definition. Retaining revisions without pinning produces a history of
/// revisions that all resolve through the same moving tag, so rolling to an older one
/// changes nothing. Turn both on, or understand which half you are getting.</para>
///
/// <para>SCOPE: the ECS/Fargate tenant-service path only. The Lambda topology builds the
/// same image string but is deliberately NOT pinned — its updater re-tags the URI by
/// string surgery that a digest reference breaks, and it runs on every deploy. The
/// FargateAlb lineage and the seed task are likewise left on the tag. A topology switch
/// therefore silently changes whether this guarantee applies.</para>
/// </summary>
public class RollbackConfig
{
    /// <summary>
    /// Name the container image by DIGEST (<c>{repo}@sha256:…</c>) instead of by the
    /// moving <c>:latest</c> tag. Default <c>false</c>.
    ///
    /// <para>The digest is resolved imperatively before the Pulumi program is built, and
    /// falls back to the tag when it cannot be resolved — an empty repository, a missing
    /// tag, or no AWS access. That fallback is what keeps a FIRST deploy working: on a new
    /// system <c>lz previewtenant</c> and <c>lz deploysystem</c> both run before any image
    /// has ever been pushed, and a design that failed there could not stand up a new
    /// environment at all.</para>
    ///
    /// <para>Costs a failure mode the tag does not have: <c>:latest</c> always resolves to
    /// something, while a pinned digest can be deleted out from under a task definition by
    /// an ECR lifecycle rule. <see cref="HygieneConfig.EcrBuildTagRetentionCount"/> is what
    /// keeps a durable tag on every pushed image; the config validator refuses the
    /// combination that would expire pinned digests without one.</para>
    /// </summary>
    public bool PinImageDigest { get; set; } = false;

    /// <summary>
    /// Keep old task-definition revisions ACTIVE instead of letting Pulumi deregister them
    /// (<c>SkipDestroy</c>). Default <c>false</c>.
    ///
    /// <para>This is the currently-binding constraint on rollback, and it is independent of
    /// pinning. Without it only the newest revision is ACTIVE — AWS documents that you
    /// cannot update an existing service to reference an INACTIVE task definition — so the
    /// revision history is not a runway however the image is named.</para>
    ///
    /// <para>Costs: revisions accumulate and are never pruned by lz. Deregistering by hand
    /// is the intended maintenance, and a deregistered revision remains retrievable by
    /// <c>DescribeTaskDefinition</c>, so history is not lost by pruning.</para>
    /// </summary>
    public bool RetainTaskDefinitionRevisions { get; set; } = false;
}
