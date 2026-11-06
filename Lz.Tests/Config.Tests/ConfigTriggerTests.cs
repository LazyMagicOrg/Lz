using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The config trigger (DecoupledCd.md P3 E): where an environment deploys its infrastructure from config bundles and builds start
/// deploys, a config record of ITS config repository starts one execution with the record alone; both rules match that prefix and
/// nothing wider; the start function reads config records and nothing else of the class; and a bundle older than one already
/// deployed is refused before anything is applied.
/// </summary>
public class ConfigTriggerTests
{
    private const string BuildAccount = "147440642635";
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string Machine = "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer";
    private const string ConfigPrefix = "config/scutara/scutara/";
    private const string ConfigKey = ConfigPrefix + "20261106T145329Z-42237962485-1.json";
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;

    private static readonly TriggerRoute Service = new("image/scutara/scutaraservice/", new[]
    {
        new TriggerTarget("mp", new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")),
    });

    private static TriggerSettings Settings(string? configPrefix = ConfigPrefix) => new(
        BuildAccount, Store, Machine, new[] { Service }, DeployerTrigger.DefaultRefs, null, ArtifactStore, null, configPrefix);

    private static string Event(string key = ConfigKey) => new JsonObject
    {
        ["detail-type"] = "Object Created",
        ["source"] = "aws.s3",
        ["account"] = BuildAccount,
        ["detail"] = new JsonObject
        {
            ["bucket"] = new JsonObject { ["name"] = Store },
            ["object"] = new JsonObject { ["key"] = key },
            ["reason"] = "PutObject",
        },
    }.ToJsonString();

    // ---- the route ----

    [Fact]
    public void AConfigRecordsEvent_StartsOneExecution_WithTheRecordAlone_AsAHandStartedOneHas()
    {
        var only = Assert.Single(DeployerTrigger.ExecutionsFor(Event(), Settings()));
        Assert.Equal("req-20261106T145329Z-42237962485-1-config-1", only.Name);
        Assert.Equal(DeployerTrigger.ConfigInputFor(new RecordLocation(Store, ConfigKey)), only.Input);
        Assert.Equal(ConfigKey, JsonNode.Parse(only.Input)!["record"]!["key"]!.GetValue<string>());
        Assert.Equal(new[] { "record" }, JsonNode.Parse(only.Input)!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData("config/scutara/other/20261106T145329Z-42237962485-1.json")]   // another config repository
    [InlineData("config/scutara/scutara/nested/20261106T145329Z-42237962485-1.json")]
    public void AnotherRepositorysOrANestedKey_StartsNothing(string key)
        => Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(Event(key), Settings()));

    [Fact]
    public void WithoutTheConfigRoute_AConfigRecordStartsNothing()
        => Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(Event(), Settings(configPrefix: null)));

