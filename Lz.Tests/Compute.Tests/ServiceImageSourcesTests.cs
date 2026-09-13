using Lz.Aws.Compute;
using Lz.Aws.Ops;
using Lz.Core.Config;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Where a tenant service's image may come from, and what the deploy path does about it (DecoupledCd.md
/// §14.1 item 3). The workstation deploy path found the service's container by "whose image mentions the
/// environment's repository"; after a pipeline deploy no image does, so the next <c>lz deploytenant</c>
/// would have put the workstation image back and <c>lz updatecontainer</c> reported "no running tasks".
///
/// <para><b>HALF OF THESE ARE THE ABSENT PATH, AND THAT HALF IS THE CONTRACT.</b> This is the first code in
/// a deploy to consult the Pipeline block, and every system without the block must get exactly the
/// historic answers. Those answers are written out below as data — what the old read returned for each
/// shape of task definition — rather than computed by calling the old code, so a change to the new code
/// cannot quietly move the expectation with it.</para>
/// </summary>
public class ServiceImageSourcesTests
{
    private const string Host = "503947800380.dkr.ecr.us-west-2.amazonaws.com";
    private const string Workstation = "scu-4df6-b9c6-dev-mp-aiphost";
    private const string PipelineRepo = "scu-4df6-b9c6-aiphost";
    private const string WorkstationDigest = "sha256:ba9773aa21a19e0ffd12d5f2cc2335fecb168c041cfd02a902655bb3b68bcadf";
    private const string PipelineDigest = "sha256:1303a9132cd0117b53ff68f390cce55a212aeb9e67a5105c9c80c6d85d5d0d26";

    private static readonly ServiceImageSources NoPipeline = new("aiphost", Workstation, null);
    private static readonly ServiceImageSources WithPipeline = new("aiphost", Workstation, new PipelineImageSource(Host, PipelineRepo));

    private static DefinedContainer C(string? name, string? image) => new(name, image);

    // ---------------------------------------------------------------------------------------
    //  Without the block: the historic read, as data
    // ---------------------------------------------------------------------------------------

    public static IEnumerable<object?[]> HistoricReads()
    {
        // (why, service active, containers, expected state, expected digest)
        yield return new object?[] { "no active service, whatever the image", false,
            new[] { C("aiphost", $"{Host}/{Workstation}@{WorkstationDigest}") }, ServiceImageState.NoService, null };
        yield return new object?[] { "a tag-form revision", true,
            new[] { C("aiphost", $"{Host}/{Workstation}:latest") }, ServiceImageState.NotDigestPinned, null };
        yield return new object?[] { "a pinned revision", true,
            new[] { C("aiphost", $"{Host}/{Workstation}@{WorkstationDigest}") }, ServiceImageState.DigestPinned, WorkstationDigest };
        yield return new object?[] { "A PIPELINE IMAGE IS INVISIBLE WITHOUT THE BLOCK — the defect, kept on purpose here", true,
            new[] { C("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}") }, ServiceImageState.NotDigestPinned, null };
        yield return new object?[] { "the container's NAME never mattered: selection is by repository", true,
            new[] { C("otel", "public.ecr.aws/aws-observability/aws-otel-collector:latest"),
                    C("app", $"{Host}/{Workstation}@{WorkstationDigest}") }, ServiceImageState.DigestPinned, WorkstationDigest };
        yield return new object?[] { "a container named after the service but pulling elsewhere is not the service's", true,
            new[] { C("aiphost", "docker.io/library/nginx:1"),
                    C("worker", $"{Host}/{Workstation}@{WorkstationDigest}") }, ServiceImageState.DigestPinned, WorkstationDigest };
        yield return new object?[] { "no containers", true,
            Array.Empty<DefinedContainer>(), ServiceImageState.NotDigestPinned, null };
        yield return new object?[] { "no container list from the SDK", true,
            null, ServiceImageState.NotDigestPinned, null };
        yield return new object?[] { "whatever follows the last @ was taken verbatim, even malformed", true,
            new[] { C("aiphost", $"{Host}/{Workstation}@sha256:abc") }, ServiceImageState.DigestPinned, "sha256:abc" };
    }

    [Theory]
    [MemberData(nameof(HistoricReads))]
    public void WithoutThePipelineBlock_TheReadIsTheHistoricOne(
        string why, bool active, DefinedContainer[]? containers, ServiceImageState state, string? digest)
    {
        var read = ImagePinPolicy.ClassifyServiceContainers(active, containers, NoPipeline);

        Assert.True(state == read.State, $"{why}: expected {state}, got {read.State}");
        Assert.Equal(digest, read.Digest);
        Assert.Null(read.Repository); // never set without the block, so nothing downstream moves
    }

