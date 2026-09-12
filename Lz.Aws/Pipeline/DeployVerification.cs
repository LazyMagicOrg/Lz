using Lz.Core.Config;
using Lz.Core.PackageLane;

namespace Lz.Aws.Pipeline;

/// <summary>One reason a build record may not be deployed. Every refusal is independent.</summary>
public sealed record VerifyRefusal(string Check, string Reason);

/// <summary>The outcome of checking a build record before anything is deployed.</summary>
public sealed record VerifyResult(IReadOnlyList<VerifyRefusal> Refusals)
{
    /// <summary>FAIL-CLOSED: deployable only when there is not a single refusal.</summary>
    public bool Deployable => Refusals.Count == 0;
}

/// <summary>What an image scan says about whether a deploy may proceed.</summary>
public enum ScanVerdict
{
    /// <summary>Scanned, and nothing at a blocking severity.</summary>
    Pass,

    /// <summary>Scanned, and at least one finding at a blocking severity.</summary>
    Block,

    /// <summary>
    /// The scan has not finished. This is a BOUNDED WAIT, never a pass (§4.4 step 6) — treating an
    /// unfinished scan as clean is how an image with a critical finding deploys because it was fast.
    /// </summary>
    NotYetAvailable,
}

/// <summary>What the running service says about whether a rollout landed.</summary>
public enum RolloutVerdict
{
    /// <summary>Every running task runs the deployed digest.</summary>
    Landed,

    /// <summary>
    /// Some tasks run the new digest and some the old, or none are running yet. Normal mid-roll; the
    /// caller waits, and its own timeout turns a roll that never converges into a failure.
    /// </summary>
    StillRolling,

    /// <summary>
    /// Tasks are running and NONE of them run the deployed digest. Not a slow roll — the deploy did
    /// not take, and waiting longer will not change that.
    /// </summary>
    NotDeployed,
}

/// <summary>
/// The decisions the deployer's three Lambdas make, as pure functions (DecoupledCd.md §4.4, §4.7).
///
/// <para>WHY THESE ARE HERE AND NOT IN THE LAMBDAS. On 2026-09-12 six defects landed in code that
/// called AWS directly, and none in the pure planners beside it — the planner had roughly fifty tests
/// and no failures; the appliers had three failed runs between them. So the logic that decides
/// whether something may DEPLOY lives where it can be tested exhaustively, and the Lambda handlers
/// are left as thin shells that fetch inputs and act on a verdict. A handler bug fetches the wrong
/// thing; a decision bug deploys the wrong thing, and only one of those is recoverable.</para>
/// </summary>
public static class DeployVerification
{
    /// <summary>
    /// Check a build record against this environment's pipeline config (§4.4 steps 1 and 1a).
    ///
    /// <para>EVERY CHECK RUNS, and all refusals are returned — not just the first. A Verify that
    /// stopped at the first problem would make an operator fix one thing, redeploy, and discover the
    /// next, once per round trip; the evidence record should show everything that was wrong.</para>
    ///
    /// <para>The record has already been through <see cref="BuildRecordFormat.Parse"/>, which is
    /// where shape is enforced — a missing builtFrom never reaches here. This is about whether a
    /// WELL-FORMED record is one this environment will deploy.</para>
    /// </summary>
    public static VerifyResult Verify(BuildRecord record, PipelineConfig pipeline)
    {
        var refusals = new List<VerifyRefusal>();

        // STEP 1 — the class is one this environment accepts. Classes is an allowlist, so a class
        // that is absent is refused rather than ignored.
        var accepted = pipeline.Classes ?? new List<string>();
        if (!accepted.Contains(record.Class))
        {
            refusals.Add(new("class",
                $"class '{record.Class}' is not in Pipeline.Classes " +
                $"[{string.Join(", ", accepted)}]. That list is an allowlist: this environment does " +
                "not deploy this class, so the record is refused rather than ignored."));
        }

        // STEP 1a — the lane. A version is an identity only on the published lane, where the
        // registry refuses a duplicate; the local feed is overwritten by every build.
        if (!string.Equals(record.BuiltFrom.Lane, "published", StringComparison.Ordinal))
        {
            refusals.Add(new("builtFrom.lane",
                $"lane is '{record.BuiltFrom.Lane}', not 'published'. A package version names one set " +
                "of bytes only on the published lane; the local feed is rewritten in place, so the " +
                "same version can mean different code."));
        }

        // STEP 1a — no -g<hex> discriminator. SHARED PREDICATE, not a reimplementation: the publish
        // guards and the lane sync already ask NBGV's one question, and a second copy is how two
        // answers to it would drift apart.
        //
        // A PLAIN PRERELEASE PASSES. 3.0.26-alpha is a deliberate channel and is in the chain today;
        // refusing every prerelease would block every deploy while looking principled.
        foreach (var (id, version) in record.BuiltFrom.Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (PackageLaneSync.CarriesACommitId(version))
            {
                refusals.Add(new("builtFrom.packages",
                    $"{id} {version} carries an NBGV commit discriminator, so it was built off a branch " +
                    "outside publicReleaseRefSpec. It is unpublishable by construction, and an image " +
                    "containing it cannot be traced to a released package."));
            }
        }

        // The identity must be a digest. Parse enforces the prefix already; this is the belt to that
        // brace, because it is the one field that decides WHAT runs.
        if (record.Class is "image" or "tooling"
            && record.Identity.Digest?.StartsWith("sha256:", StringComparison.Ordinal) != true)
        {
            refusals.Add(new("identity.digest",
                $"'{record.Identity.Digest}' is not a sha256 digest. A tag is a mutable pointer and " +
                "cannot be deployed: the point of the design is that what was verified is what runs."));
        }

        return new VerifyResult(refusals);
    }

