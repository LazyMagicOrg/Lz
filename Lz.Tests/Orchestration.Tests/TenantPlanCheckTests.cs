using Lz.Aws.Orchestration;
using Pulumi.Automation;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>The tenant plan's image checks (DecoupledCd.md V-J, slice 4).</summary>
public class TenantPlanCheckTests
{
    private const string Family = "scu-mp-aiphost";
    private const string Running = "sha256:2c3ca431bdd3f78b97c794534a7d3c430e9dfe900ae39e267a12f57afe80a264";
    private const string Other = "sha256:09256668c9ee759f233b2cbd8d2e703b2ce5e42816dbb8880119be2bceda570c";
    private const string Repo = "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost";

    private static string Defs(string image, string env = "Production")
        => $$"""[{"name":"aiphost","image":"{{image}}","environment":[{"name":"ASPNETCORE_ENVIRONMENT","value":"{{env}}"}]}]""";

    private static Dictionary<string, object> Tags(string? hash, string? builtAt, string tenant = "mp")
    {
        var t = new Dictionary<string, object> { ["System"] = "scu", ["Tenant"] = tenant };
        if (hash is not null) t[TenantPlanCheck.DefinitionsHashTag] = hash;
        if (builtAt is not null) t["lz:builtAt"] = builtAt;
        return t;
    }

    private static Dictionary<string, object> Inputs(string image, string? hash, string? builtAt, string cpu = "1024", string tenant = "mp") => new()
    {
        ["family"] = Family,
        ["cpu"] = cpu,
        ["containerDefinitions"] = new Dictionary<string, object> { ["ciphertext"] = "[secret]" },   // as a preview reports it
        ["tags"] = Tags(hash, builtAt, tenant),
        ["tagsAll"] = Tags(hash, builtAt, tenant),
        ["__defaults"] = new object[] { },
    };

    private static PlannedStep Replace(Dictionary<string, object>? old, Dictionary<string, object>? now, OperationType op = OperationType.Replace,
        string type = TenantPlanCheck.TaskDefinitionType)
        => new(op, type, $"urn:pulumi:scu-mp-dev::lz-scu::{type}::{Family}-task-def", old, now);

    // ---- the definitions hash ----

    [Fact]
    public void TheHashIgnoresTheImage_AndNothingElse()
    {
        Assert.Equal(TenantPlanCheck.DefinitionsHash(Defs($"{Repo}@{Running}")), TenantPlanCheck.DefinitionsHash(Defs($"{Repo}@{Other}")));
        Assert.NotEqual(TenantPlanCheck.DefinitionsHash(Defs($"{Repo}@{Running}")), TenantPlanCheck.DefinitionsHash(Defs($"{Repo}@{Running}", "Development")));
        Assert.Equal(64, TenantPlanCheck.DefinitionsHash(Defs($"{Repo}@{Running}")).Length);
    }

    // ---- image-only replaces ----

    [Fact]
    public void AReplaceThatChangesOnlyTheImageAndItsBuildTime_IsImageOnly()
    {
        var step = Replace(Inputs($"{Repo}@{Other}", "h1", "2026-10-20T21:00:00Z"), Inputs($"{Repo}@{Running}", "h1", "2026-10-20T22:12:00Z"));
        Assert.True(TenantPlanCheck.IsImageOnlyTaskDefinitionReplace(step));
        Assert.Empty(TenantPlanCheck.Destructive(new[] { step }));
    }

    [Fact]
    public void AReplaceOfARevisionWithNoHash_IsCounted_SinceWhatElseChangedIsUnknown()
    {
        var step = Replace(Inputs("x", null, null), Inputs("y", "h1", "2026-10-20T22:12:00Z"));
        Assert.Contains("carries no lz:defsHash", TenantPlanCheck.WhyNotImageOnly(step));
        Assert.Single(TenantPlanCheck.Destructive(new[] { step }));
    }

    [Fact]
    public void AReplaceWhoseNewHashIsUnknown_IsCounted()
        => Assert.Contains("not known yet", TenantPlanCheck.WhyNotImageOnly(Replace(Inputs("x", "h1", null), Inputs("y", null, null))));

