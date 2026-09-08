using Lz.Core.PackageLane;

namespace Lz.Tests.PackageLane.Tests;

/// <summary>
/// Reading the workspace feeds out of <c>NuGet.Config</c>. The end-to-end run proves the happy path
/// against the real file; these pin the cases that would otherwise be found only by a wrong build —
/// a registry treated as a local directory, or a config shape that yields nothing at all.
/// </summary>
public class LocalFeedsTests
{
    private const string RealShape = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="ScutaraService"    value="./repos/Service/Packages" />
            <add key="ScutaraBaseAppLib" value="./repos/BaseAppLib/Packages" />
            <add key="Lz"                value="./repos/Lz/Packages" />
            <add key="LazyMagic"         value="./repos/LazyMagic/Packages" />
            <add key="LazyMagicRegistry" value="https://nuget.pkg.github.com/LazyMagicOrg/index.json" />
            <add key="nuget.org"         value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
        </configuration>
        """;

    [Fact]
    public void TakesTheLocalDirectoriesAndNotTheRegistries()
    {
        var feeds = LocalFeeds.FromNuGetConfig(RealShape);

        Assert.Equal(new[] { "./repos/Service/Packages", "./repos/BaseAppLib/Packages",
                             "./repos/Lz/Packages", "./repos/LazyMagic/Packages" }, feeds);
    }

    [Fact]
    public void OrderIsPreserved_BecauseTheConfigSaysEntryOrderDecidesACollision()
    {
        // The root NuGet.Config's own comments say the order of sources decides a same-version
        // collision, so the scan must see them in the order the config declares.
        var feeds = LocalFeeds.FromNuGetConfig(RealShape);

        Assert.Equal("./repos/Service/Packages", feeds[0]);
        Assert.Equal("./repos/LazyMagic/Packages", feeds[^1]);
    }

    [Theory]
    [InlineData("https://nuget.pkg.github.com/Org/index.json")]
    [InlineData("http://internal.example/feed/index.json")]
    public void ARemoteSourceIsNeverTreatedAsADirectory(string url)
    {
        var xml = $"""
            <configuration><packageSources><add key="r" value="{url}" /></packageSources></configuration>
            """;

        Assert.Empty(LocalFeeds.FromNuGetConfig(xml));
    }

    [Fact]
    public void AConfigWithNoPackageSourcesYieldsNothingRatherThanThrowing()
    {
        // A config that only sets <config> keys is legal and common - the sibling workspaces' own
        // per-system files are exactly that shape.
        var xml = """
            <configuration><config><add key="globalPackagesFolder" value="./gpf" /></config></configuration>
            """;

        Assert.Empty(LocalFeeds.FromNuGetConfig(xml));
    }

    [Fact]
    public void AnAbsoluteWindowsPathIsStillALocalFeed()
    {
        // Uri.TryCreate parses "C:\feed" as an absolute URI with scheme "file"/"c" depending on the
        // form, so the check has to be on the SCHEME being http/https rather than on absoluteness.
        var xml = """
            <configuration><packageSources><add key="l" value="C:\packages" /></packageSources></configuration>
            """;

        Assert.Equal(new[] { @"C:\packages" }, LocalFeeds.FromNuGetConfig(xml));
    }

    [Fact]
    public void ScanSkipsAFeedDirectoryThatDoesNotExist()
    {
        // A feed that has never been built is empty, not broken - refusing would make a fresh clone
        // unable to run the command at all.
        var packages = LocalFeeds.Scan(Path.GetTempPath(), new[] { "definitely-not-here-" + Guid.NewGuid().ToString("N") });

        Assert.Empty(packages);
    }
}
