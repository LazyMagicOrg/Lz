using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE TOOLING IMAGE IN THE INTAKE (DecoupledCd.md P3 B, owner 2026-11-05): its Gate 1 is a smoke check of the image itself, so its
/// bundle is the image artifact - fetched once, never published as a test bundle - and the buildspec carries the checks.
/// </summary>
public class ToolingSmokeConfigTests
{
    private static PipelineRepositoryConfig Tooling(Action<PipelineRepositoryConfig>? change = null)
    {
        var r = new PipelineRepositoryConfig
        {
            Repo = "Example/Root", Class = "tooling", RepositoryId = 42, OwnerId = 7, WorkflowPath = ".github/workflows/build-tooling.yml",
            Intake = "dry", Artifacts = new() { "tooling" }, Gate1 = new() { Kind = "smoke" },
        };
        change?.Invoke(r);
        return r;
    }

    private static IReadOnlyList<string> Problems(PipelineRepositoryConfig r)
        => new BuildSideConfig { BuildAccountId = "111111111111", BuildSuffix = "ab12-cd34", Repositories = new() { r } }.Problems();

    [Fact]
    public void ASmokeCheckedToolingImage_IsSound()
        => Assert.Empty(Problems(Tooling()));

    [Fact]
    public void ASmokeCheck_IsForTheToolingImageOnly()
        => Assert.Contains(Problems(Tooling(r => r.Class = "image")), p => p.Contains("smoke"));

    [Fact]
    public void ASmokeCheck_TakesNoTestBundle_AndNoTestRunSetting()
    {
        Assert.Contains(Problems(Tooling(r => r.TestBundle = "tests")), p => p.Contains("TestBundle"));
        Assert.Contains(Problems(Tooling(r => r.Gate1!.Run = "x.dll")), p => p.Contains("Run"));
    }

    [Fact]
    public void ItsBundleIsTheImage_AndNothingIsPublishedUnderTests()
    {
        var repo = new IntakeRepository("Example/Root", 42, 7, ".github/workflows/build-tooling.yml", "dry", "tooling", new[] { "tooling" },
            new IntakeGate1("tooling", "", "", "", "", Array.Empty<IntakeImage>(), Kind: "smoke"));
        Assert.False(repo.HasTestBundle);
        Assert.Equal(new[] { "tooling" }, repo.PublishedClasses);
    }

    [Fact]
    public void TheBuildspecCarriesEveryCheck_AndTheCountTheJudgeReads()
    {
        var spec = IntakeBuildPlanner.BuildspecFor("arn:aws:codebuild:us-west-2:111111111111:report-group/x");
        Assert.Contains("\"$GATE1_KIND\" = smoke", spec);
        foreach (var check in new[] { "lz-at-its-pin", "plugin-loaded", "plugin-commands", "pulumi-aws-provider", "aws-cli", "ambient-credentials" })
            Assert.Contains($"c {check} $?", spec);
        Assert.Contains("echo \"GATE1-LIST-SMOKE checks $checks failed $failed\"", spec);
    }

    [Fact]
    public void TheSmokeCommand_IsOneYamlCommand_InTheBuildPhase()
    {
        // A ": " in it once made the whole buildspec unparseable - CodeBuild would have refused every Gate 1, not only this one.
        var spec = IntakeBuildPlanner.BuildspecFor("arn:aws:codebuild:us-west-2:111111111111:report-group/x");
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(spec);
        var build = (Dictionary<object, object>)((Dictionary<object, object>)yaml["phases"])["build"];
        var smoke = Assert.Single(((List<object>)build["commands"]).Select(c => (string)c), c => c.Contains("GATE1-LIST-SMOKE checks"));
        Assert.StartsWith("if [ \"$GATE1_KIND\" = smoke ]", smoke);
    }
}