    /// <summary>
    /// Apply the severity policy to a scan (§4.4 step 6).
    /// </summary>
    /// <param name="findingCounts">
    /// ECR's per-severity counts. A severity is ABSENT, not zero, when nothing was found at it — so a
    /// missing key means "none", and must not be read as "unknown".
    /// </param>
    /// <param name="scanComplete">
    /// False while the scan is still running. An unfinished scan is a wait, never a pass.
    /// </param>
    public static ScanVerdict Scan(
        IReadOnlyDictionary<string, int>? findingCounts, IReadOnlyList<string>? blockOn, bool scanComplete)
    {
        if (!scanComplete)
            return ScanVerdict.NotYetAvailable;

        var counts = findingCounts ?? new Dictionary<string, int>();
        foreach (var severity in blockOn ?? Array.Empty<string>())
        {
            if (counts.TryGetValue(severity, out var n) && n > 0)
                return ScanVerdict.Block;
        }

        return ScanVerdict.Pass;
    }

    /// <summary>
    /// Did the rollout land (§4.7)? Compares what is RUNNING against what was deployed.
    ///
    /// <para>"Exit codes are not evidence; these reads are." The digest a running task reports is
    /// what was actually pulled, which a successful <c>UpdateService</c> call does not tell you.</para>
    /// </summary>
    /// <param name="runningDigests">
    /// <c>containers[].imageDigest</c> from each running task — nullable, because the AWS SDK v4
    /// returns null rather than an empty list for a collection with no members, and a service that
    /// has scaled to zero is exactly that.
    /// </param>
    /// <param name="rolloutState">
    /// The PRIMARY deployment's <c>rolloutState</c> from <c>DescribeServices</c>:
    /// <c>IN_PROGRESS</c>, <c>COMPLETED</c> or <c>FAILED</c>.
    ///
    /// <para>THIS PARAMETER EXISTS BECAUSE THE FIRST VERSION OF THIS FUNCTION WAS WRONG, caught
    /// before it reached a Lambda. It declared <see cref="RolloutVerdict.NotDeployed"/> whenever
    /// tasks were running and none carried the new digest — but ECS starts new tasks BEFORE stopping
    /// old ones (the service runs with DeploymentMaximumPercent=200), so the opening moments of a
    /// perfectly healthy roll look exactly like that: every running task still on the old digest.
    /// It would have failed every deploy it was asked to verify, at the first poll.</para>
    ///
    /// <para>A single snapshot of digests cannot tell "has not reached the new task yet" from "did
    /// not take". The deployment's own rollout state can, so the verdict needs both.</para>
    /// </param>
    public static RolloutVerdict Rollout(
        IReadOnlyList<string>? runningDigests, string deployedDigest, string? rolloutState)
    {
        if (string.IsNullOrWhiteSpace(deployedDigest))
            throw new ArgumentException("a rollout cannot be verified against an empty digest.", nameof(deployedDigest));

        // ECS SAYS IT FAILED — the circuit breaker rolled back. That is not a slow roll.
        if (string.Equals(rolloutState, "FAILED", StringComparison.Ordinal))
            return RolloutVerdict.NotDeployed;

        var running = runningDigests ?? Array.Empty<string>();
        var matching = running.Count(d => string.Equals(d, deployedDigest, StringComparison.Ordinal));

        // Landed means BOTH signals agree: ECS considers the roll complete AND every running task is
        // on the new digest. Either alone is not enough — COMPLETED with a stray old task means it is
        // still draining; all-new while IN_PROGRESS means ECS has not finished its own health checks.
        if (string.Equals(rolloutState, "COMPLETED", StringComparison.Ordinal))
        {
            if (running.Count > 0 && matching == running.Count) return RolloutVerdict.Landed;

            // ECS finished and NO task runs the new digest: the deploy did not take. The service is
            // healthy and running the wrong thing — the case an exit code would have called success.
            if (running.Count > 0 && matching == 0) return RolloutVerdict.NotDeployed;
        }

        // Everything else — IN_PROGRESS, an unknown or missing state, nothing running yet, or a mix of
        // old and new — is a roll still under way. The CALLER's timeout is what turns a roll that never
        // converges into a failure; this function deliberately never guesses that from a snapshot.
        return RolloutVerdict.StillRolling;
    }
}
