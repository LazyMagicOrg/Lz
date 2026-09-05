using Lz.Core.Config;

namespace Lz.Aws.Compute;

/// <summary>What image-pinning protections to apply, derived purely from config.</summary>
public readonly record struct ImagePinDecision(bool PinDigest, bool RetainRevisions)
{
    /// <summary>Apply nothing — the byte-identical, no-opt-in baseline.</summary>
    public static readonly ImagePinDecision None = new(false, false);

    /// <summary>True when at least one protection is requested (nothing to do otherwise).</summary>
    public bool Any => PinDigest || RetainRevisions;
}

/// <summary>
/// The pure decisions behind container-image pinning and task-definition retention.
/// SDK-free and Pulumi-free on purpose, exactly like
/// <see cref="Lz.Aws.DynamoDB.TableDurabilityPolicy"/>: the components translate a decision
/// into resource arguments, but the DECISION is a pure function so it is unit-testable
/// without AWS and without a Pulumi engine.
/// </summary>
public static class ImagePinPolicy
{
    /// <summary>
    /// Create-time decision for a tenant ECS service. A null config (section omitted)
    /// yields <see cref="ImagePinDecision.None"/> — no digest is resolved, no AWS call is
    /// made, and the emitted plan is byte-identical to a pre-Rollback deploy.
    /// </summary>
    public static ImagePinDecision ForTenantService(RollbackConfig? rollback)
        => rollback is null
            ? ImagePinDecision.None
            : new ImagePinDecision(rollback.PinImageDigest, rollback.RetainTaskDefinitionRevisions);

    /// <summary>
    /// The container image reference to put in the task definition:
    /// <c>{repoUri}@{digest}</c> when pinning is on AND a digest was resolved, otherwise
    /// <c>{repoUri}:{tag}</c>.
    ///
    /// <para><b>The fallback is the whole reason this is a function and not an if.</b> A
    /// null digest is the ordinary case on a FIRST deploy — <c>lz previewtenant</c> is
    /// documented to work before any image exists, and <c>deploysystem</c>/<c>deploytenant</c>
    /// run before the first <c>deploycontainer</c> on a new system. Falling back to the tag
    /// makes the empty-repository case and the not-opted-in case the same code path, which
    /// is also why they share a test.</para>
    ///
    /// <para>Note the digest is expected in its full <c>sha256:…</c> form, which is what
    /// <c>describe-images</c> returns, so it is concatenated after <c>@</c> verbatim.</para>
    /// </summary>
    public static string ImageRef(string repoUri, string tag, string? digest, ImagePinDecision decision)
        => decision.PinDigest && !string.IsNullOrWhiteSpace(digest)
            ? $"{repoUri}@{digest}"
            : $"{repoUri}:{tag}";

    /// <summary>
    /// Classify what a successful read of the service's current task definition found. Pure
    /// — the SDK caller supplies the two facts and this names them, so the classification
    /// (and the <c>@</c> parsing) is unit-tested without ECS.
    /// </summary>
    /// <param name="serviceActive">An ACTIVE service of that name exists in an ACTIVE cluster.</param>
    /// <param name="image">The image string its definition names for our repository, or null when none does.</param>
    public static ServiceImageRead ClassifyServiceImage(bool serviceActive, string? image)
    {
        if (!serviceActive) return ServiceImageRead.NoService;
        if (!IsDigestPinned(image)) return ServiceImageRead.NotDigestPinned;
        return ServiceImageRead.Pinned(image![(image.LastIndexOf('@') + 1)..]);
    }

    /// <summary>
    /// Which digest the task definition should declare, given what the SERVICE currently runs
    /// and what the REGISTRY's <c>:latest</c> points at. The service wins.
    ///
    /// <para><b>This precedence is what makes a rollback survive a tenant deploy.</b> Pulumi
    /// owns <c>service.taskDefinition</c> and re-points the service at its own revision on
    /// every <c>deploytenant</c>. If that revision were built from <c>:latest</c>, a
    /// <c>deploytenant</c> after an <c>updatecontainer --digest</c> rollback would silently roll
    /// the service forward again — observed live on 2026-09-05 (the service went from an
    /// imperative revision 7 back to Pulumi's revision 5 during an unrelated deploy). Declaring
    /// the digest the service already runs means Pulumi's revision always matches it, so
    /// re-pointing changes nothing about the image. It is still one ECS rolling deployment on
    /// the same digest — the first <c>deploytenant</c> after any <c>updatecontainer</c> re-points
    /// the service from the imperative revision to Pulumi's, and later ones are quiet.</para>
    ///
    /// <para><b>An unreadable service is refused, not treated as absent.</b> The registry is
    /// the right answer only when there is genuinely no pinned service (a first deploy, or a
    /// pre-pinning tag-form revision). A read that FAILED tells us nothing, and falling
    /// through to <c>:latest</c> there is exactly the bug above wearing a different log line —
    /// so this throws, and the caller aborts before Pulumi runs. Nothing has been mutated at
    /// that point; the operator fixes the read (SSO session, permissions, throttling) and
    /// retries. See <see cref="ServiceImageRead"/> for why absent and unreadable are
    /// distinguishable at all.</para>
    ///
    /// <para>The consequence, stated plainly because it is a change in what <c>deploytenant</c>
    /// means on a pinned system: <b>a tenant deploy never advances the image</b>. Pushing a new
    /// <c>:latest</c> and running <c>deploytenant</c> leaves the old image running;
    /// <c>lz updatecontainer</c> is the only thing that moves it.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The service read failed (<see cref="ServiceImageState.Unreadable"/>).</exception>
    public static string? ChooseDigest(ServiceImageRead service, string? registryDigest)
        => service.State switch
        {
            ServiceImageState.DigestPinned => service.Digest,
            ServiceImageState.Unreadable => throw new InvalidOperationException(
                $"Cannot read the ECS service's current task definition ({service.Error}). " +
                "Refusing to build the task definition from ECR :latest — on a rolled-back service " +
                "that would silently roll it forward. Nothing has been changed; fix the read " +
                "(SSO session, permissions, throttling) and retry."),
            _ => registryDigest,
        };

    /// <summary>
    /// True when an image reference names a digest rather than a tag. Used by the container
    /// updater to choose between registering a new task-definition revision (a pinned
    /// definition cannot change what runs by being force-deployed) and today's plain
    /// force-deploy (a tag-pinned definition re-pulls on its own).
    ///
    /// <para>Deliberately keyed on the LAST <c>@</c> rather than on "contains sha256":
    /// a repository URI cannot contain <c>@</c>, and the tag form never does.</para>
    /// </summary>
    public static bool IsDigestPinned(string? imageRef)
        => !string.IsNullOrWhiteSpace(imageRef) && imageRef.Contains('@');
}
