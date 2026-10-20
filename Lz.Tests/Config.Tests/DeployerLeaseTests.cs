using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Ops;
using Lz.Aws.Pipeline;
using Lz.Core.Config;
using Lz.Tests.Orchestration.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer under the per-service lease (DecoupledCd.md V-J, slice 3): Prepare claims it as its execution, waits while
/// another writer holds it, and Record and RecordFailure release it. The plan grants exactly that and no delete.
/// </summary>
public class DeployerLeaseTests
{
    private const string Execution = "req-abc-1";
    private const string Cluster = "scu-dev-cluster", Service = "scu-mp-aiphost";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly string Key = ServiceLease.KeyFor(Cluster, Service);

    // A Prepare that reaches the store and nothing else: the refusals below come before any ECS read, and a claimed lease
    // is checked through the ECS fakes refusing (no service), which Prepare must release on.
    private sealed class NoServices : IServices
    {
        public int Described;
        public Task<ServiceSnapshot?> DescribeAsync(string cluster, string service) { Described++; return Task.FromResult<ServiceSnapshot?>(null); }
        public Task<IReadOnlyList<TaskSnapshot>> RunningTasksAsync(string cluster, string service) => throw new InvalidOperationException("not reached");
        public Task<IReadOnlyList<ServiceDeploymentRecord>> RecentDeploymentsAsync(string cluster, string service) => throw new InvalidOperationException("not reached");
    }

