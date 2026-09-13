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

/// <summary>One container of a task definition, as far as choosing its image needs it.</summary>
public readonly record struct DefinedContainer(string? Name, string? Image);

/// <summary>
/// The pipeline's repository for one service, in the TARGET account's registry — where the build account
/// replicates the image this service deploys (DecoupledCd.md §6).
/// </summary>
public sealed record PipelineImageSource(string RegistryHost, string Repository);

/// <summary>Where a tenant service's image may come from.</summary>
/// <param name="ContainerName">The task definition's container, which the Fargate component names after the service.</param>
/// <param name="WorkstationRepository"><c>{sk}-{suffix}-{env}-{tk}-{svc}</c>: what <c>lz deploycontainer</c> pushes.</param>
/// <param name="PipelineSource">
/// NULL ON EVERY SYSTEM WITHOUT THE PIPELINE BLOCK, and then every decision that takes these sources is
/// exactly the one made before the pipeline existed — each says so, and the tests pin it with explicit
/// tables rather than by calling the old code. Present only when the block is enabled, accepts images,
/// builds this service, and names the account whose registry the image is replicated into.
/// </param>
public sealed record ServiceImageSources(string ContainerName, string WorkstationRepository, PipelineImageSource? PipelineSource);

/// <summary>Whether a repository holds an image with a given digest — or whether that could not be read.</summary>
public enum DigestPresence
{
    Present,
    Absent,

