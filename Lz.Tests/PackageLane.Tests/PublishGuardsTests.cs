using System.IO.Compression;
using System.Text;
using Lz.Core.PackageLane;

namespace Lz.Tests.PackageLane.Tests;

/// <summary>
/// The two publish guards. The thing being protected is irreversible - a consumed version number
/// cannot be reused - so most of these pin the REFUSALS, and specifically the two states where the
/// guard cannot tell: they must refuse rather than pass.
/// </summary>
public class PublishGuardsTests
{
    private const string Commit = "6fc0b7081ef4b08b5e603f87c68c99883fcbba24";
    private const string OtherCommit = "057fcdfbd70a79d5e0298fe89b9ad29fa2f2b0fd";

    private static PackageFacts Package(string version, string? commit = Commit) =>
        new("LazyMagic.Blazor", version, commit, $"LazyMagic.Blazor.{version}.nupkg");

    // ---- guard two: refuse unless this was built as a public release -----------------------------

    [Fact]
    public void RefusesTheVersionShapeNbgvGivesANonPublicBuild()
    {
        // The real artifact in repos/LazyMagic/Packages on 2026-09-07, built off `dev` - which is not
        // that repo's publicReleaseRefSpec. This is guard two's whole job.
        Assert.Equal(PublishVerdict.NotAPublicRelease,
                     PublishGuards.Artifact(Package("3.0.23-g6fc0b7081e"), Commit));
    }

    [Fact]
    public void ClearsTheVersionShapeAPublicBuildGives()
    {
        // Lz off `main` on the same day. Both directions matter: a guard that only ever refuses is
        // indistinguishable from one that is broken.
        Assert.Equal(PublishVerdict.Publishable, PublishGuards.Artifact(Package("0.12.6"), Commit));
    }

    [Fact]
    public void DoesNotRefuseASanctionedPrereleaseLine()
    {
        // Narrower than "is a prerelease" on purpose. LazyMagic published -alpha until 2026-09-06 and
        // that was a legitimate public release; it is the commit-id discriminator, not the
        // prerelease-ness, that says a build was not public.
        Assert.Equal(PublishVerdict.Publishable, PublishGuards.Artifact(Package("3.0.23-alpha"), Commit));
    }

    [Fact]
    public void GuardTwoOutranksAMissingCommit()
    {
        // Both are wrong, but only one names the cause. Ordering here is what the operator reads.
        Assert.Equal(PublishVerdict.NotAPublicRelease,
                     PublishGuards.Artifact(Package("3.0.23-g6fc0b7081e", commit: null), Commit));
    }

    [Fact]
    public void RefusesAPackageThatRecordsNoCommit()
    {
        Assert.Equal(PublishVerdict.CommitNotRecorded,
                     PublishGuards.Artifact(Package("0.12.6", commit: null), Commit));
    }

    // ---- artifact identity: is this package even from the build being published? ------------------

    [Fact]
    public void RefusesAPackageBuiltFromAnotherCommit()
    {
        // The shared-feed hazard in one assertion: repos/Packages holds Service's and BaseAppLib's
        // output together, so a glob push from either repo would publish the other's packages.
        Assert.Equal(PublishVerdict.BuiltFromAnotherCommit,
                     PublishGuards.Artifact(Package("0.12.6", OtherCommit), Commit));
    }

    [Fact]
    public void WithoutAnExpectedCommitTheIdentityCheckIsNotAppliedAtAll()
    {
        // Deliberate, and the command announces it: passing null must not silently compare against
        // the empty string and refuse everything.
        Assert.Equal(PublishVerdict.Publishable, PublishGuards.Artifact(Package("0.12.6", OtherCommit), null));
    }

    [Theory]
    [InlineData("6fc0b7081ef4b08b5e603f87c68c99883fcbba24", true)]  // full, as github.sha gives it
    [InlineData("6fc0b70", true)]                                    // git's own seven-digit floor
    [InlineData("6FC0B7081E", true)]                                 // case is not identity
    [InlineData("6fc0b7", false)]                                    // six digits is too few to mean one commit
    [InlineData("057fcdf", false)]
    [InlineData("", false)]
    [InlineData("not-hex-at-all", false)]
    public void AnAbbreviationMatchesTheCommitItPrefixes(string candidate, bool same)
    {
        Assert.Equal(same, PublishGuards.SameCommit(Commit, candidate));
        Assert.Equal(same, PublishGuards.SameCommit(candidate, Commit));   // and it is symmetric
    }

    // ---- guard one: is this version already published, and by whom? ------------------------------

    [Fact]
    public void AVersionTheRegistryDoesNotHaveIsPublishable()
    {
        Assert.Equal(PublishVerdict.Publishable, PublishGuards.Registry(Package("0.12.6"), published: null));
    }

    [Fact]
    public void TheSameCommitIsARerunRatherThanACollision()
    {
        // This is why guard one compares the commit and not the version string: re-running a workflow
        // that already succeeded is not a mistake, and refusing it would train people to ignore this.
        var verdict = PublishGuards.Registry(Package("0.12.6"), new PublishedPackage(Commit));

        Assert.Equal(PublishVerdict.AlreadyPublishedFromThisCommit, verdict);
        Assert.False(PublishGuards.Refuses(verdict));
    }

