using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The trigger's decisions (DecoupledCd.md §4.3, P2 stage D): which forwarded S3 events start which executions, under
/// which names, and what a name that is already taken means.
///
/// <para>THE EVENT IS AWS'S DOCUMENTED SHAPE with this system's values in it — the Object Created example from the S3
/// user guide's EventBridge event page, carrying the record key the first real build wrote. Every refusal below starts
/// from that event and changes one field, so each test says exactly which field it is about.</para>
/// </summary>
public class DeployerTriggerTests
{
    private const string BuildAccount = "147440642635";
    private const string DevAccount = "503947800380";
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string Key = "image/scutara/scutaraservice/20260912T221318Z-34722020365.json";
    private const string Machine = "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer";

    private static TriggerTarget Mp => new("mp", new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost"));

    private static TriggerSettings Settings(params TriggerTarget[] targets) => new(
        BuildAccount, Store, Machine,
        new[] { new TriggerRoute("image/scutara/scutaraservice/", targets.Length == 0 ? new[] { Mp } : targets) },
        DeployerTrigger.DefaultRefs);

    private static JsonObject Event() => new()
    {
        ["version"] = "0",
        ["id"] = "17793124-05d4-b198-2fde-7ededc63b103",
        ["detail-type"] = "Object Created",
        ["source"] = "aws.s3",
        ["account"] = BuildAccount,
        ["time"] = "2026-09-12T22:13:19Z",
        ["region"] = "us-west-2",
        ["resources"] = new JsonArray($"arn:aws:s3:::{Store}"),
        ["detail"] = new JsonObject
        {
            ["version"] = "0",
            ["event-version"] = "1.2",
            ["bucket"] = new JsonObject { ["name"] = Store },
            ["object"] = new JsonObject
            {
                ["key"] = Key, ["size"] = 612, ["etag"] = "b1946ac92492d2347c6235b4d2611184",
                ["version-id"] = "IYV3p45BT0ac8hjHg1houSdS1a.Mro8e", ["sequencer"] = "617f08299329d189",
            },
            ["request-id"] = "N4N7GDK58NMKJ12R",
            ["requester"] = BuildAccount,
            ["source-ip-address"] = "192.0.2.1",
            ["reason"] = "PutObject",
        },
    };

    private static JsonObject Detail(JsonObject e) => (JsonObject)e["detail"]!;

    private const string ExpectedInput =
        "{\"record\":{\"bucket\":\"scu-build-records-4df6-b9c6\",\"key\":\"image/scutara/scutaraservice/20260912T221318Z-34722020365.json\"}," +
        "\"target\":{\"cluster\":\"scu-dev-cluster\",\"service\":\"scu-mp-aiphost\",\"container\":\"aiphost\",\"repository\":\"scu-4df6-b9c6-aiphost\"}}";

    // ---------------------------------------------------------------------------------------
    //  What a record's event starts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ARecordsEvent_StartsTheDeployer_NamedForTheBuildAndTheTenant()
    {
        var executions = DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings());

        var only = Assert.Single(executions);
        Assert.Equal("req-20260912T221318Z-34722020365-mp-1", only.Name);
        Assert.Equal(ExpectedInput, only.Input);
    }

    [Fact]
    public void TheInput_IsTheDeployersInputFields_AndReadsBackAsTheRecordAndTarget()
    {
        var input = (JsonObject)JsonNode.Parse(DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings()).Single().Input)!;

        Assert.Equal(DeployerInput.InputFields, input.Select(p => p.Key));

