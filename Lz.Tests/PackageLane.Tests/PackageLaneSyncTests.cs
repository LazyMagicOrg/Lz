using Lz.Core.PackageLane;

namespace Lz.Tests.PackageLane.Tests;

/// <summary>
/// Sync edits TRACKED files, which is what separates it from the override writer. So most of these
/// are about what it must NOT touch: third-party pins, conditions, indentation, commented-out
/// defaults, and anything whose shape it does not recognise.
/// </summary>
public class PackageLaneSyncTests
{
    private const string Consumer = """
        <Project>
        	<PropertyGroup>
        		<PkgVer_AipApi>1.0.0</PkgVer_AipApi>
        		<PkgVer_BaseApp_ViewModels>1.0.1</PkgVer_BaseApp_ViewModels>
        	</PropertyGroup>
        	<Import Project="$(MSBuildThisFileDirectory)../../Packages.Local.props" Condition="Exists('$(MSBuildThisFileDirectory)../../Packages.Local.props')" />
        	<ItemGroup>
        		<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
        		<PackageVersion Include="AipApi" Version="$(PkgVer_AipApi)" />
        	</ItemGroup>
        </Project>
        """;

    private static (string Text, IReadOnlyList<PinChange> Changes, IReadOnlyList<PinChange> Refused) Sync(
        string text, params (string Id, string Version)[] feed)
    {
        var byId = feed.ToDictionary(f => f.Id, f => f.Version, StringComparer.Ordinal);
        return PackageLaneSync.Apply(text, PackageLaneStatus.ParseConsumer("x", text),
                                     PackageLaneStatus.ByProperty(byId));
    }

    [Fact]
    public void MovesTheDefaultsTheFeedsCanSupply()
    {
        var (text, changes, _) = Sync(Consumer, ("AipApi", "1.0.2"), ("BaseApp.ViewModels", "1.0.2"));

        Assert.Equal(2, changes.Count);
        Assert.Contains("<PkgVer_AipApi>1.0.2</PkgVer_AipApi>", text);
        Assert.Contains("<PkgVer_BaseApp_ViewModels>1.0.2</PkgVer_BaseApp_ViewModels>", text);
    }

    [Fact]
    public void LeavesTheThirdPartyPinAndTheBindingsAlone()
    {
        // The bindings read $(PkgVer_X) and must keep doing so - rewriting them to a literal would
        // silently disconnect every consumer from the lane.
        var (text, _, _) = Sync(Consumer, ("AipApi", "1.0.2"));

        Assert.Contains("""<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />""", text);
        Assert.Contains("""<PackageVersion Include="AipApi" Version="$(PkgVer_AipApi)" />""", text);
    }

    [Fact]
    public void TouchesNothingWhenTheFeedsAgree()
    {
        var (text, changes, _) = Sync(Consumer, ("AipApi", "1.0.0"), ("BaseApp.ViewModels", "1.0.1"));

        Assert.Empty(changes);
        Assert.Equal(Consumer, text);
    }

