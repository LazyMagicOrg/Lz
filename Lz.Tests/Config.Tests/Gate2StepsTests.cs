using System.Text.Json.Nodes;
using Lz.Aws.Ops;
using Lz.Aws.Pipeline;
using Lz.Tests.Orchestration.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Gate 2's two functions in the deployer (DecoupledCd.md §15.5, V-E): Gate2Start takes the environment's Gate 2 lease,
/// reads what the environment runs and requires this execution's artifact to be it; Gate2Result judges the result files,
/// reads what runs again, writes <c>tests/{execution}.json</c> once, alerts an advisory change, and releases the lease.
/// </summary>
public sealed class Gate2StepsTests
{
    private const string Exec = "req-20261023T204651Z-40021850117-1-mp-1";
    private const string Digest = "sha256:" + "d5"; // short on purpose: only equality matters here
    private const string Other = "sha256:" + "de";
    private const string LeaseKey = "lease#gate2#dev";
    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 21, 0, 0, TimeSpan.Zero);

    private static readonly Gate2Set Set = new(
        new[] { new Gate2Service("scu-dev-cluster", "scu-mp-aiphost", "aiphost") },
        new[] { new Gate2App("adminapp", "scu---webapp-adminapp-4df6-b9c6"), new Gate2App("sellerapp", "scu---webapp-sellerapp-4df6-b9c6") });

    private static JsonObject ImageState() => new()
    {
        ["record"] = new JsonObject { ["bucket"] = "scu-build-records-4df6-b9c6", ["key"] = "image/scutara/scutaraservice/20261023T204651Z-40021850117-1.json" },
        ["target"] = new JsonObject { ["cluster"] = "scu-dev-cluster", ["service"] = "scu-mp-aiphost", ["container"] = "aiphost", ["repository"] = "r" },
        ["verified"] = new JsonObject { ["class"] = "image", ["digest"] = Digest },
    };

    private static JsonObject ClientState(string version) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = "scu-build-records-4df6-b9c6", ["key"] = "client/scutara/scutaraadminapp/x-1.json" },
        ["target"] = new JsonObject { ["bucket"] = "scu---webapp-adminapp-4df6-b9c6" },
        ["verified"] = new JsonObject
        {
            ["class"] = "client",
            ["identity"] = new JsonObject { ["versionId"] = version },
            ["target"] = new JsonObject { ["app"] = "adminapp", ["bucket"] = "scu---webapp-adminapp-4df6-b9c6" },
        },
    };

    // ---- fakes ----

    private sealed class Services(params string?[] digests) : IServices
    {
        public string?[] Digests = digests;
        public Task<ServiceSnapshot?> DescribeAsync(string cluster, string service) => Task.FromResult<ServiceSnapshot?>(null);
        public Task<IReadOnlyList<TaskSnapshot>> RunningTasksAsync(string cluster, string service)
            => Task.FromResult<IReadOnlyList<TaskSnapshot>>(Digests.Select(d => new TaskSnapshot("RUNNING", new[] { new ContainerSnapshot("aiphost", d) })).ToList());
        public Task<IReadOnlyList<ServiceDeploymentRecord>> RecentDeploymentsAsync(string cluster, string service)
            => Task.FromResult<IReadOnlyList<ServiceDeploymentRecord>>(Array.Empty<ServiceDeploymentRecord>());
    }

    private sealed class Markers : IGate2Markers
    {
        public readonly Dictionary<string, string> Versions = new() { ["scu---webapp-adminapp-4df6-b9c6"] = "v-admin", ["scu---webapp-sellerapp-4df6-b9c6"] = "v-seller" };
        public Task<string?> DeployedVersionAsync(string bucket) => Task.FromResult(Versions.GetValueOrDefault(bucket));
    }

    private sealed class Bundles : IGate2Bundles
    {
        public int Resolved;
        public Task<Gate2Bundles> ResolveAsync(JsonObject state, Gate2Running running)
        {
            Resolved++;
            return Task.FromResult(new Gate2Bundles(
                new Dictionary<string, string> { ["GATE2_E2E_BUNDLE_URL"] = "https://presigned/tests", ["GATE2_E2E_BUNDLE_SHA256"] = "aa" },
                new Gate2Recorded(new[] { "P.Pre" }, new[] { "B.One" }, new[] { "A.Adv" }, GateCount: 1)));
        }
    }

    private sealed class Results : IGate2Results
    {
        public readonly Dictionary<string, string> Files = new();
        public Task<string?> ReadAsync(string execution, string file) => Task.FromResult(Files.GetValueOrDefault($"{execution}/{file}"));
    }

    private sealed class Store : IEvidenceWriter, IGate2EvidenceReader
    {
        public readonly Dictionary<string, string> Objects = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body) => Task.FromResult(Objects.TryAdd(key, body));
        public Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix)
            => Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
        public Task<string?> ReadAsync(string bucket, string key) => Task.FromResult(Objects.GetValueOrDefault(key));
    }

    private sealed class Alerts : IGate2Alerts
    {
        public readonly List<(string Subject, string Message)> Sent = new();
        public Task PublishAsync(string subject, string message) { Sent.Add((subject, message)); return Task.CompletedTask; }
    }

    private static Task<JsonObject> Start(JsonObject state, IServiceLeaseStore leases, IServices services, Bundles? bundles = null, Markers? markers = null)
        => Gate2StartStep.RunAsync(state, Exec, Set, leases, LeaseKey, services, markers ?? new Markers(), bundles ?? new Bundles(), "scu-dev-gate2-results-4df6-b9c6", Now);

    // ---- Gate2Start ----

    [Fact]
    public async Task ItTakesTheGate2Lease_ReadsWhatRuns_AndHandsTheBuildItsBundlesAndWhereToWrite()
    {
        var leases = new InMemoryServiceLeaseStore();

        var start = await Start(ImageState(), leases, new Services(Digest, Digest));

        Assert.Equal("ours", start["verdict"]!.GetValue<string>());
        Assert.Equal(Gate2StartStep.LeaseHolder(Exec), leases.Live[LeaseKey].Holder);
        var vars = start["variables"]!.AsArray().OfType<JsonObject>().ToDictionary(v => v["Name"]!.GetValue<string>(), v => v["Value"]!.GetValue<string>());
        Assert.Equal("https://presigned/tests", vars["GATE2_E2E_BUNDLE_URL"]);
        Assert.Equal(Exec, vars["GATE2_EXECUTION"]);
        Assert.Equal($"s3://scu-dev-gate2-results-4df6-b9c6/{Exec}/", vars["GATE2_RESULTS"]);
        Assert.All(start["variables"]!.AsArray().OfType<JsonObject>(), v => Assert.Equal("PLAINTEXT", v["Type"]!.GetValue<string>()));
        Assert.Equal("v-admin", start["before"]!["bundles"]!["adminapp"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnotherGateHoldingTheLease_IsWaitedFor()
    {
        var leases = new InMemoryServiceLeaseStore();
        await ServiceLease.ClaimKeyAsync(leases, LeaseKey, "gate 2", "gate2:req-other", "Gate 2", Now);

        await Assert.ThrowsAsync<Gate2LeaseHeld>(() => Start(ImageState(), leases, new Services(Digest)));
    }

    [Fact]
    public async Task ARetriedStart_FindsItsOwnLease()
    {
        var leases = new InMemoryServiceLeaseStore();
        await Start(ImageState(), leases, new Services(Digest));

        var again = await Start(ImageState(), leases, new Services(Digest));

        Assert.Equal("ours", again["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnImageNotOnEveryTask_IsSuperseded_AndNoBundleIsResolved()
    {
        var bundles = new Bundles();

        var start = await Start(ImageState(), new InMemoryServiceLeaseStore(), new Services(Digest, Other), bundles);

        Assert.Equal("superseded", start["verdict"]!.GetValue<string>());
        Assert.Contains(Other, start["reason"]!.GetValue<string>());
        Assert.Equal(0, bundles.Resolved);
    }

    [Fact]
    public async Task NoTaskRunning_IsNotOurs_Either()
        => Assert.Equal("superseded", (await Start(ImageState(), new InMemoryServiceLeaseStore(), new Services()))["verdict"]!.GetValue<string>());

    [Theory]
    [InlineData("v-admin", "ours")]
    [InlineData("v-older", "superseded")]
    public async Task AClientsGate_IsOurs_OnlyWhileItsBundleIsTheOneItsAppServes(string version, string verdict)
        => Assert.Equal(verdict, (await Start(ClientState(version), new InMemoryServiceLeaseStore(), new Services(Other)))["verdict"]!.GetValue<string>());

    // ---- Gate2Result ----

    private const string Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static string Trx(params (string Method, string Outcome)[] results)
        => $"""<TestRun xmlns="{Ns}"><Results>{string.Concat(results.Select((r, i) => $"""<UnitTestResult testId="t{i}" testName="{r.Method}" outcome="{r.Outcome}" />"""))}</Results><TestDefinitions>{string.Concat(results.Select((r, i) => $"""<UnitTest id="t{i}"><TestMethod className="{r.Method[..r.Method.LastIndexOf('.')]}" name="{r.Method[(r.Method.LastIndexOf('.') + 1)..]}" /></UnitTest>"""))}</TestDefinitions></TestRun>""";

    private static string Browser(int expected, int unexpected = 0) => new JsonObject
    {
        ["suites"] = new JsonArray(), ["errors"] = new JsonArray(),
        ["stats"] = new JsonObject { ["expected"] = expected, ["unexpected"] = unexpected, ["flaky"] = 0, ["skipped"] = 0 },
    }.ToJsonString();

    private static Results Passing(string advisory = "Passed")
    {
        var r = new Results();
        r.Files[$"{Exec}/precondition.trx"] = Trx(("P.Pre", "Passed"));
        r.Files[$"{Exec}/blocking.trx"] = Trx(("B.One", "Passed"));
        r.Files[$"{Exec}/advisory.trx"] = Trx(("A.Adv", advisory));
        r.Files[$"{Exec}/browser.json"] = Browser(1);
        return r;
    }

    private static async Task<(JsonObject Result, InMemoryServiceLeaseStore Leases, Store Store, Alerts Alerts)> StartThenResult(
        Results results, IServices? after = null, Store? store = null, JsonObject? state = null)
    {
        var leases = new InMemoryServiceLeaseStore();
        state ??= ImageState();
        state["gate2Start"] = await Start(state, leases, new Services(Digest));
        store ??= new Store();
        var alerts = new Alerts();
        var result = await Gate2ResultStep.RunAsync(state, Exec, Set, leases, LeaseKey, after ?? new Services(Digest), new Markers(),
            results, Evidence, store, store, alerts, Now);
        return (result, leases, store, alerts);
    }

    [Fact]
    public async Task EveryTierPassing_WithWhatRunsUnchanged_IsPassed_WrittenOnce_AndTheLeaseReleased()
    {
        var (result, leases, store, _) = await StartThenResult(Passing());

        Assert.Equal("Passed", result["outcome"]!.GetValue<string>());
        var written = JsonNode.Parse(store.Objects[$"tests/{Exec}.json"])!;
        Assert.Equal("Passed", written["outcome"]!.GetValue<string>());
        Assert.Equal(Exec, written["execution"]!.GetValue<string>());
        Assert.DoesNotContain(LeaseKey, leases.Live.Keys);
    }

    [Fact]
    public async Task WhatRunsChangingDuringTheGate_IsSuperseded()
        => Assert.Equal("TestsSuperseded", (await StartThenResult(Passing(), after: new Services(Other))).Result["outcome"]!.GetValue<string>());

    [Fact]
    public async Task ABlockingFailure_IsPostDeployTestsFailed()
    {
        var results = Passing();
        results.Files[$"{Exec}/blocking.trx"] = Trx(("B.One", "Failed"));
        Assert.Equal("PostDeployTestsFailed", (await StartThenResult(results)).Result["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMissingResultFile_IsIncomplete_NotAPass()
    {
        var results = Passing();
        results.Files.Remove($"{Exec}/browser.json");
        var (result, leases, _, _) = await StartThenResult(results);
        Assert.Equal("TestsIncomplete", result["outcome"]!.GetValue<string>());
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("browser.json"));
        Assert.DoesNotContain(LeaseKey, leases.Live.Keys);
    }

    [Fact]
    public async Task ASupersededStart_IsRecordedAsSuperseded_WithoutReadingResults()
    {
        var leases = new InMemoryServiceLeaseStore();
        var state = ImageState();
        state["gate2Start"] = await Start(state, leases, new Services(Other));
        var store = new Store();

        var result = await Gate2ResultStep.RunAsync(state, Exec, Set, leases, LeaseKey, new Services(Other), new Markers(),
            new Results(), Evidence, store, store, new Alerts(), Now);

        Assert.Equal("TestsSuperseded", result["outcome"]!.GetValue<string>());
        Assert.True(store.Objects.ContainsKey($"tests/{Exec}.json"));
        Assert.DoesNotContain(LeaseKey, leases.Live.Keys);
    }

    [Fact]
    public async Task AnAdvisoryChange_FromThePreviousGate_IsAlerted_AndTheFirstGateAlertsNothing()
    {
        var store = new Store();
        var first = await StartThenResult(Passing(advisory: "Passed"), store: store);
        Assert.Empty(first.Alerts.Sent);

        // The next execution's gate sees A.Adv fail where the last passed.
        store.Objects[$"tests/req-20261023T205000Z-1-1-mp-1.json"] = store.Objects[$"tests/{Exec}.json"];
        store.Objects.Remove($"tests/{Exec}.json");
        var second = await StartThenResult(Passing(advisory: "Failed"), store: store);

        var alert = Assert.Single(second.Alerts.Sent);
        Assert.Contains("A.Adv: passed -> failed", alert.Message);
        Assert.Equal("Passed", second.Result["outcome"]!.GetValue<string>());   // advisory never blocks
    }

    [Fact]
    public async Task AResultRetriedAfterItsWriteLanded_ChangesNothing()
    {
        var store = new Store();
        var (first, _, _, _) = await StartThenResult(Passing(), store: store);
        var body = store.Objects[$"tests/{Exec}.json"];

        var state = ImageState();
        state["gate2Start"] = await Start(state, new InMemoryServiceLeaseStore(), new Services(Digest));
        await Gate2ResultStep.RunAsync(state, Exec, Set, new InMemoryServiceLeaseStore(), LeaseKey, new Services(Digest), new Markers(),
            Passing(), Evidence, store, store, new Alerts(), Now);

        Assert.Equal(body, store.Objects[$"tests/{Exec}.json"]);
        Assert.Equal("Passed", first["outcome"]!.GetValue<string>());
    }

    private sealed class Unresolvable : IGate2Bundles
    {
        public Task<Gate2Bundles> ResolveAsync(JsonObject state, Gate2Running running) => throw new Gate2NotResolvable("no browser tests record");
    }

    [Fact]
    public async Task NothingToRun_IsIncomplete_NoBuild_AndRecordedAsTestsIncomplete()
    {
        var leases = new InMemoryServiceLeaseStore();
        var state = ImageState();
        state["gate2Start"] = await Gate2StartStep.RunAsync(state, Exec, Set, leases, LeaseKey, new Services(Digest), new Markers(),
            new Unresolvable(), "scu-dev-gate2-results-4df6-b9c6", Now);
        Assert.Equal("incomplete", state["gate2Start"]!["verdict"]!.GetValue<string>());
        Assert.Null(state["gate2Start"]!["variables"]);
        var store = new Store();

        var result = await Gate2ResultStep.RunAsync(state, Exec, Set, leases, LeaseKey, new Services(Digest), new Markers(),
            new Results(), Evidence, store, store, new Alerts(), Now);

        Assert.Equal("TestsIncomplete", result["outcome"]!.GetValue<string>());
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("no browser tests record"));
        Assert.DoesNotContain(LeaseKey, leases.Live.Keys);
    }

    // ---- the set ----

    [Fact]
    public void TheSet_RoundTrips_ThroughItsVariable()
        => Assert.Equal(Set.Encode(), Gate2Set.Decode(Set.Encode()).Encode());
}
