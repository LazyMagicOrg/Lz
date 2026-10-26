using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// A promoted environment's decision (DecoupledCd.md §12 P5 stage F, §5.3 rule 2): written once, at
/// <c>decisions/{markerId}.json</c> in its own evidence store, after the set deployed or after RecordFailure, so a set that
/// was deployed, failed or was refused here is never deployed from the same marker again.
/// </summary>
public class DecisionStepTests
{
    private const string Store = "scu-test-deploy-evidence-d5e0-b27f", Execution = "pm-0123456789ab-1";
    private static readonly string Id = "sets/" + new string('a', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T01:00:00Z");

    private sealed class Writer(bool exists = false) : IEvidenceWriter
    {
        public Dictionary<string, string> Written { get; } = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body)
        {
            Assert.Equal(Store, bucket);
            if (exists || Written.ContainsKey(key)) return Task.FromResult(false);
            Written[key] = body;
            return Task.FromResult(true);
        }
    }

    private static JsonObject Marker(string id) => new() { ["key"] = PassMarkerFormat.KeyFor(id), ["versionId"] = "mv-1" };

    private static JsonObject Deployed() => new() { ["marker"] = Marker(Id), ["set"] = new JsonObject { ["markerId"] = Id } };

    private static JsonObject Failed(string type, bool verified = true) => new()
    {
        ["marker"] = Marker(Id),
        ["set"] = verified ? new JsonObject { ["markerId"] = Id } : null,
        ["error"] = new JsonObject { ["Error"] = type, ["Cause"] = "why" },
    };

    private static JsonObject Body(Writer w) => JsonNode.Parse(w.Written[PassMarkerFormat.DecisionKeyFor(Id)])!.AsObject();

    [Fact]
    public async Task ASetThatDeployed_IsDecidedDeployed_Once()
    {
        var w = new Writer();
        var result = await DecisionStep.RunAsync(Deployed(), Execution, Store, w, Now);

        Assert.Equal(("deployed", true), (result["outcome"]!.GetValue<string>(), result["written"]!.GetValue<bool>()));
        var body = Body(w);
        Assert.Equal((1, Id, "deployed", Execution), (body["schema"]!.GetValue<int>(), body["markerId"]!.GetValue<string>(),
            body["outcome"]!.GetValue<string>(), body["execution"]!.GetValue<string>()));
        Assert.Equal("mv-1", body["marker"]!["versionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AVerifyRefusal_IsDecidedRefused_AndAnyOtherFailure_Failed()
    {
        var refused = new Writer();
        await DecisionStep.RunAsync(Failed(nameof(DeployRefused), verified: false), Execution, Store, refused, Now);
        Assert.Equal("refused", Body(refused)["outcome"]!.GetValue<string>());
        Assert.Equal(nameof(DeployRefused), Body(refused)["error"]!["type"]!.GetValue<string>());

        var failed = new Writer();
        await DecisionStep.RunAsync(Failed("RolloutDidNotLand"), Execution, Store, failed, Now);
        Assert.Equal("failed", Body(failed)["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASiteRefusedBeforeItWasRead_IsDecidedByItsKeyToo()
    {
        // V-K step 5: a site marker undecided would alarm at every poll and never be started again.
        var site = "site/" + new string('b', 64);
        var w = new Writer();
        var state = new JsonObject
        {
            ["marker"] = Marker(site), ["set"] = null,
            ["error"] = new JsonObject { ["Error"] = nameof(DeployRefused), ["Cause"] = "why" },
        };
        var result = await DecisionStep.RunAsync(state, Execution, Store, w, Now);
        Assert.Equal(PassMarkerFormat.DecisionKeyFor(site), result["key"]!.GetValue<string>());
        Assert.StartsWith("decisions/site/", result["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARefusalBeforeTheSetWasRead_IsDecidedByTheMarkersKey()
    {
        // VerifySet refused before $.set existed: the marker's own key names the set it refused.
        var w = new Writer();
        var result = await DecisionStep.RunAsync(Failed(nameof(DeployRefused), verified: false), Execution, Store, w, Now);
        Assert.Equal(PassMarkerFormat.DecisionKeyFor(Id), result["key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("deploys/image/scutara/scutaraservice/req-1.json")]
    [InlineData("passed/sets/not-a-hash.json")]
    public async Task AFailureWithNoSetMarkerToName_DecidesNothing_AndDoesNotThrow(string? key)
    {
        var state = new JsonObject
        {
            ["marker"] = key is null ? null : new JsonObject { ["key"] = key, ["versionId"] = "v" },
            ["error"] = new JsonObject { ["Error"] = nameof(DeployRefused), ["Cause"] = "no marker" },
        };
        var w = new Writer();
        var result = await DecisionStep.RunAsync(state, Execution, Store, w, Now);
        Assert.False(result["written"]!.GetValue<bool>());
        Assert.Empty(w.Written);
    }

    [Fact]
    public async Task AFailureAfterADecisionExists_DoesNotThrow()
        // The decision is what VerifySet refused on, so it stays as it was.
        => Assert.False((await DecisionStep.RunAsync(Failed(nameof(DeployRefused)), Execution, Store, new Writer(exists: true), Now))["written"]!.GetValue<bool>());

    [Fact]
    public async Task ADeployedSetWhoseDecisionAlreadyExists_Throws_SinceTwoExecutionsRaced()
        => await Assert.ThrowsAsync<DecisionAlreadyWritten>(() => DecisionStep.RunAsync(Deployed(), Execution, Store, new Writer(exists: true), Now));
}
