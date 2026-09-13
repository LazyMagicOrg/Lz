namespace Lz.Aws.Compute;

/// <summary>What a read of an ECS service's current task-definition image established.</summary>
public enum ServiceImageState
{
    /// <summary>No ACTIVE cluster or no ACTIVE service of that name — a first deploy, or a torn-down tenant.</summary>
    NoService,

    /// <summary>The service exists but its definition names a tag (a pre-pinning revision) or no container from this repository.</summary>
    NotDigestPinned,

    /// <summary>The service's definition names <c>{repo}@{digest}</c>; <see cref="ServiceImageRead.Digest"/> carries it.</summary>
    DigestPinned,

    /// <summary>The read FAILED — throttling, AccessDenied, an expired SSO session, a missing profile. Nothing is known.</summary>
    Unreadable,

    /// <summary>
    /// UNDER THE PIPELINE BLOCK ONLY. The read succeeded, and what the service runs is not something this
    /// environment deploys: an image from neither the workstation's repository nor the pipeline's, or no
    /// single container named after the service. Neither replaced nor kept — the deploy stops, and
    /// <see cref="ServiceImageRead.Error"/> says what was found.
    /// </summary>
    Unrecognized,
}

/// <summary>
/// The result of reading what image an ECS service currently runs, kept as a tri-state
/// (plus failure) rather than a nullable string on purpose.
///
/// <para><b>Absent and unreadable are different answers and must stay different.</b> Both
/// legitimate "absent" shapes are reported by the API WITHOUT an exception — a missing
/// cluster or service comes back under <c>Failures</c>, and a tag-form image is simply a
/// string without an <c>@</c>. So a <c>catch</c> that returns null only ever converts a real
/// ERROR into "no service", and on a pinned system "no service" means "build the task
/// definition from ECR <c>:latest</c>" — which, after an <c>updatecontainer --digest</c>
/// rollback, silently rolls the service forward again while logging that no pinned service
/// exists. Carrying <see cref="ServiceImageState.Unreadable"/> explicitly is what lets
/// <see cref="ImagePinPolicy.ChooseDigest"/> refuse instead.</para>
/// </summary>
/// <param name="Repository">
/// The repository the pinned digest is in, when that is NOT the one <c>lz deploycontainer</c> pushes to —
/// the pipeline's (DecoupledCd.md §14.1). Null otherwise, which is every read on a system without the
/// Pipeline block: the digest is then in the workstation repository, as it always was.
/// </param>
public readonly record struct ServiceImageRead(
    ServiceImageState State, string? Digest = null, string? Error = null, string? Repository = null)
{
    public static readonly ServiceImageRead NoService = new(ServiceImageState.NoService);
    public static readonly ServiceImageRead NotDigestPinned = new(ServiceImageState.NotDigestPinned);
    public static ServiceImageRead Pinned(string digest, string? repository = null) => new(ServiceImageState.DigestPinned, digest, Repository: repository);
    public static ServiceImageRead Unreadable(string error) => new(ServiceImageState.Unreadable, Error: error);
    public static ServiceImageRead Unrecognized(string error) => new(ServiceImageState.Unrecognized, Error: error);

    /// <summary>
    /// True when the registry's <c>:latest</c> is the right next thing to consult: there is
    /// no pinned digest to preserve. False for <see cref="ServiceImageState.DigestPinned"/>
    /// (the service's digest wins), for <see cref="ServiceImageState.Unreadable"/> (the
    /// registry must NOT be consulted — see the type summary) and for
    /// <see cref="ServiceImageState.Unrecognized"/> (nothing is decided about an image nobody here put there).
    /// </summary>
    public bool NeedsRegistry => State is ServiceImageState.NoService or ServiceImageState.NotDigestPinned;
}
