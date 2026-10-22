using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>The intake poller's tick and the evidence it reads (DecoupledCd.md §15.4, §5.3; V-B).</summary>
public class IntakePollStepTests
{
    private const long RepoId = 1001, OwnerId = 2002;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
    private static readonly IntakeRepository Service = new("Example/ExampleService", RepoId, OwnerId, ".github/workflows/build-aiphost.yml", "dry", "image", new[] { "aiphost" });

    // ---- the repositories variable (V-D: Lambda refuses a configuration update over 5,120 bytes) ----

    private static IntakeRepository WithGate1(string name, long id) => new($"Scutara/{name}", id, OwnerId, ".github/workflows/build-bundle.yml", "live", "client",
        new[] { name.ToLowerInvariant() }, new IntakeGate1("tests", "ViewModelsTest/bin/Debug/net10.0/ViewModelsTest.dll", "FullyQualifiedName~ViewModelsTest", "10.0.301",
            new string('c', 128), Array.Empty<IntakeImage>(), BuildRoot: $"/home/runner/work/{name}/{name}"), "builtfrom");

    [Fact]
    public void TheRepositories_RoundTripThroughTheirVariable()
    {
        var repos = new[] { Service, WithGate1("ScutaraSellerApp", 2) };
        var decoded = IntakeRepositories.Decode(IntakeRepositories.Encode(repos));
        Assert.Equal(repos.Select(r => (r.Repo, r.RepositoryId, r.Mode, r.Gate1?.Run, r.Gate1?.BuildRoot, r.BuiltFrom)),
            decoded.Select(r => (r.Repo, r.RepositoryId, r.Mode, r.Gate1?.Run, r.Gate1?.BuildRoot, r.BuiltFrom)));
    }

    [Fact]
    public void ThePlainJsonTodaysFunctionsCarry_StillDecodes()
        => Assert.Equal("Example/ExampleService", Assert.Single(IntakeRepositories.Decode(System.Text.Json.JsonSerializer.Serialize(new[] { Service }))).Repo);

    [Fact]
    public void SevenRepositoriesWithGate1_FitWellInsideLambdasLimit()
    {
        // Measured 2026-10-22: four repositories as plain JSON (3,395 bytes) pushed the publish function's update past the
        // 5,120 bytes Lambda allows a configuration update, beside its other variables.
        var seven = Enumerable.Range(1, 7).Select(i => WithGate1($"ScutaraRepository{i}", 1000 + i)).ToList();
        var encoded = IntakeRepositories.Encode(seven);
        Assert.True(encoded.Length < 2000, $"{encoded.Length} bytes");
        Assert.Equal(7, IntakeRepositories.Decode(encoded).Count);
    }

