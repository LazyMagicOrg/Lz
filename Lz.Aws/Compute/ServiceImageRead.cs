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
public readonly record struct ServiceImageRead(ServiceImageState State, string? Digest = null, string? Error = null)
{
    public static readonly ServiceImageRead NoService = new(ServiceImageState.NoService);
    public static readonly ServiceImageRead NotDigestPinned = new(ServiceImageState.NotDigestPinned);
    public static ServiceImageRead Pinned(string digest) => new(ServiceImageState.DigestPinned, digest);
    public static ServiceImageRead Unreadable(string error) => new(ServiceImageState.Unreadable, Error: error);

    /// <summary>
    /// True when the registry's <c>:latest</c> is the right next thing to consult: there is
    /// no pinned digest to preserve. False for <see cref="ServiceImageState.DigestPinned"/>
    /// (the service's digest wins) and for <see cref="ServiceImageState.Unreadable"/> (the
    /// registry must NOT be consulted — see the type summary).
    /// </summary>
    public bool NeedsRegistry => State is ServiceImageState.NoService or ServiceImageState.NotDigestPinned;
}
