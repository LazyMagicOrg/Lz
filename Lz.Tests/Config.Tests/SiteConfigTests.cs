using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE SITE IN THE BUILD-SIDE FILE (DecoupledCd.md V-24, §15.7 V-K): a <c>site</c> repository uploads one bundle per
/// environment, <c>site-{env}</c>, and its Gate 1 is lz's own site check (<c>Kind: site</c>), which runs no repository code
/// and so takes none of a test bundle's settings. A Hugo build consumes no first-party package, so a live site needs no
/// <c>BuiltFrom</c>.
/// </summary>
public class SiteConfigTests
{
    private static PipelineRepositoryConfig Site(Action<PipelineRepositoryConfig>? change = null)
    {
        var r = new PipelineRepositoryConfig
        {
            Repo = "Scutara/ScutaraWebsite", Class = "site", Artifacts = new() { "site-dev" },
            RepositoryId = 1, OwnerId = 2, WorkflowPath = ".github/workflows/publish.yml", Intake = "live", Verify = "live",
            Gate1 = new PipelineGate1Config { Kind = "site", Indexed = new() { "prod" } },
        };
        change?.Invoke(r);
        return r;
    }

    private static BuildSideConfig With(PipelineRepositoryConfig repo) => new()
    {
        BuildAccountId = "147440642635",
        Repositories = new() { repo },
        Chain = new() { new() { Environment = "dev", AccountId = "503947800380" }, new() { Environment = "test", AccountId = "456687462751" } },
    };

    [Fact]
    public void ALiveSite_WithItsOwnCheck_HasNoProblems()
        => Assert.Empty(With(Site()).Problems());

    [Fact]
    public void ALiveSite_NeedsNoBuiltFrom_ButStillNeedsAGate1()
    {
        Assert.DoesNotContain(With(Site()).Problems(), p => p.Contains("needs BuiltFrom"));
        Assert.Contains(With(Site(r => r.Gate1 = null)).Problems(), p => p.Contains("needs Gate1"));
    }

    [Fact]
    public void ABundleForEachEnvironmentInTheChain_IsSound()
        => Assert.Empty(With(Site(r => r.Artifacts = new() { "site-dev", "site-test" })).Problems());

    public static TheoryData<string, PipelineRepositoryConfig> Unsound => new()
    {
        { "Gate1.Kind 'lint' is not tests, site or collection", Site(r => r.Gate1!.Kind = "lint") },
        { "a site's Gate 1 is lz's site check (Gate1.Kind: site)", Site(r => r.Gate1!.Kind = null) },
        { "Gate1.Kind site is lz's check of a site's bundles; Scutara/ScutaraWebsite is class 'client'", Site(r => r.Class = "client") },
        { "Gate1.Kind site runs no repository code, so it takes no TestBundle", Site(r => r.TestBundle = "tests") },
        { "takes no Run", Site(r => r.Gate1!.Run = "x.dll") },
        { "takes no Filter", Site(r => r.Gate1!.Filter = "Category=Unit") },
        { "takes no SdkVersion", Site(r => r.Gate1!.SdkVersion = "10.0.301") },
        { "takes no Images", Site(r => r.Gate1!.Images = new() { new() { From = "x@sha256:" + new string('a', 64), As = "y" } }) },
        { "takes no Environment", Site(r => r.Gate1!.Environment = new() { ["X"] = "1" }) },
        { "Gate1.Indexed 'Prod' is not an environment's name", Site(r => r.Gate1!.Indexed = new() { "Prod" }) },
        { "Gate1.Indexed is for a site's check only", Site(r => { r.Class = "client"; r.Gate1 = new PipelineGate1Config { Run = "a.dll", Filter = "f", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128), Indexed = new() { "prod" } }; r.TestBundle = "tests"; }) },
        { "Gate1.ThirdPartyHosts 'https://link.msgsndr.com' is not a host name", Site(r => r.Gate1!.ThirdPartyHosts = new() { "https://link.msgsndr.com" }) },
        { "Gate1.ThirdPartyHosts is for a site's check only", Site(r => { r.Class = "client"; r.Gate1 = new PipelineGate1Config { Run = "a.dll", Filter = "f", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128), ThirdPartyHosts = new() { "x.com" } }; r.TestBundle = "tests"; }) },
        { "a site consumes no first-party package, so it uploads no BuiltFrom", Site(r => r.BuiltFrom = "builtfrom") },
        { "a site uploads no Sbom", Site(r => r.Sbom = "sbom") },
        { "a site's artifact is site-{environment}, not 'website'", Site(r => r.Artifacts = new() { "website" }) },
        { "a site's artifact is site-{environment}, not 'site-Dev'", Site(r => r.Artifacts = new() { "site-Dev" }) },
        { "'site-prod' names environment 'prod', which is not in the Chain", Site(r => r.Artifacts = new() { "site-dev", "site-prod" }) },
        { "names 'site-dev' twice", Site(r => r.Artifacts = new() { "site-dev", "site-dev" }) },
        { "a site names at least one artifact, site-{environment}", Site(r => r.Artifacts = new()) },
    };

    [Theory]
    [MemberData(nameof(Unsound))]
    public void EachUnsoundSiteIsNamed(string problem, PipelineRepositoryConfig repo)
        => Assert.Contains(With(repo).Problems(), p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void WithNoChainYet_AnyEnvironmentNameIsAccepted()
    {
        var c = With(Site(r => r.Artifacts = new() { "site-prod" }));
        c.Chain = null;
        Assert.Empty(c.Problems());
    }

    [Fact]
    public void ATestsGate1_IsUnchangedByTheKind()
    {
        var c = new BuildSideConfig
        {
            BuildAccountId = "147440642635",
            Repositories = new()
            {
                new()
                {
                    Repo = "a/b", Class = "client", TestBundle = "tests",
                    Gate1 = new PipelineGate1Config { Kind = "tests", Run = "a.dll", Filter = "f", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128) },
                },
            },
        };
        Assert.Empty(c.Problems());
    }

    [Theory]
    [InlineData("site-dev", "dev")]
    [InlineData("site-test", "test")]
    [InlineData("site-us-east", "us-east")]
    public void TheArtifactNamesItsEnvironment(string artifact, string environment)
        => Assert.Equal(environment, SiteArtifacts.EnvironmentOf(artifact));

    [Theory]
    [InlineData("website")]
    [InlineData("site-")]
    [InlineData("site-Dev")]
    [InlineData("sites-dev")]
    public void AnythingElseNamesNone(string artifact)
        => Assert.Null(SiteArtifacts.EnvironmentOf(artifact));
}