    [Fact]
    public void IsIdempotent()
    {
        var first = Sync(Consumer, ("AipApi", "1.0.2"));
        var second = Sync(first.Text, ("AipApi", "1.0.2"));

        Assert.Empty(second.Changes);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void AnIdNoFeedHoldsIsLeftAtItsCommittedValue()
    {
        // Third-party and not-built-here ids: the committed pin is the right answer, which is why
        // the override writer omits them too.
        var (text, changes, _) = Sync(Consumer);

        Assert.Empty(changes);
        Assert.Equal(Consumer, text);
    }

    [Fact]
    public void PreservesIndentationAndLineEndings()
    {
        // These files are tab-indented and CRLF on disk. A sync that normalised either would show
        // up as a whole-file diff, burying the one line that actually changed.
        var crlf = Consumer.Replace("\n", "\r\n");
        var (text, changes, _) = Sync(crlf, ("AipApi", "1.0.2"));

        Assert.Single(changes);
        Assert.Contains("\r\n", text);
        Assert.Contains("\t\t<PkgVer_AipApi>1.0.2</PkgVer_AipApi>", text);
        Assert.DoesNotContain("\n\n", text.Replace("\r\n", "\n").Replace("\n\n", "KEEP"));
    }

    [Fact]
    public void KeepsAConditionOnTheElement()
    {
        var conditioned = Consumer.Replace(
            "<PkgVer_AipApi>1.0.0</PkgVer_AipApi>",
            """<PkgVer_AipApi Condition="'$(X)'==''">1.0.0</PkgVer_AipApi>""");

        var (text, changes, _) = Sync(conditioned, ("AipApi", "1.0.2"));

        Assert.Single(changes);
        Assert.Contains("""<PkgVer_AipApi Condition="'$(X)'==''">1.0.2</PkgVer_AipApi>""", text);
    }

    [Fact]
    public void NeverResurrectsACommentedOutDefault()
    {
        // ParseConsumer masks comments, so a retired pin is not in Pins - and sync must not bring
        // it back to life by writing a version into it.
        var withRetired = Consumer.Replace(
            "\t</PropertyGroup>",
            "\t\t<!-- <PkgVer_Retired>9.9.9</PkgVer_Retired> -->\n\t</PropertyGroup>");

        var (text, changes, _) = Sync(withRetired, ("AipApi", "1.0.2"), ("Retired", "5.0.0"));

        Assert.Single(changes);
        Assert.Equal("PkgVer_AipApi", changes[0].PropertyName);
        Assert.Contains("<!-- <PkgVer_Retired>9.9.9</PkgVer_Retired> -->", text);
    }

    [Fact]
    public void ADowngradeIsAppliedButFlagged()
    {
        // Height only increases, so a lower feed version means something built an older branch.
        // Applied, because the feed is the truth about what exists - but never silently.
        var (_, changes, _) = Sync(Consumer, ("AipApi", "0.9.0"));

        Assert.Single(changes);
        Assert.True(changes[0].IsDowngrade);
    }

    [Fact]
    public void AnUpgradeIsNotFlaggedAsADowngrade()
    {
        var (_, changes, _) = Sync(Consumer, ("AipApi", "1.0.2"));
        Assert.False(changes[0].IsDowngrade);
    }

    [Fact]
    public void RefusesAVersionBuiltOffThePublicReleaseBranch()
    {
        // The defect the first dry run exposed on the real workspace. A committed default IS the
        // published lane, read out of a REGISTRY by a fresh clone - and 3.0.23-g6fc0b7081e exists
        // only in the local feed that produced it. Syncing it would swap a stale-but-plausible
        // version for an unrestorable one.
        var (text, changes, refused) = Sync(Consumer, ("AipApi", "3.0.23-g6fc0b7081e"));

        Assert.Empty(changes);
        Assert.Single(refused);
        Assert.Equal("PkgVer_AipApi", refused[0].PropertyName);
        Assert.Equal(Consumer, text);          // and nothing was written
    }

    [Fact]
    public void RefusesOnlyTheCommitIdOnes_NotEveryPrerelease()
    {
        // Narrower than "is a prerelease" on purpose: a producer may legitimately publish a
        // prerelease line - LazyMagic carried -alpha until 2026-09-06 - and pinning that is
        // correct. It is the commit-id discriminator that makes a version local-only.
        Assert.True(PackageLaneSync.CarriesACommitId("3.0.23-g6fc0b7081e"));
        Assert.False(PackageLaneSync.CarriesACommitId("3.0.23-alpha"));
        Assert.False(PackageLaneSync.CarriesACommitId("1.0.3"));
        Assert.False(PackageLaneSync.CarriesACommitId("1.0.3-green"));   // -g, but not hex
    }

    [Fact]
    public void OneRefusalDoesNotBlockTheOtherProducersInTheSameFile()
    {
        // Refused per pin, not per file: in this workspace LazyMagic builds off dev while Service
        // and BaseAppLib build off main, and they share consumer files.
        var (text, changes, refused) = Sync(Consumer,
            ("AipApi", "1.0.3"), ("BaseApp.ViewModels", "3.0.23-g6fc0b7081e"));

        Assert.Single(changes);
        Assert.Single(refused);
        Assert.Contains("<PkgVer_AipApi>1.0.3</PkgVer_AipApi>", text);
        Assert.Contains("<PkgVer_BaseApp_ViewModels>1.0.1</PkgVer_BaseApp_ViewModels>", text);
    }

    [Fact]
    public void ReportsTheLineItChanged()
    {
        // The report has to be reviewable against a diff, so the line number must be real.
        var (text, changes, _) = Sync(Consumer, ("AipApi", "1.0.2"));
        var line = text.Replace("\r\n", "\n").Split('\n')[changes[0].Line - 1];

        Assert.Contains("1.0.2", line);
        Assert.Contains("PkgVer_AipApi", line);
    }
}