    [Theory]
    [InlineData("h2", "1024", "mp", "container definitions change")]
    [InlineData("h1", "2048", "mp", "its cpu changes")]
    [InlineData("h1", "1024", "other", "its tags changes")]
    public void AnyOtherChange_IsCounted(string hash, string cpu, string tenant, string why)
    {
        var step = Replace(Inputs("x", "h1", "2026-10-20T21:00:00Z"), Inputs("y", hash, "2026-10-20T22:12:00Z", cpu, tenant));
        Assert.Contains(why, TenantPlanCheck.WhyNotImageOnly(step));
        Assert.False(TenantPlanCheck.IsImageOnlyTaskDefinitionReplace(step));
    }

    [Fact]
    public void OnlyATaskDefinitionReplace_CanBeImageOnly_AndDeletesAlwaysCount()
    {
        var same = Inputs("x", "h1", null);
        Assert.False(TenantPlanCheck.IsImageOnlyTaskDefinitionReplace(Replace(same, same, OperationType.Update)));
        Assert.False(TenantPlanCheck.IsImageOnlyTaskDefinitionReplace(Replace(same, same, type: "aws:ecs/service:Service")));

        var steps = new[]
        {
            Replace(same, same),                                                  // image-only: left out
            Replace(same, null, OperationType.Delete),                            // a delete: counted
            Replace(same, same, type: "aws:lb/targetGroup:TargetGroup"),          // another replace: counted
            Replace(same, same, OperationType.Update),                            // an update: never destructive
        };
        Assert.Equal(2, TenantPlanCheck.Destructive(steps).Count);
    }

    // ---- the plan summary (P3 D) ----

    [Fact]
    public void ThePlanSummary_TakesImageOnlyReplacesOut_AndNamesWhatStaysCounted()
    {
        var same = Inputs("x", "h1", null);
        var steps = new[]
        {
            Replace(same, same),                                                  // image-only
            Replace(same, Inputs("x", "h2", null)),                               // definitions change: counted
            Replace(same, same, type: "aws:lb/targetGroup:TargetGroup"),          // another replace: counted
        };
        var plan = PlanSummary.From("scu-mp-dev",
            new Dictionary<OperationType, int> { [OperationType.Same] = 40, [OperationType.Update] = 2, [OperationType.Replace] = 3 },
            steps, ["refused: x"]);

        Assert.Equal("scu-mp-dev", plan.Stack);
        Assert.Equal(new Dictionary<string, int> { ["update"] = 2, ["replace"] = 3 }, plan.Changes);
        Assert.Equal(1, plan.ImageOnlyReplaces);
        Assert.Equal(2, plan.Replaces);
        Assert.True(plan.Destructive);
        Assert.Equal(["aws:ecs/taskDefinition:TaskDefinition", "aws:lb/targetGroup:TargetGroup"], plan.DestructiveSteps.Select(s => s.Type));
        Assert.Equal("its container definitions change apart from the image", Assert.Single(plan.CountedTaskDefinitionReplaces).Why);
        Assert.Equal(["refused: x"], plan.DigestRefusals);
    }

    [Fact]
    public void AProviderVersionSwap_IsNotNamedAsDestructive()
    {
        // As dev's foundation planned it on 2026-11-06: the summary counts two updates and no provider step.
        var steps = new[]
        {
            new PlannedStep(OperationType.Create, "pulumi:providers:aws", "urn:pulumi:scu-dev::lz-scu::pulumi:providers:aws::default_7_53_0", null, null),
            new PlannedStep(OperationType.Delete, "pulumi:providers:aws", "urn:pulumi:scu-dev::lz-scu::pulumi:providers:aws::default_7_52_1", null, null),
        };
        var plan = PlanSummary.From("scu-dev", new Dictionary<OperationType, int> { [OperationType.Update] = 2 }, steps);
        Assert.False(plan.Destructive);
        Assert.Empty(plan.DestructiveSteps);
    }