    /// <summary>The read failed. Never taken as absent: see <see cref="ServiceImageRead"/> for why.</summary>
    Unreadable,
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
            ServiceImageState.Unrecognized => throw new InvalidOperationException(
                $"The ECS service is not running an image this environment deploys: {service.Error}. " +
                "Refusing to build the task definition over it or around it. Nothing has been changed."),
            _ => registryDigest,
        };

    /// <summary>
    /// Pick the service's container out of its current task definition and classify its image.
    ///
    /// <para><b>WITHOUT THE PIPELINE, EXACTLY THE HISTORIC READ:</b> the first container whose image
    /// mentions the workstation repository, classified by <see cref="ClassifyServiceImage"/>.</para>
    ///
    /// <para><b>UNDER THE PIPELINE, BY NAME.</b> A pipeline deploy moves the container to another
    /// repository, so "whose image mentions the workstation repository" finds nothing after one — and that
    /// read then fell back to the workstation's <c>:latest</c>, so the next <c>lz deploytenant</c> would
    /// have put the workstation image back (DecoupledCd.md §14.1). The container is the one named after the
    /// service, and a pinned image is kept when it comes from either repository this environment deploys
    /// from; <see cref="ServiceImageRead.Repository"/> says which. Anything else is
    /// <see cref="ServiceImageState.Unrecognized"/>: an image nobody here put there is neither silently
    /// replaced nor silently kept.</para>
    /// </summary>
    public static ServiceImageRead ClassifyServiceContainers(
        bool serviceActive, IReadOnlyList<DefinedContainer>? containers, ServiceImageSources sources)
    {
        var all = containers ?? Array.Empty<DefinedContainer>();

        if (sources.PipelineSource is not { } pipeline)
            return ClassifyServiceImage(serviceActive, SelectContainerImage(all, sources));

        if (!serviceActive) return ServiceImageRead.NoService;

        var named = all.Where(c => string.Equals(c.Name, sources.ContainerName, StringComparison.Ordinal)).ToList();
        if (named.Count != 1)
            return ServiceImageRead.Unrecognized(
                $"its task definition has {named.Count} containers named '{sources.ContainerName}', not one");

        var image = named[0].Image;
        if (!IsDigestPinned(image)) return ServiceImageRead.NotDigestPinned;

        if (ParsePinnedImage(image!) is not { } parts)
            return ServiceImageRead.Unrecognized($"it names '{image}', which is not {{registry}}/{{repository}}@sha256:<64 hex>");

        if (string.Equals(parts.Repository, sources.WorkstationRepository, StringComparison.Ordinal))
            return ServiceImageRead.Pinned(parts.Digest);

        if (string.Equals(parts.Repository, pipeline.Repository, StringComparison.Ordinal)
            && string.Equals(parts.Host, pipeline.RegistryHost, StringComparison.Ordinal))
            return ServiceImageRead.Pinned(parts.Digest, parts.Repository);

        return ServiceImageRead.Unrecognized(
            $"it runs {image}, which is from neither {sources.WorkstationRepository} (lz deploycontainer) " +
            $"nor {pipeline.RegistryHost}/{pipeline.Repository} (the pipeline)");
    }

    /// <summary>
    /// The image of the service's container, or null when it cannot be identified: by name under the
    /// pipeline (exactly one), and without it the first container whose image mentions the workstation
    /// repository — the historic selection.
    /// </summary>
    public static string? SelectContainerImage(IReadOnlyList<DefinedContainer>? containers, ServiceImageSources sources)
    {
        var all = containers ?? Array.Empty<DefinedContainer>();
        if (sources.PipelineSource is null)
            return all.FirstOrDefault(c => c.Image != null
                                           && c.Image.Contains(sources.WorkstationRepository, StringComparison.Ordinal)).Image;

        var named = all.Where(c => string.Equals(c.Name, sources.ContainerName, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0].Image : null;
    }

    /// <summary>
    /// Whether a RUNNING task's container is the service's: by name under the pipeline, and without it by
    /// the historic test, that its image mentions the workstation repository.
    /// </summary>
    public static bool IsTheServicesContainer(string? name, string? image, ServiceImageSources sources)
        => sources.PipelineSource is null
            ? image?.Contains(sources.WorkstationRepository) ?? false
            : string.Equals(name, sources.ContainerName, StringComparison.Ordinal);

    /// <summary><c>{host}/{repository}@sha256:{64 hex}</c> in its parts, or null for anything else.</summary>
    public static (string Host, string Repository, string Digest)? ParsePinnedImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;

        var at = image.LastIndexOf('@');
        var slash = image.IndexOf('/');
        if (at < 0 || slash <= 0 || slash >= at - 1) return null;

        var digest = image[(at + 1)..];
        return IsSha256Digest(digest) ? (image[..slash], image[(slash + 1)..at], digest) : null;
    }

    /// <summary>The repository an image reference names, pinned or tagged; null when there is no image.</summary>
    public static string? RepositoryOf(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;

        var at = image.LastIndexOf('@');
        var reference = at >= 0 ? image[..at] : image;
        var slash = reference.IndexOf('/');
        var path = slash >= 0 ? reference[(slash + 1)..] : reference;
        var colon = path.LastIndexOf(':');
        return colon >= 0 ? path[..colon] : path;
    }

    /// <summary>True for <c>sha256:</c> followed by exactly 64 lowercase hex digits.</summary>
    public static bool IsSha256Digest(string? digest)
        => digest is { Length: 71 }
           && digest.StartsWith("sha256:", StringComparison.Ordinal)
           && digest[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// The repository the task definition's image names: the one the resolved digest is in when that is not
    /// the workstation's — a pipeline image the service already runs — and otherwise the workstation's.
    ///
    /// <para>ONLY A PINNED IMAGE MAY MOVE. A tag names nothing useful in the pipeline's repository, whose only
    /// tags are commit SHAs, so without a resolved digest the historic <c>:latest</c> reference stands.</para>
    /// </summary>
    public static string ImageRepository(
        string workstationRepository, string? resolvedRepository, string? digest, ImagePinDecision decision)
        => decision.PinDigest && !string.IsNullOrWhiteSpace(digest) && !string.IsNullOrWhiteSpace(resolvedRepository)
            ? resolvedRepository!
            : workstationRepository;

    /// <summary>
    /// Which repository an explicit <c>lz updatecontainer --digest</c> deploys from.
    ///
    /// <para><b>WITHOUT THE PIPELINE, the workstation repository, unread</b> — the digest is taken as given,
    /// as it always was.</para>
    ///
    /// <para><b>UNDER IT, wherever the digest is.</b> A rollback target may be a pipeline image or a
    /// workstation one, and pinning a digest to a repository that does not hold it is a revision ECS
    /// discovers is broken only when its tasks fail to start. The pipeline's repository wins when both
    /// hold the digest: the bytes are identical, and under the block the pipeline is the image family of
    /// record. A read that failed refuses rather than counting as absent, so a lost SSO session cannot
    /// become "deploy it from the other repository".</para>
    /// </summary>
    /// <returns>The repository, or a refusal saying why there is none.</returns>
    public static (string? Repository, string? Refusal) RepositoryForDigest(
        string digest, ServiceImageSources sources, DigestPresence inWorkstation, DigestPresence inPipeline)
    {
        if (sources.PipelineSource is not { } pipeline)
            return (sources.WorkstationRepository, null);

        if (!IsSha256Digest(digest))
            return (null, $"'{digest}' is not a sha256 digest (sha256: followed by 64 hex digits).");

        if (inWorkstation == DigestPresence.Unreadable || inPipeline == DigestPresence.Unreadable)
            return (null,
                $"could not read whether {digest} is in {sources.WorkstationRepository} or {pipeline.Repository}. " +
                "Nothing has been changed; fix the read (SSO session, permissions, throttling) and retry.");

        if (inPipeline == DigestPresence.Present) return (pipeline.Repository, null);
        if (inWorkstation == DigestPresence.Present) return (sources.WorkstationRepository, null);

        return (null, $"{digest} is in neither {sources.WorkstationRepository} nor {pipeline.Repository}; there is nothing to deploy.");
    }

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
