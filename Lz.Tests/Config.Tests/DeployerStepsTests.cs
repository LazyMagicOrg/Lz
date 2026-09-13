using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer functions' steps (DecoupledCd.md P2 stage C2), driven by fakes.
///
/// <para>C1's tests pin each DECISION. These pin the SEQUENCE around them — what is read before what,
/// what is never read at all, and what a refusal carries — which is where "fetch the record from
/// wherever the input says" or "resolve the image before checking provenance" would otherwise hide.</para>
/// </summary>
public class DeployerStepsTests
{
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string Key = "image/scutara/scutaraservice/20260912T171147Z-34707373278.json";
    private const string Repository = "scu-4df6-b9c6-aiphost";
    private const string Digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private const string OldDigest = "sha256:b330844ac7c512956b92b2a0229ec14223119dbf0c075fbfef7df60894fa20eb";
    private const string CurrentTd = "arn:aws:ecs:us-west-2:503947800380:task-definition/scu-mp-aiphost:41";
    private const string NewTd = "arn:aws:ecs:us-west-2:503947800380:task-definition/scu-mp-aiphost:42";

    private static VerifySettings Settings(params string[] blockOn) =>
        new(new[] { "image" }, blockOn.Length == 0 ? new[] { "CRITICAL" } : blockOn, Store, new[] { Repository });

    private static JsonObject State(string bucket = Store, string key = Key, string repository = Repository) => new()
    {
        ["record"] = new JsonObject { ["bucket"] = bucket, ["key"] = key },
        ["target"] = new JsonObject
        {
            ["cluster"] = "scu-dev-cluster", ["service"] = "scu-mp-aiphost",
            ["container"] = "aiphost", ["repository"] = repository,
        },
    };

    // ---------------------------------------------------------------------------------------
    //  Fakes — each records what it was asked, so a test can assert what was NEVER asked
    // ---------------------------------------------------------------------------------------

    private sealed class Records(string? json) : IRecordStore
    {
        public List<(string Bucket, string Key)> Reads { get; } = new();
        public Task<string?> ReadAsync(string bucket, string key) { Reads.Add((bucket, key)); return Task.FromResult(json); }
    }

    private sealed class Registry(RegistryImage? image) : IRegistryImages
    {
        public List<(string Repository, string Digest)> Describes { get; } = new();
        public int Calls => Describes.Count;
        public Task<RegistryImage?> DescribeAsync(string repository, string digest)
        {
            Describes.Add((repository, digest));
            return Task.FromResult(image);
        }
    }

    private sealed class Services(ServiceSnapshot? service, IReadOnlyList<TaskSnapshot>? tasks = null) : IServices
    {
        public Task<ServiceSnapshot?> DescribeAsync(string cluster, string name) => Task.FromResult(service);
        public Task<IReadOnlyList<TaskSnapshot>> RunningTasksAsync(string cluster, string name)
            => Task.FromResult(tasks ?? (IReadOnlyList<TaskSnapshot>)Array.Empty<TaskSnapshot>());
    }

    private static RegistryImage Scanned(string status = "COMPLETE", Dictionary<string, int>? counts = null)
        => new(Digest, status, counts);

    private static ServiceSnapshot Service() => new(CurrentTd, new[] { new DeploymentSnapshot("PRIMARY", CurrentTd, "COMPLETED") });

    private static Task<JsonObject> Verify(
        JsonObject? state = null, string? record = BuildRecordFormatTests.WorkflowEmitted, RegistryImage? image = null,
        ServiceSnapshot? service = null, VerifySettings? settings = null, Records? records = null, Registry? registry = null)
        => VerifyStep.RunAsync(
            state ?? State(), settings ?? Settings(),
            records ?? new Records(record), registry ?? new Registry(image ?? Scanned()),
            new Services(service ?? Service()));

