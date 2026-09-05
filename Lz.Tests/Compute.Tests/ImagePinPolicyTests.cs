using Lz.Aws.Compute;
using Lz.Aws.Ops;
using Lz.Core.Config;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// The pure decisions behind digest pinning: what image string goes in the task definition,
/// whether revisions are retained, and how the container updater must deploy a change. No
/// AWS and no Pulumi — the components and the updater only translate these.
/// </summary>
public class ImagePinPolicyTests
{
    private const string Repo = "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-dev-mp-aiphost";
    private const string Digest = "sha256:ba9773aa21a19e0ffd12d5f2cc2335fecb168c041cfd02a902655bb3b68bcadf";

    // ---- ForTenantService: the absent-section guarantee ----

    [Fact]
    public void NullConfig_IsNone_SoAnUnoptedSystemsPlanIsByteIdentical()
    {
        var decision = ImagePinPolicy.ForTenantService(null);

        Assert.Equal(ImagePinDecision.None, decision);
        Assert.False(decision.Any);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EachFlagIsCarriedThroughIndependently(bool pin, bool retain)
    {
        var decision = ImagePinPolicy.ForTenantService(
            new RollbackConfig { PinImageDigest = pin, RetainTaskDefinitionRevisions = retain });

        Assert.Equal(pin, decision.PinDigest);
        Assert.Equal(retain, decision.RetainRevisions);
    }

    // ---- ImageRef: the fallback is the first-deploy path, not an error path ----

    [Fact]
    public void PinnedWithADigest_NamesTheDigest()
    {
        var decision = new ImagePinDecision(PinDigest: true, RetainRevisions: false);

        Assert.Equal($"{Repo}@{Digest}", ImagePinPolicy.ImageRef(Repo, "latest", Digest, decision));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PinnedButNoDigestResolved_FallsBackToTheTag(string? digest)
    {
        // THE BOOTSTRAP CASE, and the reason this is a function rather than an if. On a new
        // system `lz previewtenant` and `deploysystem` both run before any image has been
        // pushed, so no digest can be resolved. Falling back keeps a first deploy working;
        // failing here would make it impossible to stand up a new environment.
        var decision = new ImagePinDecision(PinDigest: true, RetainRevisions: false);

        Assert.Equal($"{Repo}:latest", ImagePinPolicy.ImageRef(Repo, "latest", digest, decision));
    }

    [Fact]
    public void NotPinned_NamesTheTag_EvenWhenADigestIsKnown()
    {
        // The un-opted-in system takes the SAME branch as the empty-repository case, which
        // is why one code path serves both.
        Assert.Equal(
            $"{Repo}:latest",
            ImagePinPolicy.ImageRef(Repo, "latest", Digest, ImagePinDecision.None));
    }

    // ---- IsDigestPinned ----

    [Theory]
    [InlineData("repo@sha256:abc", true)]
    [InlineData("repo:latest", false)]
    [InlineData("repo:b-20260905-140324-g2ff63d73", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDigestPinned_KeysOnTheAtSign(string? image, bool expected)
        => Assert.Equal(expected, ImagePinPolicy.IsDigestPinned(image));

    // ---- DecideStrategy: the branch that silently breaks updatecontainer if wrong ----

    [Fact]
    public void PinnedDefinitionWithAChangingImage_MustRegisterARevision()
    {
        // A forced redeploy of a pinned definition re-pulls the SAME immutable digest. The
        // rollout completes, the wait succeeds, and the command reports "verified" having
        // deployed nothing — then never converges. This is the case that makes the register
        // path mandatory rather than an optimisation.
        Assert.Equal(
            AwsContainerUpdater.ContainerUpdateStrategy.RegisterNewRevision,
            AwsContainerUpdater.DecideStrategy($"{Repo}@{Digest}", imageChanging: true));
    }

    [Fact]
    public void TagPinnedDefinition_StillUsesTheCheaperForcedRedeploy()
        => Assert.Equal(
            AwsContainerUpdater.ContainerUpdateStrategy.ForceRedeploy,
            AwsContainerUpdater.DecideStrategy($"{Repo}:latest", imageChanging: true));

    [Fact]
    public void PinnedDefinitionWithNothingChanging_MustNotRegisterAPointlessRevision()
    {
        // `--force` on an up-to-date service. A forced redeploy is the only thing that does
        // anything here; turning it into a new revision would churn the definition for no
        // reason. The second half of the rule matters as much as the first.
        Assert.Equal(
            AwsContainerUpdater.ContainerUpdateStrategy.ForceRedeploy,
            AwsContainerUpdater.DecideStrategy($"{Repo}@{Digest}", imageChanging: false));
    }

    [Fact]
    public void AnUnreadableDefinition_FallsBackToTheHistoricBehaviour()
    {
        // GetServiceTaskDefinitionImageAsync returns null when the service or definition
        // cannot be read. Forcing is what this command did before pinning existed, so an
        // unknown answer must not silently switch strategies.
        Assert.Equal(
            AwsContainerUpdater.ContainerUpdateStrategy.ForceRedeploy,
            AwsContainerUpdater.DecideStrategy(null, imageChanging: true));
    }
}
