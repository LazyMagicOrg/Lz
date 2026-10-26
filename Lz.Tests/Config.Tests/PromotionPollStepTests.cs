using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The poller's tick (DecoupledCd.md §12 P5 stage I): it reads the hold first and does nothing else under one, then the
/// predecessor's set markers, this environment's decisions and its deployer's executions, and does what
/// <see cref="PromotionPoll.Decide"/> says - starts one execution with the marker and the image target, writes superseded
/// decisions, and publishes each alarm.
/// </summary>
public class PromotionPollStepTests
{
    private const string Dev = "scu-dev-deploy-evidence-4df6-b9c6", Own = "scu-test-deploy-evidence-d5e0-b27f";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T02:00:00Z");

    private static readonly PromotionPollSettings Settings = new(
        "dev", Dev, Own, "arn:aws:states:us-west-2:456687462751:stateMachine:scu-test-deployer", "/scu/test/pipeline/hold",
        "arn:aws:sns:us-west-2:456687462751:scu-test-pipeline-alerts",
        new JsonObject { ["cluster"] = "scu-test-cluster", ["service"] = "scu-mp-aiphost", ["container"] = "aiphost", ["repository"] = "scu-4df6-b9c6-aiphost" });

    private static PassMarker Marker(string serviceStem)
    {
        var m = new PassMarkerMember("image", "Scutara/ScutaraService",
            new PassMarkerRecordReference("scu-build-records-4df6-b9c6", $"image/scutara/scutaraservice/{serviceStem}.json", "rv"),
            new BuildRecordIdentity("image", Digest: "sha256:" + serviceStem.GetHashCode().ToString("x8").PadLeft(64, '0')),
            "1.0.421", new BuildRecordBuiltFrom("Scutara/ScutaraService", new string('1', 40), "published", new Dictionary<string, string>()),
            new PassMarkerReference($"deploys/{serviceStem}.json", "dv"));
        return new PassMarker(1, PassMarkerFormat.MarkerIdFor(new[] { m }), "dev", new[] { m },
            new PassMarkerEvidence(new PassMarkerReference("tests/x.json", "tv")), "req-x", "2026-10-26T00:00:00Z");
    }

    private static readonly PassMarker Older = Marker("20261025T100000Z-1-1"), Newer = Marker("20261025T200000Z-2-1");

    private sealed class World : IPromotionReads, IExecutionStarter, IEvidenceWriter, IAlertPublisher
    {
        public string? Hold { get; set; } = "off";
        public List<(string Key, string Json, DateTimeOffset At)> Markers { get; } = new();
        public Dictionary<string, string> Decisions { get; } = new();   // markerId -> outcome
        public List<PromotionExecution> Executions { get; } = new();
        public List<(string Name, string Input)> Started { get; } = new();
        public Dictionary<string, string> Written { get; } = new();
        public List<string> Alerts { get; } = new();
        public List<string> Reads { get; } = new();

        public void Pass(PassMarker m, int minutesAgo = 1) => Markers.Add((PassMarkerFormat.KeyFor(m.MarkerId), PassMarkerFormat.Write(m), Now.AddMinutes(-minutesAgo)));

