using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>The infrastructure branch's steps, plan only (DecoupledCd.md P3 D): Verify's config branch, VerifyTooling, ReadPlan.</summary>
public class InfraPlanStepsTests
{
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string ConfigKey = "config/scutara/scutara/20261106T015936Z-42147413554-1.json";
    private const string BundleSha = "08178faf3539027495fafb759eb7f1ac9fea1f12ecbb4e57e1f10b7358312229";
    private const string Tooling158 = "sha256:da7ed59a66c7667c2f6a3906037b24a276d92ce55c3fe82391fdbb9c00df1fd3";
    private const string Tooling157 = "sha256:65c83c50aa1d34f1c0a3b7c4c0de5d1e8d7f6a6b5c4d3e2f1a0b9c8d7e6f5a4b";
    private const string Registry = "503947800380.dkr.ecr.us-west-2.amazonaws.com";

    // The config record the intake published on 2026-11-06, as it is.
    private static string ConfigRecord(string lz = "0.13.158") => $$"""
        {"schema":1,"class":"config",
         "builtFrom":{"repo":"Scutara/Scutara","commit":"830c276555b7b5198ccda3a31b2b2c390effeb97","lane":"published",
                      "packages":{"Lz.Aws":"{{lz}}","Lz.Cli":"{{lz}}","Lz.Core":"{{lz}}"},"ref":"refs/heads/main"},
         "identity":{"kind":"bundle","bucket":"{{ArtifactStore}}","key":"config/scutara/scutara/20261106T015936Z-42147413554-1.zip",
                     "versionId":"40leQqm9xG_uRJcRRhA4LdPZi1eUuZDl","sha256":"{{BundleSha}}"},
         "builtAt":"2026-11-06T01:59:36Z","builtBy":"tmay57","workflowRunId":"42147413554",
         "intake":{"runId":"42147413554","runAttempt":1,"jobId":"127662155116",
                   "workflowRef":"Scutara/Scutara/.github/workflows/build-config.yml@refs/heads/main",
                   "githubDigest":"sha256:639a4e73ceb777368ce9a320ad40ef6fb4b824cdab536893a98606be5712a912",
                   "gate1":{"result":"passed","evidence":"gate1/1233186125-42147413554-1.json"} } }
        """;

    private static JsonObject ToolingRecord(string lz, string digest) => JsonNode.Parse($$"""
        {"schema":1,"class":"tooling","builtFrom":{"repo":"Scutara/Scutara","commit":"x","lane":"published","packages":{"Lz.Cli":"{{lz}}"} },
         "identity":{"kind":"image","digest":"{{digest}}"} }
        """)!.AsObject();

