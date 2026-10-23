using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer's site plan (DecoupledCd.md V-24, §15.7 V-K step 1): the environment's one site — the site repository whose
/// artifacts name this environment's bundle — its two functions and what their roles may touch, the branch on the class, the
/// trigger's route, and the build account's grants, all on <c>site/{owner}/{name}/{environment}/</c> only, so no environment
/// reads, starts on or alerts about another's bundle.
/// </summary>
public class DeployerSitePlanTests
{
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;
    private const string BuildAccount = "147440642635";
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string SiteBucket = "scu-mp--webapp-website-4df6-b9c6";
    private const string SitePrefix = "site/scutara/scutarawebsite/dev/";

    private static readonly DeployerTriggerInputs DevInputs = new("scu-dev-cluster", new[] { "mp" });
    private static readonly DeployerSiteInputs Website = new(SiteBucket, "aiproxydev.click");

    private static SystemConfig Dev(bool clients = true, bool site = true, bool trigger = true, bool alerts = true, List<string>? artifacts = null)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "site" });
        c.Pipeline!.DeployOnBuildRecord = trigger;
        c.Pipeline.Alerts = alerts;
        if (!clients)
            foreach (var r in c.Pipeline.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;
        if (site)
            c.Pipeline.Repositories!.Add(new PipelineRepositoryConfig
            {
                Repo = "Scutara/ScutaraWebsite", Class = "site", Artifacts = artifacts ?? new List<string> { "site-dev", "site-test" },
            });
        return c;
    }

    /// <summary>Dev as it was before V-24: no site class, no site repository.</summary>
    private static SystemConfig Before()
    {
        var c = DeployerClientPlanTests.Config();
        c.Pipeline!.DeployOnBuildRecord = true;
        c.Pipeline.Alerts = true;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config)
        => DeployerPlanner.Plan(config, TargetAccount, config.Pipeline!.DeployOnBuildRecord ? DevInputs : null,
            DeployerPlanner.ClientApps(config).Count > 0 ? DeployerClientPlanTests.Distributions : null,
            DeployerPlanner.SiteApps(config).Count > 0 ? Website : null);

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);

    private static IEnumerable<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };

    private static IEnumerable<(string Action, string Resource)> Grants(DeployerFunction f)
        => Statements(f.Policy).Where(s => s.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(s => Strings(s.GetProperty("Action")).SelectMany(a => Strings(s.GetProperty("Resource")).Select(r => (a, r))));

    private static IEnumerable<string> Prefixes(DeployerFunction f)
        => Statements(f.Policy).Where(s => s.TryGetProperty("Condition", out var c) && c.TryGetProperty("StringLike", out _))
            .SelectMany(s => Strings(s.GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix")));

    // ---- the site --------------------------------------------------------------------------

    [Fact]
    public void TheSiteRepository_DeploysThisEnvironmentsBundle_FromItsEnvironmentsPrefix()
        => Assert.Equal(new[] { new SiteApp("Scutara/ScutaraWebsite", "dev", SitePrefix) }, DeployerPlanner.SiteApps(Dev()));

    [Fact]
    public void NoBundleForThisEnvironment_OrNoSiteClass_MeansNoSite()
    {
        Assert.Empty(DeployerPlanner.SiteApps(Dev(artifacts: new List<string> { "site-test" })));
        var noClass = Dev();
        noClass.Pipeline!.Classes = new List<string> { "image", "client" };
        Assert.Empty(DeployerPlanner.SiteApps(noClass));
    }

    [Fact]
    public void TwoSiteRepositories_AreRefused()
    {
        var c = Dev();
        c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Other", Class = "site", Artifacts = new List<string> { "site-dev" } });
        Assert.Contains("one site", Assert.Throws<InvalidOperationException>(() => DeployerPlanner.SiteApps(c)).Message);
    }

    [Fact]
    public void ASite_WithoutItsBucketAndDomain_IsRefused()
    {
        var c = Dev();
        var ex = Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(c, TargetAccount, DevInputs, DeployerClientPlanTests.Distributions));
        Assert.Contains("lz bootstrapdeployer", ex.Message);
    }

    // ---- Verify and the site functions ------------------------------------------------------

    [Fact]
    public void Verify_KnowsTheSiteTarget_AndReadsOnlyThisEnvironmentsSiteRecordsAndBundles()
    {
        var verify = Function(Plan(Dev()), DeployerHandlers.Verify);
        var target = SiteTargets.Decode(verify.Environment[DeployerEnvironment.SiteTargets]).Single();
        Assert.Equal(new SiteTarget("Scutara/ScutaraWebsite", "dev", SiteBucket, "aiproxydev.click"), target);
        Assert.Equal(ArtifactStore, verify.Environment[DeployerEnvironment.ArtifactStore]);

        var grants = Grants(verify).ToList();
        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{RecordStore}/{SitePrefix}*"), grants);
        Assert.Contains(("s3:GetObjectVersion", $"arn:aws:s3:::{ArtifactStore}/{SitePrefix}*"), grants);
        Assert.DoesNotContain(grants, g => g.Resource.Contains("/site/*") || g.Resource.EndsWith("/site/scutara/scutarawebsite/*"));
        Assert.Contains($"{SitePrefix}*", Prefixes(verify));
    }

    [Fact]
    public void DeploySite_MirrorsIntoTheSitesServedPrefixAndItsMarker_AndCreatesInvalidatesAndPoliciesNothing()
    {
        var deploy = Function(Plan(Dev()), DeployerHandlers.DeploySite);
        var grants = Grants(deploy).ToList();

        Assert.Contains(("s3:GetObjectVersion", $"arn:aws:s3:::{ArtifactStore}/{SitePrefix}*"), grants);
        Assert.Contains(("s3:ListBucket", $"arn:aws:s3:::{SiteBucket}"), grants);
        foreach (var action in new[] { "s3:GetObject", "s3:PutObject", "s3:DeleteObject" })
        {
            Assert.Contains((action, $"arn:aws:s3:::{SiteBucket}/wwwroot/*"), grants);
            Assert.Contains((action, $"arn:aws:s3:::{SiteBucket}/lz/*"), grants);
        }
        Assert.DoesNotContain(grants, g => g.Action.StartsWith("cloudfront:") || g.Action is "s3:CreateBucket" or "s3:PutBucketPolicy" or "s3:PutBucketPublicAccessBlock");
        Assert.DoesNotContain(grants, g => g.Resource == $"arn:aws:s3:::{SiteBucket}/*");
        Assert.Equal(DeployerPlanner.DeploySiteRoleName(Dev()), deploy.RoleName);
    }

    [Fact]
    public void VerifySite_OnlyReads()
        => Assert.All(Grants(Function(Plan(Dev()), DeployerHandlers.VerifySite)).Where(g => g.Action.StartsWith("s3:")),
            g => Assert.Contains(g.Action, new[] { "s3:GetObject", "s3:ListBucket" }));

    [Fact]
    public void TheMachine_BranchesASiteToDeploySiteThenVerifySiteThenRecord()
    {
        var states = JsonDocument.Parse(Plan(Dev()).Definition).RootElement.GetProperty("States");
        var choices = states.GetProperty("ByClass").GetProperty("Choices").EnumerateArray()
            .Select(c => (c.GetProperty("StringEquals").GetString(), c.GetProperty("Next").GetString())).ToList();
        Assert.Contains(("site", "DeploySite"), choices);
        Assert.Contains(("client", "DeployBundle"), choices);
        Assert.Equal("VerifySite", states.GetProperty("DeploySite").GetProperty("Next").GetString());
        Assert.Equal("Record", states.GetProperty("VerifySite").GetProperty("Next").GetString());
        Assert.Equal("$.rollout", states.GetProperty("VerifySite").GetProperty("ResultPath").GetString());
    }

    [Fact]
    public void ASiteWithoutClients_StillBranchesOnTheClass()
    {
        var states = JsonDocument.Parse(Plan(Dev(clients: false)).Definition).RootElement.GetProperty("States");
        var nexts = states.GetProperty("ByClass").GetProperty("Choices").EnumerateArray().Select(c => c.GetProperty("Next").GetString()).ToList();
        Assert.Equal(new[] { "Prepare", "DeploySite" }, nexts);
        Assert.False(states.TryGetProperty("DeployBundle", out _));
    }

    [Fact]
    public void WithoutASite_ThePlanIsWhatItWas()
    {
        var with = Plan(Dev(site: false));
        var before = Plan(Before());
        Assert.Equal(before.Definition, with.Definition);
        Assert.Equal(before.Functions.Select(f => f.Name), with.Functions.Select(f => f.Name));
        Assert.Equal(Function(before, DeployerHandlers.Start).Environment, Function(with, DeployerHandlers.Start).Environment);
        Assert.DoesNotContain(with.Functions, f => f.Handler == DeployerHandlers.DeploySite);
        Assert.False(Function(with, DeployerHandlers.Verify).Environment.ContainsKey(DeployerEnvironment.SiteTargets));
    }

    [Fact]
    public void TheRole_MayInvokeTheSiteFunctions()
    {
        var plan = Plan(Dev());
        foreach (var handler in new[] { DeployerHandlers.DeploySite, DeployerHandlers.VerifySite })
            Assert.Contains(Function(plan, handler).Name, plan.RolePolicy);
    }

    // ---- the trigger and the sweep ---------------------------------------------------------

    [Fact]
    public void BothRules_MatchThisEnvironmentsSiteRecords_AndNoOtherEnvironments()
    {
        var plan = Plan(Dev());
        var prefixes = DeployerTrigger.RecordPrefixesOf(plan.Trigger!.EventPattern);
        Assert.Contains(SitePrefix, prefixes);
        Assert.DoesNotContain("site/", prefixes);
        Assert.Equal(plan.Trigger.EventPattern, PipelineBootstrapPlanner.Plan(Dev(), BuildAccount).RecordForwarding!.EventPattern);
    }

    [Fact]
    public void TheStartFunction_RoutesTheSite_ToItsBucket_AndMayReadItsRecords()
    {
        var start = Function(Plan(Dev()), DeployerHandlers.Start);
        var route = DeployerTrigger.DecodeClientRoutes(start.Environment[DeployerEnvironment.TriggerClientRoutes])
            .Single(r => r.RecordPrefix == SitePrefix);
        Assert.Equal(("site", SiteBucket), (route.App, route.Bucket));
        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{RecordStore}/{SitePrefix}*"), Grants(start));
    }

    [Fact]
    public void TheSweep_ChecksTheSitesBundles()
    {
        var sweep = Function(Plan(Dev()), DeployerHandlers.Corroborate);
        Assert.Contains(SitePrefix, DeployerAlerts.DecodeBundleSources(sweep.Environment[DeployerEnvironment.CorroborateBundleSources]));
        Assert.Contains($"{SitePrefix}*", Prefixes(sweep));
    }

    // ---- the build account -----------------------------------------------------------------

    private static string Role(string name) => $"arn:aws:iam::{TargetAccount}:role/{name}";

    private static IEnumerable<string> Principals(System.Text.Json.Nodes.JsonObject s)
        => s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"] is System.Text.Json.Nodes.JsonArray a
            ? a.Select(n => n!.GetValue<string>())
            : new[] { s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>() };

    [Fact]
    public void TheBuildAccount_LetsThisEnvironmentReadItsSiteRecordsAndBundles_Only()
    {
        var config = Dev();
        var build = PipelineBootstrapPlanner.Plan(config, BuildAccount);

        var records = build.BuildRecordReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsSiteRecordsDev");
        Assert.Equal($"arn:aws:s3:::{RecordStore}/{SitePrefix}*", records["Resource"]!.GetValue<string>());
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role(DeployerPlanner.StartFunctionRoleName(config)),
            Role(DeployerPlanner.CorroborateFunctionRoleName(config)) }, Principals(records));

        var bundles = build.ArtifactReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsSiteBundlesDev");
        Assert.Equal($"arn:aws:s3:::{ArtifactStore}/{SitePrefix}*", bundles["Resource"]!.GetValue<string>());
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role(DeployerPlanner.DeploySiteRoleName(config)) }, Principals(bundles));

        var list = build.ArtifactReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerListsSiteBundlesDev");
        Assert.Equal($"{SitePrefix}*", list["Condition"]!["StringLike"]!["s3:prefix"]!.GetValue<string>());
    }

    [Fact]
    public void WithoutASite_TheBuildAccountGrantsWhatItDidBefore()
    {
        var build = PipelineBootstrapPlanner.Plan(Dev(site: false), BuildAccount);
        var before = PipelineBootstrapPlanner.Plan(Before(), BuildAccount);
        Assert.Equal(before.BuildRecordReadGrant!.Select(s => s.ToJsonString()), build.BuildRecordReadGrant!.Select(s => s.ToJsonString()));
        Assert.Equal(before.ArtifactReadGrant!.Select(s => s.ToJsonString()), build.ArtifactReadGrant!.Select(s => s.ToJsonString()));
        Assert.DoesNotContain(build.BuildRecordReadGrant!, s => s.ToJsonString().Contains("site/"));
    }
}