    // ---------------------------------------------------------------------------------------
    //  Verify
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRealRecord_AtItsRealKey_Verifies()
    {
        // The baseline: the record the workflow wrote on 2026-09-12, where it wrote it.
        var registry = new Registry(Scanned());
        var result = await Verify(registry: registry);

        // The image and its scan are looked up by the RECORD's digest in the TARGET's repository.
        Assert.Equal(new[] { (Repository, Digest) }, registry.Describes);
        Assert.Equal(Digest, result["digest"]!.GetValue<string>());
        Assert.Equal("Pass", result["scan"]!["verdict"]!.GetValue<string>());
        Assert.Equal(CurrentTd, result["previousTaskDefinition"]!.GetValue<string>());
        Assert.Contains(Digest, result["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARecordOutsideTheStore_IsRefused_WithoutBeingRead()
    {
        // Not fetched and then judged — never fetched. The input does not get to choose a bucket.
        var records = new Records(BuildRecordFormatTests.WorkflowEmitted);

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(State(bucket: "attacker-bucket"), records: records));

        Assert.Contains(ex.Refusals, r => r.Check == "record.bucket");
        Assert.Empty(records.Reads);
    }

    [Fact]
    public async Task AMissingRecord_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(record: null));
        Assert.Contains("no build record", ex.Message);
    }

