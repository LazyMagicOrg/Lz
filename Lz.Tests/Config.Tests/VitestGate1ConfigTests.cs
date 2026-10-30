using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// A NODE CLIENT'S GATE 1 (BuyerAppReactPunchlist BR-P3): <c>Gate1.Kind: vitest</c> runs the repository's own tests - npm ci,
/// then vitest run, in the test bundle's root - so it takes a TestBundle and none of a .NET run's settings; and the intake
/// carries it as a vitest Gate 1, for the buildspec to branch on.
/// </summary>
public class VitestGate1ConfigTests
{
    private static PipelineRepositoryConfig Client(Action<PipelineRepositoryConfig>? change = null)
    {
        var r = new PipelineRepositoryConfig
        {
            Repo = "Scutara/ScutaraBuyerAppReact", Class = "client", Artifacts = new() { "buyerreact" },
            RepositoryId = 1, OwnerId = 2, WorkflowPath = ".github/workflows/build-bundle.yml", Intake = "live", Verify = "live",
            BuiltFrom = "builtfrom", TestBundle = "tests",
            Gate1 = new PipelineGate1Config { Kind = "vitest" },
        };
        change?.Invoke(r);
        return r;
    }

    private static BuildSideConfig With(PipelineRepositoryConfig repo) => new() { BuildAccountId = "147440642635", Repositories = new() { repo } };

    [Fact]
    public void ALiveClient_RunByVitest_HasNoProblems()
        => Assert.Empty(With(Client()).Problems());

    [Fact]
    public void ABuildRoot_IsAccepted_AsForAnyRunOfRepositoryCode()
        => Assert.Empty(With(Client(r => r.Gate1!.BuildRoot = "/home/runner/work/ScutaraBuyerAppReact/ScutaraBuyerAppReact")).Problems());

    public static TheoryData<string, PipelineRepositoryConfig> Unsound => new()
    {
        { "a vitest run runs a TestBundle, and none is named", Client(r => r.TestBundle = null) },
        { "so it takes no Run", Client(r => r.Gate1!.Run = "x.dll") },
        { "so it takes no Filter", Client(r => r.Gate1!.Filter = "Category=Unit") },
        { "so it takes no SdkVersion", Client(r => r.Gate1!.SdkVersion = "10.0.301") },
        { "so it takes no SdkSha512", Client(r => r.Gate1!.SdkSha512 = new string('a', 128)) },
        { "so it takes no Images", Client(r => r.Gate1!.Images = new() { new() { From = "x@sha256:" + new string('a', 64), As = "y" } }) },
        { "so it takes no Environment", Client(r => r.Gate1!.Environment = new() { ["X"] = "1" }) },
        { "Gate1.BuildRoot 'relative/path' is not an absolute path", Client(r => r.Gate1!.BuildRoot = "relative/path") },
        { "a site repository has its own check", Client(r => r.Class = "site") },
    };

    [Theory]
    [MemberData(nameof(Unsound))]
    public void EachUnsoundVitestGate1IsNamed(string problem, PipelineRepositoryConfig repo)
        => Assert.Contains(With(repo).Problems(), p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void TheIntake_CarriesItAsAVitestGate1_WithItsBundleAndBuildRoot()
    {
        var config = new SystemConfig { Pipeline = new PipelineConfig { Repositories = new() { Client(r => r.Gate1!.BuildRoot = "/w/app") } } };
        var gate1 = IntakeBuildPlanner.RepositoriesFor(config).Single().Gate1!;
        Assert.Equal(("vitest", "tests", "/w/app"), (gate1.Kind, gate1.Artifact, gate1.BuildRoot));
    }
}
