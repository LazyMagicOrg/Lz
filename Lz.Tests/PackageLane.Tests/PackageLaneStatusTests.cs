using Lz.Core.PackageLane;

namespace Lz.Tests.PackageLane.Tests;

/// <summary>
/// The status view exists to make the two-lane state reviewable, and the one thing it must never
/// get wrong is which lane a consumer is actually on. That is decided by the position of one
/// import line, not by whether the override file exists - so most of these tests are about
/// ordering rather than parsing.
/// </summary>
public class PackageLaneStatusTests
{
    private const string Override = """
        <Project>
          <PropertyGroup>
            <PkgVer_AipApi>1.0.1</PkgVer_AipApi>
            <PkgVer_BaseApp_ViewModels>1.0.2</PkgVer_BaseApp_ViewModels>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>The shape every consumer in the workspace has: defaults, then the import.</summary>
    private const string Correct = """
        <Project>
          <PropertyGroup>
            <PkgVer_AipApi>1.0.0</PkgVer_AipApi>
            <PkgVer_BaseApp_ViewModels>1.0.1</PkgVer_BaseApp_ViewModels>
          </PropertyGroup>
          <Import Project="$(MSBuildThisFileDirectory)../../Packages.Local.props" Condition="Exists('$(MSBuildThisFileDirectory)../../Packages.Local.props')" />
        </Project>
        """;

    /// <summary>The same file with the import moved up. Restore still succeeds; the defaults win.</summary>
    private const string Misordered = """
        <Project>
          <Import Project="$(MSBuildThisFileDirectory)../../Packages.Local.props" Condition="Exists('$(MSBuildThisFileDirectory)../../Packages.Local.props')" />
          <PropertyGroup>
            <PkgVer_AipApi>1.0.0</PkgVer_AipApi>
            <PkgVer_BaseApp_ViewModels>1.0.1</PkgVer_BaseApp_ViewModels>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void ReadsBackWhatRenderWrote()
    {
        // Round-trip against the real writer rather than a hand-made string: a parser tested only
        // against its own fixture cannot notice the writer changing shape underneath it.
        var chosen = new Dictionary<string, string> { ["AipApi"] = "1.0.1", ["BaseApp.ViewModels"] = "1.0.2" };
        var parsed = PackageLaneStatus.ParseOverride(LocalPackageOverrides.Render(chosen, "test"));

        Assert.Equal("1.0.1", parsed["PkgVer_AipApi"]);
        Assert.Equal("1.0.2", parsed["PkgVer_BaseApp_ViewModels"]);
        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void FindsTheDefaultsAndTheImport()
    {
        var c = PackageLaneStatus.ParseConsumer("x.props", Correct);

        Assert.Equal(2, c.Pins.Count);
        Assert.Equal("1.0.0", c.Pins.Single(p => p.PropertyName == "PkgVer_AipApi").CommittedDefault);
        Assert.True(c.ImportLine > c.LastDefaultLine);
    }

    [Fact]
    public void AnImportBelowTheDefaultsCanWin()
    {
        Assert.True(PackageLaneStatus.ImportCanWin(PackageLaneStatus.ParseConsumer("x", Correct)));
    }

    [Fact]
    public void AnImportABOVETheDefaultsCannot()
    {
        // The whole reason this type is line-oriented. Both files import the same override and both
        // restore successfully; only the line numbers distinguish working from silently wrong.
        Assert.False(PackageLaneStatus.ImportCanWin(PackageLaneStatus.ParseConsumer("x", Misordered)));
    }

    [Fact]
    public void NoImportIsNeitherWinningNorMisordered()
    {
        var removed = string.Join("\n", Correct.Split('\n').Where(l => !l.Contains("<Import")));
        Assert.Null(PackageLaneStatus.ImportCanWin(PackageLaneStatus.ParseConsumer("x", removed)));

        // A COMMENTED-OUT import is the same thing: present in the text, inert at restore. Counting
        // it would report the consumer as lane-wired while restore reads only its committed
        // defaults - wrong in the direction that hides the problem.
        var commented = Correct.Replace("  <Import", "  <!-- <Import");
        Assert.Null(PackageLaneStatus.ImportCanWin(PackageLaneStatus.ParseConsumer("x", commented)));
    }

    [Fact]
    public void AMisorderedImportReportsTheDefaultAsEffective_NotTheLaneValue()
    {
        // The mutation that matters: same override, same defaults, only the import moved. If status
        // reported the lane value here it would show a consumer as up to date while restore used
        // the stale default - the exact failure it exists to catch.
        var feeds = PackageLaneStatus.ByProperty(new Dictionary<string, string>
        {
            ["AipApi"] = "1.0.1",
            ["BaseApp.ViewModels"] = "1.0.2",
        });
        var ovr = PackageLaneStatus.ParseOverride(Override);

        var good = PackageLaneStatus.Evaluate(PackageLaneStatus.ParseConsumer("x", Correct), ovr, feeds);
        var bad = PackageLaneStatus.Evaluate(PackageLaneStatus.ParseConsumer("x", Misordered), ovr, feeds);

        Assert.All(good, r => Assert.Equal(PinVerdict.Overridden, r.Verdict));
        Assert.All(good, r => Assert.NotNull(r.LaneValue));

        Assert.All(bad, r => Assert.Equal(PinVerdict.MissingFromLane, r.Verdict));
        Assert.All(bad, r => Assert.Null(r.LaneValue));
    }

    [Fact]
    public void AnIdNoFeedBuildsKeepsItsCommittedPin()
    {
        // Third-party and not-built-here ids must not be reported as problems: the committed pin is
        // the right answer for them, which is why the writer omits them in the first place.
        var rows = PackageLaneStatus.Evaluate(
            PackageLaneStatus.ParseConsumer("x", Correct),
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.All(rows, r => Assert.Equal(PinVerdict.NotBuiltHere, r.Verdict));
    }

    [Fact]
    public void AFeedHoldsItButTheLaneDoesNot_IsCalledOut()
    {
        // This is the state that surfaces as NU1101 inside a consumer: the producer was rebuilt and
        // the override was never refreshed.
        var rows = PackageLaneStatus.Evaluate(
            PackageLaneStatus.ParseConsumer("x", Correct),
            new Dictionary<string, string>(),
            PackageLaneStatus.ByProperty(new Dictionary<string, string> { ["AipApi"] = "1.0.9" }));

        var aip = rows.Single(r => r.PropertyName == "PkgVer_AipApi");
        Assert.Equal(PinVerdict.MissingFromLane, aip.Verdict);
        Assert.Equal("1.0.9", aip.FeedNewest);
    }

    [Fact]
    public void ALaneValueEqualToTheDefaultIsAgreement_NotAnOverride()
    {
        var rows = PackageLaneStatus.Evaluate(
            PackageLaneStatus.ParseConsumer("x", Correct),
            new Dictionary<string, string> { ["PkgVer_AipApi"] = "1.0.0" },
            PackageLaneStatus.ByProperty(new Dictionary<string, string> { ["AipApi"] = "1.0.0" }));

        Assert.Equal(PinVerdict.Agrees, rows.Single(r => r.PropertyName == "PkgVer_AipApi").Verdict);
    }

    [Fact]
    public void PropertyNamesJoinTheFeedToTheConsumer()
    {
        // The join key is the only link between a feed's package ids and a consumer's properties.
        var byProp = PackageLaneStatus.ByProperty(new Dictionary<string, string> { ["LazyMagic.OIDC.WASM"] = "3.0.22" });
        Assert.Equal("3.0.22", byProp["PkgVer_LazyMagic_OIDC_WASM"]);
    }

    [Fact]
    public void TabIndentedConsumersParseToo()
    {
        // AdminApp, SellerApp, BaseAppLib and Service are tab-indented; Deploy and ProjectTemplates
        // are space-indented. A parser that only handled one would silently report zero pins for
        // the others, which reads identically to "this consumer has no first-party pins".
        var tabbed = "<Project>\r\n\t<PropertyGroup>\r\n\t\t<PkgVer_AipApi>1.0.0</PkgVer_AipApi>\r\n\t</PropertyGroup>\r\n</Project>";
        var c = PackageLaneStatus.ParseConsumer("x", tabbed);
        Assert.Single(c.Pins);
        Assert.Equal("1.0.0", c.Pins[0].CommittedDefault);
    }
}
