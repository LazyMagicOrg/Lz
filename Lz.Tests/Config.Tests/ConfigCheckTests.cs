using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE CONFIG BUNDLE'S GATE 1 (DecoupledCd.md P3 C, owner 2026-11-06): the edge tests, and every environment's config loaded by
/// lz validateconfig in the tooling image that carries the lz the bundle pins - chosen from the tooling records, waited for when
/// it is not published yet, and pulled by a build that may pull nothing else.
/// </summary>
public class ConfigCheckTests
{
    private const long RepoId = 2;
    private static readonly IntakeRepository Config = new("Example/Root", RepoId, 7, ".github/workflows/build-config.yml", "dry", "config", new[] { "config" },
        new IntakeGate1("config", "", "", "", "", Array.Empty<IntakeImage>(), Kind: "config"), "builtfrom");

    private static JsonObject Record(string lz, string digest) => new()
    {
        ["builtFrom"] = new JsonObject { ["packages"] = new JsonObject { ["Lz.Cli"] = lz } },
        ["identity"] = new JsonObject { ["digest"] = digest },
    };

    // ---- which tooling image ----

    [Fact]
    public void TheNewestImageCarryingThePinnedLz_IsChosen()
    {
        var records = new[]
        {
            ("tooling/x/20261106T005559Z-3-1.json", Record("0.13.156", "sha256:new")),
            ("tooling/x/20261105T200000Z-1-1.json", Record("0.13.155", "sha256:older")),
            ("tooling/x/20261106T000000Z-2-1.json", Record("0.13.156", "sha256:earlier")),
        };
        Assert.Equal("sha256:new", ToolingImageChoice.DigestFor(records, "0.13.156"));
        Assert.Equal("sha256:older", ToolingImageChoice.DigestFor(records, "0.13.155"));
        Assert.Null(ToolingImageChoice.DigestFor(records, "0.13.157"));
    }

    // ---- Gate1Start ----