    [Fact]
    public void TheSettings_TakeTheConfigPrefixOnlyWithTheArtifactStore_AndOnlyAConfigRepositorysPrefix()
    {
        var env = new Dictionary<string, string>
        {
            [DeployerEnvironment.ArtifactAccount] = BuildAccount, [DeployerEnvironment.BuildRecordStore] = Store,
            [DeployerEnvironment.StateMachine] = Machine, [DeployerEnvironment.TriggerRoutes] = DeployerTrigger.EncodeRoutes(new[] { Service }),
            [DeployerEnvironment.TriggerRefs] = "refs/heads/main", [DeployerEnvironment.TriggerConfigPrefix] = ConfigPrefix,
        };
        Assert.Throws<InvalidOperationException>(() => TriggerSettings.Read(k => env.GetValueOrDefault(k)));
        env[DeployerEnvironment.ArtifactStore] = ArtifactStore;
        var read = TriggerSettings.Read(k => env.GetValueOrDefault(k));
        Assert.Equal(ConfigPrefix, read.ConfigPrefix);
        Assert.Equal(ArtifactStore, read.ArtifactStore);

        env[DeployerEnvironment.TriggerConfigPrefix] = "config/";
        Assert.Throws<InvalidOperationException>(() => TriggerSettings.Read(k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public void ThePattern_MatchesTheConfigRepositorysPrefixExactly_AfterTheClasses_AndRefusesAWiderOne()
    {
        var pattern = DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "client" }, new[] { "site/scutara/scutarawebsite/dev/" }, new[] { ConfigPrefix });
        Assert.Equal(new[] { "image/", "client/", "site/scutara/scutarawebsite/dev/", ConfigPrefix }, DeployerTrigger.RecordPrefixesOf(pattern));
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }, null, new[] { "config/" }));
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "config" }));
    }

    // ---- the start step ----

    private sealed class Records(string json) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key) => Task.FromResult<StoredRecord?>(new StoredRecord(json, "v-1"));
    }

    private sealed class Executions : IExecutions
    {
        public readonly List<(string Name, string Input)> Started = new();
        public Task<bool> StartAsync(string stateMachineArn, string name, string input) { Started.Add((name, input)); return Task.FromResult(true); }
        public Task<string?> InputOfAsync(string executionArn) => Task.FromResult<string?>(null);
        public Task<string?> HoldAsync(string parameter) => Task.FromResult<string?>("off");
    }

    // The shape of root e269d49's real config record, its ref the parameter.
    private static string ConfigRecord(string gitRef) => $$"""
        {
          "schema": 1, "class": "config",
          "builtFrom": { "repo": "Scutara/Scutara", "commit": "e269d493cb99bbc2cd916ff4246a15c88592d263", "lane": "published",
                         "packages": { "Lz.Aws": "0.13.166", "Lz.Cli": "0.13.166", "Lz.Core": "0.13.166" }, "ref": "{{gitRef}}" },
          "identity": { "kind": "bundle", "bucket": "{{ArtifactStore}}", "key": "config/scutara/scutara/20261106T145329Z-42237962485-1.zip",
                        "versionId": "rEKPhw4vGJdF1RL3AWjDlbiXQOnlwYk6", "sha256": "160f48b074e84c2e0a3640a94cd8c8e0054562bf21b5bf9060ab661f7b249baf" },
          "builtAt": "2026-11-06T14:53:29Z", "builtBy": "a-person", "workflowRunId": "42237962485",
          "intake": { "runId": "42237962485", "runAttempt": 1, "jobId": "127960463356",
                      "workflowRef": "Scutara/Scutara/.github/workflows/build-config.yml@refs/heads/main",
                      "githubDigest": "sha256:21b3ba60062cb3d41bf990af457773032f46f8410745566b6209519b61234ada",
                      "gate1": { "result": "passed", "evidence": "gate1/1233186125-42237962485-1.json" } }
        }
        """;

    [Fact]
    public async Task AConfigRecordFromMain_IsStarted_AndOneFromABranchIsSkipped()
    {
        var executions = new Executions();
        var started = await StartStep.RunAsync(Event(), Settings(), new Records(ConfigRecord("refs/heads/main")), executions);
        Assert.Equal("req-20261106T145329Z-42237962485-1-config-1", Assert.Single(started["started"]!.AsArray())!.GetValue<string>());

        var branch = new Executions();
        var skipped = await StartStep.RunAsync(Event(), Settings(), new Records(ConfigRecord("refs/heads/try")), branch);
        Assert.Empty(branch.Started);
        Assert.NotNull(skipped["skipped"]);
    }

    // ---- the plan ----

    private static SystemConfig Dev(string infrastructure = PipelineConfig.InfrastructureDeploy, bool trigger = true)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client", "config", "tooling" });
        c.State = new StateConfig
        {
            Backend = "s3://scu-dev-pulumi-state-4df6-b9c6?region=us-west-2",
            SecretsProvider = "awskms://alias/scu-dev-pulumi-key-4df6-b9c6",
        };
        c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "config", Artifacts = new List<string> { "config" } });
        c.Pipeline.Repositories.Add(new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        c.Pipeline.Infrastructure = infrastructure;
        c.Pipeline.DeployOnBuildRecord = trigger;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config) => DeployerPlanner.Plan(config, TargetAccount,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        infraInputs: new DeployerInfraInputs("subnet-0abc", "sg-0abc"));

    [Fact]
    public void UnderDeployWithTheTrigger_BothRulesForwardTheConfigRepositorysRecords_AndTheStartFunctionRoutesAndReadsThem()
    {
        var config = Dev();
        Assert.Equal(new[] { ConfigPrefix }, DeployerPlanner.TriggerConfigPrefixes(config));

        var plan = Plan(config);
        Assert.Contains(ConfigPrefix, DeployerTrigger.RecordPrefixesOf(plan.Trigger!.EventPattern));
        Assert.DoesNotContain("config/", DeployerTrigger.RecordPrefixesOf(plan.Trigger.EventPattern));

        var start = plan.Functions.Single(f => f.Handler == DeployerHandlers.Start);
        Assert.Equal(ConfigPrefix, start.Environment[DeployerEnvironment.TriggerConfigPrefix]);
        Assert.Equal(ArtifactStore, start.Environment[DeployerEnvironment.ArtifactStore]);
        var read = JsonDocument.Parse(start.Policy).RootElement.GetProperty("Statement").EnumerateArray()
            .Single(s => s.GetProperty("Sid").GetString() == "ReadConfigRecordsBranch");
        Assert.Equal($"arn:aws:s3:::{Store}/{ConfigPrefix}*", read.GetProperty("Resource")[0].GetString());

        // THE BUILD ACCOUNT'S HALF: its forwarding rule's pattern is the same document, and the start role reads config records.
        var build = PipelineBootstrapPlanner.Plan(config, BuildAccount);
        Assert.Equal(plan.Trigger.EventPattern, build.RecordForwarding!.EventPattern);
        var grant = build.BuildRecordReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "TriggerReadsConfigRecordsDev");
        Assert.Equal($"arn:aws:s3:::{Store}/{ConfigPrefix}*", grant["Resource"]!.GetValue<string>());
        Assert.Equal($"arn:aws:iam::{TargetAccount}:role/{DeployerPlanner.StartFunctionRoleName(config)}",
            grant["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(PipelineConfig.InfrastructurePlan, true)]    // a plan alone is not worth an execution per push
    [InlineData(PipelineConfig.InfrastructureDeploy, false)] // builds start nothing in this environment
    public void OtherwiseNothingOfTheConfigRouteIsPlanned(string infrastructure, bool trigger)
    {
        var config = Dev(infrastructure, trigger);
        Assert.Empty(DeployerPlanner.TriggerConfigPrefixes(config));
        if (trigger)
        {
            var plan = Plan(config);
            Assert.DoesNotContain(DeployerTrigger.RecordPrefixesOf(plan.Trigger!.EventPattern), p => p.StartsWith("config/"));
            Assert.DoesNotContain(DeployerEnvironment.TriggerConfigPrefix, plan.Functions.Single(f => f.Handler == DeployerHandlers.Start).Environment.Keys);
        }
        Assert.DoesNotContain(PipelineBootstrapPlanner.Plan(config, BuildAccount).BuildRecordReadGrant ?? Array.Empty<JsonObject>(),
            s => s["Sid"]!.GetValue<string>().StartsWith("TriggerReadsConfigRecords"));
    }

    [Fact]
    public void TheDeployTask_IsGivenTheRecordsBuildTimeAndRepository_AndMayReadTheDeploysRecorded()
    {
        var plan = Plan(Dev());
        var command = JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States").GetProperty("DeployInfrastructure")
            .GetProperty("Parameters").GetProperty("Overrides").GetProperty("ContainerOverrides")[0].GetProperty("Command.$").GetString()!;
        Assert.Contains("'--built-at', $.verified.builtAt", command);
        Assert.Contains("'--repo', $.verified.builtFrom.repo", command);
        var read = JsonDocument.Parse(plan.InfrastructureDeploy!.TaskRolePolicy).RootElement.GetProperty("Statement").EnumerateArray()
            .Single(s => s.GetProperty("Sid").GetString() == "ReadTheDeploysAlreadyRecorded");
        Assert.Equal("arn:aws:s3:::scu-dev-deploy-evidence-4df6-b9c6/deploys/config/scutara/scutara/*", read.GetProperty("Resource").GetString());
    }

    // ---- never backwards ----

    [Fact]
    public void ABundleBuiltBeforeOneAlreadyDeployed_IsSuperseded_TheSameBuildAgainIsNot()
    {
        var deployed = new (string, string?)[]
        {
            ("deploys/config/scutara/scutara/deploy-20261106T1505Z-42237962485.json", "2026-11-06T14:53:29Z"),
            ("deploys/config/scutara/scutara/req-old.json", "2026-11-06T03:32:19Z"),
            ("deploys/config/scutara/scutara/unreadable.json", null),
        };
        Assert.Contains("deploy-20261106T1505Z-42237962485", InfraDeploy.SupersededBy(deployed, "2026-11-06T14:10:36Z"));
        Assert.Null(InfraDeploy.SupersededBy(deployed, "2026-11-06T14:53:29Z"));
        Assert.Null(InfraDeploy.SupersededBy(deployed, "2026-11-06T16:00:00Z"));
        Assert.Null(InfraDeploy.SupersededBy(Array.Empty<(string, string?)>(), "2026-11-06T14:10:36Z"));
        Assert.Equal("deploys/config/scutara/scutara/", InfraDeploy.DeploysPrefixFor("Scutara/Scutara"));
    }
}