        public Task<string?> HoldAsync(string parameter) { Reads.Add("hold"); return Task.FromResult(Hold); }
        public Task<IReadOnlyList<(string Key, DateTimeOffset LastModified)>> ListMarkersAsync(string bucket)
        {
            Reads.Add("markers");
            Assert.Equal(Dev, bucket);
            return Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>(Markers.Select(m => (m.Key, m.At)).ToList());
        }
        public Task<StoredRecord?> ReadMarkerAsync(string bucket, string key)
            => Task.FromResult<StoredRecord?>(Markers.Where(m => m.Key == key).Select(m => new StoredRecord(m.Json, "mv-" + key.Length)).FirstOrDefault());
        public Task<IReadOnlyDictionary<string, string>> DecisionsAsync(string bucket)
        {
            Assert.Equal(Own, bucket);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Decisions);
        }
        public Task<IReadOnlyList<PromotionExecution>> ExecutionsAsync(string stateMachineArn) => Task.FromResult<IReadOnlyList<PromotionExecution>>(Executions);
        public Task<bool> StartAsync(string stateMachineArn, string name, string input) { Started.Add((name, input)); return Task.FromResult(true); }
        public Task<bool> PutOnceAsync(string bucket, string key, string body) { Assert.Equal(Own, bucket); Written[key] = body; return Task.FromResult(true); }
        public Task PublishAsync(string topicArn, string subject, string message) { Alerts.Add(message); return Task.CompletedTask; }
    }

    private static Task<JsonObject> Tick(World w) => PromotionPollStep.RunAsync(Settings, w, w, w, w, Now);

    [Fact]
    public async Task TheNewestUndecidedMarker_IsStarted_WithItsKeyItsVersionAndTheImageTarget()
    {
        var w = new World();
        w.Pass(Older); w.Pass(Newer);
        await Tick(w);

        var (name, input) = Assert.Single(w.Started);
        Assert.Equal(PromotionPoll.ExecutionNameFor(Newer.MarkerId), name);
        var body = JsonNode.Parse(input)!;
        Assert.Equal(PassMarkerFormat.KeyFor(Newer.MarkerId), body["marker"]!["key"]!.GetValue<string>());
        Assert.StartsWith("mv-", body["marker"]!["versionId"]!.GetValue<string>());
        Assert.Equal("scu-test-cluster", body["target"]!["cluster"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("on")]
    [InlineData("ON ")]
    public async Task UnderAHeldOrMissingHold_NothingElseIsReadOrDone(string? hold)
    {
        var w = new World { Hold = hold };
        w.Pass(Newer);
        await Tick(w);
        Assert.Equal(new[] { "hold" }, w.Reads);
        Assert.Empty(w.Started);
        Assert.Empty(w.Written);
    }

    [Fact]
    public async Task ARunningExecution_HoldsEveryStart()
    {
        var w = new World();
        w.Pass(Newer);
        w.Executions.Add(new PromotionExecution("pm-0000-1", "RUNNING", Now.AddMinutes(-3), null));
        await Tick(w);
        Assert.Empty(w.Started);
    }

    [Fact]
    public async Task AMarkerAnExecutionWasStartedFor_IsNotStartedAgain_AndItsCloseWithoutADecisionAlarms()
    {
        var w = new World();
        w.Pass(Newer);
        w.Executions.Add(new PromotionExecution(PromotionPoll.ExecutionNameFor(Newer.MarkerId), "FAILED", Now.AddMinutes(-9), Now.AddMinutes(-8)));
        await Tick(w);
        Assert.Empty(w.Started);
        Assert.Contains(w.Alerts, a => a.Contains("without a decision"));
    }

    [Fact]
    public async Task OlderMarkersThanASetDeployedHere_AreWrittenSuperseded()
    {
        var w = new World();
        w.Pass(Older); w.Pass(Newer);
        w.Decisions[Newer.MarkerId] = DecisionStep.Deployed;
        await Tick(w);

        var body = JsonNode.Parse(w.Written[PassMarkerFormat.DecisionKeyFor(Older.MarkerId)])!;
        Assert.Equal(("superseded", Older.MarkerId), (body["outcome"]!.GetValue<string>(), body["markerId"]!.GetValue<string>()));
        Assert.Empty(w.Started);
    }

    [Fact]
    public async Task AMarkerThatDoesNotParse_IsAlarmedAndLeftOut_NotStarted()
    {
        var w = new World();
        w.Pass(Older);
        w.Markers.Add((PassMarkerFormat.KeyFor("sets/" + new string('f', 64)), "{ \"schema\": 9 }", Now));
        await Tick(w);
        Assert.Contains(w.Alerts, a => a.Contains("schema"));
        Assert.Equal(PromotionPoll.ExecutionNameFor(Older.MarkerId), Assert.Single(w.Started).Name);
    }

    [Fact]
    public async Task EachAlarm_IsPublished()
    {
        var w = new World();
        w.Pass(Newer, minutesAgo: 30);
        await Tick(w);
        Assert.Contains(w.Alerts, a => a.Contains("ten minutes"));
    }
}
