using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The decisions the deployer makes before and after it deploys (DecoupledCd.md §4.4, §4.7).
///
/// <para>These are the tests that stand between a build record and a running service, so they err
/// toward exhaustive: a Verify that wrongly passes deploys something that should not run, and a
/// rollout check that wrongly fails would stop every deploy. Both directions are pinned.</para>
/// </summary>
public class DeployVerificationTests
{
    private const string Digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private const string OldDigest = "sha256:b330844ac7c512956b92b2a0229ec14223119dbf0c075fbfef7df60894fa20eb";

    private static PipelineConfig Pipeline(params string[] classes) =>
        new() { Enabled = true, Classes = classes.ToList() };

    /// <summary>The record the real workflow wrote on 2026-09-12, as the baseline.</summary>
    private static BuildRecord Real(
        string lane = "published", string cls = "image",
        Dictionary<string, string>? packages = null, string? digest = Digest) =>
        new(1, cls,
            new BuildRecordBuiltFrom("Scutara/ScutaraService", "57d2d67e5413b1c40e02ccdae6838947a4ebd656", lane,
                packages ?? new Dictionary<string, string>
                {
                    ["LazyMagic.Shared"] = "3.0.26-alpha",
                    ["LazyMagic.Service.Shared"] = "3.0.26-alpha",
                }),
            new BuildRecordIdentity("image", Digest: digest),
            "2026-09-12T17:11:47Z", "tmay57", "34707373278");

    // ---------------------------------------------------------------------------------------
    //  Verify — the happy path must pass, or nothing ever deploys
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRealRecordTheWorkflowWrote_IsDeployable()
    {
        // The baseline. If this fails, the check is refusing the very artifact the pipeline built.
        var result = DeployVerification.Verify(Real(), Pipeline("image"));

        Assert.True(result.Deployable, string.Join("; ", result.Refusals.Select(r => r.Reason)));
    }

    [Fact]
    public void APlainPrerelease_IsNotRefused()
    {
        // 3.0.26-alpha is a deliberate channel and is in the chain today. Refusing every prerelease
        // would block every deploy while looking principled.
        var r = Real(packages: new() { ["LazyMagic.Shared"] = "3.0.26-alpha" });

        Assert.True(DeployVerification.Verify(r, Pipeline("image")).Deployable);
    }

    // ---------------------------------------------------------------------------------------
    //  Verify — each refusal
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AClassThisEnvironmentDoesNotAccept_IsRefused()
    {
        var result = DeployVerification.Verify(Real(), Pipeline("client", "site"));

        Assert.False(result.Deployable);
        Assert.Contains(result.Refusals, x => x.Check == "class");
    }

    [Fact]
    public void AnEmptyClassesList_RefusesEverything()
    {
        // An allowlist that names nothing accepts nothing — fail closed, never "no list means all".
        var result = DeployVerification.Verify(Real(), new PipelineConfig { Enabled = true });

        Assert.False(result.Deployable);
    }

    [Fact]
    public void TheLocalLane_IsRefused()
    {
        // A version is an identity only on the published lane; the local feed is rewritten in place.
        var result = DeployVerification.Verify(Real(lane: "local"), Pipeline("image"));

        Assert.False(result.Deployable);
        Assert.Contains(result.Refusals, x => x.Check == "builtFrom.lane");
    }

    [Fact]
    public void ACommitDiscriminator_IsRefused()
    {
        // Built off a branch outside publicReleaseRefSpec — unpublishable by construction.
        var r = Real(packages: new() { ["LazyMagic.Shared"] = "3.0.23-g6fc0b7081e" });
        var result = DeployVerification.Verify(r, Pipeline("image"));

        Assert.False(result.Deployable);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("LazyMagic.Shared", refusal.Reason);
    }

    [Theory]
    [InlineData("1.0.0-gamma")]      // "-g" followed by letters that are not a hash
    [InlineData("1.0.0-gabcdefg")]   // 'g' is not a hex digit
    [InlineData("1.0.0-g12345")]     // too short to be a commit id
    public void ThingsThatMerelyStartWithG_AreNotMistakenForADiscriminator(string version)
    {
        // The shared predicate needs seven hex digits; a word that happens to begin with g is not one.
        var r = Real(packages: new() { ["LazyMagic.Shared"] = version });

        Assert.True(DeployVerification.Verify(r, Pipeline("image")).Deployable);
    }

    [Fact]
    public void ATagInsteadOfADigest_IsRefused()
    {
        var result = DeployVerification.Verify(Real(digest: "latest"), Pipeline("image"));

        Assert.Contains(result.Refusals, x => x.Check == "identity.digest");
    }

    [Fact]
    public void EveryRefusalIsReported_NotJustTheFirst()
    {
        // A Verify that stopped at the first problem makes an operator fix one thing, redeploy, and
        // discover the next — once per round trip. The evidence should show everything wrong at once.
        var r = Real(lane: "local", digest: "latest",
            packages: new() { ["LazyMagic.Shared"] = "3.0.23-g6fc0b7081e" });

        var result = DeployVerification.Verify(r, Pipeline("client")); // wrong class too

        Assert.Equal(
            new[] { "class", "builtFrom.lane", "builtFrom.packages", "identity.digest" },
            result.Refusals.Select(x => x.Check));
    }