    [Fact]
    public void WithoutThePipelineBlock_RunningTasksAreMatchedByRepository_AsBefore()
    {
        Assert.True(ImagePinPolicy.IsTheServicesContainer("app", $"{Host}/{Workstation}@{WorkstationDigest}", NoPipeline));
        Assert.False(ImagePinPolicy.IsTheServicesContainer("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}", NoPipeline));
        Assert.False(ImagePinPolicy.IsTheServicesContainer("aiphost", null, NoPipeline));
    }

    [Fact]
    public void WithoutThePipelineBlock_TheImageRepositoryIsTheServicesOwn()
    {
        // ResolvedImageRepositories is empty without the block, so the component builds exactly the
        // reference it always built.
        var pinned = new ImagePinDecision(PinDigest: true, RetainRevisions: true);
        Assert.Equal(Workstation, ImagePinPolicy.ImageRepository(Workstation, null, WorkstationDigest, pinned));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256:not-even-checked")]
    public void WithoutThePipelineBlock_ADigestIsTakenAsGiven_FromTheWorkstationRepository(string? digest)
    {
        // No presence read, no format check: updatecontainer --digest as it always was.
        var (repository, refusal) = ImagePinPolicy.RepositoryForDigest(
            digest ?? "", NoPipeline, DigestPresence.Unreadable, DigestPresence.Unreadable);

        Assert.Equal(Workstation, repository);
        Assert.Null(refusal);
    }

    [Fact]
    public void WithoutThePipelineBlock_ANewRevisionRepinsTheCurrentRepository()
    {
        Assert.Equal($"{Host}/{Workstation}@{WorkstationDigest}",
            AwsContainerUpdater.ImageToRegister($"{Host}/{Workstation}:latest", NoPipeline, Workstation, WorkstationDigest));
    }

    [Fact]
    public void WithoutThePipelineBlock_NoRepositoryChangeMeansTheHistoricStrategy()
    {
        Assert.Equal(AwsContainerUpdater.ContainerUpdateStrategy.ForceRedeploy,
            AwsContainerUpdater.DecideStrategy($"{Host}/{Workstation}:latest", imageChanging: true));
    }

    // ---------------------------------------------------------------------------------------
    //  With the block: by name, and a pipeline image is kept
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void APipelineImageTheServiceRuns_IsKept_WithItsRepository()
    {
        // THE CASE. Without this, deploytenant fell back to the workstation's :latest and re-pointed
        // the service at it.
        var read = ImagePinPolicy.ClassifyServiceContainers(true,
            new[] { C("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}") }, WithPipeline);

        Assert.Equal(ServiceImageState.DigestPinned, read.State);
        Assert.Equal(PipelineDigest, read.Digest);
        Assert.Equal(PipelineRepo, read.Repository);
        Assert.False(read.NeedsRegistry);
    }

    [Fact]
    public void AWorkstationImage_IsStillPinnedAsBefore_WithNoRepositoryOverride()
    {
        var read = ImagePinPolicy.ClassifyServiceContainers(true,
            new[] { C("aiphost", $"{Host}/{Workstation}@{WorkstationDigest}") }, WithPipeline);

        Assert.Equal(ServiceImageRead.Pinned(WorkstationDigest), read);
    }

    [Fact]
    public void ATagFormRevision_StillGoesToTheRegistry()
    {
        var read = ImagePinPolicy.ClassifyServiceContainers(true,
            new[] { C("aiphost", $"{Host}/{Workstation}:latest") }, WithPipeline);

        Assert.Equal(ServiceImageRead.NotDigestPinned, read);
        Assert.True(read.NeedsRegistry);
    }

    [Fact]
    public void NoActiveService_IsStillNoService()
        => Assert.Equal(ServiceImageRead.NoService, ImagePinPolicy.ClassifyServiceContainers(false, null, WithPipeline));

    [Fact]
    public void SidecarsAreIgnored_TheServicesContainerIsTheOneNamedAfterIt()
    {
        var read = ImagePinPolicy.ClassifyServiceContainers(true, new[]
        {
            C("otel", "public.ecr.aws/aws-observability/aws-otel-collector:latest"),
            C("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}"),
        }, WithPipeline);

        Assert.Equal(ServiceImageRead.Pinned(PipelineDigest, PipelineRepo), read);
    }

    public static IEnumerable<object[]> Unrecognized()
    {
        yield return new object[] { "the pipeline repository in ANOTHER account's registry",
            new[] { C("aiphost", $"982408502448.dkr.ecr.us-west-2.amazonaws.com/{PipelineRepo}@{PipelineDigest}") } };
        yield return new object[] { "a repository neither path deploys from",
            new[] { C("aiphost", $"{Host}/scu-4df6-b9c6-something-else@{PipelineDigest}") } };
        yield return new object[] { "no container named after the service",
            new[] { C("app", $"{Host}/{Workstation}@{WorkstationDigest}") } };
        yield return new object[] { "two containers named after the service",
            new[] { C("aiphost", $"{Host}/{Workstation}@{WorkstationDigest}"), C("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}") } };
        yield return new object[] { "a digest that is not sha256 and 64 hex",
            new[] { C("aiphost", $"{Host}/{Workstation}@sha256:abc") } };
    }

    [Theory]
    [MemberData(nameof(Unrecognized))]
    public void AnythingElse_IsUnrecognized_NeitherReplacedNorKept(string why, DefinedContainer[] containers)
    {
        var read = ImagePinPolicy.ClassifyServiceContainers(true, containers, WithPipeline);

        Assert.True(read.State == ServiceImageState.Unrecognized, $"{why}: got {read.State}");
        Assert.False(read.NeedsRegistry);
        var ex = Assert.Throws<InvalidOperationException>(() => ImagePinPolicy.ChooseDigest(read, WorkstationDigest));
        Assert.Contains(read.Error!, ex.Message);
    }

    [Fact]
    public void RunningTasksAreMatchedByName_SoAPipelineImageIsSeenRunning()
    {
        // Without this, updatecontainer on a pipeline-deployed service reported "no running tasks —
        // run 'lz deploytenant'", pointing the operator at the command that would undo the deploy.
        Assert.True(ImagePinPolicy.IsTheServicesContainer("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}", WithPipeline));
        Assert.False(ImagePinPolicy.IsTheServicesContainer("otel", $"{Host}/{Workstation}@{WorkstationDigest}", WithPipeline));
    }

    [Fact]
    public void TheDefinitionsImage_IsTheNamedContainers_OrNullWhenThereIsNotExactlyOne()
    {
        Assert.Equal($"{Host}/{PipelineRepo}@{PipelineDigest}", ImagePinPolicy.SelectContainerImage(
            new[] { C("otel", "x:1"), C("aiphost", $"{Host}/{PipelineRepo}@{PipelineDigest}") }, WithPipeline));
        Assert.Null(ImagePinPolicy.SelectContainerImage(
            new[] { C("aiphost", "a:1"), C("aiphost", "b:1") }, WithPipeline));
    }

    [Fact]
    public void AKeptPipelineImage_MovesTheTaskDefinitionsRepository()
    {
        var pinned = new ImagePinDecision(PinDigest: true, RetainRevisions: true);

        Assert.Equal(PipelineRepo, ImagePinPolicy.ImageRepository(Workstation, PipelineRepo, PipelineDigest, pinned));
    }

    [Fact]
    public void OnlyAPinnedImageMayMove_ATagNamesNothingInThePipelinesRepository()
    {
        Assert.Equal(Workstation, ImagePinPolicy.ImageRepository(Workstation, PipelineRepo, null, new ImagePinDecision(true, true)));
        Assert.Equal(Workstation, ImagePinPolicy.ImageRepository(Workstation, PipelineRepo, PipelineDigest, ImagePinDecision.None));
    }

    // ---------------------------------------------------------------------------------------
    //  updatecontainer --digest under the block
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ADigestOnlyThePipelineHas_IsDeployedFromThePipelinesRepository()
    {
        var (repository, refusal) = ImagePinPolicy.RepositoryForDigest(
            PipelineDigest, WithPipeline, DigestPresence.Absent, DigestPresence.Present);

        Assert.Equal(PipelineRepo, repository);
        Assert.Null(refusal);
    }

    [Fact]
    public void ADigestOnlyTheWorkstationHas_IsDeployedFromTheWorkstationRepository()
    {
        // The rollback lever back to a workstation image, from a service running a pipeline one.
        var (repository, _) = ImagePinPolicy.RepositoryForDigest(
            WorkstationDigest, WithPipeline, DigestPresence.Present, DigestPresence.Absent);

        Assert.Equal(Workstation, repository);
    }

    [Fact]
    public void ADigestBothHave_IsDeployedFromThePipelinesRepository()
    {
        var (repository, _) = ImagePinPolicy.RepositoryForDigest(
            PipelineDigest, WithPipeline, DigestPresence.Present, DigestPresence.Present);

        Assert.Equal(PipelineRepo, repository);
    }

    [Theory]
    [InlineData(DigestPresence.Unreadable, DigestPresence.Present)]
    [InlineData(DigestPresence.Present, DigestPresence.Unreadable)]
    [InlineData(DigestPresence.Unreadable, DigestPresence.Unreadable)]
    public void AFailedRead_Refuses_RatherThanChoosingTheOtherRepository(DigestPresence inWorkstation, DigestPresence inPipeline)
    {
        var (repository, refusal) = ImagePinPolicy.RepositoryForDigest(PipelineDigest, WithPipeline, inWorkstation, inPipeline);

        Assert.Null(repository);
        Assert.Contains("could not read", refusal);
    }

    [Fact]
    public void ADigestNeitherHas_IsRefused()
    {
        var (repository, refusal) = ImagePinPolicy.RepositoryForDigest(
            PipelineDigest, WithPipeline, DigestPresence.Absent, DigestPresence.Absent);

        Assert.Null(repository);
        Assert.Contains("neither", refusal);
    }

    [Fact]
    public void AMalformedDigest_IsRefusedBeforeAnythingIsRead()
    {
        var (repository, refusal) = ImagePinPolicy.RepositoryForDigest(
            "sha256:abc", WithPipeline, DigestPresence.Present, DigestPresence.Present);

        Assert.Null(repository);
        Assert.Contains("not a sha256 digest", refusal);
    }

    [Fact]
    public void ANewRevisionNamesTheTargetRepository_NotTheOneTheContainerNamesNow()
    {
        // Re-pinning would have named the workstation digest in the pipeline's repository, which does
        // not hold it: a revision whose tasks cannot start.
        Assert.Equal($"{Host}/{Workstation}@{WorkstationDigest}",
            AwsContainerUpdater.ImageToRegister($"{Host}/{PipelineRepo}@{PipelineDigest}", WithPipeline, Workstation, WorkstationDigest));
        Assert.Equal($"{Host}/{PipelineRepo}@{PipelineDigest}",
            AwsContainerUpdater.ImageToRegister($"{Host}/{Workstation}:latest", WithPipeline, PipelineRepo, PipelineDigest));
    }

    [Fact]
    public void AMovingRepository_RegistersARevision_EvenFromATagFormDefinition()
    {
        // A forced redeploy re-pulls the definition's own :latest — never an image from another repository.
        Assert.Equal(AwsContainerUpdater.ContainerUpdateStrategy.RegisterNewRevision,
            AwsContainerUpdater.DecideStrategy($"{Host}/{Workstation}:latest", imageChanging: true, repositoryChanging: true));
        Assert.Equal(AwsContainerUpdater.ContainerUpdateStrategy.ForceRedeploy,
            AwsContainerUpdater.DecideStrategy($"{Host}/{Workstation}:latest", imageChanging: false, repositoryChanging: true));
    }

    // ---------------------------------------------------------------------------------------
    //  The parsing the decisions rest on
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData($"{Host}/{Workstation}:latest", Workstation)]
    [InlineData($"{Host}/{PipelineRepo}@{PipelineDigest}", PipelineRepo)]
    [InlineData("localhost:5000/team/app:1.2", "team/app")]
    [InlineData("nginx:1", "nginx")]
    [InlineData(null, null)]
    public void TheRepositoryOfAnImage(string? image, string? repository)
        => Assert.Equal(repository, ImagePinPolicy.RepositoryOf(image));

    [Theory]
    [InlineData(PipelineDigest, true)]
    [InlineData("sha256:1303A9132CD0117B53FF68F390CCE55A212AEB9E67A5105C9C80C6D85D5D0D26", false)]
    [InlineData("sha256:abc", false)]
    [InlineData("1303a9132cd0117b53ff68f390cce55a212aeb9e67a5105c9c80c6d85d5d0d26", false)]
    [InlineData(null, false)]
    public void ASha256Digest(string? digest, bool expected)
        => Assert.Equal(expected, ImagePinPolicy.IsSha256Digest(digest));

    [Fact]
    public void APinnedImage_SplitsIntoHostRepositoryAndDigest()
    {
        Assert.Equal((Host, PipelineRepo, PipelineDigest), ImagePinPolicy.ParsePinnedImage($"{Host}/{PipelineRepo}@{PipelineDigest}"));
        Assert.Null(ImagePinPolicy.ParsePinnedImage($"{Host}/{PipelineRepo}:latest"));
        Assert.Null(ImagePinPolicy.ParsePinnedImage($"{PipelineRepo}@{PipelineDigest}"));
    }
}
