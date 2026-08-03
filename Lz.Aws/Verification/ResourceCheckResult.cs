namespace Lz.Aws.Verification;

/// <summary>
/// Lifecycle category of an expected AWS resource.
/// </summary>
public enum ResourceCategory
{
    /// <summary>
    /// Pulumi-managed: exists while the system is deployed, must be gone
    /// after <c>lz destroytenant</c>/<c>lz destroysystem</c>.
    /// </summary>
    Stack,

    /// <summary>
    /// Imperative persistent layer (ECR, DynamoDB tables, webapp buckets,
    /// SSM, Pulumi state backend, hosted zone): created outside Pulumi
    /// state, survives every destroy by design.
    /// </summary>
    Persistent,

    /// <summary>
    /// Runtime smoke probe of the DEPLOYED system's public surfaces (the
    /// E2ETestPlan P0.7 post-deploy gate): the /config edge bootstrap serving
    /// every declared pool, an API round-trip through CloudFront, and the
    /// function-URL origin-verify lockout. Present only while deployed AND healthy;
    /// counted into the <c>--expect deployed</c> verdict alongside Stack.
    /// Unreachable surfaces report Absent (the expected post-destroy state),
    /// never Error.
    /// </summary>
    Smoke,
}

/// <summary>
/// Observed live-AWS state of one expected resource.
/// </summary>
public enum ResourceState
{
    Present,
    Absent,

    /// <summary>
    /// Secrets Manager only: the secret exists but carries a deletion
    /// tombstone (DeletedDate set). Blocks a redeploy that recreates the
    /// same name — the exact failure mode RecoveryWindowInDays=0 avoids.
    /// </summary>
    ScheduledForDeletion,

    /// <summary>The check itself failed (auth, throttle, network).</summary>
    Error,
}

/// <summary>
/// One expected resource and what live AWS says about it.
/// </summary>
public sealed record ResourceCheckResult(
    ResourceCategory Category,
    string Service,      // e.g. "cloudfront", "dynamodb", "cognito-idp"
    string Kind,         // e.g. "distribution", "table", "user-pool"
    string Name,         // the deterministic physical name checked
    ResourceState State,
    string? Detail = null);