    private sealed class Records(Dictionary<string, string> objects) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key)
            => Task.FromResult(objects.TryGetValue($"{bucket}/{key}", out var json) ? new StoredRecord(json, "v-1") : null);
    }

    private sealed class Artifacts(bool present = true) : IArtifactObjects
    {
        public int Heads;
        public Task<ArtifactHead?> HeadAsync(string bucket, string key, string versionId)
        {
            Heads++;
            return Task.FromResult(present
                ? new ArtifactHead(versionId, DeployVerification.Sha256Base64(BundleSha), "FULL_OBJECT", 217987)
                : null);
        }
        public Task<(string? VersionId, long Bytes)> DownloadAsync(string bucket, string key, string versionId, string path) => throw new NotSupportedException();
    }

    private sealed class Tooling(params (string Key, JsonObject Record)[] records) : IToolingRecords
    {
        public Task<IReadOnlyList<(string Key, JsonObject Record)>> NewestAsync(int max) => Task.FromResult<IReadOnlyList<(string, JsonObject)>>(records);
    }

    private static VerifySettings Settings(params string[] classes) => new(
        classes.Length == 0 ? new[] { "image", "client", "site", "config", "tooling" } : classes,
        new[] { "CRITICAL" }, RecordStore, new[] { "scu-4df6-b9c6-aiphost" }, ArtifactStore);

    private static JsonObject Input() => new() { ["record"] = new JsonObject { ["bucket"] = RecordStore, ["key"] = ConfigKey } };

    private static Task<JsonObject> Verify(VerifySettings settings, IArtifactObjects artifacts, IToolingRecords? tooling, string lz = "0.13.158")
        => VerifyStep.RunAsync(Input(), settings, new Records(new() { [$"{RecordStore}/{ConfigKey}"] = ConfigRecord(lz) }),
            null!, null!, null!, artifacts, tooling);

    private static readonly Tooling BothBuilds = new(
        ("tooling/scutara/scutara/20261105T120000Z-1-1.json", ToolingRecord("0.13.157", Tooling157)),
        ("tooling/scutara/scutara/20261106T015936Z-42147413570-1.json", ToolingRecord("0.13.158", Tooling158)));

    // ---- Verify ----

    [Fact]
    public async Task AConfigRecord_IsVerifiedWithTheToolingImageCarryingTheLzItPins()
    {
        var artifacts = new Artifacts();
        var v = await Verify(Settings(), artifacts, BothBuilds);
        Assert.Equal("config", v["class"]!.GetValue<string>());
        Assert.Equal(Tooling158, v["tooling"]!["digest"]!.GetValue<string>());
        Assert.Equal("tooling/scutara/scutara/20261106T015936Z-42147413570-1.json", v["tooling"]!["record"]!.GetValue<string>());
        Assert.Equal(BundleSha, v["identity"]!["sha256"]!.GetValue<string>());
        Assert.Equal("40leQqm9xG_uRJcRRhA4LdPZi1eUuZDl", v["identity"]!["versionId"]!.GetValue<string>());
        Assert.Equal(1, artifacts.Heads);

        var older = await Verify(Settings(), new Artifacts(), BothBuilds, lz: "0.13.157");
        Assert.Equal(Tooling157, older["tooling"]!["digest"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithTheBranchOff_AConfigRecordIsRefusedByName_BeforeAnyArtifactIsAskedAbout()
    {
        var artifacts = new Artifacts();
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Settings(), artifacts, tooling: null));
        Assert.Contains("Pipeline.Infrastructure is off", ex.Message);
        Assert.Equal(0, artifacts.Heads);
    }

    [Fact]
    public async Task NoToolingImageCarryingThePinnedLz_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Settings(), new Artifacts(), BothBuilds, lz: "0.13.159"));
        Assert.Contains("Lz.Cli 0.13.159", ex.Message);
    }

    [Fact]
    public async Task ABundleTheStoreDoesNotHold_IsRefused()
        => await Assert.ThrowsAsync<DeployRefused>(() => Verify(Settings(), new Artifacts(present: false), BothBuilds));

    [Fact]
    public async Task AnEnvironmentWhoseClassesOmitConfig_RefusesTheRecord()
    {
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => Verify(Settings("image", "tooling"), new Artifacts(), BothBuilds));
        Assert.Contains("not in Pipeline.Classes", ex.Message);
    }

    // ---- VerifyTooling ----

    private static readonly ToolingSettings Tool = new(
        new[] { "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutara" },
        Registry, "scu-4df6-b9c6-tooling", "scu-dev-deployer-plan",
        "arn:aws:iam::503947800380:role/scu-dev-deployer-tooling-exec", "arn:aws:iam::503947800380:role/scu-dev-deployer-plan-task",
        "/lz/scu-dev-deployer-plan", "us-west-2", new[] { "subnet-0abc" }, new[] { "sg-0abc" });

    private sealed class Notation(int exit = 0, string? say = null) : INotation
    {
        public string? Policy;
        public string? Verified;
        public Task<string?> InstallAsync(NotationLayout layout, string trustPolicyJson) { Policy = trustPolicyJson; return Task.FromResult<string?>(null); }
        public Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(NotationLayout layout, string reference, IReadOnlyDictionary<string, string> environment)
        {
            Verified = reference;
            return Task.FromResult((exit, say ?? $"Successfully verified signature for {reference}\n", ""));
        }
    }

    private sealed class Definitions : ITaskDefinitions
    {
        public Amazon.ECS.Model.RegisterTaskDefinitionRequest? Registered;
        public Task<(Amazon.ECS.Model.TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string arn) => throw new NotSupportedException();
        public Task<string> RegisterAsync(Amazon.ECS.Model.RegisterTaskDefinitionRequest request)
        {
            Registered = request;
            return Task.FromResult("arn:aws:ecs:us-west-2:503947800380:task-definition/scu-dev-deployer-plan:1");
        }
    }

    private static JsonObject Verified(string digest = Tooling158, string cls = "config") => new()
    {
        ["verified"] = new JsonObject { ["class"] = cls, ["tooling"] = new JsonObject { ["digest"] = digest } },
    };

    private static Task<JsonObject> VerifyTooling(JsonObject state, Notation notation, Definitions definitions)
        => InfraPlanSteps.VerifyToolingAsync(state, "exec-1", Tool, () => Task.FromResult(("AWS", "token")), notation,
            NotationLayout.Under("/tmp/lz-notation"), definitions);

    [Fact]
    public async Task AVerifiedToolingDigest_GetsAPlanTaskDefinitionPinningExactlyIt()
    {
        var notation = new Notation();
        var definitions = new Definitions();
        var r = await VerifyTooling(Verified(), notation, definitions);

        var reference = $"{Registry}/scu-4df6-b9c6-tooling@{Tooling158}";
        Assert.Equal(reference, notation.Verified);
        Assert.Contains("signing-profiles/scu_build_ci_scutara", notation.Policy);
        Assert.Contains($"{Registry}/scu-4df6-b9c6-tooling", notation.Policy);
        Assert.Equal(reference, r["reference"]!.GetValue<string>());

        var d = definitions.Registered!;
        Assert.Equal("scu-dev-deployer-plan", d.Family);
        Assert.Equal(reference, Assert.Single(d.ContainerDefinitions).Image);
        Assert.Equal(InfraPlanSteps.Container, d.ContainerDefinitions[0].Name);
        Assert.Equal(Tool.TaskRoleArn, d.TaskRoleArn);
        Assert.Equal(Tool.ExecutionRoleArn, d.ExecutionRoleArn);
        Assert.Contains(d.ContainerDefinitions[0].Environment, e => e.Name == "LZ_CREDENTIALS" && e.Value == "ambient");
        Assert.Equal("arn:aws:ecs:us-west-2:503947800380:task-definition/scu-dev-deployer-plan:1", r["taskDefinitionArn"]!.GetValue<string>());
        Assert.Equal("subnet-0abc", Assert.Single(r["subnets"]!.AsArray())!.GetValue<string>());
        Assert.Equal("sg-0abc", Assert.Single(r["securityGroups"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task ANetworkTheApplyHasNotCreated_IsRefused_AndNoDefinitionIsRegistered()
    {
        var definitions = new Definitions();
        var placeholders = Tool with { Subnets = new[] { "<subnet-created-by-apply>" }, SecurityGroups = new[] { "<security-group-created-by-apply>" } };
        var ex = await Assert.ThrowsAsync<DeployRefused>(() => InfraPlanSteps.VerifyToolingAsync(Verified(), "exec-1", placeholders,
            () => Task.FromResult(("AWS", "token")), new Notation(), NotationLayout.Under("/tmp/lz-notation"), definitions));
        Assert.Contains("Run the apply", ex.Message);
        Assert.Null(definitions.Registered);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(0, "something else\n")]
    public async Task ADigestNotationDoesNotVerify_IsRefused_AndNoDefinitionIsRegistered(int exit, string? say)
    {
        var definitions = new Definitions();
        await Assert.ThrowsAsync<DeployRefused>(() => VerifyTooling(Verified(), new Notation(exit, say), definitions));
        Assert.Null(definitions.Registered);
    }

    [Theory]
    [InlineData("latest", "config")]
    [InlineData(Tooling158, "image")]
    public async Task ATagOrAnotherClass_IsRefusedBeforeNotationRuns(string digest, string cls)
    {
        var notation = new Notation();
        await Assert.ThrowsAsync<DeployRefused>(() => VerifyTooling(Verified(digest, cls), notation, new Definitions()));
        Assert.Null(notation.Verified);
    }

    // ---- ReadPlan ----

    private const string Evidence = "scu-dev-deploy-evidence-4df6-b9c6";

    private static string PlanJson(bool destructive) => $$"""
        {"command":"infraplan","plans":[
          {"stack":"scu-dev","changes":{"update":2},"destructive":false,"destructiveSteps":[]},
          {"stack":"scu-mp-dev","changes":{"delete":1},"destructive":{{(destructive ? "true" : "false")}},
           "destructiveSteps":{{(destructive ? """[{"op":"delete","type":"aws:s3/bucket:Bucket","urn":"urn:x::bucket"}]""" : "[]")}}}],
         "schema":1,"destructive":{{(destructive ? "true" : "false")}},"hasChanges":true}
        """;

    private static JsonObject Stopped(int? exit) => new()
    {
        ["plan"] = new JsonObject { ["Containers"] = new JsonArray(new JsonObject { ["ExitCode"] = exit }) },
    };

    private static Task<JsonObject> ReadPlan(JsonObject state, string? planJson)
        => InfraPlanSteps.ReadPlanAsync(state, "exec-1", Evidence,
            new Records(planJson is null ? new() : new() { [$"{Evidence}/plans/exec-1.json"] = planJson }));

    [Fact]
    public async Task APlanThatIsNotDestructive_EndsPlanned_WithItsStacks()
    {
        var r = await ReadPlan(Stopped(0), PlanJson(destructive: false));
        Assert.Equal("Planned", r["verdict"]!.GetValue<string>());
        Assert.Equal("plans/exec-1.json", r["key"]!.GetValue<string>());
        Assert.Equal("v-1", r["versionId"]!.GetValue<string>());
        Assert.Equal(2, r["stacks"]!.AsArray().Count);
    }

    [Fact]
    public async Task ADestructivePlan_IsRefused_NamingWhatItWouldReplaceOrDelete()
    {
        var ex = await Assert.ThrowsAsync<DestructivePlanRefused>(() => ReadPlan(Stopped(2), PlanJson(destructive: true)));
        Assert.Contains("scu-mp-dev: delete urn:x::bucket", ex.Message);
    }

    [Fact]
    public async Task NoPlan_IsPlanMissing()
        => await Assert.ThrowsAsync<PlanMissing>(() => ReadPlan(Stopped(1), null));

    [Theory]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AnExitCodeThatDisagreesWithThePlan_IsNotTrusted(int exit, bool destructive)
        => await Assert.ThrowsAsync<PlanMissing>(() => ReadPlan(Stopped(exit), PlanJson(destructive)));

    [Fact]
    public async Task APlanOfAnotherShape_IsNotTrusted()
        => await Assert.ThrowsAsync<PlanMissing>(() => ReadPlan(Stopped(0), """{"schema":2,"plans":[],"destructive":false}"""));

    [Fact]
    public void ThePlanKey_IsTheExecutionsUnderPlans()
        => Assert.Equal("plans/req-1.json", InfraPlanSteps.PlanKeyFor("req-1"));
}
