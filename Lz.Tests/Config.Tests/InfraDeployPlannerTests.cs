using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The infrastructure branch's deploy half in the deployer's plan (DecoupledCd.md P3 D): planned under Pipeline.Infrastructure:
/// deploy and nothing of it under plan; ReadPlan then ApproveInfrastructure, DeployInfrastructure, VerifyInfrastructure,
/// RecordInfrastructure; the infrastructure role's AdministratorAccess and its Deny of every pipeline resource; the evidence store
/// holding that role to applies/; the build account's grant.
/// </summary>
public class InfraDeployPlannerTests
{
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private static readonly DeployerInfraInputs Network = new("subnet-0abc", "sg-0abc");

    private static SystemConfig Dev(string infrastructure = PipelineConfig.InfrastructureDeploy)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "config", "tooling" });
        c.State = new StateConfig
        {
            Backend = "s3://scu-dev-pulumi-state-4df6-b9c6?region=us-west-2",
            SecretsProvider = "awskms://alias/scu-dev-pulumi-key-4df6-b9c6",
        };
        c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "config", Artifacts = new List<string> { "config" } });
        c.Pipeline.Repositories.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        c.Pipeline.Infrastructure = infrastructure;
        return c;
    }

    /// <summary>Dev as it runs: in a chain, with the trigger, alerts and Gate 2, so every pipeline resource is in the plan.</summary>
    private static PipelineDeployer DevAsItRuns(string infrastructure = PipelineConfig.InfrastructureDeploy, bool markConfig = false)
    {
        var c = DevAsItRunsConfig(infrastructure);
        c.Pipeline!.MarkConfig = markConfig;
        return DeployerPlanner.Plan(c, TargetAccount,
            triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
            gate2Inputs: new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc" }),
            infraInputs: Network);
    }

    private static SystemConfig DevAsItRunsConfig(string infrastructure = PipelineConfig.InfrastructureDeploy)
    {
        var c = Dev(infrastructure);
        c.Pipeline!.DeployOnBuildRecord = true;
        c.Pipeline.Alerts = true;
        c.Pipeline.Gate2 = "on";
        c.Pipeline.Gate2BrowserTests = "Scutara/ScutaraE2ETests";
        c.Pipeline.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        c.Pipeline.Successor = new PipelineNeighbour("test", "456687462751", "scu-test-deploy-evidence-d5e0-b27f");
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config)
        => DeployerPlanner.Plan(config, TargetAccount, clientInputs: DeployerClientPlanTests.Distributions, infraInputs: Network);

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);
    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();
    private static JsonElement Sid(string policy, string sid) => Statements(policy).Single(s => s.GetProperty("Sid").GetString() == sid);
    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };
    private static JsonElement States(PipelineDeployer plan) => JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");
    private static string Role(string name) => $"arn:aws:iam::{TargetAccount}:role/{name}";

    private static bool Covered(IEnumerable<string> patterns, string arn)
        => patterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(arn, "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*") + "$"));

    private static string Deny(PipelineDeployer plan) => InfraDeployPlanner.PipelineDeny(plan, "us-west-2", TargetAccount, "scu", "dev");

    // ---- P3 I1: a config execution is gated, and its set may name the config ----

    [Fact]
    public void UnderGate2_AConfigExecution_GoesOnFromRecordToGate2_AndHasNoEndOfItsOwn()
    {
        var states = States(DevAsItRuns());
        Assert.Equal("GatesThisClass", states.GetProperty("RecordInfrastructure").GetProperty("Next").GetString());
        Assert.False(states.TryGetProperty("InfrastructureDeployed", out _));
        Assert.Equal("Gate2Start", states.GetProperty("GatesThisClass").GetProperty("Default").GetString());
    }

    private static List<MarkMember> MarkMembers(PipelineDeployer plan)
        => JsonSerializer.Deserialize<List<MarkMember>>(Function(plan, DeployerHandlers.MarkPassed).Environment[DeployerEnvironment.MarkMembers],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void UnderMarkConfig_EverySetNamesTheRootsConfig_AndWithoutItNone()
    {
        Assert.DoesNotContain(MarkMembers(DevAsItRuns()), m => m.Class == PassMarkerFormat.ConfigClass);
        var config = Assert.Single(MarkMembers(DevAsItRuns(markConfig: true)), m => m.Class == PassMarkerFormat.ConfigClass);
        Assert.Equal("Scutara/Scutara", config.Repo);
    }

    [Fact]
    public void MarkConfig_NeedsADeployedConfig_APassedGate_AndASuccessor()
    {
        var ok = DevAsItRunsConfig();
        ok.Pipeline!.MarkConfig = true;
        ok.Rollback = new RollbackConfig { PinImageDigest = true };
        ConfigValidator.Validate(ok, "dev.yaml");   // no throw

        foreach (var breakIt in new Action<PipelineConfig>[] { p => p.Successor = null, p => p.Gate2 = "provisioned", p => p.Infrastructure = PipelineConfig.InfrastructurePlan })
        {
            var c = DevAsItRunsConfig();
            c.Pipeline!.MarkConfig = true;
            c.Rollback = new RollbackConfig { PinImageDigest = true };
            breakIt(c.Pipeline);
            Assert.Contains("Pipeline.MarkConfig", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(c, "dev.yaml")).Message);
        }
    }

    // ---- plan only is unchanged ----

    [Fact]
    public void UnderPlan_NothingOfTheDeployHalfIsPlanned()
    {
        var plan = Plan(Dev(PipelineConfig.InfrastructurePlan));
        Assert.Null(plan.InfrastructureDeploy);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.ReadApply);
        Assert.Equal("Planned", States(plan).GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.False(States(plan).TryGetProperty("DeployInfrastructure", out _));
        Assert.DoesNotContain(ToolingSettings.DeployFamilyVariable, Function(plan, DeployerHandlers.VerifyTooling).Environment.Keys);
        Assert.DoesNotContain("deployer-infra", plan.RolePolicy);
        Assert.DoesNotContain(plan.EvidenceStorePolicy, s => s.ToJsonString().Contains("applies/"));
    }

    // ---- the machine ----

    [Fact]
    public void UnderDeploy_ReadPlanGoesOnToApproveDeployVerifyAndRecord()
    {
        var plan = Plan(Dev());
        var states = States(plan);
        Assert.Equal("ApproveInfrastructure", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.False(states.TryGetProperty("Planned", out _));
        Assert.Equal("Pass", states.GetProperty("ApproveInfrastructure").GetProperty("Type").GetString());
        Assert.Equal("DeployInfrastructure", states.GetProperty("ApproveInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("VerifyInfrastructure", states.GetProperty("DeployInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("RecordInfrastructure", states.GetProperty("VerifyInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("InfrastructureDeployed", states.GetProperty("RecordInfrastructure").GetProperty("Next").GetString());
        Assert.Equal("Succeed", states.GetProperty("InfrastructureDeployed").GetProperty("Type").GetString());

        // VERIFY AND RECORD ARE THE FUNCTIONS THEY NAME: ReadApply, then the same Record every deploy writes evidence with.
        Assert.EndsWith($":function:{plan.InfrastructureDeploy!.ReadApplyFunctionName}", states.GetProperty("VerifyInfrastructure").GetProperty("Resource").GetString());
        Assert.Equal("$.rollout", states.GetProperty("VerifyInfrastructure").GetProperty("ResultPath").GetString());
        var record = Function(plan, DeployerHandlers.Record).Name;
        Assert.EndsWith($":function:{record}", states.GetProperty("RecordInfrastructure").GetProperty("Resource").GetString());

        // A FAILED DEPLOY TASK STILL GOES TO VERIFY, which decides from its result; it is never retried.
        var deploy = states.GetProperty("DeployInfrastructure");
        Assert.False(deploy.TryGetProperty("Retry", out _));
        var catches = deploy.GetProperty("Catch").EnumerateArray()
            .Select(c => (Strings(c.GetProperty("ErrorEquals")).Single(), c.GetProperty("Next").GetString())).ToList();
        Assert.Equal(new[] { ("States.TaskFailed", "VerifyInfrastructure"), ("States.ALL", "RecordFailure") }, catches);
        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    [Fact]
    public void TheDeployState_RunsTheDeployRevision_WithTheBundleThePlanAndThisExecutionsResultKey()
    {
        var p = States(Plan(Dev())).GetProperty("DeployInfrastructure").GetProperty("Parameters");
        Assert.Equal("$.tooling.deployTaskDefinitionArn", p.GetProperty("TaskDefinition.$").GetString());
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:cluster/scu-dev-deployer-tooling", p.GetProperty("Cluster").GetString());
        var command = p.GetProperty("Overrides").GetProperty("ContainerOverrides")[0].GetProperty("Command.$").GetString()!;
        Assert.StartsWith("States.Array('infradeploy', '--env', 'dev'", command);
        Assert.Contains("'--bundle-sha256', $.verified.identity.sha256", command);
        Assert.Contains($"'--evidence-bucket', '{Evidence}'", command);
        Assert.Contains("'--plan-key', States.Format('plans/{}.json', $$.Execution.Name)", command);
        Assert.Contains("'--apply-key', States.Format('applies/{}.json', $$.Execution.Name)", command);
        // THE OVERRIDE REACHES THE TASK ONLY AS READPLAN PASSED IT: destructive is true only for a plan an input allowed.
        Assert.Contains("'--allow-destructive', States.Format('{}', $.planned.destructive)", command);
    }

    [Fact]
    public void TheMachine_RunsTheDeployFamilyInItsCluster_AndPassesTheInfrastructureRoleToEcsTasksOnly()
    {
        var plan = Plan(Dev());
        var run = Sid(plan.RolePolicy, "RunTheDeployTaskInItsCluster");
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:task-definition/scu-dev-deployer-infra:*", run.GetProperty("Resource").GetString());
        var pass = Sid(plan.RolePolicy, "PassTheDeployTasksRole");
        Assert.Equal(Role("scu-dev-deployer-infra-task"), pass.GetProperty("Resource").GetString());
        Assert.Equal("ecs-tasks.amazonaws.com", pass.GetProperty("Condition").GetProperty("StringEquals").GetProperty("iam:PassedToService").GetString());
    }

    [Fact]
    public void VerifyTooling_RegistersTheDeployFamilyToo_OnTheInfrastructureRole()
    {
        var plan = Plan(Dev());
        var f = Function(plan, DeployerHandlers.VerifyTooling);
        Assert.Equal("scu-dev-deployer-infra", f.Environment[ToolingSettings.DeployFamilyVariable]);
        Assert.Equal(Role("scu-dev-deployer-infra-task"), f.Environment[ToolingSettings.DeployTaskRoleVariable]);
        Assert.Equal(Role("scu-dev-deployer-infra-task"), Sid(f.Policy, "PassOnlyTheDeployTasksRole").GetProperty("Resource").GetString());
    }

    [Fact]
    public void ReadApply_ReadsResultsOnly()
    {
        var f = Function(Plan(Dev()), DeployerHandlers.ReadApply);
        Assert.Equal($"arn:aws:s3:::{Evidence}/applies/*", Sid(f.Policy, "ReadTheResults").GetProperty("Resource").GetString());
        Assert.DoesNotContain("PutObject", f.Policy);
        Assert.Equal(Evidence, f.Environment[DeployerEnvironment.EvidenceStore]);
    }

    // ---- the infrastructure role ----

    [Fact]
    public void TheInfrastructureRole_AppliesWithAdministratorAccess_AndReadsOnlyItsBundlesAndPlans_AndPutsOnlyItsResult()
    {
        var d = Plan(Dev()).InfrastructureDeploy!;
        Assert.Equal("scu-dev-deployer-infra-task", d.TaskRoleName);
        Assert.Equal(new[] { InfraDeployPlanner.AdministratorAccess }, d.TaskRoleManagedPolicies);
        Assert.Contains("ecs-tasks.amazonaws.com", d.TaskTrustPolicy);
        Assert.Equal("arn:aws:s3:::scu-artifacts-4df6-b9c6/config/scutara/scutara/*", Sid(d.TaskRolePolicy, "ReadConfigBundles").GetProperty("Resource").GetString());
        Assert.Equal($"arn:aws:s3:::{Evidence}/plans/*", Sid(d.TaskRolePolicy, "ReadTheApprovedPlan").GetProperty("Resource").GetString());
        Assert.Equal($"arn:aws:s3:::{Evidence}/applies/*", Sid(d.TaskRolePolicy, "WriteTheResult").GetProperty("Resource").GetString());
    }

    [Fact]
    public void TheDeny_NamesEveryFunctionAndRoleTheDeployerPlanCreates()
    {
        var plan = DevAsItRuns();
        var deny = Deny(plan);
        // COVERED AS IAM MATCHES: a resource pattern's * matches anything, the function's qualified ARNs included.
        var functions = Strings(Sid(deny, "NoPipelineFunctions").GetProperty("Resource")).ToList();
        foreach (var f in plan.Functions)
        {
            Assert.True(Covered(functions, $"arn:aws:lambda:us-west-2:{TargetAccount}:function:{f.Name}"), f.Name);
            Assert.True(Covered(functions, $"arn:aws:lambda:us-west-2:{TargetAccount}:function:{f.Name}:live"), $"{f.Name}:live");
        }
        Assert.Contains(plan.Functions, f => !f.Name.StartsWith("scu-dev-deployer"));   // the hook: a name the prefix misses
        var roles = Strings(Sid(deny, "NoPipelineRoles").GetProperty("Resource")).ToList();
        foreach (var r in plan.Functions.Select(f => f.RoleName).Append(plan.RoleName).Append(plan.HookInvokerRoleName)
                     .Append(plan.Infrastructure!.ExecutionRoleName).Append(plan.Infrastructure.TaskRoleName).Append(plan.InfrastructureDeploy!.TaskRoleName))
            Assert.True(Covered(roles, Role(r)), r);
        Assert.False(Covered(roles, Role("scu-dev-aiphost-task")));   // the topology's own roles are not the pipeline's
        Assert.Equal("iam:*", Strings(Sid(deny, "NoPipelineRoles").GetProperty("Action")).Single());
    }

    [Fact]
    public void TheDeny_NamesTheTriggerAlertsGate2AndTheToolingResources()
    {
        var plan = DevAsItRuns();
        var deny = Deny(plan);
        Assert.Contains(plan.Trigger!.RuleArn, Strings(Sid(deny, "NoPipelineRules").GetProperty("Resource")));
        Assert.Contains(plan.Trigger.BusArn, Strings(Sid(deny, "NoPipelineRules").GetProperty("Resource")));
        Assert.Contains(plan.Alerts!.FailedDeployRuleArn, Strings(Sid(deny, "NoPipelineRules").GetProperty("Resource")));
        Assert.Contains($"arn:aws:events:us-west-2:{TargetAccount}:rule/StepFunctionsGetEventsFor*", Strings(Sid(deny, "NoPipelineRules").GetProperty("Resource")));
        Assert.Contains(plan.Alerts.TopicArn, Strings(Sid(deny, "NoPipelineTopics").GetProperty("Resource")));
        Assert.Contains(plan.Trigger.DeadLetterQueueArn, Strings(Sid(deny, "NoPipelineQueues").GetProperty("Resource")));
        Assert.Equal(plan.Gate2!.ProjectArn, Sid(deny, "NoGate2Project").GetProperty("Resource").GetString());
        Assert.Contains($"arn:aws:ecr:us-west-2:{TargetAccount}:repository/{plan.Infrastructure!.ToolingRepository}",
            Strings(Sid(deny, "NoPipelineRepositoryWrites").GetProperty("Resource")));
        foreach (var r in plan.ImageRepositories)
            Assert.Contains($"arn:aws:ecr:us-west-2:{TargetAccount}:repository/{r}", Strings(Sid(deny, "NoPipelineRepositoryWrites").GetProperty("Resource")));
        Assert.Contains(plan.Infrastructure.ClusterArn, Strings(Sid(deny, "NoToolingClusterOrTaskFamilies").GetProperty("Resource")));
        Assert.Equal(plan.Infrastructure.Network.Name,
            Sid(deny, "NoToolingNetworkChanges").GetProperty("Condition").GetProperty("StringEquals").GetProperty($"aws:ResourceTag/{InfraPlanNetwork.Tag}").GetString());
        Assert.Contains($"arn:aws:ssm:us-west-2:{TargetAccount}:parameter/scu/dev/pipeline/*", Strings(Sid(deny, "NoPipelineParameters").GetProperty("Resource")));
    }

    [Fact]
    public void TheDeny_LeavesTheTopologysOwnIamAlone_AndRefusesStateMachinesAndOtherRoles()
    {
        var deny = Deny(DevAsItRuns());
        foreach (var s in Statements(deny))
        {
            // NOT THE DEPLOYER'S DENY: nothing here refuses IAM on every resource, which would refuse the topology's roles.
            if (s.TryGetProperty("Action", out var action) && Strings(action).Any(a => a.StartsWith("iam:")))
                Assert.DoesNotContain("*", Strings(s.GetProperty("Resource")));
        }
        Assert.Equal("*", Sid(deny, "NoStateMachines").GetProperty("Resource").GetString());
        Assert.Contains("sts:AssumeRole", Strings(Sid(deny, "NoOtherRoles").GetProperty("Action")));
        Assert.Contains("scheduler:*", Strings(Sid(deny, "NoSchedulesSigningOrRegistrySettings").GetProperty("Action")));
        Assert.Contains("ecr:PutReplicationConfiguration", Strings(Sid(deny, "NoSchedulesSigningOrRegistrySettings").GetProperty("Action")));

        // THE EVIDENCE STORE: everything but reading and its own put is denied; the store's policy holds the put to applies/.
        var evidence = Sid(deny, "NoEvidenceStoreChangesButItsResult");
        Assert.Equal(new[] { "s3:PutObject", "s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket" }, Strings(evidence.GetProperty("NotAction")));
        Assert.Contains($"arn:aws:s3:::{Evidence}", Strings(evidence.GetProperty("Resource")));
    }

    [Fact]
    public void TheRolesInlinePolicies_FitIam_WhichCountsThemTogether()
    {
        // IAM: a role's inline policies, together and without whitespace, at most 10,240 characters.
        var plan = DevAsItRuns();
        static int Size(string policy) => policy.Count(c => !char.IsWhiteSpace(c));
        var total = Size(Deny(plan)) + Size(plan.InfrastructureDeploy!.TaskRolePolicy);
        Assert.True(total <= 10_240, $"the infrastructure role's inline policies are {total} characters; IAM takes 10,240");
    }

    [Fact]
    public void TheEvidenceStore_HoldsTheInfrastructureRoleToApplies_PinnedOrNot()
    {
        foreach (var plan in new[] { Plan(Dev()), DevAsItRuns() })
        {
            var confine = plan.EvidenceStorePolicy.Single(s => s["Sid"]!.GetValue<string>() == "LzInfrastructureRolePutsOnlyApplies");
            Assert.Equal("Deny", confine["Effect"]!.GetValue<string>());
            Assert.Equal($"arn:aws:s3:::{Evidence}/applies/*", confine["NotResource"]!.GetValue<string>());
            Assert.Equal(Role("scu-dev-deployer-infra-task"), confine["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
        }
        var pin = DevAsItRuns().EvidenceStorePolicy.Single(s => s["Sid"]!.GetValue<string>() == "LzOnlyItsWritersPutApplies");
        Assert.Equal($"arn:aws:s3:::{Evidence}/applies/*", pin["Resource"]!.GetValue<string>());
        Assert.Equal(Role("scu-dev-deployer-infra-task"), pin["Condition"]!["ArnNotLike"]!["aws:PrincipalArn"]!.GetValue<string>());
    }

    // ---- the build account ----

    [Fact]
    public void TheBuildAccount_LetsTheInfrastructureRoleReadConfigBundles_UnderDeployOnly()
    {
        string[] Readers(SystemConfig config) => PipelineBootstrapPlanner.Plan(config, "147440642635").ArtifactReadGrant!
            .Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsConfigBundlesDev")["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!
            .AsArray().Select(r => r!.GetValue<string>()).ToArray();

        var config = Dev();
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role("scu-dev-deployer-plan-task"), Role("scu-dev-deployer-infra-task") }, Readers(config));
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role("scu-dev-deployer-plan-task") }, Readers(Dev(PipelineConfig.InfrastructurePlan)));
        Assert.Equal(Plan(config).InfrastructureDeploy!.TaskRoleName, InfraDeployPlanner.TaskRoleNameFor("scu", "dev"));
    }

    // ---- validation ----

    [Fact]
    public void Deploy_IsAKnownValue_AndPlansToo()
    {
        var p = new PipelineConfig { Infrastructure = PipelineConfig.InfrastructureDeploy };
        Assert.True(p.InfrastructurePlans);
        Assert.True(p.InfrastructureDeploys);
        Assert.False(new PipelineConfig { Infrastructure = PipelineConfig.InfrastructurePlan }.InfrastructureDeploys);
    }
}