    [Fact]
    public void ADifferentCommitAtTheSameVersionIsTheCollisionThisExistsFor()
    {
        var verdict = PublishGuards.Registry(Package("0.12.6"), new PublishedPackage(OtherCommit));

        Assert.Equal(PublishVerdict.CollidesWithADifferentCommit, verdict);
        Assert.True(PublishGuards.Refuses(verdict));
    }

    [Fact]
    public void APublishedPackageWithNoRecordedCommitIsRefusedNotAssumedToBeARerun()
    {
        // Fails closed. Packages published before this repo carried NBGV record no commit, and
        // guessing "probably a re-run" is how a collision gets through.
        var verdict = PublishGuards.Registry(Package("0.12.6"), new PublishedPackage(null));

        Assert.Equal(PublishVerdict.CollidesAndCannotBeCompared, verdict);
        Assert.True(PublishGuards.Refuses(verdict));
    }

    [Fact]
    public void EveryVerdictThatIsNotACleanPassOrARerunRefuses()
    {
        // Pins the fail-closed rule against a future verdict being added and quietly defaulting to
        // "allowed" - including the two states that mean "could not tell".
        foreach (PublishVerdict verdict in Enum.GetValues<PublishVerdict>())
        {
            var expected = verdict is not (PublishVerdict.Publishable or PublishVerdict.AlreadyPublishedFromThisCommit);
            Assert.Equal(expected, PublishGuards.Refuses(verdict));
        }

        Assert.True(PublishGuards.Refuses(PublishVerdict.RegistryUndeterminable));
    }

    // ---- reading the facts -----------------------------------------------------------------------

    [Fact]
    public void ReadsIdVersionAndCommitThroughTheNuspecNamespace()
    {
        var facts = PublishGuards.ParseNuspec("""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Lz.Core</id>
                <version>0.12.6</version>
                <repository type="git" url="https://github.com/LazyMagicOrg/Lz" commit="347f193c398cc80fef6a896d972339dd8fb9bc46" />
              </metadata>
            </package>
            """, "Lz.Core.0.12.6.nupkg");

        Assert.Equal("Lz.Core", facts.Id);
        Assert.Equal("0.12.6", facts.Version);
        Assert.Equal("347f193c398cc80fef6a896d972339dd8fb9bc46", facts.RepositoryCommit);
    }

    [Fact]
    public void ANuspecWithNoRepositoryElementYieldsNoCommitRatherThanThrowing()
    {
        var facts = PublishGuards.ParseNuspec(
            "<package><metadata><id>X</id><version>1.0.0</version></metadata></package>", "X.nupkg");

        Assert.Null(facts.RepositoryCommit);
        Assert.Equal(PublishVerdict.CommitNotRecorded, PublishGuards.Artifact(facts, Commit));
    }

    [Fact]
    public void ReadsTheNuspecAtTheArchiveRootAndNotOneShippedAsContent()
    {
        // Lz's own ProjectTemplates ship .nuspec files as package content. Matching the first entry
        // ending in .nuspec would read whichever the zip happened to list first.
        var path = Path.Combine(Path.GetTempPath(), $"lz-publish-guard-{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(zip, "content/templates/Decoy.nuspec",
                      "<package><metadata><id>Decoy</id><version>9.9.9</version></metadata></package>");
                Write(zip, "Lz.Core.nuspec",
                      "<package><metadata><id>Lz.Core</id><version>0.12.6</version></metadata></package>");
            }

            Assert.Equal("Lz.Core", PublishCandidates.Read(path).Id);
        }
        finally
        {
            File.Delete(path);
        }

        static void Write(ZipArchive zip, string name, string text)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    // ---- the registry probe's one testable half --------------------------------------------------

    [Fact]
    public void FindsThePackageBaseAddressAndGivesItATrailingSlash()
    {
        var address = NuGetV3PublishedPackages.FindPackageBaseAddress("""
            {"version":"3.0.0","resources":[
              {"@id":"https://example.test/query","@type":"SearchQueryService/3.0.0-rc"},
              {"@id":"https://example.test/flat","@type":"PackageBaseAddress/3.0.0"}
            ]}
            """, "index.json");

        Assert.Equal("https://example.test/flat/", address);
    }

    [Fact]
    public void RefusesSilenceWhenTheIndexPublishesNoFlatContainer()
    {
        // The state that decides whether guard one can run against GitHub Packages at all - unverified
        // as of 2026-09-07, because no credential in this workspace can read that registry. It must
        // throw, so the caller records RegistryUndeterminable and refuses, rather than reading the
        // absence as "this version is not published".
        Assert.Throws<InvalidDataException>(() => NuGetV3PublishedPackages.FindPackageBaseAddress(
            """{"version":"3.0.0","resources":[{"@id":"https://example.test/q","@type":"SearchQueryService/3.0.0-rc"}]}""",
            "index.json"));

        Assert.Throws<InvalidDataException>(() =>
            NuGetV3PublishedPackages.FindPackageBaseAddress("""{"version":"3.0.0"}""", "index.json"));
    }
}
