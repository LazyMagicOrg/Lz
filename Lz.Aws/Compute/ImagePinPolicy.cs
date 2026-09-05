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
