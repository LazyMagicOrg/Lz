using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The trigger for client records (DecoupledCd.md P4 stage D): which forwarded S3 events start a bundle deploy, under which
/// name and with which input, and the pattern both rules match them by.
///
/// <para>THE RECORD IS THE REAL ONE: <see cref="BuildRecordFormatTests.BundleWorkflowEmitted"/>, SellerApp's first bundle,
/// whose execution was started by hand on 2026-09-14 with <see cref="HandStartedInput"/> — copied from the file that start
/// used. The trigger must name that deploy the way the person did, so the two are one deploy and never two.</para>
/// </summary>
public class DeployerClientTriggerTests
{
    private const string BuildAccount = "147440642635";
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string Machine = "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer";
    private const string SellerKey = "client/scutara/scutarasellerapp/20260914T173258Z-34875215076.json";
    private const string SellerBucket = "scu---webapp-sellerapp-4df6-b9c6";
    private const string AdminBucket = "scu---webapp-adminapp-4df6-b9c6";

    /// <summary>The input of <c>req-20260914T173258Z-34875215076-sellerapp-1</c>, started by hand, byte for byte.</summary>
    private const string HandStartedInput =
        "{\"record\":{\"bucket\":\"scu-build-records-4df6-b9c6\",\"key\":\"client/scutara/scutarasellerapp/20260914T173258Z-34875215076.json\"}," +
        "\"target\":{\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}}";

    private static readonly TriggerRoute Service = new("image/scutara/scutaraservice/", new[]
    {
        new TriggerTarget("mp", new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")),
    });

    private static readonly ClientTriggerRoute Seller = new("client/scutara/scutarasellerapp/", "sellerapp", SellerBucket);
    private static readonly ClientTriggerRoute Admin = new("client/scutara/scutaraadminapp/", "adminapp", AdminBucket);

    private static TriggerSettings Settings(bool clients = true) => new(
        BuildAccount, Store, Machine, new[] { Service }, DeployerTrigger.DefaultRefs,
        clients ? new[] { Seller, Admin } : null,
        clients ? ArtifactStore : null);

    private static JsonObject Event(string key = SellerKey) => new()
    {
        ["version"] = "0",
        ["id"] = "4f1d7c2e-9b0a-4c55-8e1f-3a6b2d9c7e10",
        ["detail-type"] = "Object Created",
        ["source"] = "aws.s3",
        ["account"] = BuildAccount,
        ["time"] = "2026-09-14T17:36:41Z",
        ["region"] = "us-west-2",
        ["resources"] = new JsonArray($"arn:aws:s3:::{Store}"),
        ["detail"] = new JsonObject
        {
            ["version"] = "0",
            ["bucket"] = new JsonObject { ["name"] = Store },
            ["object"] = new JsonObject { ["key"] = key, ["size"] = 1687, ["version-id"] = "3sL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY" },
            ["requester"] = BuildAccount,
            ["reason"] = "PutObject",
        },
    };

    // ---------------------------------------------------------------------------------------
    //  What a client record's event starts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AClientRecordsEvent_StartsOneExecution_NamedAndShapedAsTheHandStartedOneWas()
    {
        var only = Assert.Single(DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings()));

        Assert.Equal("req-20260914T173258Z-34875215076-sellerapp-1", only.Name);
        Assert.Equal(HandStartedInput, only.Input);
        Assert.Equal(new RecordLocation(Store, SellerKey), only.Record);
    }

    [Fact]
    public void TheClientInput_ReadsBackAsTheRecordAndTheBucket_AndCarriesNothingElse()
    {
        var input = (JsonObject)JsonNode.Parse(DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings()).Single().Input)!;

