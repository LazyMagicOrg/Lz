using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The revert in a promoted environment (DecoupledCd.md §12 P5 stage J, T-14, §4.4 step 2a): started only through the
/// revert-start function, with an input naming <c>revertTo</c>, refused while any other execution runs; admitted by Verify
/// only for a set this environment deployed and decided deployed; the hold set before Prepare; recorded at
/// <c>reverts/{execution}.json</c>, naming the set it restored and the set it replaced, with no decision.
/// </summary>
public class RevertStepsTests
{
    private const string Machine = "arn:aws:states:us-west-2:456687462751:stateMachine:scu-test-deployer";
    private const string Own = "scu-test-deploy-evidence-d5e0-b27f";
    private static readonly string Id = "sets/" + new string('b', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T03:04:05Z");
    private static readonly JsonObject Target = new() { ["cluster"] = "scu-test-cluster", ["service"] = "scu-mp-aiphost", ["container"] = "aiphost", ["repository"] = "scu-4df6-b9c6-aiphost" };

    private static JsonObject RevertTo(string? key = null, string? version = "mv-1")
        => new() { ["revertTo"] = key is null && version is null ? null : new JsonObject { ["key"] = key ?? PassMarkerFormat.KeyFor(Id), ["versionId"] = version } };

    private sealed class Sfn(params PromotionExecution[] executions) : IPromotionExecutions
    {
        public List<(string Name, string Input)> Started { get; } = new();
        public Task<IReadOnlyList<PromotionExecution>> ExecutionsAsync(string stateMachineArn)
        {
            Assert.Equal(Machine, stateMachineArn);
            return Task.FromResult<IReadOnlyList<PromotionExecution>>(executions);
        }
        public Task<bool> StartAsync(string stateMachineArn, string name, string input) { Started.Add((name, input)); return Task.FromResult(true); }
    }

    // ---- the revert-start function ----

    [Fact]
    public async Task ARevert_IsStartedWithItsMarkerAndTheImageTarget_UnderANameDerivedFromIt()
    {
        var sfn = new Sfn(new PromotionExecution("pm-aaaa-1", "SUCCEEDED", Now.AddHours(-1), Now.AddHours(-1)));
        var result = await RevertStartStep.RunAsync(RevertTo(), Machine, Target, sfn, Now);

        var (name, input) = Assert.Single(sfn.Started);
        Assert.Equal($"rv-{new string('b', 20)}-202610260304", name);
        Assert.Equal(name, result["started"]!.GetValue<string>());
        var body = JsonNode.Parse(input)!;
        Assert.Equal(PassMarkerFormat.KeyFor(Id), body["revertTo"]!["key"]!.GetValue<string>());
        Assert.Equal("scu-test-cluster", body["target"]!["cluster"]!.GetValue<string>());
        Assert.Null(body["marker"]);
    }

    [Fact]
    public async Task AnInputWithoutRevertTo_IsRefused()
        => await Assert.ThrowsAsync<DeployRefused>(() => RevertStartStep.RunAsync(new JsonObject(), Machine, Target, new Sfn(), Now));

    [Theory]
    [InlineData("deploys/image/scutara/scutaraservice/req-1.json", "v")]
    [InlineData(null, null)]
    public async Task ARevertToSomethingButAVersionedSetMarker_IsRefused(string? key, string? version)
        => await Assert.ThrowsAsync<DeployRefused>(() => RevertStartStep.RunAsync(RevertTo(key, version), Machine, Target, new Sfn(), Now));

    [Fact]
    public async Task ARevert_IsRefusedWhileAnyOtherExecutionRuns()
    {
        var sfn = new Sfn(new PromotionExecution("pm-cccc-1", "RUNNING", Now.AddMinutes(-2), null));
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => RevertStartStep.RunAsync(RevertTo(), Machine, Target, sfn, Now));
        Assert.Contains("pm-cccc-1", ex.Message);
        Assert.Empty(sfn.Started);
    }

    // ---- the hold, set before Prepare ----

    private sealed class Ssm : IHoldWriter
    {
        public List<(string Name, string Value)> Puts { get; } = new();
        public Task SetAsync(string parameter, string value) { Puts.Add((parameter, value)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ARevertSetsTheHold()
    {
        var ssm = new Ssm();
        await SetHoldStep.RunAsync(RevertTo(), "pm-exec", "/scu/test/pipeline/hold", ssm);
        Assert.Equal(("/scu/test/pipeline/hold", "on"), Assert.Single(ssm.Puts));
    }

    [Fact]
    public async Task AnExecutionThatIsNotARevert_MayNotSetTheHold()
    {
        var ssm = new Ssm();
        await Assert.ThrowsAsync<DeployRefused>(() => SetHoldStep.RunAsync(new JsonObject { ["marker"] = new JsonObject() }, "pm-exec", "/scu/test/pipeline/hold", ssm));
        Assert.Empty(ssm.Puts);
    }

    // ---- the record, and no decision ----

    private sealed class Writer : IEvidenceWriter
    {
        public Dictionary<string, string> Written { get; } = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body) { Assert.Equal(Own, bucket); Written[key] = body; return Task.FromResult(true); }
    }

    [Fact]
    public async Task ARevertIsRecordedUnderReverts_NamingTheSetItRestoredAndTheSetItReplaced()
    {
        var w = new Writer();
        var state = RevertTo();
        state["set"] = new JsonObject { ["markerId"] = Id, ["replaces"] = "sets/" + new string('c', 64) };
        await RecordRevertStep.RunAsync(state, "rv-exec", Own, w, Now);

        var body = JsonNode.Parse(w.Written["reverts/rv-exec.json"])!;
        Assert.Equal((Id, "sets/" + new string('c', 64), "rv-exec"),
            (body["restored"]!["markerId"]!.GetValue<string>(), body["replaced"]!.GetValue<string>(), body["execution"]!.GetValue<string>()));
        Assert.DoesNotContain(w.Written.Keys, k => k.StartsWith("decisions/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedRevert_WritesNoDecision_SinceBothSetsWereDecidedWhenFirstDeployed()
    {
        var w = new Writer();
        var state = RevertTo();
        state["set"] = new JsonObject { ["markerId"] = Id };
        state["error"] = new JsonObject { ["Error"] = "RolloutDidNotLand", ["Cause"] = "x" };
        var result = await DecisionStep.RunAsync(state, "rv-exec", Own, w, Now);
        Assert.False(result["written"]!.GetValue<bool>());
        Assert.Empty(w.Written);
    }
}