    [Fact]
    public void NoRepositories_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => IntakeRepositories.Decode(IntakeRepositories.Encode(Array.Empty<IntakeRepository>())));

    private static string Name(long run, int attempt = 1, int retry = 0) => IntakeNames.For(RepoId, run, attempt, retry);

    private static GitHubRun Run(long id, int attempt = 1, string conclusion = "success", TimeSpan? ago = null) => new(
        id, attempt, RepoId, OwnerId, RepoId, Service.WorkflowPath, "main", "push", "completed", conclusion,
        Now - (ago ?? TimeSpan.FromMinutes(2)) - TimeSpan.FromMinutes(3), Now - (ago ?? TimeSpan.FromMinutes(2)));

    private sealed class Fakes : IIntakeRuns, IIntakeEvidenceStore, IIntakeExecutions, IIntakeAlerts
    {
        public List<GitHubRun> Runs = new();
        public (string Repo, string File, DateTimeOffset Since)? Asked;
        public readonly Dictionary<string, string> Evidence = new(StringComparer.Ordinal);
        public readonly List<(string Name, string Status)> Executions = new();
        public readonly List<(string Name, string Input)> Started = new();
        public readonly List<string> Published = new();
        public bool StartsCollide;   // StartExecution answers ExecutionAlreadyExists, as for a name another start just took

        public Task<IReadOnlyList<GitHubRun>> CompletedRunsAsync(string repo, string workflowFile, DateTimeOffset since)
        { Asked = (repo, workflowFile, since); return Task.FromResult<IReadOnlyList<GitHubRun>>(Runs); }

        public Task<IReadOnlyList<string>> KeysAsync(string prefix)
            => Task.FromResult<IReadOnlyList<string>>(Evidence.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());

        public Task<bool> PutOnceAsync(string key, string body) => Task.FromResult(Evidence.TryAdd(key, body));

        public Task<IReadOnlyList<(string Name, string Status)>> SinceAsync(DateTimeOffset since)
            => Task.FromResult<IReadOnlyList<(string, string)>>(Executions);

        public Task<bool> StartAsync(string name, string input)
        {
            if (StartsCollide || Executions.Any(e => e.Name == name)) return Task.FromResult(false);
            Executions.Add((name, "RUNNING"));
            Started.Add((name, input));
            return Task.FromResult(true);
        }

        public Task PublishAsync(string subject, string message) { Published.Add(message); return Task.CompletedTask; }
    }

    private static Task<JsonObject> Tick(Fakes f) => IntakePollStep.RunAsync(new[] { Service }, f, f, f, f, Now);

    // ---- the evidence ----

    [Fact]
    public void States_ReadOutcomesFaultsAndExecutions()
    {
        var states = IntakeEvidence.States(
            new[]
            {
                IntakeEvidence.TakenIn(Name(1)), IntakeEvidence.Refused(Name(2)),
                IntakeEvidence.Fault(Name(3)), IntakeEvidence.Fault(Name(3, retry: 1)),
                "staged/" + Name(4) + ".json", "alarms/abc.json",
            },
            new[] { (Name(3, retry: 2), "RUNNING"), (Name(5), "SUCCEEDED"), (Name(6), "FAILED"), ("not-an-intake", "RUNNING") });

        Assert.Equal(IntakeOutcome.TakenIn, states[Name(1)].Outcome);
        Assert.Equal(IntakeOutcome.Refused, states[Name(2)].Outcome);
        Assert.Equal(1, states[Name(3)].FaultedTry);
        Assert.True(states[Name(3)].Running);
        Assert.False(states[Name(3)].ClosedWithoutRecord);
        Assert.True(states[Name(5)].ClosedWithoutRecord);     // closed, recorded nothing
        Assert.True(states[Name(6)].ClosedWithoutRecord);
        Assert.False(states.ContainsKey(Name(4)));             // staging is not a decision
    }

    [Fact]
    public void AFaultedTryThatClosed_IsNotClosedWithoutRecord_ItIsAFault()
    {
        var states = IntakeEvidence.States(new[] { IntakeEvidence.Fault(Name(7)) }, new[] { (Name(7), "FAILED") });
        Assert.Equal(0, states[Name(7)].FaultedTry);
        Assert.False(states[Name(7)].ClosedWithoutRecord);
    }

    [Fact]
    public void TheHighestFaultedTry_IsKept_WhateverOrderTheKeysCome()
        => Assert.Equal(2, IntakeEvidence.States(
            new[] { IntakeEvidence.Fault(Name(9, retry: 2)), IntakeEvidence.Fault(Name(9)), IntakeEvidence.Fault(Name(9, retry: 1)) },
            Array.Empty<(string, string)>())[Name(9)].FaultedTry);

    [Fact]
    public async Task AStartAnotherStartTook_IsNotReportedAsStarted()
    {
        var f = new Fakes { Runs = { Run(9001) }, StartsCollide = true };
        var result = await Tick(f);
        Assert.Empty(result["started"]!.AsArray());
    }

    [Fact]
    public void AnOutcomeUnderARetryName_IsNotAnOutcome()   // outcomes are recorded under the attempt's first-try name
        => Assert.Empty(IntakeEvidence.States(new[] { IntakeEvidence.TakenIn(Name(8, retry: 1)) }, Array.Empty<(string, string)>()));

    // ---- the tick ----

    [Fact]
    public async Task ANewSuccessfulRun_IsStarted_UnderItsName_WithIdsOnly()
    {
        var f = new Fakes { Runs = { Run(9001) } };

        var result = await Tick(f);

        var (name, input) = Assert.Single(f.Started);
        Assert.Equal(Name(9001), name);
        var json = JsonNode.Parse(input)!.AsObject();
        Assert.Equal(new[] { "repo", "repositoryId", "runId", "runAttempt", "retry" }, json.Select(p => p.Key));
        Assert.Equal(9001, json["runId"]!.GetValue<long>());
        Assert.Equal(Name(9001), result["started"]![0]!.GetValue<string>());
        Assert.Equal(("Example/ExampleService", "build-aiphost.yml", Now - TimeSpan.FromDays(7)), f.Asked);   // a week: past the artifacts' day
    }

    [Fact]
    public async Task ADecidedOrRunningAttempt_IsNotStartedAgain()
    {
        var f = new Fakes { Runs = { Run(9001), Run(9002), Run(9003) } };
        f.Evidence[IntakeEvidence.TakenIn(Name(9001))] = "{}";
        f.Evidence[IntakeEvidence.Refused(Name(9002))] = "{}";
        f.Executions.Add((Name(9003), "RUNNING"));

        await Tick(f);

        Assert.Empty(f.Started);
    }

    [Fact]
    public async Task AFault_IsRetriedUnderTheNextName()
    {
        var f = new Fakes { Runs = { Run(9001) } };
        f.Evidence[IntakeEvidence.Fault(Name(9001))] = "{}";
        f.Executions.Add((Name(9001), "FAILED"));

        await Tick(f);

        Assert.Equal(Name(9001, retry: 1), Assert.Single(f.Started).Name);
    }

    [Fact]
    public async Task AnAlarm_IsSentOnce_ThoughEveryTickRaisesIt()
    {
        // Finished 20 minutes ago and still running: overdue on every tick.
        var f = new Fakes { Runs = { Run(9001, ago: TimeSpan.FromMinutes(20)) } };
        f.Executions.Add((Name(9001), "RUNNING"));

        var first = await Tick(f);
        var second = await Tick(f);

        Assert.Single(f.Published);
        Assert.Contains("no outcome", f.Published[0]);
        Assert.Single(first["alarmed"]!.AsArray());
        Assert.Empty(second["alarmed"]!.AsArray());
        Assert.Single(f.Evidence.Keys, k => k.StartsWith("alarms/"));
    }

    [Fact]
    public async Task AStartThatCollides_IsNotCountedAsStarted()
    {
        var f = new Fakes { Runs = { Run(9001) } };
        f.Executions.Add((Name(9001), "SUCCEEDED"));       // closed without record: alarms, starts nothing
        f.Evidence[IntakeEvidence.Fault(Name(9001))] = "{}"; // ...unless it faulted, which retries
        f.Executions.Add((Name(9001, retry: 1), "SUCCEEDED"));
        f.Evidence[IntakeEvidence.Fault(Name(9001, retry: 1))] = "{}";

        var result = await Tick(f);

        Assert.Equal(Name(9001, retry: 2), Assert.Single(f.Started).Name);
        Assert.Single(result["started"]!.AsArray());
    }

    [Fact]
    public void TheRepositories_RoundTripThroughTheirVariable_AndNoneIsRefused()
    {
        var back = IntakeRepositories.Decode(IntakeRepositories.Encode(new[] { Service }));
        Assert.Equal(Service.Repo, back.Single().Repo);
        Assert.Equal(Service.Artifacts, back.Single().Artifacts);
        Assert.Equal("build-aiphost.yml", back.Single().WorkflowFile);
        Assert.Throws<InvalidOperationException>(() => IntakeRepositories.Decode("[]"));
    }
}
