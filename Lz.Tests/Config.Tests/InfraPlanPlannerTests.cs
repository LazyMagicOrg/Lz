using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The infrastructure branch in the deployer's plan, plan only (DecoupledCd.md P3 D): planned under Pipeline.Infrastructure: plan
/// and nothing of it otherwise; VerifyTooling, Plan, ReadPlan, Planned after ByClass; what each role may do; the Plan task's
/// network, roles and command.
/// </summary>
public class InfraPlanPlannerTests
{
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private static readonly DeployerInfraInputs Network = new("subnet-0abc", "sg-0abc");

    private static SystemConfig Dev(string? infrastructure = PipelineConfig.InfrastructurePlan, bool rootRepos = true)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "config", "tooling" });
        c.State = new StateConfig
        {
            Backend = "s3://scu-dev-pulumi-state-4df6-b9c6?region=us-west-2",
            SecretsProvider = "awskms://alias/scu-dev-pulumi-key-4df6-b9c6",
        };
        if (rootRepos)
        {
            c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "config", Artifacts = new List<string> { "config" } });
            c.Pipeline.Repositories.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        }
        c.Pipeline!.Infrastructure = infrastructure;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config, DeployerInfraInputs? network = null)
        => DeployerPlanner.Plan(config, TargetAccount, clientInputs: DeployerClientPlanTests.Distributions, infraInputs: network ?? Network);

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);

    private static IEnumerable<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };

    private static JsonElement Sid(string policy, string sid) => Statements(policy).Single(s => s.GetProperty("Sid").GetString() == sid);

    private static JsonElement States(PipelineDeployer plan) => JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");

    // ---- absent ----

    [Theory]
    [InlineData(null)]
    [InlineData(PipelineConfig.InfrastructureOff)]
    public void WithoutTheBranch_ThePlanIsWhatItWas(string? infrastructure)
    {
        var off = Plan(Dev(infrastructure));
        var before = DeployerPlanner.Plan(DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "config", "tooling" }),
            TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);

        Assert.Null(off.Infrastructure);
        Assert.Equal(before.Definition, off.Definition);
        Assert.Equal(before.RolePolicy, off.RolePolicy);
        Assert.Equal(before.DenyPolicy, off.DenyPolicy);
        Assert.Equal(before.Functions.Select(f => (f.Name, f.Policy)), off.Functions.Select(f => (f.Name, f.Policy)));
        Assert.Equal(Function(before, DeployerHandlers.Verify).Environment, Function(off, DeployerHandlers.Verify).Environment);
        Assert.DoesNotContain(off.Functions, f => f.Handler == DeployerHandlers.VerifyTooling || f.Handler == DeployerHandlers.ReadPlan);
    }

    // ---- the machine ----

    [Fact]
    public void AConfigRecord_BranchesToVerifyToolingThenPlanThenReadPlanThenPlanned_AndNothingElse()
    {
        var plan = Plan(Dev());
        var states = States(plan);
        var choices = states.GetProperty("ByClass").GetProperty("Choices").EnumerateArray()
            .Select(c => (c.GetProperty("StringEquals").GetString(), c.GetProperty("Next").GetString())).ToList();
        Assert.Contains(("config", "VerifyTooling"), choices);
        Assert.Contains(("image", "Prepare"), choices);
        Assert.Equal("Plan", states.GetProperty("VerifyTooling").GetProperty("Next").GetString());
        Assert.Equal("ReadPlan", states.GetProperty("Plan").GetProperty("Next").GetString());
        Assert.Equal("Planned", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.Equal("Succeed", states.GetProperty("Planned").GetProperty("Type").GetString());

        // A FAILED TASK STILL GOES TO READPLAN, which decides from what it wrote; anything else is recorded as a failure.
        var catches = states.GetProperty("Plan").GetProperty("Catch").EnumerateArray()
            .Select(c => (Strings(c.GetProperty("ErrorEquals")).Single(), c.GetProperty("Next").GetString())).ToList();
        Assert.Equal(new[] { ("States.TaskFailed", "ReadPlan"), ("States.ALL", "RecordFailure") }, catches);
        Assert.Equal("RecordFailure", states.GetProperty("ReadPlan").GetProperty("Catch")[0].GetProperty("Next").GetString());

        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    [Fact]
    public void ThePlanState_RunsThePinnedRevisionInItsClusterAndNetwork_WithTheVerifiedBundleAndThisExecutionsPlanKey()
    {
        var plan = States(Plan(Dev())).GetProperty("Plan");
        Assert.Equal("arn:aws:states:::ecs:runTask.sync", plan.GetProperty("Resource").GetString());
        var p = plan.GetProperty("Parameters");
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:cluster/scu-dev-deployer-tooling", p.GetProperty("Cluster").GetString());
        Assert.Equal("$.tooling.taskDefinitionArn", p.GetProperty("TaskDefinition.$").GetString());
        var vpc = p.GetProperty("NetworkConfiguration").GetProperty("AwsvpcConfiguration");
        Assert.Equal("$.tooling.subnets", vpc.GetProperty("Subnets.$").GetString());
        Assert.Equal("ENABLED", vpc.GetProperty("AssignPublicIp").GetString());

        var command = p.GetProperty("Overrides").GetProperty("ContainerOverrides")[0].GetProperty("Command.$").GetString()!;
        Assert.StartsWith("States.Array('infraplan', '--env', 'dev', ", command);
        Assert.Contains("'--bundle-sha256', $.verified.identity.sha256", command);
        Assert.Contains("'--bundle-version', $.verified.identity.versionId", command);
        Assert.Contains($"'--plan-bucket', '{Evidence}'", command);
        Assert.Contains("'--plan-key', States.Format('plans/{}.json', $$.Execution.Name)", command);
        Assert.Equal(InfraPlanPlanner.PlanTimeoutSeconds, plan.GetProperty("TimeoutSeconds").GetInt32());
    }

    // ---- what each role may do ----

    [Fact]
    public void TheMachine_MayRunOnlyThePlanFamilyInItsCluster_FollowIt_AndPassOnlyItsTwoRoles()
    {
        var plan = Plan(Dev());
        var run = Sid(plan.RolePolicy, "RunThePlanTaskInItsCluster");
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:task-definition/scu-dev-deployer-plan:*", run.GetProperty("Resource").GetString());
        Assert.Equal($"arn:aws:ecs:us-west-2:{TargetAccount}:cluster/scu-dev-deployer-tooling",
            run.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("ecs:cluster").GetString());
        Assert.Equal(new[] { $"arn:aws:iam::{TargetAccount}:role/scu-dev-deployer-tooling-exec", $"arn:aws:iam::{TargetAccount}:role/scu-dev-deployer-plan-task" },
            Strings(Sid(plan.RolePolicy, "PassThePlanTasksRoles").GetProperty("Resource")));
        foreach (var handler in new[] { DeployerHandlers.VerifyTooling, DeployerHandlers.ReadPlan })
            Assert.Contains($"arn:aws:lambda:us-west-2:{TargetAccount}:function:{Function(plan, handler).Name}",
                Strings(Sid(plan.RolePolicy, "InvokeItsOwnFunctions").GetProperty("Resource")));
    }

    [Fact]
    public void TheDeny_LeavesOnlyTheEcsSyncRuleOpen()
    {
        var deny = Plan(Dev()).DenyPolicy;
        var rule = Sid(deny, "NorPutAnyRuleButTheSyncTasksManagedOnes");
        Assert.Equal(new[] { $"arn:aws:events:us-west-2:{TargetAccount}:rule/StepFunctionsGetEventsForECSTaskRule" }, Strings(rule.GetProperty("NotResource")));
    }

    [Fact]
    public void VerifyTooling_TrustsTheToolingProfileAlone_AndIsConfiguredWithTheNetworkTheApplyMade()
    {
        var plan = Plan(Dev());
        var f = Function(plan, DeployerHandlers.VerifyTooling);
        Assert.Equal(DeployerPackages.SignatureHook, f.Package);
        Assert.Equal("arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutara", f.Environment[ToolingSettings.TrustedProfilesVariable]);
        Assert.Equal("scu-4df6-b9c6-tooling", f.Environment[ToolingSettings.RepositoryVariable]);
        Assert.Equal("subnet-0abc", f.Environment[ToolingSettings.SubnetsVariable]);
        Assert.Equal("sg-0abc", f.Environment[ToolingSettings.SecurityGroupsVariable]);
        Assert.Equal($"arn:aws:ecr:us-west-2:{TargetAccount}:repository/scu-4df6-b9c6-tooling",
            Sid(f.Policy, "ReadTheToolingImageAndItsSignature").GetProperty("Resource").GetString());

        // A DRY RUN ON A NEW ACCOUNT plans placeholders, which VerifyTooling refuses.
        var dry = DeployerPlanner.Plan(Dev(), TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);
        Assert.StartsWith("<", Function(dry, DeployerHandlers.VerifyTooling).Environment[ToolingSettings.SubnetsVariable]);
    }

    [Fact]
    public void Verify_ReadsConfigAndToolingRecordsAndTheBundlesHead_Only()
    {
        var f = Function(Plan(Dev()), DeployerHandlers.Verify);
        Assert.Equal("tooling/scutara/scutara/", f.Environment[DeployerEnvironment.ToolingRecords]);
        Assert.Equal("scu-artifacts-4df6-b9c6", f.Environment[DeployerEnvironment.ArtifactStore]);
        Assert.Equal(new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/config/scutara/scutara/*", "arn:aws:s3:::scu-build-records-4df6-b9c6/tooling/scutara/scutara/*" },
            Strings(Sid(f.Policy, "ReadConfigAndToolingRecords").GetProperty("Resource")));
        Assert.Equal("arn:aws:s3:::scu-artifacts-4df6-b9c6/config/scutara/scutara/*", Sid(f.Policy, "HeadTheConfigBundleByVersion").GetProperty("Resource").GetString());
    }

    [Fact]
    public void ThePlanTaskRole_ReadsTheAccount_DecryptsOnlyTheStateKey_WritesOnlyUnderPlans_AndReadsNoData()
    {
        var r = Plan(Dev()).Infrastructure!;
        Assert.Equal(new[] { InfraPlanPlanner.ReadOnlyAccess }, r.TaskRoleManagedPolicies);
        Assert.Equal("scu-dev-deployer-plan-task", r.TaskRoleName);
        Assert.Equal("alias/scu-dev-pulumi-key-4df6-b9c6",
            Sid(r.TaskRolePolicy, "DecryptTheStateWithItsKey").GetProperty("Condition").GetProperty("ForAnyValue:StringEquals").GetProperty("kms:ResourceAliases").GetString());
        Assert.Equal($"arn:aws:s3:::{Evidence}/plans/*", Sid(r.TaskRolePolicy, "WriteThePlan").GetProperty("Resource").GetString());
        var writes = Statements(r.TaskRolePolicy).Where(s => s.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(s => Strings(s.GetProperty("Action"))).Where(a => !a.Contains(":Get") && a != "kms:Decrypt").ToList();
        Assert.Equal(new[] { "s3:PutObject" }, writes);
        var denied = Strings(Sid(r.TaskRolePolicy, "NoDataPlaneReads").GetProperty("Action")).ToList();
        Assert.Contains("dynamodb:Scan", denied);
        Assert.Contains("secretsmanager:GetSecretValue", denied);
        Assert.Equal("ecs-tasks.amazonaws.com",
            JsonDocument.Parse(r.TaskTrustPolicy).RootElement.GetProperty("Statement")[0].GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal(new InfraPlanNetwork("scu-dev-deployer-tooling", "10.254.0.0/24", "10.254.0.0/25", "us-west-2a"), r.Network);
    }

    [Fact]
    public void ReadPlan_ReadsPlansOnly()
    {
        var f = Function(Plan(Dev()), DeployerHandlers.ReadPlan);
        Assert.Equal($"arn:aws:s3:::{Evidence}/plans/*", Sid(f.Policy, "ReadThePlans").GetProperty("Resource").GetString());
        Assert.Equal(Evidence, f.Environment[DeployerEnvironment.EvidenceStore]);
    }

    // ---- dev as it runs: in a chain, with Gate 2 ----

    private static PipelineDeployer DevAsItRuns(string? infrastructure)
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
        return DeployerPlanner.Plan(c, TargetAccount,
            triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
            gate2Inputs: new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc" }),
            infraInputs: Network);
    }

    [Fact]
    public void InAChain_PlansIsPinnedToThePlanTasksRole_AndBothSyncRulesStayOpen()
    {
        var plan = DevAsItRuns(PipelineConfig.InfrastructurePlan);
        var pin = plan.EvidenceStorePolicy.Single(s => s["Sid"]!.GetValue<string>() == "LzOnlyItsWritersPutPlans");
        Assert.Equal($"arn:aws:s3:::{Evidence}/plans/*", pin["Resource"]!.GetValue<string>());
        Assert.Equal($"arn:aws:iam::{TargetAccount}:role/scu-dev-deployer-plan-task", pin["Condition"]!["ArnNotLike"]!["aws:PrincipalArn"]!.GetValue<string>());

        Assert.Equal(new[]
            {
                $"arn:aws:events:us-west-2:{TargetAccount}:rule/StepFunctionsGetEventForCodeBuildStartBuildRule",
                $"arn:aws:events:us-west-2:{TargetAccount}:rule/StepFunctionsGetEventsForECSTaskRule",
            },
            Strings(Sid(plan.DenyPolicy, "NorPutAnyRuleButTheSyncTasksManagedOnes").GetProperty("NotResource")));

        // GATE 2 RUNS AFTER RECORD, which the branch never reaches: a plan is not a deploy to test.
        var states = States(plan);
        Assert.Equal("Planned", states.GetProperty("ReadPlan").GetProperty("Next").GetString());
        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    [Fact]
    public void InAChain_WithoutTheBranch_NothingIsPinnedUnderPlans_AndThePlanIsWhatItWas()
    {
        var off = DevAsItRuns(null);
        Assert.DoesNotContain(off.EvidenceStorePolicy, s => s["Sid"]!.GetValue<string>() == "LzOnlyItsWritersPutPlans");
        Assert.DoesNotContain("ECSTaskRule", off.DenyPolicy);
    }

    // ---- the build account's half ----

    private static string Role(string name) => $"arn:aws:iam::{TargetAccount}:role/{name}";

    [Fact]
    public void TheBuildAccount_LetsVerifyReadConfigAndToolingRecords_AndVerifyAndThePlanTaskReadConfigBundles_Only()
    {
        var config = Dev();
        var build = PipelineBootstrapPlanner.Plan(config, "147440642635");

        var records = build.BuildRecordReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsConfigAndToolingRecordsDev");
        Assert.Equal(new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/config/scutara/scutara/*", "arn:aws:s3:::scu-build-records-4df6-b9c6/tooling/scutara/scutara/*" },
            records["Resource"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(Role(DeployerPlanner.VerifyRoleName(config)), records["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
        Assert.Equal("s3:GetObject", records["Action"]!.GetValue<string>());

        var bundles = build.ArtifactReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsConfigBundlesDev");
        Assert.Equal("arn:aws:s3:::scu-artifacts-4df6-b9c6/config/scutara/scutara/*", bundles["Resource"]!.GetValue<string>());
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role("scu-dev-deployer-plan-task") },
            bundles["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(Plan(config).Infrastructure!.TaskRoleName, InfraPlanPlanner.TaskRoleNameFor("scu", "dev"));
    }

    [Fact]
    public void WithoutTheBranch_TheBuildAccountGrantsNothingOfIt()
    {
        var build = PipelineBootstrapPlanner.Plan(Dev(null), "147440642635");
        Assert.DoesNotContain(build.BuildRecordReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>(), s => s.ToJsonString().Contains("config/"));
        Assert.DoesNotContain(build.ArtifactReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>(), s => s.ToJsonString().Contains("config/"));
    }

    // ---- refusals ----

    [Fact]
    public void WithoutOneConfigAndOneToolingRepository_TheBranchIsNotPlanned()
        => Assert.Throws<InvalidOperationException>(() => Plan(Dev(rootRepos: false)));
}