    private sealed class Fakes : IGate1Builds, IScratchPresigner, IToolingRecords
    {
        public IReadOnlyDictionary<string, string>? Variables;
        public List<(string, JsonObject)> Records = new();
        public string GetUrl(string key, TimeSpan lifetime) => $"https://scratch/{key}";
        public Task<string> StartAsync(string project, IReadOnlyDictionary<string, string> variables) { Variables = variables; return Task.FromResult("build:1"); }
        public Task<Gate1Build> DescribeAsync(string buildId) => throw new NotImplementedException();
        public Task<Gate1Report?> ReportAsync(IReadOnlyList<string> reportArns) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListLinesAsync(Gate1Build build) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> FailedCasesAsync(IReadOnlyList<string> reportArns, int max) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string Key, JsonObject Record)>> NewestAsync(int max) => Task.FromResult<IReadOnlyList<(string, JsonObject)>>(Records);
    }

    private static JsonObject State(string? lz = "0.13.156") => new()
    {
        ["repo"] = Config.Repo, ["repositoryId"] = RepoId, ["runId"] = 9L, ["runAttempt"] = 1, ["retry"] = 0,
        ["workflowPath"] = Config.WorkflowPath,
        ["check"] = new JsonObject { ["headSha"] = new string('d', 40) },
        ["fetch"] = new JsonObject
        {
            ["tests"] = new JsonObject { ["artifact"] = "config-1", ["key"] = "x/config-1.zip", ["sha256"] = new string('1', 64) },
            ["builtFrom"] = lz is null ? new JsonObject { ["packages"] = new JsonObject() } : new JsonObject { ["packages"] = new JsonObject { ["Lz.Cli"] = lz } },
        },
    };

    private const string Registry = "147440642635.dkr.ecr.us-west-2.amazonaws.com/scu-x-tooling";

    [Fact]
    public async Task AConfigCheck_RunsInTheToolingImageCarryingItsLz()
    {
        var f = new Fakes { Records = { ("k/20261106T005559Z-3-1.json", Record("0.13.156", "sha256:abc")) } };

        var started = await IntakeGate1StartStep.RunAsync(State(), "e", new[] { Config }, f, f, "p", f, Registry);

        Assert.Equal($"{Registry}@sha256:abc", f.Variables!["GATE1_TOOLING_IMAGE"]);
        Assert.Equal("config", f.Variables["GATE1_KIND"]);
        Assert.Equal($"{Registry}@sha256:abc", started["toolingImage"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithNoImageCarryingItsLzYet_TheCheckWaits_AndStartsNothing()
    {
        var f = new Fakes { Records = { ("k/1.json", Record("0.13.155", "sha256:old")) } };
        await Assert.ThrowsAsync<Gate1Waiting>(() => IntakeGate1StartStep.RunAsync(State(), "e", new[] { Config }, f, f, "p", f, Registry));
        Assert.Null(f.Variables);
    }

    [Fact]
    public async Task ABundleNamingNoLz_IsAFault_NotAWait()
    {
        var f = new Fakes();
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeGate1StartStep.RunAsync(State(lz: null), "e", new[] { Config }, f, f, "p", f, Registry));
    }

    // ---- the build-side file and the plan ----

    private static PipelineRepositoryConfig ConfigEntry(Action<PipelineRepositoryConfig>? change = null)
    {
        var r = new PipelineRepositoryConfig
        {
            Repo = "Example/Root", Class = "config", RepositoryId = 42, OwnerId = 7, WorkflowPath = ".github/workflows/build-config.yml",
            Intake = "dry", Artifacts = new() { "config" }, Gate1 = new() { Kind = "config" },
        };
        change?.Invoke(r);
        return r;
    }

    private static IReadOnlyList<string> Problems(params PipelineRepositoryConfig[] repos)
        => new BuildSideConfig { BuildAccountId = "111111111111", BuildSuffix = "ab12-cd34", Repositories = repos.ToList() }.Problems();

    [Fact]
    public void AConfigCheckedBundle_IsSound()
        => Assert.Empty(Problems(ConfigEntry()));

    [Fact]
    public void AConfigCheck_IsForTheConfigClassOnly_AndTakesNoTestBundle()
    {
        Assert.Contains(Problems(ConfigEntry(r => r.Class = "client")), p => p.Contains("config"));
        Assert.Contains(Problems(ConfigEntry(r => r.TestBundle = "tests")), p => p.Contains("TestBundle"));
    }

    [Fact]
    public void ConfigIsTheIntakes_SoGitHubGetsNoRoleForIt()
        => Assert.True(PipelineBootstrapPlanner.IntakeOnly("config"));

    private static PipelineRepositoryConfig ToolingEntry() => new()
    {
        Repo = "Example/Root", Class = "tooling", RepositoryId = 42, OwnerId = 7, WorkflowPath = ".github/workflows/build-tooling.yml",
        Intake = "live", Artifacts = new() { "tooling" }, BuiltFrom = "builtfrom", Sbom = "sbom", Gate1 = new() { Kind = "smoke" },
    };

    private static IntakeBuildPlan Plan(params PipelineRepositoryConfig[] repos) => IntakeBuildPlanner.Plan(new SystemConfig
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
        Pipeline = new PipelineConfig { Enabled = true, Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" }, Repositories = repos.ToList() },
    }, "147440642635")!;

    [Fact]
    public void AConfigCheckWithNoToolingRepository_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => Plan(ConfigEntry()));

    [Fact]
    public void TheConfigCheck_IsGrantedTheToolingRecordsAndImage_AndNothingWider()
    {
        var plan = Plan(ToolingEntry(), ConfigEntry());
        var start = plan.Functions.Single(f => f.Handler == DeployerHandlers.IntakeGate1Start);

        Assert.Contains("ReadTheToolingRecords", start.Policy);
        Assert.Contains("tooling/example/root/*", start.Policy);
        Assert.Equal("147440642635.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-tooling", start.Environment[IntakeEnvironment.ToolingRepository]);
        Assert.Contains("PullTheToolingImage", plan.Gate1!.RolePolicy);
        Assert.Contains("repository/scu-4df6-b9c6-tooling", plan.Gate1.RolePolicy);
        Assert.DoesNotContain("ecr:PutImage", plan.Gate1.RolePolicy);
    }

    [Fact]
    public void WithoutAConfigCheck_TheGate1BuildPullsNothing_AndItsPolicyHasNoNullStatement()
    {
        var plan = Plan(ToolingEntry());
        Assert.DoesNotContain("PullTheToolingImage", plan.Gate1!.RolePolicy);
        Assert.DoesNotContain("null", plan.Gate1.RolePolicy);
    }

    [Fact]
    public void TheBuildspecCarriesTheConfigCheck_AsOneCommand_InTheBuildPhase()
    {
        var spec = IntakeBuildPlanner.BuildspecFor("arn:aws:codebuild:us-west-2:111111111111:report-group/x");
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(spec);
        var build = (Dictionary<object, object>)((Dictionary<object, object>)yaml["phases"])["build"];
        var check = Assert.Single(((List<object>)build["commands"]).Select(c => (string)c), c => c.Contains("GATE1-LIST-CONFIG checks"));
        Assert.StartsWith("if [ \"$GATE1_KIND\" = config ]", check);
        Assert.Contains("node --test test/*.test.mjs); c edge-tests $?", check);
        Assert.Contains("-e LZ_CREDENTIALS= -v /tmp/config:/work/_Dev:ro \"$GATE1_TOOLING_IMAGE\" validateconfig --env \"$env\"", check);
        // The registry sign-in and pull come before the first check: a failure there decides nothing.
        Assert.True(check.IndexOf("docker pull", StringComparison.Ordinal) < check.IndexOf("checks=0", StringComparison.Ordinal));
    }
}
