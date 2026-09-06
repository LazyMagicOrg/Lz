using Lz.Core.PackageLane;

namespace Lz.Tests.PackageLane.Tests;

/// <summary>
/// The pure half of the local lane. No disk, no MSBuild — the CLI only reads the feed directories
/// and writes what this produces.
///
/// <para>The cases that matter are the ones where a wrong answer is SILENT: a file name split at the
/// wrong dot (every id here contains dots), a prerelease sorted above its own release, and an id
/// present at two versions being picked between without saying so.</para>
/// </summary>
public class LocalPackageOverridesTests
{
    // ---- file-name parsing -------------------------------------------------------------------

    [Theory]
    [InlineData("LazyMagic.Blazor.3.0.18.nupkg", "LazyMagic.Blazor", "3.0.18")]
    [InlineData("Lz.Aws.0.12.0.nupkg", "Lz.Aws", "0.12.0")]
    [InlineData("AipApi.1.0.0.nupkg", "AipApi", "1.0.0")]
    [InlineData("BaseApp.BlazorUI.1.0.1.nupkg", "BaseApp.BlazorUI", "1.0.1")]
    [InlineData("LazyMagic.Shared.3.0.19-alpha-g8ffa2a72c5.nupkg", "LazyMagic.Shared", "3.0.19-alpha-g8ffa2a72c5")]
    [InlineData("LazyMagic.Shared.3.0.19-alpha.g8ffa2a72c5.nupkg", "LazyMagic.Shared", "3.0.19-alpha.g8ffa2a72c5")]
    public void SplitsIdFromVersion(string file, string id, string version)
    {
        var p = LocalPackageOverrides.ParseFileName(file);
        Assert.NotNull(p);
        Assert.Equal(id, p!.Value.Id);
        Assert.Equal(version, p.Value.Version);
    }

    [Theory]
    [InlineData("LazyMagic.Blazor.3.0.18.snupkg")]   // symbols, not a package
    [InlineData("README.md")]
    [InlineData("notapackage.nupkg")]
    public void RejectsWhatIsNotAPackageFileName(string file)
        => Assert.Null(LocalPackageOverrides.ParseFileName(file));

    [Fact]
    public void AnIdWhoseLastSegmentIsNumericIsStillSplitAtTheVersion()
    {
        // The trap the left-to-right walk exists for: a naive "split at the first dot followed by a
        // digit" is right here only because a version needs at least major.minor after it.
        var p = LocalPackageOverrides.ParseFileName("LazyMagic.Client.ViewModels2.3.0.18.nupkg");

        Assert.NotNull(p);
        Assert.Equal("LazyMagic.Client.ViewModels2", p!.Value.Id);
        Assert.Equal("3.0.18", p.Value.Version);
    }

    // ---- version ordering --------------------------------------------------------------------

    [Theory]
    [InlineData("3.0.18", "3.0.17")]                       // patch
    [InlineData("3.1.0", "3.0.99")]                        // minor beats a bigger patch
    [InlineData("3.0.18", "3.0.18-alpha")]                 // a release outranks its own prerelease
    [InlineData("3.0.19-alpha", "3.0.18")]                 // ...but a higher core still wins
    [InlineData("3.0.18-alpha.2", "3.0.18-alpha.1")]       // numeric identifiers compare numerically
    [InlineData("3.0.18-beta", "3.0.18-alpha")]            // ordinal otherwise
    [InlineData("0.12.0", "0.11.1")]
    public void OrdersLikeNuGet(string higher, string lower)
    {
        Assert.True(LocalPackageOverrides.Compare(higher, lower) > 0, $"{higher} should outrank {lower}");
        Assert.True(LocalPackageOverrides.Compare(lower, higher) < 0, $"{lower} should rank below {higher}");
    }