        Assert.Equal(DeployerInput.InputFields, input.Select(p => p.Key));
        Assert.Equal(new[] { "bucket" }, ((JsonObject)input["target"]!).Select(p => p.Key));
        Assert.Equal(new RecordLocation(Store, SellerKey), DeployerInput.RecordFrom(input));
        Assert.Equal(SellerBucket, DeployerInput.BundleBucketFrom(input));
        // Never the override a person may add by hand (P-12's argument for allowOlderBuild).
        Assert.False(DeployerInput.AllowsOlderBuild(input));
    }

    [Fact]
    public void EachClientRepository_DeploysIntoItsOwnAppsBucket()
    {
        var admin = Assert.Single(DeployerTrigger.ExecutionsFor(
            Event("client/scutara/scutaraadminapp/20260914T173254Z-34875218320.json").ToJsonString(), Settings()));

        Assert.Equal("req-20260914T173254Z-34875218320-adminapp-1", admin.Name);
        Assert.Contains($"\"bucket\":\"{AdminBucket}\"", admin.Input);
        Assert.DoesNotContain(SellerBucket, admin.Input);
    }

    [Fact]
    public void ImageRecords_RouteAsTheyDid_BesideClientRoutes()
    {
        var image = Assert.Single(DeployerTrigger.ExecutionsFor(
            Event("image/scutara/scutaraservice/20260912T221318Z-34722020365.json").ToJsonString(), Settings()));

        Assert.Equal("req-20260912T221318Z-34722020365-mp-1", image.Name);
        Assert.Contains("\"service\":\"scu-mp-aiphost\"", image.Input);
    }

    [Theory]
    [InlineData("client/scutara/scutarawebsite/20260914T173258Z-34875215076.json")] // a client repository with no app
    [InlineData("site/scutara/scutarawebsite/20260914T173258Z-34875215076.json")] // a class with no route
    [InlineData("client/scutara/scutarasellerapp/nested/20260914T173258Z-34875215076.json")] // below the prefix
    [InlineData("client/scutara/scutarasellerapp/20260914T173258Z-34875215076.zip")] // the bundle, not its record
    public void AClientKeyNoRouteOwns_OrThatIsNotARecordsName_StartsNothing(string key)
    {
        Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(Event(key).ToJsonString(), Settings()));
    }

    [Fact]
    public void WithoutClientRoutes_AClientRecordStartsNothing()
    {
        var ex = Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings(clients: false)));

        Assert.Contains("no route", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The start step: the record is parsed with the artifact store
    // ---------------------------------------------------------------------------------------

    private sealed class Records(string? body) : IRecordStore
    {
        public List<(string Bucket, string Key)> Reads { get; } = new();

        public Task<StoredRecord?> ReadAsync(string bucket, string key)
        {
            Reads.Add((bucket, key));
            return Task.FromResult(body is null ? null : new StoredRecord(body, "3sL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY"));
        }
    }

    private sealed class Executions : IExecutions
    {
        public Dictionary<string, string> Existing { get; } = new(StringComparer.Ordinal);
        public List<(string Name, string Input)> Starts { get; } = new();

        public Task<bool> StartAsync(string stateMachineArn, string name, string input)
        {
            Starts.Add((name, input));
            if (Existing.ContainsKey(name)) return Task.FromResult(false);
            Existing[name] = input;
            return Task.FromResult(true);
        }

        public Task<string?> InputOfAsync(string executionArn)
            => Task.FromResult(Existing.GetValueOrDefault(executionArn[(executionArn.LastIndexOf(':') + 1)..]));
    }

    [Fact]
    public async Task AClientRecordFromMain_IsReadAsABundleRecord_AndStarted()
    {
        // Parsed without the artifact store, a bundle record is refused (P4 stage A), so this is the check that the start
        // function reads it with the store its settings name.
        var records = new Records(BuildRecordFormatTests.BundleWorkflowEmitted);
        var executions = new Executions();

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), records, executions);

        Assert.Equal((Store, SellerKey), Assert.Single(records.Reads));
        Assert.Equal(("req-20260914T173258Z-34875215076-sellerapp-1", HandStartedInput), Assert.Single(executions.Starts));
        Assert.Equal("req-20260914T173258Z-34875215076-sellerapp-1", result["started"]!.AsArray().Single()!.GetValue<string>());
        Assert.Null(result["skipped"]);
    }

    [Fact]
    public async Task AClientRecordFromABranch_IsSkipped()
    {
        var branch = BuildRecordFormatTests.BundleWorkflowEmitted.Replace("\"ref\": \"refs/heads/main\"", "\"ref\": \"refs/heads/try-a-theme\"");
        Assert.NotEqual(BuildRecordFormatTests.BundleWorkflowEmitted, branch);
        var executions = new Executions();

        var result = await StartStep.RunAsync(Event().ToJsonString(), Settings(), new Records(branch), executions);

        Assert.Empty(executions.Starts);
        Assert.Equal("refs/heads/try-a-theme", result["skipped"]!["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheHandStartedExecution_IsTheSameDeploy_NotAConflict()
    {
        // req-…-sellerapp-1 exists with the hand-started input, reformatted as the service hands it back.
        var executions = new Executions();
        executions.Existing["req-20260914T173258Z-34875215076-sellerapp-1"] =
            JsonNode.Parse(HandStartedInput)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var result = await StartStep.RunAsync(
            Event().ToJsonString(), Settings(), new Records(BuildRecordFormatTests.BundleWorkflowEmitted), executions);

        Assert.Empty(result["started"]!.AsArray());
        Assert.Equal("req-20260914T173258Z-34875215076-sellerapp-1", result["duplicates"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public async Task ABundleRecordNamingAnotherStore_IsRefused_NotStarted()
    {
        var elsewhere = BuildRecordFormatTests.BundleWorkflowEmitted.Replace(
            "\"bucket\": \"scu-artifacts-4df6-b9c6\"", "\"bucket\": \"someone-elses-artifacts\"");
        Assert.NotEqual(BuildRecordFormatTests.BundleWorkflowEmitted, elsewhere);
        var executions = new Executions();

        await Assert.ThrowsAsync<TriggerRefused>(() =>
            StartStep.RunAsync(Event().ToJsonString(), Settings(), new Records(elsewhere), executions));

        Assert.Empty(executions.Starts);
    }

    // ---------------------------------------------------------------------------------------
    //  The pattern both rules use
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheImageOnlyPattern_IsTheOneStageDWrote_ByteForByte()
    {
        // The live rules in both accounts hold this text (P2 stage D). An environment with no client target keeps it.
        Assert.Equal(
            "{\"account\":[\"147440642635\"],\"source\":[\"aws.s3\"],\"detail-type\":[\"Object Created\"]," +
            "\"detail\":{\"bucket\":{\"name\":[\"scu-build-records-4df6-b9c6\"]},\"object\":{\"key\":[{\"prefix\":\"image/\"}]}," +
            "\"reason\":[\"PutObject\"]}}",
            DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }));
    }

    [Fact]
    public void WithClients_ThePatternMatchesBothPrefixes_AndNothingElseChanges()
    {
        var pattern = JsonNode.Parse(DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "client" }))!;
        var imageOnly = JsonNode.Parse(DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }))!;

        Assert.Equal(new[] { "image/", "client/" },
            pattern["detail"]!["object"]!["key"]!.AsArray().Select(m => m!["prefix"]!.GetValue<string>()));

        // Everything but the key matcher is the image-only pattern's.
        ((JsonObject)pattern["detail"]!).Remove("object");
        ((JsonObject)imageOnly["detail"]!).Remove("object");
        Assert.True(JsonNode.DeepEquals(pattern, imageOnly));
    }

    [Fact]
    public void ThePrefixesOfAPattern_AreReadBack()
    {
        Assert.Equal(new[] { "image/", "client/" },
            DeployerTrigger.RecordPrefixesOf(DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "client" })));
        Assert.Equal(new[] { "image/" }, DeployerTrigger.RecordPrefixesOf(DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" })));
    }

    [Fact]
    public void TheRulesDescriptions_NameTheClassesTheirPatternForwards_AndAnImageOnlyRuleKeepsItsWords()
    {
        var imageOnly = DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" });
        var withClients = DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "client" });

        // The descriptions the live rules were created with read "new image build records" and "a new image build record".
        Assert.Equal("image", TriggerApply.RecordClassesOf(imageOnly, " and "));
        Assert.Equal("image and client", TriggerApply.RecordClassesOf(withClients, " and "));
        Assert.Equal("image or client", TriggerApply.RecordClassesOf(withClients, " or "));
    }

    [Theory]
    [InlineData("site")] // no branch deploys it yet
    [InlineData("config")]
    [InlineData("Client")]
    [InlineData("")]
    public void ThePattern_RefusesAClassTheTriggerDoesNotRoute(string cls)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", cls }));
    }

    [Fact]
    public void ThePattern_RefusesNoClass_AndAClassTwice()
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, Array.Empty<string>()));
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "image" }));
    }

    // ---------------------------------------------------------------------------------------
    //  Client routes, as the function's environment carries them
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ClientRoutes_RoundTripThroughTheEnvironment()
    {
        var encoded = DeployerTrigger.EncodeClientRoutes(new[] { Seller, Admin });

        Assert.Equal(new[] { Seller, Admin }, DeployerTrigger.DecodeClientRoutes(encoded));
        Assert.Equal(
            "[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}," +
            "{\"prefix\":\"client/scutara/scutaraadminapp/\",\"app\":\"adminapp\",\"bucket\":\"scu---webapp-adminapp-4df6-b9c6\"}]",
            encoded);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[{\"prefix\":\"image/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}]")] // another class
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}]")] // no slash
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"SellerApp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}]")]
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"seller app\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}]")]
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"Not_A_Bucket\"}]")]
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\"}]")]
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}," +
                "{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"adminapp\",\"bucket\":\"scu---webapp-adminapp-4df6-b9c6\"}]")] // one prefix twice
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}," +
                "{\"prefix\":\"client/scutara/scutaraadminapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-adminapp-4df6-b9c6\"}]")] // one app twice
    [InlineData("[{\"prefix\":\"client/scutara/scutarasellerapp/\",\"app\":\"sellerapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}," +
                "{\"prefix\":\"client/scutara/scutaraadminapp/\",\"app\":\"adminapp\",\"bucket\":\"scu---webapp-sellerapp-4df6-b9c6\"}]")] // one bucket twice
    public void ClientRoutes_TheFunctionCouldNotActOn_AreRefusedWhenRead(string json)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.DecodeClientRoutes(json));
    }

    private static Dictionary<string, string> Variables(bool clientRoutes, bool artifactStore)
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.ArtifactAccount] = BuildAccount,
            [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.StateMachine] = Machine,
            [DeployerEnvironment.TriggerRoutes] = DeployerTrigger.EncodeRoutes(new[] { Service }),
            [DeployerEnvironment.TriggerRefs] = "refs/heads/main",
        };
        if (clientRoutes) env[DeployerEnvironment.TriggerClientRoutes] = DeployerTrigger.EncodeClientRoutes(new[] { Seller, Admin });
        if (artifactStore) env[DeployerEnvironment.ArtifactStore] = ArtifactStore;
        return env;
    }

    [Fact]
    public void TheSettings_ReadClientRoutes_WithTheArtifactStore()
    {
        var env = Variables(clientRoutes: true, artifactStore: true);

        var settings = TriggerSettings.Read(k => env.GetValueOrDefault(k));

        Assert.Equal(new[] { Seller, Admin }, settings.ClientRoutes);
        Assert.Equal(ArtifactStore, settings.ArtifactStore);
    }

    [Fact]
    public void TheSettings_RefuseClientRoutesWithoutTheArtifactStore()
    {
        // Without it every client record would be refused into the queue as unparseable — refused here instead, where the
        // message names the variable.
        var env = Variables(clientRoutes: true, artifactStore: false);

        var ex = Assert.Throws<InvalidOperationException>(() => TriggerSettings.Read(k => env.GetValueOrDefault(k)));

        Assert.Contains(DeployerEnvironment.ArtifactStore, ex.Message);
    }

    [Fact]
    public void TheSettings_WithoutClientRoutes_AreTheImageTriggersSettings()
    {
        var env = Variables(clientRoutes: false, artifactStore: false);

        var settings = TriggerSettings.Read(k => env.GetValueOrDefault(k));

        Assert.Empty(settings.ClientRoutes ?? Array.Empty<ClientTriggerRoute>());
        Assert.Null(settings.ArtifactStore);
    }
}