    private sealed class NoDefinitions : ITaskDefinitions
    {
        public Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string arn)
            => throw new InvalidOperationException("not reached");
        public Task<string> RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request)
            => throw new InvalidOperationException("not reached");
    }

    private sealed class Writer : IEvidenceWriter
    {
        public Task<bool> PutOnceAsync(string bucket, string key, string body) => Task.FromResult(true);
    }

    private static JsonObject ImageState() => new()
    {
        ["record"] = new JsonObject { ["bucket"] = "b", ["key"] = "k" },
        ["target"] = new JsonObject
        {
            ["cluster"] = Cluster, ["service"] = Service, ["container"] = "aiphost", ["repository"] = "scutara/scutaraservice",
        },
        ["verified"] = new JsonObject { ["digest"] = "sha256:" + new string('a', 64), ["builtAt"] = "2026-10-20T17:00:00Z" },
    };

    private static Task<JsonObject> Prepare(InMemoryServiceLeaseStore leases, NoServices services, string execution = Execution)
        => PrepareStep.RunAsync(ImageState(), execution, "503947800380.dkr.ecr.us-west-2.amazonaws.com", services, new NoDefinitions(), leases, Now);

    [Fact]
    public async Task Prepare_WaitsWhileAnotherWriterHoldsTheService_HavingReadNothing()
    {
        var leases = new InMemoryServiceLeaseStore();
        await ServiceLease.ClaimAsync(leases, Cluster, Service, "updatecontainer:tmay@ws", "lz updatecontainer", Now.AddMinutes(-2));
        var services = new NoServices();

        var ex = await Assert.ThrowsAsync<ServiceRollInProgress>(() => Prepare(leases, services));

        Assert.Contains("updatecontainer:tmay@ws", ex.Message);
        Assert.Equal(0, services.Described);
        Assert.Equal("updatecontainer:tmay@ws", leases.Live[Key].Holder);
    }

    [Fact]
    public async Task Prepare_ARefusalAfterTheClaim_ReleasesIt()
    {
        var leases = new InMemoryServiceLeaseStore();
        var services = new NoServices();

        await Assert.ThrowsAsync<DeployRefused>(() => Prepare(leases, services));   // no ACTIVE service

        Assert.Equal(1, services.Described);
        Assert.Equal("deployer:req-abc-1", leases.Items[Key].Holder);
        Assert.Empty(leases.Live);
    }

    [Fact]
    public async Task Prepare_RetriedByItsOwnExecution_FindsItsOwnClaim()
    {
        // A Lambda fault after the claim: Step Functions retries Prepare, which must not wait out its own lease.
        var leases = new InMemoryServiceLeaseStore();
        var mine = (ServiceLeaseClaim.Claimed)await ServiceLease.ClaimAsync(leases, Cluster, Service, PrepareStep.LeaseHolder(Execution), "roll", Now.AddSeconds(-5));

        var claim = await ServiceLease.ClaimReentrantAsync(leases, Cluster, Service, PrepareStep.LeaseHolder(Execution), "roll", Now);

        Assert.Equal(mine.Lease.Token, Assert.IsType<ServiceLeaseClaim.Claimed>(claim).Lease.Token);
        Assert.IsType<ServiceLeaseClaim.Held>(
            await ServiceLease.ClaimReentrantAsync(leases, Cluster, Service, PrepareStep.LeaseHolder("req-other-1"), "roll", Now));
    }

    [Fact]
    public async Task Record_ReleasesThisExecutionsLease()
    {
        var leases = new InMemoryServiceLeaseStore();
        await ServiceLease.ClaimAsync(leases, Cluster, Service, PrepareStep.LeaseHolder(Execution), "roll", Now);

        var released = await LeaseRelease.AfterAsync(ImageState(), Execution, leases);

        Assert.Equal("released", released);
        Assert.Empty(leases.Live);
    }

    [Fact]
    public async Task RecordFailure_ReleasesThisExecutionsLease_AndSaysSo()
    {
        var leases = new InMemoryServiceLeaseStore();
        await ServiceLease.ClaimAsync(leases, Cluster, Service, PrepareStep.LeaseHolder(Execution), "roll", Now);
        var state = ImageState();
        state["error"] = new JsonObject { ["Error"] = "RolloutNotDeployed" };

        var result = await RecordFailureStep.RunAsync(state, Execution, "evidence", new Writer(), Now, leases);

        Assert.Equal("released", result["lease"]!.GetValue<string>());
        Assert.Empty(leases.Live);
    }

    [Fact]
    public async Task AnotherWritersLease_IsNotReleased()
    {
        var leases = new InMemoryServiceLeaseStore();
        await ServiceLease.ClaimAsync(leases, Cluster, Service, "deploytenant:tmay@ws", "lz deploytenant mp", Now);

        Assert.Equal("held by another writer; not released", await LeaseRelease.AfterAsync(ImageState(), Execution, leases));
        Assert.Equal("deploytenant:tmay@ws", leases.Live[Key].Holder);
        Assert.Equal("none held", await LeaseRelease.AfterAsync(ImageState(), Execution, new InMemoryServiceLeaseStore()));
    }

    [Fact]
    public async Task AFailedRelease_IsReported_NeverThrown()
    {
        var leases = new InMemoryServiceLeaseStore { FailWith = new InvalidOperationException("AccessDenied") };

        var released = await LeaseRelease.AfterAsync(ImageState(), Execution, leases);

        Assert.StartsWith("release failed, so it expires instead", released);
        Assert.Contains("AccessDenied", released);
    }

    [Fact]
    public async Task AClientBundlesExecution_HasNoServiceLeaseToRelease()
    {
        var state = new JsonObject { ["record"] = new JsonObject { ["bucket"] = "b", ["key"] = "k" }, ["target"] = new JsonObject { ["bucket"] = "scu-sellerapp" } };
        Assert.Equal("none: the input names no service", await LeaseRelease.AfterAsync(state, Execution, new InMemoryServiceLeaseStore()));
    }

    // ---- the plan ----

    // The planner tests' configuration (DeployerPlannerTests.Config), without approval.
    private static PipelineDeployer Plan() => DeployerPlanner.Plan(new SystemConfig
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
        Rollback = new RollbackConfig { PinImageDigest = true },
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            ArtifactAccountId = "147440642635",
            TargetAccountId = "503947800380",
            Classes = new List<string> { "image" },
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Scutara/ScutaraService", Class = "image", Artifacts = new List<string> { "aiphost" } },
            },
            Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
            Scan = new PipelineScanConfig { BlockOn = new List<string> { "CRITICAL" } },
            Approval = new PipelineApprovalConfig { Required = false },
        },
    }, "503947800380");

    private static IEnumerable<JsonElement> LeaseStatements(DeployerFunction f)
        => JsonDocument.Parse(f.Policy).RootElement.GetProperty("Statement").EnumerateArray()
            .Where(s => s.GetProperty("Action").EnumerateArray().Any(a => a.GetString()!.StartsWith("dynamodb:")));

    [Fact]
    public void OnlyPrepareRecordAndRecordFailure_ReachTheLease_InTheSystemTable_ByItsKeysAlone()
    {
        var plan = Plan();
        var withLease = plan.Functions.Where(f => LeaseStatements(f).Any()).Select(f => f.Handler).OrderBy(h => h).ToList();
        Assert.Equal(new[] { DeployerHandlers.Prepare, DeployerHandlers.Record, DeployerHandlers.RecordFailure }.OrderBy(h => h), withLease);

        foreach (var f in plan.Functions.Where(f => LeaseStatements(f).Any()))
        {
            var s = Assert.Single(LeaseStatements(f));
            Assert.Equal("arn:aws:dynamodb:us-west-2:503947800380:table/scu", s.GetProperty("Resource").GetString());
            Assert.Equal(new[] { "lease#ecs-service#*" },
                s.GetProperty("Condition").GetProperty("ForAllValues:StringLike").GetProperty("dynamodb:LeadingKeys").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("scu", f.Environment[DeployerEnvironment.LeaseTable]);
        }

        var prepareActions = LeaseStatements(plan.Functions.Single(f => f.Handler == DeployerHandlers.Prepare))
            .Single().GetProperty("Action").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("dynamodb:PutItem", prepareActions);
        var recordActions = LeaseStatements(plan.Functions.Single(f => f.Handler == DeployerHandlers.Record))
            .Single().GetProperty("Action").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.DoesNotContain("dynamodb:PutItem", recordActions);   // a release cannot claim
    }

    [Fact]
    public void Prepare_WaitsOnAHeldLease_ForLongerThanALeaseLasts()
    {
        var prepare = JsonDocument.Parse(Plan().Definition).RootElement.GetProperty("States").GetProperty("Prepare");
        var retry = prepare.GetProperty("Retry").EnumerateArray()
            .Single(r => r.GetProperty("ErrorEquals").EnumerateArray().Any(e => e.GetString() == nameof(ServiceRollInProgress)));

        var waited = TimeSpan.FromSeconds(retry.GetProperty("IntervalSeconds").GetInt32() * retry.GetProperty("MaxAttempts").GetInt32());
        Assert.True(waited > ServiceLease.Duration, $"Prepare waits {waited}, a lease lasts {ServiceLease.Duration}");
    }

    [Fact]
    public void TheLeaseOutlastsTheRolloutWait()
        => Assert.True(TimeSpan.FromSeconds(DeployerPlanner.RolloutRetryIntervalSeconds * DeployerPlanner.RolloutRetryMaxAttempts)
                       < ServiceLease.Duration);
}