    [Fact]
    public void UnderImageDeployPulumi_AnImageOnlyReplace_CountsLikeAnyOther()
    {
        // P3 J4: where every image deploy lands in the tenant state, a full plan has no image step, so no replace is exempt.
        var step = Replace(Inputs($"{Repo}@{Other}", "h1", "2026-10-20T21:00:00Z"), Inputs($"{Repo}@{Running}", "h1", "2026-10-20T22:12:00Z"));
        Assert.Single(TenantPlanCheck.Destructive(new[] { step }, imageOnlyExempt: false));

        var plan = PlanSummary.From("s", new Dictionary<OperationType, int> { [OperationType.Replace] = 1 }, [step], imageOnlyExempt: false);
        Assert.True(plan.Destructive);
        Assert.Equal(1, plan.Replaces);
        Assert.Equal(0, plan.ImageOnlyReplaces);
        Assert.Single(plan.DestructiveSteps);
        Assert.Equal(TenantPlanCheck.NotExempt, Assert.Single(plan.CountedTaskDefinitionReplaces).Why);
    }

    [Fact]
    public void ARollThatOnlyMovesTheImage_IsNotDestructive()
    {
        var same = Inputs("x", "h1", null);
        var plan = PlanSummary.From("s", new Dictionary<OperationType, int> { [OperationType.Replace] = 1 }, [Replace(same, same)]);
        Assert.True(plan.HasChanges);
        Assert.False(plan.Destructive);
        Assert.Empty(plan.DestructiveSteps);
        Assert.Equal(0, plan.Replaces);
    }

    [Fact]
    public void WithoutSteps_EveryReplaceAndDeleteCounts_AndNoChangeIsNone()
    {
        var plan = PlanSummary.From("s", new Dictionary<OperationType, int> { [OperationType.Delete] = 1, [OperationType.Same] = 5 });
        Assert.True(plan.Destructive);
        Assert.Equal(1, plan.Deletes);

        var none = PlanSummary.From("s", new Dictionary<OperationType, int> { [OperationType.Same] = 5 });
        Assert.False(none.HasChanges);
        Assert.False(none.Destructive);
        Assert.False(PlanSummary.From("s", null).Destructive);
    }

    [Fact]
    public void ThePrintedVerdict_IsTheSummarys()
    {
        var same = Inputs("x", "h1", null);
        Assert.False(SystemDeployment.PrintPreviewSummary(new Dictionary<OperationType, int> { [OperationType.Replace] = 1 }, [Replace(same, same)]));
        Assert.True(SystemDeployment.PrintPreviewSummary(new Dictionary<OperationType, int> { [OperationType.Replace] = 1 }));
    }

    [Fact]
    public void TheSummaryIsWrittenInCamelCase_WithItsSchema()
    {
        var json = System.Text.Json.JsonDocument.Parse(
            PlanSummary.From("s", new Dictionary<OperationType, int> { [OperationType.Delete] = 1 }).ToJson()).RootElement;
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.True(json.GetProperty("destructive").GetBoolean());
        Assert.Equal(1, json.GetProperty("changes").GetProperty("delete").GetInt32());
        Assert.Equal(0, json.GetProperty("destructiveSteps").GetArrayLength());
        Assert.Equal("s", json.GetProperty("stack").GetString());
    }

