using System.Text.Json.Nodes;
using Lz.Aws.Orchestration;
using Lz.Aws.Pipeline;
using Pulumi.Automation;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The infrastructure branch's deploy half, its steps (DecoupledCd.md P3 D): ReadPlan's override (P-12), VerifyTooling's Deploy
/// task, infradeploy's rule for applying a fresh preview, and ReadApply, read from the result infradeploy writes and handed to
/// the Record step every deploy writes its evidence with.
/// </summary>
public class InfraDeployStepsTests
{
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private const string Registry = "503947800380.dkr.ecr.us-west-2.amazonaws.com";
    private const string Tooling = "sha256:4fc8d0336f2b0a1e9c8d7f6a5b4c3d2e1f0a9b8c7d6e5f4a3b2c1d0e9f8a7b6c";

    private sealed class Records(Dictionary<string, string> objects) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key)
            => Task.FromResult(objects.TryGetValue($"{bucket}/{key}", out var json) ? new StoredRecord(json, "v-1") : null);
    }

    // ---- ReadPlan's override (P-12) ----

    private static string DestructivePlan => """
        {"command":"infraplan","plans":[
          {"stack":"scu-mp-dev","changes":{"delete":1},"destructive":true,
           "destructiveSteps":[{"op":"delete","type":"aws:s3/bucket:Bucket","urn":"urn:x::bucket"}]}],
         "schema":1,"destructive":true,"hasChanges":true}
        """;

    private static Task<JsonObject> ReadPlan(JsonObject state)
    {
        state["plan"] = new JsonObject { ["Containers"] = new JsonArray(new JsonObject { ["ExitCode"] = 2 }) };
        return InfraPlanSteps.ReadPlanAsync(state, "exec-1", Evidence, new Records(new() { [$"{Evidence}/plans/exec-1.json"] = DestructivePlan }));
    }

    [Fact]
    public async Task ADestructivePlan_GoesOn_OnlyForAnInputThatAllowsIt_AndSaysWhatItAllowed()
    {
        var r = await ReadPlan(new JsonObject { ["allowDestructivePlan"] = true });
        Assert.Equal("PlannedDestructiveAllowed", r["verdict"]!.GetValue<string>());
        Assert.True(r["destructive"]!.GetValue<bool>());
        Assert.Equal("scu-mp-dev: delete urn:x::bucket", r["destructiveAllowed"]!.GetValue<string>());

        var refused = await Assert.ThrowsAsync<DestructivePlanRefused>(() => ReadPlan(new JsonObject { ["allowDestructivePlan"] = false }));
        Assert.Contains("\"allowDestructivePlan\": true", refused.Message);
        await Assert.ThrowsAsync<DestructivePlanRefused>(() => ReadPlan(new JsonObject()));
    }

    [Fact]
    public async Task AnOverrideThatIsNotAJsonBoolean_IsRefused_NotGuessedAt()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => ReadPlan(new JsonObject { ["allowDestructivePlan"] = "true" }));
        Assert.Contains("allowDestructivePlan", ex.Message);
    }

    [Fact]
    public void TheOverride_IsAnInputOnlyAPersonWrites()
        => Assert.Contains("allowDestructivePlan", DeployerInput.OptionalInputFields);

    // ---- VerifyTooling's Deploy task ----

    private static readonly ToolingSettings Tool = new(
        new[] { "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutara" },
        Registry, "scu-4df6-b9c6-tooling", "scu-dev-deployer-plan",
        "arn:aws:iam::503947800380:role/scu-dev-deployer-tooling-exec", "arn:aws:iam::503947800380:role/scu-dev-deployer-plan-task",
        "/lz/scu-dev-deployer-plan", "us-west-2", new[] { "subnet-0abc" }, new[] { "sg-0abc" },
        "scu-dev-deployer-infra", "arn:aws:iam::503947800380:role/scu-dev-deployer-infra-task");

    private sealed class Notation : INotation
    {
        public Task<string?> InstallAsync(NotationLayout layout, string trustPolicyJson) => Task.FromResult<string?>(null);
        public Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(NotationLayout layout, string reference, IReadOnlyDictionary<string, string> environment)
            => Task.FromResult((0, $"Successfully verified signature for {reference}\n", ""));
    }

    private sealed class Definitions : ITaskDefinitions
    {
        public readonly List<Amazon.ECS.Model.RegisterTaskDefinitionRequest> Registered = new();
        public Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string arn) => throw new NotSupportedException();
        public Task<string> RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request)
        {
            Registered.Add(request);
            return Task.FromResult($"arn:aws:ecs:us-west-2:503947800380:task-definition/{request.Family}:{Registered.Count}");
        }
    }

    [Fact]
    public async Task UnderDeploy_TheDeployTaskPinsTheSameVerifiedDigest_OnTheInfrastructureRole()
    {
        var definitions = new Definitions();
        var state = new JsonObject { ["verified"] = new JsonObject { ["class"] = "config", ["tooling"] = new JsonObject { ["digest"] = Tooling } } };
        var r = await InfraPlanSteps.VerifyToolingAsync(state, "exec-1", Tool, () => Task.FromResult(("AWS", "token")), new Notation(),
            NotationLayout.Under("/tmp/lz-notation"), definitions);

        Assert.Equal(2, definitions.Registered.Count);
        var (plan, deploy) = (definitions.Registered[0], definitions.Registered[1]);
        Assert.Equal("scu-dev-deployer-plan", plan.Family);
        Assert.Equal(Tool.TaskRoleArn, plan.TaskRoleArn);
        Assert.Equal("scu-dev-deployer-infra", deploy.Family);
        Assert.Equal(Tool.DeployTaskRoleArn, deploy.TaskRoleArn);
        Assert.Equal(plan.ContainerDefinitions[0].Image, deploy.ContainerDefinitions[0].Image);
        Assert.Equal($"{Registry}/scu-4df6-b9c6-tooling@{Tooling}", deploy.ContainerDefinitions[0].Image);
        Assert.Equal("deploy", deploy.ContainerDefinitions[0].LogConfiguration.Options["awslogs-stream-prefix"]);
        Assert.Equal("plan", plan.ContainerDefinitions[0].LogConfiguration.Options["awslogs-stream-prefix"]);
        Assert.Equal(Tool.ExecutionRoleArn, deploy.ExecutionRoleArn);
        Assert.Equal("arn:aws:ecs:us-west-2:503947800380:task-definition/scu-dev-deployer-infra:2", r["deployTaskDefinitionArn"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnderPlan_OnlyThePlanTaskIsRegistered()
    {
        var definitions = new Definitions();
        var state = new JsonObject { ["verified"] = new JsonObject { ["class"] = "config", ["tooling"] = new JsonObject { ["digest"] = Tooling } } };
        var r = await InfraPlanSteps.VerifyToolingAsync(state, "exec-1", Tool with { DeployFamily = null, DeployTaskRoleArn = null },
            () => Task.FromResult(("AWS", "token")), new Notation(), NotationLayout.Under("/tmp/lz-notation"), definitions);
        Assert.Equal("scu-dev-deployer-plan", Assert.Single(definitions.Registered).Family);
        Assert.Null(r["deployTaskDefinitionArn"]);
    }

    [Fact]
    public void TheDeploySettings_AreBothOrNeither()
    {
        var env = new Dictionary<string, string>
        {
            [ToolingSettings.TrustedProfilesVariable] = "arn:p", [ToolingSettings.RegistryVariable] = Registry,
            [ToolingSettings.RepositoryVariable] = "r", [ToolingSettings.FamilyVariable] = "f",
            [ToolingSettings.ExecutionRoleVariable] = "e", [ToolingSettings.TaskRoleVariable] = "t",
            [ToolingSettings.LogGroupVariable] = "l", [ToolingSettings.RegionVariable] = "us-west-2",
            [ToolingSettings.SubnetsVariable] = "subnet-0abc", [ToolingSettings.SecurityGroupsVariable] = "sg-0abc",
            [ToolingSettings.DeployFamilyVariable] = "scu-dev-deployer-infra",
        };
        var one = Assert.Throws<InvalidOperationException>(() => ToolingSettings.Read(k => env.GetValueOrDefault(k)));
        Assert.Contains("set together or not at all", one.Message);
        env[ToolingSettings.DeployTaskRoleVariable] = "arn:role";
        Assert.True(ToolingSettings.Read(k => env.GetValueOrDefault(k)).Deploys);
        env.Remove(ToolingSettings.DeployFamilyVariable);
        env.Remove(ToolingSettings.DeployTaskRoleVariable);
        Assert.False(ToolingSettings.Read(k => env.GetValueOrDefault(k)).Deploys);
    }

    // ---- infradeploy: apply what was approved ----

    private static PlannedStep Step(OperationType op, string urn, params string[] diffs)
        => new(op, "aws:lambda/function:Function", urn, null, null, diffs.Length == 0 ? null : diffs);

    private static PlanSummary Stack(string name, params PlannedStep[] steps)
    {
        var summary = steps.GroupBy(s => s.Op).ToDictionary(g => g.Key, g => g.Count());
        return PlanSummary.From(name, summary, steps);
    }

    private static PlanFile File(params PlanSummary[] stacks) => new("infraplan", stacks);

    private static readonly PlanFile Approved = File(
        Stack("scu-dev", Step(OperationType.Update, "urn:fn:custom-auth", "code"), Step(OperationType.Update, "urn:lt:tailscale", "userData")),
        Stack("scu-mp-dev", Step(OperationType.Update, "urn:td:aiphost")));

    [Fact]
    public void TheSameSteps_MayBeApplied_WhateverPropertiesTheyNowChange()
    {
        var fresh = File(
            Stack("scu-dev", Step(OperationType.Update, "urn:fn:custom-auth", "code", "lastModified"), Step(OperationType.Update, "urn:lt:tailscale", "userData")),
            Stack("scu-mp-dev", Step(OperationType.Update, "urn:td:aiphost")));
        Assert.Empty(InfraDeploy.WhyNotApply(Approved, fresh, allowDestructive: false));
    }

    [Fact]
    public void AStepAddedDroppedOrMadeAReplace_IsRefused_NamingIt()
    {
        var fresh = File(
            Stack("scu-dev", Step(OperationType.Replace, "urn:fn:custom-auth"), Step(OperationType.Update, "urn:lt:tailscale", "userData"),
                Step(OperationType.Create, "urn:bucket:new")),
            Stack("scu-mp-dev"));
        var why = InfraDeploy.WhyNotApply(Approved, fresh, allowDestructive: true);
        Assert.Contains("scu-dev: approved, no longer planned: update urn:fn:custom-auth", why);
        Assert.Contains("scu-dev: planned now, never approved: replace urn:fn:custom-auth", why);
        Assert.Contains("scu-dev: planned now, never approved: create urn:bucket:new", why);
        Assert.Contains("scu-mp-dev: approved, no longer planned: update urn:td:aiphost", why);
    }

    [Fact]
    public void OtherStacks_AreRefused()
    {
        var why = InfraDeploy.WhyNotApply(Approved, File(Stack("scu-dev")), allowDestructive: false);
        Assert.Contains(why, w => w.StartsWith("the stacks were [scu-dev, scu-mp-dev] and are now [scu-dev]"));
    }

    [Fact]
    public void ADestructivePlan_IsRefusedWithoutTheOverride_AndAppliedWithIt()
    {
        var destructive = File(Stack("scu-dev", Step(OperationType.Delete, "urn:bucket:old")));
        Assert.Contains(InfraDeploy.WhyNotApply(destructive, destructive, allowDestructive: false),
            w => w.Contains("the approved plan replaces or deletes"));
        Assert.Empty(InfraDeploy.WhyNotApply(destructive, destructive, allowDestructive: true));
    }

    [Fact]
    public void AnApprovedPlanWithChangesButNoSteps_CannotBeHeldTo_AndIsRefused()
    {
        // A plan written before plans carried their steps (Lz 0.13.164's), or by a preview that captured none.
        var countsOnly = PlanSummary.From("scu-dev", new Dictionary<OperationType, int> { [OperationType.Update] = 2 });
        var why = InfraDeploy.WhyNotApply(File(countsOnly), File(countsOnly), allowDestructive: false);
        Assert.Contains(why, w => w.Contains("names no steps"));
    }

    [Fact]
    public void APlanFile_RoundTripsThroughTheEvidenceStore_WithItsSteps()
    {
        var parsed = PlanFile.Parse(Approved.ToJson());
        Assert.Equal(new[] { "scu-dev", "scu-mp-dev" }, parsed.Plans.Select(p => p.Stack));
        Assert.Equal(new[] { "code" }, parsed.Plans[0].Steps[0].Keys);
        Assert.Empty(Approved.DifferencesFrom(parsed));
        Assert.Throws<System.Text.Json.JsonException>(() => PlanFile.Parse("""{"command":"x","plans":[],"schema":2}"""));
    }

    [Fact]
    public void ThePlansSteps_LeaveOutWhatItDoesNotChange()
    {
        var s = Stack("scu-dev", Step(OperationType.Update, "urn:a", "code"), Step(OperationType.Read, "urn:data"), Step(OperationType.Same, "urn:same"));
        Assert.Equal("update urn:a", Assert.Single(s.Steps.Select(x => $"{x.Op} {x.Urn}")));
    }

    // ---- ReadApply, from what infradeploy writes ----

    private static readonly PlanFile Empty = File(Stack("scu-dev"), Stack("scu-mp-dev"));

    private static JsonObject StateAfterDeploy(int? exit = 0) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = "scu-build-records-4df6-b9c6", ["key"] = "config/scutara/scutara/x.json" },
        ["verified"] = new JsonObject { ["class"] = "config", ["builtFrom"] = new JsonObject { ["repo"] = "Scutara/Scutara", ["commit"] = "d86cd09" } },
        ["tooling"] = new JsonObject { ["reference"] = $"{Registry}/scu-4df6-b9c6-tooling@{Tooling}" },
        ["planned"] = new JsonObject { ["key"] = "plans/exec-1.json", ["versionId"] = "p-1", ["destructive"] = false },
        ["approval"] = new JsonObject { ["approvedBy"] = "pass-through" },
        ["deploy"] = new JsonObject { ["Containers"] = new JsonArray(new JsonObject { ["ExitCode"] = exit }) },
    };

    private static string Result(string outcome, PlanFile? after, string? reason = null, params string[] differences)
        => InfraDeploy.ResultJson(outcome, reason, "plans/exec-1.json", "p-1", differences, new[] { "scu-dev", "scu-mp-dev" }, new[] { "scu/mp" }, after, false);

    private static Task<JsonObject> ReadApply(JsonObject state, string? result)
        => InfraPlanSteps.ReadApplyAsync(state, "exec-1", Evidence,
            new Records(result is null ? new() : new() { [$"{Evidence}/applies/exec-1.json"] = result }),
            new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ADeployWhoseSecondPreviewIsEmpty_IsDeployed_AndRecordWritesItsEvidenceWhereTheClassSays()
    {
        var state = StateAfterDeploy();
        var r = await ReadApply(state, Result(InfraDeploy.Deployed, Empty));
        Assert.Equal("Deployed", r["verdict"]!.GetValue<string>());
        Assert.Equal("applies/exec-1.json", r["key"]!.GetValue<string>());
        var evidence = r["evidence"]!.AsObject();
        Assert.Equal("deploys/config/scutara/scutara/exec-1.json", evidence["key"]!.GetValue<string>());
        var body = JsonNode.Parse(evidence["body"]!.GetValue<string>())!;
        Assert.Equal("plans/exec-1.json", body["plan"]!["key"]!.GetValue<string>());
        Assert.Equal("pass-through", body["approval"]!["approvedBy"]!.GetValue<string>());
        Assert.Equal(new[] { "scu-dev", "scu-mp-dev" }, body["apply"]!["applied"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("empty", body["secondPreview"]!.GetValue<string>());

        // THE SAME RECORD STEP: it recomputes the key from the state and writes the body once; a config input names no service.
        state["rollout"] = r;
        var writer = new Writer();
        var recorded = await RecordStep.RunAsync(state, "exec-1", Evidence, writer, new NoLeases());
        Assert.Equal("deploys/config/scutara/scutara/exec-1.json", recorded["key"]!.GetValue<string>());
        Assert.Equal(evidence["body"]!.GetValue<string>(), writer.Written[$"{Evidence}/deploys/config/scutara/scutara/exec-1.json"]);
        Assert.StartsWith("none", recorded["lease"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASecondPreviewThatIsNotEmpty_IsAnIncompleteRollout()
    {
        var after = File(Stack("scu-dev", Step(OperationType.Update, "urn:lt:tailscale", "userData")), Stack("scu-mp-dev"));
        var ex = await Assert.ThrowsAsync<InfraRolloutIncomplete>(() => ReadApply(StateAfterDeploy(), Result(InfraDeploy.Deployed, after, "the second preview is not empty")));
        Assert.Contains("scu-dev", ex.Message);
    }

    [Fact]
    public async Task ARefusal_SaysNothingWasApplied_AndWhy()
    {
        var ex = await Assert.ThrowsAsync<InfraDeployRefused>(() => ReadApply(StateAfterDeploy(1),
            Result(InfraDeploy.Refused, null, "the plan it would apply is not the plan that was approved", "scu-dev: planned now, never approved: create urn:b")));
        Assert.Contains("applied nothing", ex.Message);
        Assert.Contains("never approved: create urn:b", ex.Message);
    }

    [Fact]
    public async Task AFailure_OrNoResult_MayHaveAppliedSomething()
    {
        var failed = await Assert.ThrowsAsync<InfraDeployFailed>(() => ReadApply(StateAfterDeploy(1), Result(InfraDeploy.Failed, null, "deploysystem of scu-dev stopped")));
        Assert.Contains("after applying [scu-dev, scu-mp-dev]", failed.Message);
        var missing = await Assert.ThrowsAsync<InfraDeployFailed>(() => ReadApply(StateAfterDeploy(null), null));
        Assert.Contains("may have applied part of the plan", missing.Message);
    }

    [Fact]
    public async Task AnExitCodeThatDisagreesWithDeployed_OrAnotherShape_IsNotTrusted()
    {
        await Assert.ThrowsAsync<InfraDeployFailed>(() => ReadApply(StateAfterDeploy(1), Result(InfraDeploy.Deployed, Empty)));
        await Assert.ThrowsAsync<InfraDeployFailed>(() => ReadApply(StateAfterDeploy(), """{"schema":2,"outcome":"deployed"}"""));
        await Assert.ThrowsAsync<InfraDeployFailed>(() => ReadApply(StateAfterDeploy(), Result(InfraDeploy.Deployed, null)));
    }

    [Fact]
    public void TheApplyKey_IsTheExecutionsUnderApplies_AndNothingElseIsOne()
    {
        Assert.Equal("applies/exec-1.json", InfraPlan.ApplyKeyFor("exec-1"));
        Assert.Null(InfraDeployPlanner.WhyNotAnApplyKey("applies/exec-1.json"));
        Assert.NotNull(InfraDeployPlanner.WhyNotAnApplyKey("plans/exec-1.json"));
        Assert.NotNull(InfraDeployPlanner.WhyNotAnApplyKey("applies/../deploys/x.json"));
        Assert.Throws<DeployRefused>(() => InfraPlan.ApplyKeyFor("../x"));
    }

    private sealed class Writer : IEvidenceWriter
    {
        public readonly Dictionary<string, string> Written = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body) => Task.FromResult(Written.TryAdd($"{bucket}/{key}", body));
    }

    private sealed class NoLeases : Lz.Aws.Ops.IServiceLeaseStore
    {
        public Task<bool> PutIfFreeAsync(string key, Lz.Aws.Ops.ServiceLeaseRecord record, DateTimeOffset now) => throw new NotSupportedException();
        public Task<Lz.Aws.Ops.ServiceLeaseRecord?> GetAsync(string key) => throw new NotSupportedException();
        public Task<bool> ReleaseIfTokenAsync(string key, string token) => throw new NotSupportedException();
    }
}