    [Fact]
    public void APrereleaseIsNotSortedAboveItsOwnRelease()
    {
        // THE ordering trap. Sorted naively as strings, "3.0.18-alpha" > "3.0.18" because the dash
        // extends the string — and the local lane would then pin a prerelease over the release
        // sitting beside it in the feed.
        Assert.True(LocalPackageOverrides.Compare("3.0.18", "3.0.18-alpha") > 0);
        Assert.True(string.CompareOrdinal("3.0.18-alpha", "3.0.18") > 0);   // what a string sort would say
    }

    [Fact]
    public void BuildMetadataDoesNotAffectOrder()
        => Assert.Equal(0, LocalPackageOverrides.Compare("3.0.18+abc", "3.0.18+zzz"));

    // ---- resolution --------------------------------------------------------------------------

    [Fact]
    public void OneVersionPerId_IsTheOrdinaryCase_AndReportsNoAmbiguity()
    {
        var (chosen, ambiguous) = LocalPackageOverrides.Resolve(new[]
        {
            new FeedPackage("LazyMagic.Shared", "3.0.18"),
            new FeedPackage("Lz.Aws", "0.12.0"),
        });

        Assert.Equal("3.0.18", chosen["LazyMagic.Shared"]);
        Assert.Equal("0.12.0", chosen["Lz.Aws"]);
        Assert.Empty(ambiguous);
    }

    [Fact]
    public void TwoVersionsOfOneId_PicksTheNewer_AndSaysSo()
    {
        // A -SkipClean or resumed build legitimately leaves an older package behind. Picking is
        // fine; picking silently is not, because "which one did it take" is exactly the question a
        // mis-resolved build makes someone ask.
        var (chosen, ambiguous) = LocalPackageOverrides.Resolve(new[]
        {
            new FeedPackage("LazyMagic.Shared", "3.0.17"),
            new FeedPackage("LazyMagic.Shared", "3.0.18"),
        });

        Assert.Equal("3.0.18", chosen["LazyMagic.Shared"]);
        var note = Assert.Single(ambiguous);
        Assert.Contains("LazyMagic.Shared", note);
        Assert.Contains("3.0.17", note);
        Assert.Contains("3.0.18", note);
    }

    // ---- rendering ---------------------------------------------------------------------------

    [Fact]
    public void RendersAPropertyPerId_Deterministically()
    {
        var chosen = new Dictionary<string, string>
        {
            ["Lz.Aws"] = "0.12.0",
            ["LazyMagic.Shared"] = "3.0.18",
        };

        var a = LocalPackageOverrides.Render(chosen, "lz packages mode local");
        var b = LocalPackageOverrides.Render(
            new Dictionary<string, string> { ["LazyMagic.Shared"] = "3.0.18", ["Lz.Aws"] = "0.12.0" },
            "lz packages mode local");

        // Same content whatever order the ids arrived in: a no-op refresh must not churn the file.
        Assert.Equal(a, b);
        Assert.Contains("<PkgVer_LazyMagic_Shared>3.0.18</PkgVer_LazyMagic_Shared>", a);
        Assert.Contains("<PkgVer_Lz_Aws>0.12.0</PkgVer_Lz_Aws>", a);
    }

    [Fact]
    public void TheRenderedFileWarnsAboutTheImportOrdering()
    {
        // Measured 2026-09-06: an import placed before the property defaults loses to them, restore
        // still succeeds, and the wrong version is used with no diagnostic. The generated file is
        // where someone debugging that will actually look.
        var text = LocalPackageOverrides.Render(
            new Dictionary<string, string> { ["X"] = "1.0.0" }, "lz packages mode local");

        Assert.Contains("AFTER", text);
        Assert.Contains("GENERATED", text);
        Assert.Contains("do not commit", text);
    }

    [Fact]
    public void PropertyNamesAreValidMsBuildIdentifiers()
    {
        var p = LocalPackageOverrides.PropertyName("LazyMagic.Client.ViewModels");

        Assert.Equal("PkgVer_LazyMagic_Client_ViewModels", p);
        Assert.DoesNotContain(".", p);
    }
}