    [Fact]
    public void ThePlanFile_IsDestructiveWhenAnyStackIs_AndWritesWhereItIsTold()
    {
        var quiet = PlanSummary.From("scu-dev", new Dictionary<OperationType, int> { [OperationType.Same] = 3 });
        var loud = PlanSummary.From("scu-mp-dev", new Dictionary<OperationType, int> { [OperationType.Delete] = 1 });
        Assert.False(new PlanFile("previewsystem", [quiet]).Destructive);
        Assert.False(new PlanFile("previewsystem", [quiet]).HasChanges);
        Assert.True(new PlanFile("previewtenant", [quiet, loud]).Destructive);

        var path = Path.Combine(Path.GetTempPath(), $"lz-plan-{Guid.NewGuid():N}", "plans", "tenant.json");
        try
        {
            new PlanFile("previewtenant", [quiet, loud]).WriteTo(path);
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            Assert.Equal("previewtenant", json.GetProperty("command").GetString());
            Assert.True(json.GetProperty("destructive").GetBoolean());
            Assert.Equal(["scu-dev", "scu-mp-dev"], json.GetProperty("plans").EnumerateArray().Select(p => p.GetProperty("stack").GetString()));
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true); }
    }

    // ---- the digest the plan pins ----

    private static readonly Dictionary<string, RunningServiceImage> RunningServices = new()
    {
        [Family] = new RunningServiceImage("aiphost", Running, "2026-10-20T22:12:00Z"),
    };

    [Fact]
    public void APlanThatKeepsTheRunningDigest_IsNotRefused()
        => Assert.Empty(TenantPlanCheck.DigestRefusals(new[] { Replace(Inputs("a", "h", null), Inputs("b", "h", null)) },
            RunningServices, new Dictionary<string, string> { [Family] = $"{Repo}@{Running}" }));

    [Fact]
    public void APlanThatPinsAnotherDigest_IsRefused_NamingBoth()
    {
        var refusals = TenantPlanCheck.DigestRefusals(new[] { Replace(Inputs("a", "h", null), Inputs("b", "h", null)) },
            RunningServices, new Dictionary<string, string> { [Family] = $"{Repo}@{Other}" });
        var r = Assert.Single(refusals);
        Assert.Contains(Other, r);
        Assert.Contains(Running[..19], r);
    }

    [Fact]
    public void APlanForAnImageTheProgramNeverDeclared_IsRefused()
        => Assert.Contains("never declared", Assert.Single(TenantPlanCheck.DigestRefusals(
            new[] { Replace(null, Inputs("b", "h", null), OperationType.Create) }, RunningServices, new Dictionary<string, string>())));

    [Fact]
    public void APlanThatLeavesTheDefinitionAlone_OrNamesAServiceNotRunning_IsNotChecked()
    {
        var none = new Dictionary<string, string>();
        Assert.Empty(TenantPlanCheck.DigestRefusals(new[] { Replace(Inputs("a", "h", null), Inputs("a", "h", null), OperationType.Update) }, RunningServices, none));
        var otherFamily = Inputs("b", "h", null);
        otherFamily["family"] = "scu-mp-other";
        Assert.Empty(TenantPlanCheck.DigestRefusals(new[] { Replace(Inputs("a", "h", null), otherFamily) }, RunningServices, none));
    }

    // ---- after applying ----

    private static readonly RunningServiceImage Before = new("aiphost", Running, "2026-10-20T22:12:00Z");

    [Fact]
    public void TheSameDigestAfterwards_IsUnchanged()
        => Assert.Equal(TenantPlanCheck.AfterApply.Unchanged, TenantPlanCheck.JudgeAfterApply(Family, Before, Running, null).Verdict);

    [Fact]
    public void AnOlderBuildAfterwards_IsARevert()
    {
        var (verdict, detail) = TenantPlanCheck.JudgeAfterApply(Family, Before, Other, "2026-10-20T21:09:00Z");
        Assert.Equal(TenantPlanCheck.AfterApply.Older, verdict);
        Assert.Contains("OLDER", detail);
    }

    [Fact]
    public void ANewerBuildAfterwards_IsNewer()
        => Assert.Equal(TenantPlanCheck.AfterApply.Newer, TenantPlanCheck.JudgeAfterApply(Family, Before, Other, "2026-10-20T23:00:00Z").Verdict);

    [Theory]
    [InlineData(null)]
    [InlineData("not a time")]
    public void AnotherDigestWhoseBuildTimeIsUnknown_CannotBeOrdered(string? builtAt)
        => Assert.Equal(TenantPlanCheck.AfterApply.Unordered, TenantPlanCheck.JudgeAfterApply(Family, Before, Other, builtAt).Verdict);
}
