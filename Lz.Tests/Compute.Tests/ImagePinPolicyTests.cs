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

    // ---- ClassifyServiceImage: the three honest answers a successful read can give ----

    [Fact]
    public void Classify_NoActiveService_IsNoService_WhateverTheImageSays()
        => Assert.Equal(ServiceImageRead.NoService, ImagePinPolicy.ClassifyServiceImage(false, $"{Repo}@{Digest}"));

    [Theory]
    [InlineData("latest")]
    [InlineData("b-20260905-140324-g2ff63d73")]
    public void Classify_TagFormRevision_IsNotDigestPinned(string tag)
    {
        // A pre-pinning revision (Scutara's revision 4). The registry is the right next
        // stop — there is no pinned digest to preserve.
        var read = ImagePinPolicy.ClassifyServiceImage(true, $"{Repo}:{tag}");

        Assert.Equal(ServiceImageRead.NotDigestPinned, read);
        Assert.True(read.NeedsRegistry);
    }

    [Fact]
    public void Classify_ServiceWithoutOurContainer_IsNotDigestPinned()
        => Assert.Equal(ServiceImageRead.NotDigestPinned, ImagePinPolicy.ClassifyServiceImage(true, null));

    [Fact]
    public void Classify_PinnedRevision_CarriesTheDigestAfterTheAtSign()
    {
        var read = ImagePinPolicy.ClassifyServiceImage(true, $"{Repo}@{Digest}");

        Assert.Equal(ServiceImageState.DigestPinned, read.State);
        Assert.Equal(Digest, read.Digest);
        Assert.False(read.NeedsRegistry);
    }

    // ---- ChooseDigest: the precedence that makes a rollback survive deploytenant ----

    [Fact]
    public void ChooseDigest_TheServiceWins_OverTheRegistry()
    {
        // Observed live 2026-09-05: with the registry winning, a deploytenant re-pointed the
        // service from an imperative revision 7 back to Pulumi's revision 5. Had the service
        // been rolled back, that deploy would have silently rolled it forward. The service's
        // digest must win so Pulumi's revision always matches what is running.
        const string rolledBackTo = "sha256:f65abb74";
        const string latest = "sha256:ba9773aa";

        Assert.Equal(rolledBackTo, ImagePinPolicy.ChooseDigest(ServiceImageRead.Pinned(rolledBackTo), latest));
    }

    [Fact]
    public void ChooseDigest_FallsBackToTheRegistry_OnlyWhenThereIsNoPinnedService()
    {
        // The first deploy (no service), and the first PINNED deploy over a tag-form
        // revision: :latest is the only answer in both.
        Assert.Equal("sha256:ba9773aa", ImagePinPolicy.ChooseDigest(ServiceImageRead.NoService, "sha256:ba9773aa"));
        Assert.Equal("sha256:ba9773aa", ImagePinPolicy.ChooseDigest(ServiceImageRead.NotDigestPinned, "sha256:ba9773aa"));
    }

    [Fact]
    public void ChooseDigest_NullWhenNeitherExists_SoTheTagFallbackEngages()
        => Assert.Null(ImagePinPolicy.ChooseDigest(ServiceImageRead.NoService, null));

    [Fact]
    public void ChooseDigest_UnreadableIsNotAbsent_ItRefuses()
    {
        // THE FAIL-CLOSED GUARANTEE, and the test the adversarial review asked for. Every
        // genuine "absent" shape arrives WITHOUT an exception (a missing cluster or service
        // is reported under Failures; a tag is a string with no '@'), so an Unreadable can
        // only ever be a real error — throttling, AccessDenied, an expired SSO session. If
        // that were allowed to fall through to the registry, a deploytenant run while the
        // read was failing would rebuild the task definition from :latest and undo a
        // rollback, while logging "no pinned service yet". Refusing is the only safe answer.
        var unreadable = ServiceImageRead.Unreadable("AmazonECSException: expired token");

        Assert.False(unreadable.NeedsRegistry);
        var ex = Assert.Throws<InvalidOperationException>(
            () => ImagePinPolicy.ChooseDigest(unreadable, "sha256:ba9773aa"));
        Assert.Contains("expired token", ex.Message);
        Assert.Contains("Refusing", ex.Message);
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
