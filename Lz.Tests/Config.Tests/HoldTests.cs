using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>The hold's four values (PerKindHold.md): off, sets, sites, and everything else holding both - fail closed.</summary>
public class HoldTests
{
    [Theory]
    [InlineData("off", false, false)]
    [InlineData(" off\n", false, false)]
    [InlineData("sets", true, false)]
    [InlineData("sites", false, true)]
    [InlineData(" sites ", false, true)]
    [InlineData("on", true, true)]
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("OFF", true, true)]
    [InlineData("Sets", true, true)]
    [InlineData("site", true, true)]
    [InlineData("set", true, true)]
    [InlineData("sets sites", true, true)]
    public void EachValue_HoldsItsKinds(string? value, bool sets, bool sites)
        => Assert.Equal(new Hold(sets, sites), Hold.Parse(value));

    [Theory]
    [InlineData("site", "sites", true)]
    [InlineData("site", "sets", false)]
    [InlineData("image", "sets", true)]
    [InlineData("client", "sets", true)]
    [InlineData("config", "sets", true)]
    [InlineData("tooling", "sets", true)]
    [InlineData("image", "sites", false)]
    [InlineData("client", "sites", false)]
    [InlineData("Site", "sets", true)]   // classes are lower case; anything not exactly "site" is a set's member
    public void ARecordsClass_PicksItsKind(string recordClass, string hold, bool held)
        => Assert.Equal(held, Hold.Parse(hold).HoldsClass(recordClass));

    [Fact]
    public void TheKinds_AreLoggedByName()
    {
        Assert.Equal(new[] { "set", "site" }, Hold.Parse("on").Kinds);
        Assert.Equal(new[] { "set" }, Hold.Parse("sets").Kinds);
        Assert.Equal(new[] { "site" }, Hold.Parse("sites").Kinds);
        Assert.Empty(Hold.Parse("off").Kinds);
    }
}