    // ---------------------------------------------------------------------------------------
    //  Scan
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnUnfinishedScan_IsAWait_NeverAPass()
    {
        // Treating an unfinished scan as clean is how an image with a critical finding deploys because
        // it was fast.
        Assert.Equal(ScanVerdict.NotYetAvailable,
            DeployVerification.Scan(new Dictionary<string, int>(), new[] { "CRITICAL" }, scanComplete: false));
    }

    [Fact]
    public void AnUnfinishedScanWithFindings_IsStillAWait()
    {
        // Partial results are not a verdict either way.
        Assert.Equal(ScanVerdict.NotYetAvailable,
            DeployVerification.Scan(new Dictionary<string, int> { ["CRITICAL"] = 3 }, new[] { "CRITICAL" }, false));
    }

    [Fact]
    public void ABlockingFinding_Blocks()
    {
        Assert.Equal(ScanVerdict.Block,
            DeployVerification.Scan(new Dictionary<string, int> { ["CRITICAL"] = 1 }, new[] { "CRITICAL" }, true));
    }

    [Fact]
    public void AnAbsentSeverityKey_MeansNone_NotUnknown()
    {
        // ECR omits a severity whose count is zero. A missing key must read as "none found".
        Assert.Equal(ScanVerdict.Pass,
            DeployVerification.Scan(new Dictionary<string, int> { ["LOW"] = 9 }, new[] { "CRITICAL" }, true));
    }

    [Fact]
    public void FindingsBelowTheBlockingSeverity_Pass()
    {
        Assert.Equal(ScanVerdict.Pass,
            DeployVerification.Scan(
                new Dictionary<string, int> { ["HIGH"] = 4, ["MEDIUM"] = 12 }, new[] { "CRITICAL" }, true));
    }

    [Fact]
    public void NullCountsFromTheSdk_AreNone()
    {
        // SDK v4 returns null for a collection with no members — the greenfield-account lesson.
        Assert.Equal(ScanVerdict.Pass, DeployVerification.Scan(null, new[] { "CRITICAL" }, true));
    }

    // ---------------------------------------------------------------------------------------
    //  Rollout — including the bug caught before this reached a Lambda
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void THE_EARLY_ROLL_REGRESSION_AllTasksOldWhileInProgress_IsStillRolling()
    {
        // THE BUG. The first version returned NotDeployed here. But ECS starts new tasks before
        // stopping old ones, so the opening moments of a HEALTHY roll look exactly like this: every
        // running task still on the old digest. It would have failed every deploy at the first poll.
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(new[] { OldDigest, OldDigest }, Digest, "IN_PROGRESS"));
    }

    [Fact]
    public void AllNewAndCompleted_HasLanded()
    {
        Assert.Equal(RolloutVerdict.Landed,
            DeployVerification.Rollout(new[] { Digest, Digest }, Digest, "COMPLETED"));
    }

    [Fact]
    public void AllNewButStillInProgress_IsStillRolling()
    {
        // ECS has not finished its own health checks. The digests agreeing is not enough alone.
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(new[] { Digest }, Digest, "IN_PROGRESS"));
    }

    [Fact]
    public void CompletedWithAStrayOldTask_IsStillRolling()
    {
        // Still draining. COMPLETED alone is not enough either.
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(new[] { Digest, OldDigest }, Digest, "COMPLETED"));
    }

    [Fact]
    public void CompletedWithNoTaskOnTheNewDigest_IsNotDeployed()
    {
        // ECS finished and nothing runs the new image: the service is healthy and running the wrong
        // thing — the case an exit code would have called success.
        Assert.Equal(RolloutVerdict.NotDeployed,
            DeployVerification.Rollout(new[] { OldDigest }, Digest, "COMPLETED"));
    }

    [Fact]
    public void AFailedRollout_IsNotDeployed_RegardlessOfDigests()
    {
        // The circuit breaker rolled back. Even if a new task briefly appears, this did not land.
        Assert.Equal(RolloutVerdict.NotDeployed,
            DeployVerification.Rollout(new[] { Digest }, Digest, "FAILED"));
    }

    [Fact]
    public void NothingRunningYet_IsStillRolling()
    {
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(Array.Empty<string>(), Digest, "IN_PROGRESS"));
    }

    [Fact]
    public void NullTasksFromTheSdk_AreStillRolling_NotACrash()
    {
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(null, Digest, "IN_PROGRESS"));
    }

    [Fact]
    public void AnUnknownRolloutState_NeverClaimsLanded()
    {
        // A state this code does not recognise must not be read as success. It waits, and the caller's
        // timeout decides.
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(new[] { Digest }, Digest, rolloutState: null));
        Assert.Equal(RolloutVerdict.StillRolling,
            DeployVerification.Rollout(new[] { Digest }, Digest, "SOMETHING_NEW"));
    }

    [Fact]
    public void AnEmptyDeployedDigest_IsACallerError()
    {
        Assert.Throws<ArgumentException>(() =>
            DeployVerification.Rollout(new[] { Digest }, "", "COMPLETED"));
    }
}
