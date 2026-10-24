using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The <c>tests</c> class (DecoupledCd.md V-E): a repository whose records are test bundles, which Gate 2 reads and no deployer
/// deploys — the browser tests, ScutaraE2ETests. A build-side class: a repository may build it although no environment's
/// <c>Classes</c> accepts it, it is given no role GitHub could assume, and its Gate 1 is the collection check.
/// </summary>
public sealed class TestsClassTests
{
    private static PipelineRepositoryConfig BrowserTests() => new()
    {
        Repo = "Scutara/ScutaraE2ETests", Class = "tests", RepositoryId = 1343104872, OwnerId = 320723031,
        WorkflowPath = ".github/workflows/publish-tests.yml", Intake = "dry", TestBundle = "tests",
        Gate1 = new PipelineGate1Config { Kind = "collection" },
    };

    private static SystemConfig Config(PipelineRepositoryConfig? extra = null)
    {
        var config = DeployerClientPlanTests.Config();   // Classes: image, client
        config.Pipeline!.Repositories!.Add(extra ?? BrowserTests());
        return config;
    }

    /// <summary>What the validator says, or nothing: the base config is a planner fixture, refused for reasons of its own.</summary>
    private static string Said(SystemConfig config)
    {
        try { ConfigValidator.Validate(config, "systemconfig.scu.dev.yaml"); return ""; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    [Fact]
    public void ATestsRepository_IsValid_ThoughNoEnvironmentAcceptsTheClass()
    {
        Assert.DoesNotContain("tests", Config().Pipeline!.Classes!);
        Assert.DoesNotContain("ScutaraE2ETests", Said(Config()));
    }

    [Fact]
    public void AClassThatIsNeitherDeployableNorBuildSide_IsStillRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigValidator.Validate(Config(new PipelineRepositoryConfig { Repo = "a/b", Class = "fixtures" }), "systemconfig.scu.dev.yaml"));
        Assert.Contains("class 'fixtures'", ex.Message);
    }

    [Fact]
    public void ATestsRepository_IsGivenNoRoleGitHubCouldAssume()
    {
        var plan = PipelineBootstrapPlanner.Plan(Config(), "147440642635");
        Assert.DoesNotContain(plan.Roles, r => r.Name.Contains("scutarae2etests", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Roles, r => r.Name.Contains("scutarasellerapp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATestsRecord_StartsNoDeploy_TheForwardingRuleMatchesNotItsClass()
    {
        var classes = DeployerPlanner.TriggerRecordClasses(Config());
        Assert.NotEmpty(classes);
        Assert.DoesNotContain("tests", classes);
    }

    // ---- the build-side file's rules for a tests entry ----

    private static IEnumerable<string> Problems(Action<PipelineRepositoryConfig> change)
    {
        var repo = BrowserTests();
        change(repo);
        return new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { repo } }.Problems();
    }

    [Fact]
    public void ASoundTestsEntry_HasNoProblems() => Assert.Empty(Problems(_ => { }));

    [Fact]
    public void ATestsEntry_IsGatedByTheCollectionCheck_AndBuildsNothingElse()
    {
        Assert.Contains(Problems(r => r.Gate1 = new PipelineGate1Config { Run = "x.dll", Filter = "f", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128) }),
            p => p.Contains("collection"));
        Assert.Contains(Problems(r => r.Gate1 = null), p => p.Contains("collection"));
        Assert.Contains(Problems(r => r.Artifacts = new() { "app" }), p => p.Contains("Artifacts"));
        Assert.Contains(Problems(r => r.Sbom = "sbom"), p => p.Contains("Sbom"));
        Assert.Contains(Problems(r => r.BuiltFrom = "builtfrom"), p => p.Contains("BuiltFrom"));
        Assert.Contains(Problems(r => r.Verify = "live"), p => p.Contains("Verify"));
    }

    [Fact]
    public void ATestsEntry_GoesLiveWithoutBuiltFrom_SinceItsRecordsCarryNoPackages()
        => Assert.Empty(Problems(r => r.Intake = "live"));
}
