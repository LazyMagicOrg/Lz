using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The site, promoted (DecoupledCd.md V-K step 5): where the Website entry builds a bundle for this environment's successor,
/// a site execution ends by writing its site marker - MarkPassed's site mode - and then Recorded; where it builds none, the
/// site ends Recorded as before (V-24), byte for byte.
/// </summary>
public class DeployerSitePromotionPlanTests
{
    private const string Dev = "503947800380", Test = "456687462751";
    private const string TestStore = "scu-test-deploy-evidence-d5e0-b27f";

    internal static SystemConfig DevConfig(List<string>? siteArtifacts = null, bool successor = true)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "site" });
        c.Pipeline!.DeployOnBuildRecord = true;
        c.Pipeline.Alerts = true;
        c.Pipeline.Gate2 = "on";
        c.Pipeline.Gate2BrowserTests = "Scutara/ScutaraE2ETests";
        c.Pipeline.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        c.Pipeline.Repositories!.Add(new PipelineRepositoryConfig
        {
            Repo = "Scutara/ScutaraWebsite", Class = "site", Artifacts = siteArtifacts ?? new List<string> { "site-dev", "site-test" },
        });
        if (successor) c.Pipeline.Successor = new PipelineNeighbour("test", Test, TestStore);
        return c;
    }

    internal static PipelineDeployer PlanDev(SystemConfig config) => DeployerPlanner.Plan(config, Dev,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        siteInputs: new DeployerSiteInputs("scu-mp--webapp-website-4df6-b9c6", "aiproxydev.click"),
        gate2Inputs: new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc" }));

    private static JsonElement States(PipelineDeployer p) => JsonDocument.Parse(p.Definition).RootElement.GetProperty("States");

    private static string SiteNext(PipelineDeployer p)
    {
        var choice = States(p).GetProperty("GatesThisClass").GetProperty("Choices").EnumerateArray()
            .Single(c => c.GetProperty("StringEquals").GetString() == "site");
        return choice.GetProperty("Next").GetString()!;
    }

    [Fact]
    public void WhereTheSuccessorTakesTheSite_ASiteExecution_WritesItsMarker_ThenEndsRecorded()
    {
        var p = PlanDev(DevConfig());
        Assert.Equal("MarkSitePassed", SiteNext(p));

        var mark = States(p).GetProperty("MarkSitePassed");
        Assert.Equal("Task", mark.GetProperty("Type").GetString());
        Assert.Equal($"arn:aws:lambda:us-west-2:{Dev}:function:scu-dev-deployer-mark-passed", mark.GetProperty("Resource").GetString());
        Assert.Equal("$.marked", mark.GetProperty("ResultPath").GetString());
        Assert.Equal("Recorded", mark.GetProperty("Next").GetString());
        Assert.Equal("MarkPassedFailed", mark.GetProperty("Catch")[0].GetProperty("Next").GetString());
        Assert.Equal("Succeed", States(p).GetProperty("Recorded").GetProperty("Type").GetString());
    }

    [Fact]
    public void WhereTheSuccessorTakesNoSite_ASiteExecution_EndsRecorded_AsBefore()
    {
        var p = PlanDev(DevConfig(siteArtifacts: new List<string> { "site-dev" }));
        Assert.Equal("Recorded", SiteNext(p));
        Assert.False(States(p).TryGetProperty("MarkSitePassed", out _));
    }

    [Fact]
    public void WithNoSuccessorTakingIt_TheSitesStatesAreTheOnesBeforeSiteMarkers()
    {
        // The definition with no site-test is the one dev runs today: no state names the site marker.
        var p = PlanDev(DevConfig(siteArtifacts: new List<string> { "site-dev" }));
        Assert.DoesNotContain("MarkSitePassed", p.Definition);
    }
}