        var read = DeployerInput.From(input);
        Assert.Equal(new RecordLocation(Store, Key), read.Record);
        Assert.Equal(Mp.Target, read.Target);
    }

    [Fact]
    public void TheSameEventTwice_GivesTheSameNamesAndByteIdenticalInput()
    {
        // StartExecution absorbs a duplicate only when the name AND the input match, so this is the premise the whole
        // at-least-once story rests on. Different envelope ids and times, as a real duplicate delivery would have.
        var second = Event();
        second["id"] = "2ee9cc15-d022-99ea-1fb8-1b1bac4850f9";
        second["time"] = "2026-09-12T22:13:25Z";

        var a = DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings());
        var b = DeployerTrigger.ExecutionsFor(second.ToJsonString(), Settings());

        Assert.Equal(a, b);
    }

    [Fact]
    public void TwoTenants_TwoExecutions_WhoseNamesCannotCollide()
    {
        var other = new TriggerTarget("zz", new DeployTarget("scu-dev-cluster", "scu-zz-aiphost", "aiphost", "scu-4df6-b9c6-aiphost"));

        var executions = DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings(Mp, other));

        Assert.Equal(
            new[] { "req-20260912T221318Z-34722020365-mp-1", "req-20260912T221318Z-34722020365-zz-1" },
            executions.Select(e => e.Name));
        Assert.Contains("scu-zz-aiphost", executions[1].Input);
    }

    // ---------------------------------------------------------------------------------------
    //  What it refuses — one field at a time
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("an event put on the bus by the target account itself", "account", DevAccount)]
    [InlineData("an event with no account", "account", null)]
    [InlineData("another service's event", "source", "aws.ecr")]
    [InlineData("a custom event", "source", "lz.test")]
    [InlineData("a deletion", "detail-type", "Object Deleted")]
    public void AnEventThatIsNotTheBuildAccountsRecordCreation_StartsNothing(string why, string field, string? value)
    {
        var e = Event();
        if (value is null) e.Remove(field); else e[field] = value;

        var ex = Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(e.ToJsonString(), Settings()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), why);
    }

    [Fact]
    public void AnObjectInAnotherBucket_StartsNothing()
    {
        var e = Event();
        Detail(e)["bucket"] = new JsonObject { ["name"] = "scu-artifacts-4df6-b9c6" };

        var ex = Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(e.ToJsonString(), Settings()));
        Assert.Contains("scu-artifacts-4df6-b9c6", ex.Message);
    }

    [Theory]
    [InlineData("CopyObject")]
    [InlineData("CompleteMultipartUpload")]
    [InlineData(null)]
    public void AnObjectNotWrittenByPutObject_StartsNothing(string? reason)
    {
        var e = Event();
        if (reason is null) Detail(e).Remove("reason"); else Detail(e)["reason"] = reason;

        Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(e.ToJsonString(), Settings()));
    }

    [Theory]
    [InlineData("image/scutara/scutarasellerapp/20260912T221318Z-34722020365.json")] // a repository with no route
    [InlineData("client/scutara/scutaraservice/20260912T221318Z-34722020365.json")] // another class
    [InlineData("image/scutara/scutaraservice/nested/20260912T221318Z-34722020365.json")] // below the prefix
    [InlineData("image/scutara/scutaraservice20260912T221318Z-34722020365.json")] // the prefix without its slash
    public void AKeyNoRouteOwns_StartsNothing(string key)
    {
        var e = Event();
        Detail(e)["object"]!["key"] = key;

        Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(e.ToJsonString(), Settings()));
    }

    [Theory]
    [InlineData("image/scutara/scutaraservice/20260912t221318z-34722020365.json")] // not the stamp's form
    [InlineData("image/scutara/scutaraservice/2026-09-12T22:13:18Z-34722020365.json")] // separators not stripped
    [InlineData("image/scutara/scutaraservice/20260912T221318Z-run1.json")] // not a run id
    [InlineData("image/scutara/scutaraservice/20260912T221318Z-34722020365.txt")]
    [InlineData("image/scutara/scutaraservice/")]
    public void AKeyThatIsNotARecordsName_StartsNothing(string key)
    {
        var e = Event();
        Detail(e)["object"]!["key"] = key;

        var ex = Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(e.ToJsonString(), Settings()));
        Assert.Contains("record", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"source\":\"aws.s3\",\"detail-type\":\"Object Created\",\"account\":\"147440642635\"}")]
    public void AnEventThatCannotBeRead_StartsNothing(string body)
    {
        Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(body, Settings()));
    }

    // ---------------------------------------------------------------------------------------
    //  The pattern both rules use
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ThePattern_PinsTheBuildAccount_TheStore_ImageRecords_AndPutObject()
    {
        var pattern = JsonNode.Parse(DeployerTrigger.EventPattern(BuildAccount, Store))!;

        Assert.Equal(BuildAccount, pattern["account"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("aws.s3", pattern["source"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("Object Created", pattern["detail-type"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(Store, pattern["detail"]!["bucket"]!["name"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("image/", pattern["detail"]!["object"]!["key"]!.AsArray().Single()!["prefix"]!.GetValue<string>());
        Assert.Equal("PutObject", pattern["detail"]!["reason"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public void TheDocumentedEvent_HasEveryValueThePatternRequires()
    {
        // Not an EventBridge matcher — a check that each path the pattern names exists in the event AWS documents, with
        // the value named. A pattern keyed on a field the event does not carry would match nothing, silently.
        var e = Event();
        Assert.Equal(BuildAccount, e["account"]!.GetValue<string>());
        Assert.Equal("aws.s3", e["source"]!.GetValue<string>());
        Assert.Equal("Object Created", e["detail-type"]!.GetValue<string>());
        Assert.Equal(Store, Detail(e)["bucket"]!["name"]!.GetValue<string>());
        Assert.StartsWith("image/", Detail(e)["object"]!["key"]!.GetValue<string>());
        Assert.Equal("PutObject", Detail(e)["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("14744064263")]
    [InlineData("not-an-account")]
    public void ThePattern_RefusesAnAccountThatIsNotTwelveDigits(string account)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(account, Store));
    }

    // ---------------------------------------------------------------------------------------
    //  Routes, as the function's environment carries them
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Routes_RoundTripThroughTheEnvironment()
    {
        var routes = Settings().Routes;

        var decoded = DeployerTrigger.DecodeRoutes(DeployerTrigger.EncodeRoutes(routes));

        var route = Assert.Single(decoded);
        Assert.Equal("image/scutara/scutaraservice/", route.RecordPrefix);
        Assert.Equal(Mp, Assert.Single(route.Targets));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"targets\":[{\"tenant\":\"mp\",\"cluster\":\"c\",\"service\":\"s\",\"container\":\"a\",\"repository\":\"r\"}]}]")]
    [InlineData("[{\"prefix\":\"image/scutara/scutaraservice/\",\"targets\":[]}]")]
    [InlineData("[{\"prefix\":\"image/scutara/scutaraservice/\",\"targets\":[{\"tenant\":\"MP\",\"cluster\":\"c\",\"service\":\"s\",\"container\":\"a\",\"repository\":\"r\"}]}]")]
    [InlineData("[{\"prefix\":\"image/scutara/scutaraservice/\",\"targets\":[{\"tenant\":\"mp\",\"cluster\":\"c\",\"service\":\"s s\",\"container\":\"a\",\"repository\":\"r\"}]}]")]
    [InlineData("[{\"prefix\":\"image/scutara/scutaraservice/\",\"targets\":[{\"tenant\":\"mp\",\"cluster\":\"c\",\"service\":\"s\",\"container\":\"a\"}]}]")]
    [InlineData("[{\"prefix\":\"image/scutara/scutaraservice/\",\"targets\":[{\"tenant\":\"mp\",\"cluster\":\"c\",\"service\":\"s\",\"container\":\"a\",\"repository\":\"r\"},{\"tenant\":\"mp\",\"cluster\":\"c\",\"service\":\"t\",\"container\":\"a\",\"repository\":\"r\"}]}]")]
    public void Routes_TheFunctionCouldNotActOn_AreRefusedWhenRead(string json)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.DecodeRoutes(json));
    }

    [Fact]
    public void TwoRoutesForOnePrefix_AreRefused()
    {
        var route = Settings().Routes.Single();
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EncodeRoutes(new[] { route, route }));
    }

    [Fact]
    public void RoutesTooLargeForAnEnvironmentVariable_AreRefused()
    {
        var targets = Enumerable.Range(0, 60)
            .Select(i => new TriggerTarget($"t{i}", new DeployTarget("scu-dev-cluster", $"scu-t{i}-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployerTrigger.EncodeRoutes(new[] { new TriggerRoute("image/scutara/scutaraservice/", targets) }));
        Assert.Contains("environment", ex.Message);
    }

    [Theory]
    [InlineData(DeployerEnvironment.ArtifactAccount)]
    [InlineData(DeployerEnvironment.BuildRecordStore)]
    [InlineData(DeployerEnvironment.StateMachine)]
    [InlineData(DeployerEnvironment.TriggerRoutes)]
    [InlineData(DeployerEnvironment.TriggerRefs)]
    public void TheSettings_RequireEveryVariable(string missing)
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.ArtifactAccount] = BuildAccount,
            [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.StateMachine] = Machine,
            [DeployerEnvironment.TriggerRoutes] = DeployerTrigger.EncodeRoutes(Settings().Routes),
            [DeployerEnvironment.TriggerRefs] = "refs/heads/main",
        };
        Assert.Equal(Settings().Routes.Single().RecordPrefix, TriggerSettings.Read(k => env.GetValueOrDefault(k)).Routes.Single().RecordPrefix);

        env.Remove(missing);
        var ex = Assert.Throws<InvalidOperationException>(() => TriggerSettings.Read(k => env.GetValueOrDefault(k)));
        Assert.Contains(missing, ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  Names already taken
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnExecutionsArn_ComesFromItsStateMachines()
    {
        Assert.Equal(
            "arn:aws:states:us-west-2:503947800380:execution:scu-dev-deployer:req-20260912T221318Z-34722020365-mp-1",
            DeployerTrigger.ExecutionArn(Machine, "req-20260912T221318Z-34722020365-mp-1"));
    }

    [Theory]
    [InlineData("arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer:PROD")] // an alias
    [InlineData("arn:aws:states:us-west-2:503947800380:execution:scu-dev-deployer:x")]
    [InlineData("scu-dev-deployer")]
    public void OnlyAnUnqualifiedStateMachineArn_NamesItsExecutions(string arn)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.ExecutionArn(arn, "req-x-1"));
    }

    [Fact]
    public void SameInput_IsJson_NotText()
    {
        var reformatted = JsonNode.Parse(ExpectedInput)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        Assert.True(DeployerTrigger.SameInput(ExpectedInput, reformatted));
        Assert.False(DeployerTrigger.SameInput(ExpectedInput, ExpectedInput.Replace("scu-mp-aiphost", "scu-zz-aiphost")));
        Assert.False(DeployerTrigger.SameInput(ExpectedInput, null));
        Assert.False(DeployerTrigger.SameInput(ExpectedInput, "not json"));
    }

    private sealed class Executions : IExecutions
    {
        public Dictionary<string, string> Existing { get; } = new(StringComparer.Ordinal);
        public List<(string Machine, string Name, string Input)> Starts { get; } = new();
        public List<string> Reads { get; } = new();
        public bool ExistsButUnreadable { get; init; }

        public Task<bool> StartAsync(string stateMachineArn, string name, string input)
        {
            Starts.Add((stateMachineArn, name, input));
            if (Existing.ContainsKey(name) || ExistsButUnreadable) return Task.FromResult(false);
            Existing[name] = input;
            return Task.FromResult(true);
        }

        public Task<string?> InputOfAsync(string executionArn)
        {
            Reads.Add(executionArn);
            var name = executionArn[(executionArn.LastIndexOf(':') + 1)..];
            return Task.FromResult(ExistsButUnreadable ? null : Existing.GetValueOrDefault(name));
        }
    }

    [Fact]
    public async Task AFreshRecord_IsStarted_UnderItsName_WithItsInput()
    {
        var executions = new Executions();

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), MainRecord(), executions);

        var start = Assert.Single(executions.Starts);
        Assert.Equal((Machine, "req-20260912T221318Z-34722020365-mp-1", ExpectedInput), start);
        Assert.Equal("req-20260912T221318Z-34722020365-mp-1", result["started"]!.AsArray().Single()!.GetValue<string>());
        Assert.Empty(result["duplicates"]!.AsArray());
        Assert.Empty(executions.Reads);
    }

    [Fact]
    public async Task ADuplicateAfterTheExecutionFinished_IsRecognisedByItsInput_AndStartsNothing()
    {
        // StartExecution answers ExecutionAlreadyExists for a CLOSED execution even with identical input.
        var executions = new Executions();
        executions.Existing["req-20260912T221318Z-34722020365-mp-1"] =
            JsonNode.Parse(ExpectedInput)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), MainRecord(), executions);

        Assert.Empty(result["started"]!.AsArray());
        Assert.Equal("req-20260912T221318Z-34722020365-mp-1", result["duplicates"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(
            "arn:aws:states:us-west-2:503947800380:execution:scu-dev-deployer:req-20260912T221318Z-34722020365-mp-1",
            Assert.Single(executions.Reads));
    }

    [Fact]
    public async Task ANameTakenByADifferentDeploy_IsAConflict_NeverASecondExecution()
    {
        var executions = new Executions();
        executions.Existing["req-20260912T221318Z-34722020365-mp-1"] = ExpectedInput.Replace("scu-dev-cluster", "scu-cluster");

        var ex = await Assert.ThrowsAsync<ExecutionConflict>(() => StartStep.RunAsync(Event().ToJsonString(), Settings(), MainRecord(), executions));

        Assert.Contains("different input", ex.Message);
        Assert.Single(executions.Starts);
    }

    [Fact]
    public async Task ATakenNameThatCannotBeReadBack_IsAConflict()
    {
        var executions = new Executions { ExistsButUnreadable = true };

        await Assert.ThrowsAsync<ExecutionConflict>(() => StartStep.RunAsync(Event().ToJsonString(), Settings(), MainRecord(), executions));
    }

    [Fact]
    public async Task ARefusedEvent_NeverReachesStartExecution()
    {
        var executions = new Executions();
        var forged = Event();
        forged["account"] = DevAccount;

        await Assert.ThrowsAsync<TriggerRefused>(() => StartStep.RunAsync(forged.ToJsonString(), Settings(), MainRecord(), executions));

        Assert.Empty(executions.Starts);
    }

    [Fact]
    public async Task TenantsAreStartedInOrder_SoARetryAfterAPartialFailure_FindsTheFirstAsADuplicate()
    {
        var other = new TriggerTarget("zz", new DeployTarget("scu-dev-cluster", "scu-zz-aiphost", "aiphost", "scu-4df6-b9c6-aiphost"));
        var executions = new Executions();
        executions.Existing["req-20260912T221318Z-34722020365-zz-1"] = "{\"something\":\"else\"}";

        await Assert.ThrowsAsync<ExecutionConflict>(() => StartStep.RunAsync(Event().ToJsonString(), Settings(Mp, other), MainRecord(), executions));
        Assert.Contains("req-20260912T221318Z-34722020365-mp-1", executions.Existing.Keys);

        // The retry, once the conflicting execution is gone: mp answers as a duplicate, zz starts.
        executions.Existing.Remove("req-20260912T221318Z-34722020365-zz-1");
        var retry = await StartStep.RunAsync(Event().ToJsonString(), Settings(Mp, other), MainRecord(), executions);

        Assert.Equal("req-20260912T221318Z-34722020365-mp-1", retry["duplicates"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("req-20260912T221318Z-34722020365-zz-1", retry["started"]!.AsArray().Single()!.GetValue<string>());
    }

    // ---------------------------------------------------------------------------------------
    //  Only main's builds start on their own (P2 stage D2)
    // ---------------------------------------------------------------------------------------

    private sealed class Records(string? body) : IRecordStore
    {
        public List<(string Bucket, string Key)> Reads { get; } = new();

        public Task<string?> ReadAsync(string bucket, string key)
        {
            Reads.Add((bucket, key));
            return Task.FromResult(body);
        }
    }

    private static string RecordBody(string? gitRef) => BuildRecordFormat.Serialize(new BuildRecord(
        BuildRecordFormat.CurrentSchema, "image",
        new BuildRecordBuiltFrom("Scutara/ScutaraService", "a108a57c725e0c85c96ed1a6c0cdbbf5dad14b4f", "published",
            new Dictionary<string, string> { ["LazyMagic.Shared"] = "3.0.26-alpha" }, gitRef),
        new BuildRecordIdentity("image", Digest: "sha256:c889f5df73e1055c0c629b61be055c13a793b584dac3a3016565a18da58f3cde"),
        "2026-09-12T22:13:18Z", "tmay", "34722020365"));

    private static Records MainRecord() => new(RecordBody("refs/heads/main"));

    [Fact]
    public async Task ABuildFromMain_IsStarted_AfterItsRecordIsReadFromTheStore()
    {
        var records = MainRecord();
        var executions = new Executions();

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), records, executions);

        Assert.Equal((Store, Key), Assert.Single(records.Reads));
        Assert.Single(executions.Starts);
        Assert.Null(result["skipped"]);
    }

    [Theory]
    [InlineData("refs/heads/feature/try-something")]
    [InlineData("refs/heads/Main")]
    [InlineData("refs/tags/v1.0.0")]
    [InlineData(null)] // a record written before builtFrom.ref existed
    public async Task ABuildFromAnyOtherRef_OrNone_IsSkipped_NotStarted_AndNotAFailure(string? gitRef)
    {
        var executions = new Executions();

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), new Records(RecordBody(gitRef)), executions);

        Assert.Empty(executions.Starts);
        Assert.Empty(result["started"]!.AsArray());
        var skipped = (JsonObject)result["skipped"]!;
        Assert.Equal(Key, skipped["record"]!.GetValue<string>());
        Assert.Equal(gitRef, skipped["ref"]?.GetValue<string>());
        Assert.Contains("refs/heads/main", skipped["reason"]!.GetValue<string>());
        Assert.Contains("by hand", skipped["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheAllowedRefs_ComeFromTheSettings()
    {
        var settings = Settings() with { AllowedRefs = new[] { "refs/heads/main", "refs/heads/release" } };
        var executions = new Executions();

        await StartStep.RunAsync(Event().ToJsonString(), settings, new Records(RecordBody("refs/heads/release")), executions);

        Assert.Single(executions.Starts);
    }

    [Fact]
    public async Task ARecordThatIsNotThere_IsRefused_NotSkipped()
    {
        var executions = new Executions();

        var ex = await Assert.ThrowsAsync<TriggerRefused>(() =>
            StartStep.RunAsync(Event().ToJsonString(), Settings(), new Records(null), executions));

        Assert.Contains("no build record", ex.Message);
        Assert.Empty(executions.Starts);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\": 2}")]
    public async Task ARecordThatCannotBeParsed_IsRefused_NotSkipped(string body)
    {
        var executions = new Executions();

        await Assert.ThrowsAsync<TriggerRefused>(() =>
            StartStep.RunAsync(Event().ToJsonString(), Settings(), new Records(body), executions));

        Assert.Empty(executions.Starts);
    }

    [Fact]
    public async Task ARefusedEvent_NeverReadsTheStore()
    {
        var records = MainRecord();
        var forged = Event();
        forged["account"] = DevAccount;

        await Assert.ThrowsAsync<TriggerRefused>(() => StartStep.RunAsync(forged.ToJsonString(), Settings(), records, new Executions()));

        Assert.Empty(records.Reads);
    }

    [Fact]
    public void ShouldStart_NamesTheRefItSawAndTheOnesItAllows()
    {
        var record = BuildRecordFormat.Parse(RecordBody("refs/heads/feature"));

        var (start, reason) = DeployerTrigger.ShouldStart(record, DeployerTrigger.DefaultRefs);

        Assert.False(start);
        Assert.Contains("refs/heads/feature", reason);
        Assert.Contains("refs/heads/main", reason);
        Assert.True(DeployerTrigger.ShouldStart(BuildRecordFormat.Parse(RecordBody("refs/heads/main")), DeployerTrigger.DefaultRefs).Start);
    }

    [Fact]
    public void TheSettings_RefuseAnEmptyRefList()
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.ArtifactAccount] = BuildAccount,
            [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.StateMachine] = Machine,
            [DeployerEnvironment.TriggerRoutes] = DeployerTrigger.EncodeRoutes(Settings().Routes),
            [DeployerEnvironment.TriggerRefs] = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => TriggerSettings.Read(k => env.GetValueOrDefault(k)));
        Assert.Contains(DeployerEnvironment.TriggerRefs, ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The start function's invoke permission, as read back
    // ---------------------------------------------------------------------------------------

    private const string RuleArn = "arn:aws:events:us-west-2:503947800380:rule/scu-dev-deployer-trigger/scu-dev-deployer-start";

    private static string LambdaPolicy(string sid, string principal, string sourceArn) =>
        "{\"Version\":\"2012-10-17\",\"Id\":\"default\",\"Statement\":[{\"Sid\":\"" + sid + "\",\"Effect\":\"Allow\"," +
        "\"Principal\":{\"Service\":\"" + principal + "\"},\"Action\":\"lambda:InvokeFunction\"," +
        "\"Resource\":\"arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-start\"," +
        "\"Condition\":{\"ArnLike\":{\"AWS:SourceArn\":\"" + sourceArn + "\"}}}]}";

    [Fact]
    public void TheInvokePermission_IsRecognisedOnlyForEventBridgeAndThisRule()
    {
        Assert.True(TriggerApply.InvokePermissionIsFor(LambdaPolicy(TriggerApply.InvokePermissionSid, "events.amazonaws.com", RuleArn), RuleArn));

        Assert.False(TriggerApply.InvokePermissionIsFor(null, RuleArn));
        Assert.False(TriggerApply.InvokePermissionIsFor(LambdaPolicy("SomethingElse", "events.amazonaws.com", RuleArn), RuleArn));
        Assert.False(TriggerApply.InvokePermissionIsFor(LambdaPolicy(TriggerApply.InvokePermissionSid, "s3.amazonaws.com", RuleArn), RuleArn));
        Assert.False(TriggerApply.InvokePermissionIsFor(
            LambdaPolicy(TriggerApply.InvokePermissionSid, "events.amazonaws.com", RuleArn.Replace("deployer-start", "other")), RuleArn));
    }
}