    [Fact]
    public async Task AnUnparseableRecord_IsRefused_WithTheParsersReason()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(record: """{ "schema": 2 }"""));
        Assert.Contains("schema 2", ex.Message);
    }

    [Fact]
    public async Task ARecordUnderAnotherRepositorysPrefix_IsRefused()
    {
        // THE PROVENANCE CASE. SellerApp's role may write under client/scutara/scutarasellerapp/ — a
        // record there claiming to be ScutaraService's image was written by the wrong role.
        var registry = new Registry(Scanned());

        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(
            State(key: "client/scutara/scutarasellerapp/20260912T171147Z-34707373278.json"), registry: registry));

        Assert.Contains(ex.Refusals, r => r.Check == "record.key" && r.Reason.Contains("different repository's role"));
        // And the registry was never consulted about an image this record had no right to name.
        Assert.Equal(0, registry.Calls);
    }

    [Fact]
    public async Task ARecordUnderItsOwnPrefixAtTheWrongKey_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(
            State(key: "image/scutara/scutaraservice/20260101T000000Z-1.json")));

        Assert.Contains(ex.Refusals, r => r.Check == "record.key" && r.Reason.Contains("not the key its contents"));
    }

    [Fact]
    public async Task ARepositoryThisEnvironmentDoesNotDeployFrom_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(State(repository: "scu-4df6-b9c6-something-else")));
        Assert.Contains(ex.Refusals, r => r.Check == "target.repository");
    }

    [Fact]
    public async Task AnImageNotInThisRegistry_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyStep.RunAsync(
            State(), Settings(), new Records(BuildRecordFormatTests.WorkflowEmitted), new Registry(null), new Services(Service())));

        Assert.Contains(ex.Refusals, r => r.Check == "identity");
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("PENDING")]
    public async Task AScanStillRunning_IsTheRetriedError_NotARefusal(string status)
    {
        // ScanNotYetAvailable is what the definition retries. A DeployRefused here would end the
        // execution on the first look at a scan that was about to finish.
        await Assert.ThrowsAsync<ScanNotYetAvailable>(() => Verify(image: Scanned(status)));
    }

    [Fact]
    public async Task NoScanYet_IsTheRetriedError_NotARefusal()
    {
        // The first look at a replica that landed seconds ago: no scan exists until scan-on-push starts
        // one. A refusal here would end every triggered deploy that looked too early.
        var ex = await Assert.ThrowsAsync<ScanNotYetAvailable>(() => Verify(image: new RegistryImage(Digest, null, null)));
        Assert.Contains("no scan of this image exists yet", ex.Message);
    }

    [Fact]
    public async Task ABlockingFinding_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(image: Scanned(counts: new() { ["CRITICAL"] = 1 })));
        Assert.Contains(ex.Refusals, r => r.Check == "scan");
    }

    [Fact]
    public async Task AFailedScan_IsRefused_NotPassed()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(image: Scanned("FAILED")));
        Assert.Contains("FAILED", ex.Message);
    }

    [Fact]
    public async Task AMissingService_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => VerifyStep.RunAsync(
            State(), Settings(), new Records(BuildRecordFormatTests.WorkflowEmitted), new Registry(Scanned()), new Services(null)));

        Assert.Contains(ex.Refusals, r => r.Check == "target.service");
    }

    [Fact]
    public async Task EveryRecordProblem_IsReportedTogether()
    {
        // Wrong prefix AND wrong repository: both in one refusal, not one per round trip.
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(
            State(key: "client/scutara/scutarasellerapp/x.json", repository: "nope")));

        Assert.Contains(ex.Refusals, r => r.Check == "record.key");
        Assert.Contains(ex.Refusals, r => r.Check == "target.repository");
    }

    // ---------------------------------------------------------------------------------------
    //  Prepare — the one step that writes before Deploy
    // ---------------------------------------------------------------------------------------

    private const string RegistryHost = "503947800380.dkr.ecr.us-west-2.amazonaws.com";
    private const string Secret = "not-a-real-secret-but-shaped-like-one";

    private sealed class Definitions(Amazon.ECS.Model.TaskDefinition definition) : ITaskDefinitions
    {
        public List<string> Described { get; } = new();
        public List<Amazon.ECS.Model.RegisterTaskDefinitionRequest> Registered { get; } = new();

        public Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string arn)
        {
            Described.Add(arn);
            return Task.FromResult<(Amazon.ECS.Model.TaskDefinition, List<Amazon.ECS.Model.Tag>?)>(
                (definition, new List<Amazon.ECS.Model.Tag> { new() { Key = "System", Value = "scu" } }));
        }

        public Task<string> RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request)
        {
            Registered.Add(request);
            return Task.FromResult(NewTd);
        }
    }

    private static Amazon.ECS.Model.TaskDefinition CurrentDefinition() => new()
    {
        Family = "scu-mp-aiphost",
        Revision = 41,
        TaskRoleArn = "arn:aws:iam::503947800380:role/scu-mp-aiphost-task-0114517",
        ExecutionRoleArn = "arn:aws:iam::503947800380:role/scu-mp-aiphost-exec-7a3f57c",
        Cpu = "512",
        Memory = "1024",
        ContainerDefinitions = new List<Amazon.ECS.Model.ContainerDefinition>
        {
            new()
            {
                Name = "aiphost",
                Image = $"{RegistryHost}/scu-4df6-b9c6-dev-mp-aiphost@{OldDigest}",
                Environment = new List<Amazon.ECS.Model.KeyValuePair> { new() { Name = "LZ_BFF_CLIENT_SECRET", Value = Secret } },
            },
            new() { Name = "otel", Image = "public.ecr.aws/aws-observability/aws-otel-collector:latest" },
        },
    };

    private static JsonObject PrepareState()
    {
        var s = State();
        s["verified"] = new JsonObject { ["digest"] = Digest };
        return s;
    }

    [Fact]
    public async Task Prepare_RegistersTheServicesCurrentRevision_WithOnlyItsContainerRepinned()
    {
        var definitions = new Definitions(CurrentDefinition());

        var result = await PrepareStep.RunAsync(PrepareState(), RegistryHost, new Services(Service()), definitions);

        // The base is what the service runs NOW, not what Verify saw.
        Assert.Equal(new[] { CurrentTd }, definitions.Described);

        var request = Assert.Single(definitions.Registered);
        Assert.Equal($"{RegistryHost}/{Repository}@{Digest}", request.ContainerDefinitions.Single(c => c.Name == "aiphost").Image);
        Assert.Equal("public.ecr.aws/aws-observability/aws-otel-collector:latest",
            request.ContainerDefinitions.Single(c => c.Name == "otel").Image);
        Assert.Equal(Secret, request.ContainerDefinitions.Single(c => c.Name == "aiphost").Environment.Single().Value);
        Assert.Equal("scu-mp-aiphost", request.Family);
        Assert.Equal(CurrentDefinition().TaskRoleArn, request.TaskRoleArn);
        Assert.Equal("scu", Assert.Single(request.Tags).Value);

        Assert.Equal(NewTd, result["taskDefinitionArn"]!.GetValue<string>());
        Assert.Equal(CurrentTd, result["previousTaskDefinitionArn"]!.GetValue<string>());
        Assert.Equal($"{RegistryHost}/scu-4df6-b9c6-dev-mp-aiphost@{OldDigest}", result["previousImage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prepare_ReturnsNothingCopiedFromTheDefinition_SoItsSecretStaysOutOfStateAndEvidence()
    {
        // What Prepare returns lands in execution state, and DeployEvidence.Deployed copies it into kept evidence.
        var result = await PrepareStep.RunAsync(
            PrepareState(), RegistryHost, new Services(Service()), new Definitions(CurrentDefinition()));

        Assert.DoesNotContain(Secret, result.ToJsonString());
        Assert.Equal(new[] { "taskDefinitionArn", "image", "previousTaskDefinitionArn", "previousImage" },
            result.Select(p => p.Key));
    }

    [Fact]
    public async Task Prepare_WithoutAVerifiedDigest_ReadsAndRegistersNothing()
    {
        var definitions = new Definitions(CurrentDefinition());

        var ex = await Assert.ThrowsAsync<DeployRefused>(() =>
            PrepareStep.RunAsync(State(), RegistryHost, new Services(Service()), definitions));

        Assert.Contains(ex.Refusals, r => r.Check == "verified");
        Assert.Empty(definitions.Described);
        Assert.Empty(definitions.Registered);
    }

    [Fact]
    public async Task Prepare_WithNoActiveService_RegistersNothing()
    {
        var definitions = new Definitions(CurrentDefinition());

        var ex = await Assert.ThrowsAsync<DeployRefused>(() =>
            PrepareStep.RunAsync(PrepareState(), RegistryHost, new Services(null), definitions));

        Assert.Contains(ex.Refusals, r => r.Check == "target.service");
        Assert.Empty(definitions.Registered);
    }

    [Fact]
    public async Task Prepare_AContainerTheDefinitionDoesNotHave_RegistersNothing()
    {
        var state = PrepareState();
        state["target"]!["container"] = "worker";
        var definitions = new Definitions(CurrentDefinition());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareStep.RunAsync(state, RegistryHost, new Services(Service()), definitions));

        Assert.Contains("no container named 'worker'", ex.Message);
        Assert.Empty(definitions.Registered);
    }

    [Fact]
    public async Task Prepare_ARegistryThatIsNotAnEcrHost_IsRefusedBeforeAnyRead()
    {
        var definitions = new Definitions(CurrentDefinition());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareStep.RunAsync(PrepareState(), "registry.example.com", new Services(Service()), definitions));

        Assert.Empty(definitions.Described);
    }

    // ---------------------------------------------------------------------------------------
    //  VerifyRollout
    // ---------------------------------------------------------------------------------------

    private static JsonObject RolloutState()
    {
        var s = State();
        s["verified"] = new JsonObject
        {
            ["class"] = "image", ["digest"] = Digest,
            ["builtFrom"] = new JsonObject { ["repo"] = "Scutara/ScutaraService" },
        };
        s["deploy"] = new JsonObject { ["taskDefinitionArn"] = NewTd, ["image"] = $"r/{Repository}@{Digest}" };
        return s;
    }

    private static TaskSnapshot Running(string digest) =>
        new("RUNNING", new[] { new ContainerSnapshot("aiphost", digest) });

    private static ServiceSnapshot Rolling(string state, params DeploymentSnapshot[] others) =>
        new(NewTd, new[] { new DeploymentSnapshot("PRIMARY", NewTd, state) }.Concat(others).ToList());

    [Fact]
    public async Task ALandedRoll_ProducesEvidence_UnderTheDeployKey()
    {
        var result = await VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1",
            new Services(Rolling("COMPLETED"), new[] { Running(Digest), Running(Digest) }),
            DateTimeOffset.Parse("2026-09-12T19:00:00Z"));

        Assert.Equal("Landed", result["verdict"]!.GetValue<string>());
        Assert.Equal("deploys/image/scutara/scutaraservice/req-abc-1.json", result["evidence"]!["key"]!.GetValue<string>());

        using var body = JsonDocument.Parse(result["evidence"]!["body"]!.GetValue<string>());
        Assert.Equal("deployed", body.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(NewTd, body.RootElement.GetProperty("deploy").GetProperty("taskDefinitionArn").GetString());
    }

    [Fact]
    public async Task ARollInProgress_IsTheRetriedError()
    {
        await Assert.ThrowsAsync<RolloutStillRolling>(() => VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1",
            new Services(Rolling("IN_PROGRESS"), new[] { Running(OldDigest), Running(Digest) }),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ACompletedRollRunningSomethingElse_IsNotDeployed()
    {
        await Assert.ThrowsAsync<RolloutNotDeployed>(() => VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1",
            new Services(Rolling("COMPLETED"), new[] { Running(OldDigest) }),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ACircuitBreakerRollback_IsNotDeployed_EvenThoughTheNewPrimaryIsHealthy()
    {
        // THE CASE "READ THE PRIMARY" GETS WRONG. After a rollback ECS's primary deployment is the OLD
        // revision, COMPLETED and healthy. Ours is still listed, FAILED. Judging the primary would see a
        // clean roll; judging ours sees the failure.
        var service = new ServiceSnapshot(CurrentTd, new[]
        {
            new DeploymentSnapshot("PRIMARY", CurrentTd, "IN_PROGRESS"),
            new DeploymentSnapshot("ACTIVE", NewTd, "FAILED"),
        });

        await Assert.ThrowsAsync<RolloutNotDeployed>(() => VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1", new Services(service, new[] { Running(OldDigest) }), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ASupersededDeployment_IsNotDeployed()
    {
        var other = "arn:aws:ecs:us-west-2:503947800380:task-definition/scu-mp-aiphost:43";
        var service = new ServiceSnapshot(other, new[]
        {
            new DeploymentSnapshot("PRIMARY", other, "IN_PROGRESS"),
            new DeploymentSnapshot("ACTIVE", NewTd, "IN_PROGRESS"),
        });

        var ex = await Assert.ThrowsAsync<RolloutNotDeployed>(() => VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1", new Services(service, new[] { Running(Digest) }), DateTimeOffset.UtcNow));
        Assert.Contains("superseded", ex.Message);
    }

    [Fact]
    public async Task AServiceThatIsGone_IsNotDeployed()
    {
        await Assert.ThrowsAsync<RolloutNotDeployed>(() => VerifyRolloutStep.RunAsync(
            RolloutState(), "req-abc-1", new Services(null), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OurDeploymentIsFoundByRevision_NotByBeingPrimary()
    {
        Assert.Equal(("FAILED", (string?)null), VerifyRolloutStep.DeploymentStateOf(new[]
        {
            new DeploymentSnapshot("PRIMARY", CurrentTd, "COMPLETED"),
            new DeploymentSnapshot("ACTIVE", NewTd, "FAILED"),
        }, NewTd));

        Assert.NotNull(VerifyRolloutStep.DeploymentStateOf(null, NewTd).Problem);
    }

    // ---------------------------------------------------------------------------------------
    //  Record
    // ---------------------------------------------------------------------------------------

    private static JsonObject RecordState(string key = "deploys/image/scutara/scutaraservice/req-abc-1.json")
    {
        var s = RolloutState();
        s["rollout"] = new JsonObject
        {
            ["verdict"] = "Landed",
            ["evidence"] = new JsonObject { ["key"] = key, ["body"] = "{\"outcome\":\"deployed\"}" },
        };
        return s;
    }

    [Fact]
    public async Task Record_WritesTheEvidenceOnce_AtTheKeyThisExecutionImplies()
    {
        var writer = new Evidence();

        var result = await RecordStep.RunAsync(RecordState(), "req-abc-1", "scu-dev-deploy-evidence-4df6-b9c6", writer);

        Assert.Equal(
            ("scu-dev-deploy-evidence-4df6-b9c6", "deploys/image/scutara/scutaraservice/req-abc-1.json", "{\"outcome\":\"deployed\"}"),
            Assert.Single(writer.Writes));
        Assert.True(result["written"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Record_EvidenceThisExecutionAlreadyWrote_IsSuccess()
    {
        // THE RETRY THE SDK INTEGRATION COULD NOT SURVIVE: the write landed, its response was lost, and the
        // retry met the conditional write. That is a recorded deploy, not a failed one.
        var result = await RecordStep.RunAsync(RecordState(), "req-abc-1", "bucket", new Evidence(exists: true));

        Assert.False(result["written"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Record_AKeyInStateThatTheRecordDoesNotImply_IsRefused_AndNothingIsWritten()
    {
        var writer = new Evidence();

        var ex = await Assert.ThrowsAsync<DeployRefused>(() =>
            RecordStep.RunAsync(RecordState("deploys/image/someone/else/req-abc-1.json"), "req-abc-1", "bucket", writer));

        Assert.Contains(ex.Refusals, r => r.Check == "rollout.evidence.key");
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task Record_BeforeVerifyRollout_IsRefused()
    {
        await Assert.ThrowsAsync<DeployRefused>(() => RecordStep.RunAsync(RolloutState(), "req-abc-1", "bucket", new Evidence()));
    }

    [Fact]
    public async Task WhatVerifyRolloutProduces_IsExactlyWhatRecordWrites()
    {
        // The two steps derive the key separately; this is the pin that they agree.
        var state = RolloutState();
        state["rollout"] = await VerifyRolloutStep.RunAsync(
            state, "req-abc-1", new Services(Rolling("COMPLETED"), new[] { Running(Digest) }),
            DateTimeOffset.Parse("2026-09-12T19:00:00Z"));
        var writer = new Evidence();

        await RecordStep.RunAsync(state, "req-abc-1", "bucket", writer);

        var write = Assert.Single(writer.Writes);
        Assert.Equal(state["rollout"]!["evidence"]!["key"]!.GetValue<string>(), write.Key);
        Assert.Equal(state["rollout"]!["evidence"]!["body"]!.GetValue<string>(), write.Body);
    }

    // ---------------------------------------------------------------------------------------
    //  RecordFailure
    // ---------------------------------------------------------------------------------------

    private sealed class Evidence(bool exists = false) : IEvidenceWriter
    {
        public List<(string Bucket, string Key, string Body)> Writes { get; } = new();
        public Task<bool> PutOnceAsync(string bucket, string key, string body)
        {
            Writes.Add((bucket, key, body));
            return Task.FromResult(!exists);
        }
    }

    [Fact]
    public async Task AFailureIsWrittenOnce_UnderTheExecutionName()
    {
        var state = State();
        state["error"] = new JsonObject { ["Error"] = "DeployRefused", ["Cause"] = "{\"errorMessage\":\"[scan] …\"}" };
        var writer = new Evidence();

        var result = await RecordFailureStep.RunAsync(state, "req-abc-1", "scu-dev-deploy-evidence-4df6-b9c6", writer, DateTimeOffset.UtcNow);

        var write = Assert.Single(writer.Writes);
        Assert.Equal("failures/req-abc-1.json", write.Key);
        Assert.True(result["written"]!.GetValue<bool>());
        Assert.Contains("DeployRefused", write.Body);
    }

    [Fact]
    public async Task EvidenceAlreadyWrittenByThisExecution_IsNotAFailureOfItsOwn()
    {
        var result = await RecordFailureStep.RunAsync(State(), "req-abc-1", "bucket", new Evidence(exists: true), DateTimeOffset.UtcNow);
        Assert.False(result["written"]!.GetValue<bool>());
    }

    [Fact]
    public void FailureEvidence_CopiesOnlyNamedStateFields()
    {
        // Whitelisted, so a field someone adds to the state later — say a task definition — cannot leak
        // into evidence people read without someone choosing to put it there.
        var state = State();
        state["containerDefinitions"] = new JsonArray(new JsonObject { ["environment"] = "CLIENT_SECRET=hunter2" });

        var body = DeployEvidence.Failed("req-abc-1", DateTimeOffset.UtcNow, state);

        Assert.DoesNotContain("hunter2", body);
        Assert.Contains("scu-mp-aiphost", body); // the target IS copied
    }

    [Fact]
    public void FailureEvidence_CarriesTheRollout_SoARecordThatFailedAfterALandedRollSaysItLanded()
    {
        var state = RecordState();
        state["error"] = new JsonObject { ["Error"] = "AmazonS3Exception", ["Cause"] = "{}" };

        using var body = JsonDocument.Parse(DeployEvidence.Failed("req-abc-1", DateTimeOffset.UtcNow, state));

        Assert.Equal("Landed", body.RootElement.GetProperty("rollout").GetProperty("verdict").GetString());
    }

    // ---------------------------------------------------------------------------------------
    //  The payload and settings contracts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ThePayloadEveryLambdaStatePasses_Unwraps()
    {
        var (name, state) = DeployerInput.Unwrap("""{ "state": { "record": {} }, "executionName": "req-abc-1" }""");

        Assert.Equal("req-abc-1", name);
        Assert.NotNull(state["record"]);
    }

    [Theory]
    [InlineData("""{ "record": {} }""")]                                        // the state itself, not wrapped
    [InlineData("""{ "state": {}, "executionName": 7 }""")]
    [InlineData("""{ "state": {}, "executionName": "../../deploys/x" }""")]      // would move the evidence key
    [InlineData("""{ "state": {}, "executionName": "a b" }""")]
    [InlineData("not json")]
    public void AnythingElse_IsRefused(string payload)
    {
        Assert.Throws<DeployRefused>(() => DeployerInput.Unwrap(payload));
    }

    [Fact]
    public void AnInputMissingFields_NamesEveryOne()
    {
        var ex = Assert.Throws<DeployRefused>(() => DeployerInput.From(new JsonObject
        {
            ["record"] = new JsonObject { ["bucket"] = Store },
            ["target"] = new JsonObject { ["cluster"] = "c" },
        }));

        foreach (var field in new[] { "record.key", "target.service", "target.container", "target.repository" })
            Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void AMissingEnvironmentVariable_IsAFault_NotADefault()
    {
        Assert.Throws<InvalidOperationException>(() => VerifySettings.Read(_ => null));
    }

    [Fact]
    public void AnEmptyList_IsAnEmptyList_WhichTheAllowlistReadsAsNothing()
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.Classes] = "",
            [DeployerEnvironment.ScanBlockOn] = "",
            [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.ImageRepositories] = Repository,
        };

        var settings = VerifySettings.Read(n => env.GetValueOrDefault(n));

        Assert.Empty(settings.Classes);
        Assert.Empty(settings.ScanBlockOn);
    }

    [Fact]
    public void AListValueContainingTheSeparator_IsRefused_NotCorrupted()
    {
        Assert.Throws<InvalidOperationException>(() => DeployerEnvironment.Join(new[] { "a,b" }));
    }

    [Theory]
    [InlineData(typeof(DeployRefused))]
    [InlineData(typeof(ScanNotYetAvailable))]
    [InlineData(typeof(RolloutStillRolling))]
    [InlineData(typeof(RolloutNotDeployed))]
    public void TheErrorsTheMachineBranchesOn_AreNotSuffixed(Type error)
    {
        // Step Functions matches the Lambda runtime's `exception.GetType().Name`. These names are that
        // wire value; an "Exception" suffix would be carried into every Retry and Catch.
        Assert.DoesNotContain("Exception", error.Name);
        Assert.True(typeof(Exception).IsAssignableFrom(error));
    }

    // ---------------------------------------------------------------------------------------
    //  The signature hook, end to end over fakes
    // ---------------------------------------------------------------------------------------

    private const string Registry_ = "503947800380.dkr.ecr.us-west-2.amazonaws.com";
    private static readonly string Image = $"{Registry_}/{Repository}@{Digest}";
    private static readonly HookSettings Hook = new(
        new[] { "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutaraservice" },
        new[] { $"{Registry_}/{Repository}" });

    private sealed class HookAws(IReadOnlyList<string?> images, bool revisionKnown = true, bool throws = false) : IHookReads
    {
        public Task<string?> TaskDefinitionOfRevisionAsync(string arn)
            => throws ? throw new InvalidOperationException("AccessDenied") : Task.FromResult(revisionKnown ? NewTd : null);
        public Task<IReadOnlyList<string?>> ContainerImagesAsync(string td) => Task.FromResult(images);
        public Task<(string Username, string Password)> RegistryCredentialsAsync() => Task.FromResult(("AWS", "s3cr3t-token"));
    }

    private sealed class FakeNotation(Func<string, (int, string, string)>? verify = null, string? installError = null) : INotation
    {
        public int Installs { get; private set; }
        public List<(string Reference, IReadOnlyDictionary<string, string> Env)> Verifications { get; } = new();

        public Task<string?> InstallAsync(NotationLayout layout, string trustPolicyJson) { Installs++; return Task.FromResult(installError); }

        public Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(
            NotationLayout layout, string reference, IReadOnlyDictionary<string, string> environment)
        {
            Verifications.Add((reference, environment));
            return Task.FromResult(verify?.Invoke(reference) ?? (0, $"Successfully verified signature for {reference}\n", ""));
        }
    }

    private static string HookEvent(string stage = "PRE_SCALE_UP") => JsonSerializer.Serialize(new
    {
        executionId = "x",
        lifecycleStage = stage,
        executionDetails = new { serviceArn = "arn:aws:ecs:us-west-2:1:service/c/s", targetServiceRevisionArn = "arn:aws:ecs:us-west-2:1:service-revision/c/s/1" },
    });

    private static readonly NotationLayout Layout = NotationLayout.Under("/tmp/lz-notation");

    [Fact]
    public async Task EveryImageSigned_Succeeds_AndEachDistinctImageIsVerifiedOnce()
    {
        var notation = new FakeNotation();

        var (status, reason) = await SignatureHookStep.RunAsync(
            HookEvent(), Hook, new HookAws(new[] { Image, Image }), notation, Layout);

        Assert.Equal(HookStatus.SUCCEEDED, status);
        var only = Assert.Single(notation.Verifications);
        Assert.Equal("s3cr3t-token", only.Env["NOTATION_PASSWORD"]);
        // The credential reaches Notation and nothing else.
        Assert.DoesNotContain("s3cr3t-token", reason);
    }

    [Fact]
    public async Task ATag_Fails_AndTheVerifierIsNeverInstalled()
    {
        var notation = new FakeNotation();

        var (status, _) = await SignatureHookStep.RunAsync(
            HookEvent(), Hook, new HookAws(new[] { $"{Registry_}/{Repository}:latest" }), notation, Layout);

        Assert.Equal(HookStatus.FAILED, status);
        Assert.Equal(0, notation.Installs);
    }

    [Fact]
    public async Task APackageWithoutTheVerifier_Fails()
    {
        var (status, reason) = await SignatureHookStep.RunAsync(
            HookEvent(), Hook, new HookAws(new[] { Image }), new FakeNotation(installError: "missing notation/notation"), Layout);

        Assert.Equal(HookStatus.FAILED, status);
        Assert.Contains("missing notation/notation", reason);
    }

    [Fact]
    public async Task AnImageThatDoesNotVerify_Fails()
    {
        var notation = new FakeNotation(_ => (1, "", "Error: signature verification failed: no signature is associated with the artifact"));

        var (status, reason) = await SignatureHookStep.RunAsync(HookEvent(), Hook, new HookAws(new[] { Image }), notation, Layout);

        Assert.Equal(HookStatus.FAILED, status);
        Assert.Contains("no signature is associated", reason);
    }

    [Fact]
    public async Task AnUnreadableRevision_Fails()
    {
        var (status, _) = await SignatureHookStep.RunAsync(
            HookEvent(), Hook, new HookAws(new[] { Image }, revisionKnown: false), new FakeNotation(), Layout);

        Assert.Equal(HookStatus.FAILED, status);
    }

    [Fact]
    public async Task AnExceptionAnywhere_Fails_RatherThanEscaping()
    {
        var (status, reason) = await SignatureHookStep.RunAsync(
            HookEvent(), Hook, new HookAws(new[] { Image }, throws: true), new FakeNotation(), Layout);

        Assert.Equal(HookStatus.FAILED, status);
        Assert.Contains("AccessDenied", reason);
    }

    [Fact]
    public async Task TheWrongStage_Fails()
    {
        var (status, _) = await SignatureHookStep.RunAsync(
            HookEvent("POST_SCALE_UP"), Hook, new HookAws(new[] { Image }), new FakeNotation(), Layout);

        Assert.Equal(HookStatus.FAILED, status);
    }
}
