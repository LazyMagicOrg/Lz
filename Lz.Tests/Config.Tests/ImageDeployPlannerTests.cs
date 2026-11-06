using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer's image branch through Pulumi (DecoupledCd.md §12 P3 J, stage J3): planned only under Pipeline.ImageDeploy: pulumi;
/// Prepare -> ChooseConfig -> VerifyImageTooling -> Deploy (`lz updateimage` as the tooling task) -> VerifyRollout; the
/// targeted-update role; ChooseConfig naming the config last deployed; and the validator's requirements.
/// </summary>
public class ImageDeployPlannerTests
{
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";

    private static SystemConfig Dev(string? imageDeploy = PipelineConfig.ImageDeployPulumi)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "config", "tooling" });
        c.State = new StateConfig
        {
            Backend = "s3://scu-dev-pulumi-state-4df6-b9c6?region=us-west-2",
            SecretsProvider = "awskms://alias/scu-dev-pulumi-key-4df6-b9c6",
        };
        c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "config", Artifacts = new List<string> { "config" } });
        c.Pipeline.Repositories.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        c.Pipeline.Infrastructure = PipelineConfig.InfrastructureDeploy;
        c.Pipeline.EnforceSignatures = true;
        c.Pipeline.DeployOnBuildRecord = true;
        c.Pipeline.ImageDeploy = imageDeploy;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config) => DeployerPlanner.Plan(config, TargetAccount,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        infraInputs: new DeployerInfraInputs("subnet-0abc", "sg-0abc"));

    private static JsonElement States(PipelineDeployer plan) => JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");
    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();
    private static JsonElement Sid(string policy, string sid) => Statements(policy).Single(s => s.GetProperty("Sid").GetString() == sid);

    [Fact]
    public void UnderEcs_TheImageBranchIsWhatItWas()
    {
        var plan = Plan(Dev(PipelineConfig.ImageDeployEcs));
        Assert.Null(plan.ImageDeploy);
        var states = States(plan);
        Assert.Equal("Deploy", states.GetProperty("Prepare").GetProperty("Next").GetString());
        Assert.Equal("arn:aws:states:::aws-sdk:ecs:updateService", states.GetProperty("Deploy").GetProperty("Resource").GetString());
        Assert.False(states.TryGetProperty("ChooseConfig", out _));
        Assert.DoesNotContain(DeployerEnvironment.ImageDeploy, plan.Functions.Single(f => f.Handler == DeployerHandlers.Prepare).Environment.Keys);
        Assert.Equal(DeployerPlanner.Plan(Dev(null), TargetAccount, triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }),
            clientInputs: DeployerClientPlanTests.Distributions, infraInputs: new DeployerInfraInputs("subnet-0abc", "sg-0abc")).Definition, plan.Definition);
    }

    [Fact]
    public void UnderPulumi_PrepareGoesToChooseConfigThenTheToolingTaskThenVerifyRollout()
    {
        var plan = Plan(Dev());
        var states = States(plan);
        Assert.Equal("ChooseConfig", states.GetProperty("Prepare").GetProperty("Next").GetString());
        Assert.Equal("VerifyImageTooling", states.GetProperty("ChooseConfig").GetProperty("Next").GetString());
        Assert.Equal("$.config", states.GetProperty("ChooseConfig").GetProperty("ResultPath").GetString());
        Assert.Equal("$.config", states.GetProperty("VerifyImageTooling").GetProperty("Parameters").GetProperty("state").GetProperty("verified.$").GetString());
        Assert.Equal("Deploy", states.GetProperty("VerifyImageTooling").GetProperty("Next").GetString());

        var deploy = states.GetProperty("Deploy");
        Assert.Equal("arn:aws:states:::ecs:runTask.sync", deploy.GetProperty("Resource").GetString());
        Assert.Equal("VerifyRollout", deploy.GetProperty("Next").GetString());
        Assert.False(deploy.TryGetProperty("Retry", out _));
        Assert.Equal("$.deployResult", deploy.GetProperty("ResultPath").GetString());   // never over Prepare's $.deploy
        var p = deploy.GetProperty("Parameters");
        Assert.Equal("$.tooling.imageTaskDefinitionArn", p.GetProperty("TaskDefinition.$").GetString());
        var command = p.GetProperty("Overrides").GetProperty("ContainerOverrides")[0].GetProperty("Command.$").GetString()!;
        Assert.StartsWith("States.Array('updateimage', '--env', 'dev'", command);
        Assert.Contains("'--digest', $.verified.digest", command);
        Assert.Contains("'--lease-holder', States.Format('deployer:{}', $$.Execution.Name)", command);
        Assert.Contains("'--bundle-sha256', $.config.identity.sha256", command);
        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));

        Assert.Equal(PipelineConfig.ImageDeployPulumi, plan.Functions.Single(f => f.Handler == DeployerHandlers.Prepare).Environment[DeployerEnvironment.ImageDeploy]);
        var verifyTooling = plan.Functions.Single(f => f.Handler == DeployerHandlers.VerifyTooling);
        Assert.Equal("scu-dev-deployer-image", verifyTooling.Environment[ToolingSettings.ImageFamilyVariable]);
    }

    [Fact]
    public void TheTargetedUpdateRole_WritesOnlyTheTenantStacksState_TheRevisionAndService_TheInputsAndLeases()
    {
        var d = Plan(Dev()).ImageDeploy!;
        Assert.Equal("scu-dev-deployer-image-task", d.TaskRoleName);
        Assert.Equal(new[] { InfraPlanPlanner.ReadOnlyAccess }, d.TaskRoleManagedPolicies);
        var state = Sid(d.TaskRolePolicy, "WriteOnlyTheTenantStacksState").GetProperty("Resource").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("arn:aws:s3:::scu-dev-pulumi-state-4df6-b9c6/.pulumi/stacks/lz-scu/scu-mp-dev.json", state);
        Assert.Contains("arn:aws:s3:::scu-dev-pulumi-state-4df6-b9c6/.pulumi/locks/lz-scu/scu-mp-dev/*", state);
        Assert.DoesNotContain(state, r => r!.Contains("/scu-dev.json"));          // never the foundation's state
        Assert.Equal(new[] { "ecs:UpdateService" }, Sid(d.TaskRolePolicy, "UpdateTheServices").GetProperty("Action").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal($"arn:aws:dynamodb:us-west-2:{TargetAccount}:table/scu", Sid(d.TaskRolePolicy, "NoTableItemsButTheLeases").GetProperty("NotResource").GetString());
        Assert.Equal("Deny", Sid(d.TaskRolePolicy, "NoSecretValues").GetProperty("Effect").GetString());
        Assert.Equal($"arn:aws:s3:::{Evidence}/deploys/config/scutara/scutara/*",
            Sid(Plan(Dev()).Functions.Single(f => f.Handler == DeployerHandlers.ChooseConfig).Policy, "ReadTheConfigDeploys").GetProperty("Resource").GetString());
    }

    [Fact]
    public void TheMachine_RunsTheImageFamilyInItsCluster_AndTheBuildAccountLetsTheRoleReadConfigBundles()
    {
        var plan = Plan(Dev());
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:task-definition/scu-dev-deployer-image:*",
            Sid(plan.RolePolicy, "RunTheImageTaskInItsCluster").GetProperty("Resource").GetString());
        var readers = PipelineBootstrapPlanner.Plan(Dev(), "147440642635").ArtifactReadGrant!
            .Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsConfigBundlesDev")["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.AsArray()
            .Select(r => r!.GetValue<string>());
        Assert.Contains($"arn:aws:iam::{TargetAccount}:role/scu-dev-deployer-image-task", readers);
    }

    // ---- ChooseConfig ----

    private sealed class Keys(params string[] keys) : IRecordKeys
    {
        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, string startAfter)
            => Task.FromResult<IReadOnlyList<string>>(keys.Where(k => k.StartsWith(prefix) && string.CompareOrdinal(k, startAfter) > 0).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    private sealed class Records(Dictionary<string, string> objects) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key) => Task.FromResult(objects.TryGetValue(key, out var j) ? new StoredRecord(j, "v") : null);
    }

    private static string ConfigEvidence(string recordedAt, string tooling, string version) => new JsonObject
    {
        ["recordedAt"] = recordedAt,
        ["verified"] = new JsonObject
        {
            ["class"] = "config",
            ["identity"] = new JsonObject { ["bucket"] = "scu-artifacts-4df6-b9c6", ["key"] = "config/scutara/scutara/x.zip", ["versionId"] = version, ["sha256"] = new string('a', 64) },
            ["tooling"] = new JsonObject { ["digest"] = tooling, ["lz"] = "0.13.172" },
        },
    }.ToJsonString();

    [Fact]
    public async Task ChooseConfig_NamesTheConfigLastDeployed_ByWhenItWasRecorded()
    {
        const string prefix = "deploys/config/scutara/scutara/";
        var keys = new Keys(prefix + "deploy-1.json", prefix + "req-2.json", prefix + "req-3.json");
        var records = new Records(new()
        {
            [prefix + "deploy-1.json"] = ConfigEvidence("2026-11-06T15:15:00Z", "sha256:" + new string('1', 64), "v1"),
            [prefix + "req-2.json"] = ConfigEvidence("2026-11-06T20:10:00Z", "sha256:" + new string('2', 64), "v2"),
            [prefix + "req-3.json"] = ConfigEvidence("2026-11-06T17:45:00Z", "sha256:" + new string('3', 64), "v3"),
        });
        var chosen = await ChooseConfigStep.RunAsync(Evidence, prefix, keys, records);
        Assert.Equal("v2", chosen["identity"]!["versionId"]!.GetValue<string>());
        Assert.Equal("sha256:" + new string('2', 64), chosen["tooling"]!["digest"]!.GetValue<string>());
        Assert.Equal("config", chosen["class"]!.GetValue<string>());
        Assert.Equal(prefix + "req-2.json", chosen["evidence"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChooseConfig_WithNoConfigDeployed_Refuses()
        => await Assert.ThrowsAsync<NoConfigDeployed>(() => ChooseConfigStep.RunAsync(Evidence, "deploys/config/scutara/scutara/", new Keys(), new Records(new())));

    // ---- validation ----

    [Fact]
    public void Pulumi_NeedsTheInfrastructureBranchTheInputsAndTheTrigger()
    {
        var c = Dev();
        c.Pipeline!.Infrastructure = PipelineConfig.InfrastructureOff;
        var errors = new List<string>();
        c.SystemSuffix = "abcd-1234"; c.Profile = "p"; c.Rollback = new RollbackConfig { PinImageDigest = true };
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(c, "test.yaml"));
        Assert.Contains("Pipeline.ImageDeploy is pulumi", ex.Message);
    }
}
