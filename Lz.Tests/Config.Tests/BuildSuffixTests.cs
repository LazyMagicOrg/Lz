using Lz.Aws.Config;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// P5 stage A (DecoupledCd.md §12): the build side's names apart from any environment's suffix. Every name in the build
/// account - the record and artifact stores, the neutral image repository, the intake's evidence store, scratch bucket and
/// probe repository - comes from <c>BuildSuffix</c> in the build-side file, so test (<c>d5e0-b27f</c>) names dev's
/// (<c>4df6-b9c6</c>) rather than an empty set of its own; the environment's own stores stay on its <c>SystemSuffix</c>.
/// Each chain entry names its evidence store, and an environment plans only the repositories whose class it accepts.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class BuildSuffixTests : IDisposable
{
    private readonly string _dir;

    public BuildSuffixTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _dir = Path.Combine(Path.GetTempPath(), "lz-buildsuffix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Head(string suffix) =>
        $"Platform: aws\nTopology: lambda-cognito-dynamodb\nSystemSuffix: {suffix}\nProfile: dummy\nRegion: us-west-2\nRollback:\n  PinImageDigest: true\n";

    private static string EnvPipeline(string target, string classes = "[image, client]", string extra = "")
        => "Pipeline:\n  Enabled: true\n  TargetAccountId: \"" + target + "\"\n  EnforceSignatures: true\n  Alerts: true\n  Classes: " + classes + "\n" + extra;

    private static string BuildSide(string? buildSuffix = "4df6-b9c6", string naming = "neutral", bool evidence = true, string extraRepo = "") => $"""
        BuildAccountId: "147440642635"
        {(buildSuffix is null ? "" : $"BuildSuffix: \"{buildSuffix}\"")}
        Registry:
          RepositoryNaming: {naming}
          TagImmutability: true
        Repositories:
          - Repo: Scutara/ScutaraService
            Class: image
            Artifacts: [aiphost]
          - Repo: Scutara/ScutaraSellerApp
            Class: client
            Artifacts: [sellerapp]
        {extraRepo}
        Chain:
          - Environment: dev
            AccountId: "503947800380"
            {(evidence ? "EvidenceStore: scu-dev-deploy-evidence-4df6-b9c6" : "")}
          - Environment: test
            AccountId: "456687462751"
            {(evidence ? "EvidenceStore: scu-test-deploy-evidence-d5e0-b27f" : "")}
        """;

    private const string DevBody = "__dev__", TestBody = "__test__";

    /// <summary>Writes the build-side file and both environments' systemconfigs, and loads the one asked for.</summary>
    private SystemConfig Load(string env, string? buildSide = null, string? devPipeline = null, string? testPipeline = null)
    {
        File.WriteAllText(Path.Combine(_dir, "pipelineconfig.scu.yaml"), buildSide ?? BuildSide());
        File.WriteAllText(Path.Combine(_dir, "systemconfig.scu.dev.yaml"), Head("4df6-b9c6") + (devPipeline ?? EnvPipeline("503947800380")));
        File.WriteAllText(Path.Combine(_dir, "systemconfig.scu.test.yaml"), Head("d5e0-b27f") + (testPipeline ?? EnvPipeline("456687462751")));
        return ConfigLoader.LoadSystemConfig(Path.Combine(_dir, $"systemconfig.scu.{env}.yaml"));
    }

    private static string Refusal(Action load) => Assert.Throws<InvalidOperationException>(load).Message;

    // ---- A1: the value ----

    [Fact]
    public void TheBuildSuffix_IsFilledIntoEveryEnvironmentsPipeline()
    {
        Assert.Equal("4df6-b9c6", Load("dev").Pipeline!.BuildSuffix);
        ConfigLoader.ResetForTests(); ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        Assert.Equal("4df6-b9c6", Load("test").Pipeline!.BuildSuffix);
    }

    [Theory]
    [InlineData("4DF6-B9C6")]
    [InlineData("4df6 b9c6")]
    [InlineData("-4df6")]
    public void AMalformedBuildSuffix_IsRefused(string value)
        => Assert.Contains("BuildSuffix", Refusal(() => Load("dev", BuildSide(value))));

    [Fact]
    public void AChainOfMoreThanOneEnvironment_NeedsABuildSuffix()
        => Assert.Contains("BuildSuffix", Refusal(() => Load("dev", BuildSide(buildSuffix: null))));

    [Fact]
    public void AnEnvironmentConfigNamingTheBuildSuffix_IsRefused()
        => Assert.Contains("BuildSuffix", Refusal(() => Load("dev", devPipeline: EnvPipeline("503947800380", extra: "  BuildSuffix: \"4df6-b9c6\"\n"))));

    [Fact]
    public void ABuildSuffixOtherThanTheEnvironmentsSuffix_NeedsNeutralRepositoryNaming()
    {
        // The environment mode names {env} in every repository, so it can never be shared between environments.
        var message = Refusal(() => Load("test", BuildSide(naming: "environment")));
        Assert.Contains("BuildSuffix", message);
        Assert.Contains("neutral", message);
    }

    // ---- A3: the chain names each environment's evidence store ----

    [Fact]
    public void EveryEntryOfAChainOfMoreThanOne_NamesItsEvidenceStore()
        => Assert.Contains("EvidenceStore", Refusal(() => Load("dev", BuildSide(evidence: false))));

    [Fact]
    public void TheChainsEvidenceStore_IsFilledIntoThePipeline()
        => Assert.Equal("scu-test-deploy-evidence-d5e0-b27f", Load("test").Pipeline!.ChainEvidenceStore);

    [Fact]
    public void TheDeployer_RefusesToCreateAnEvidenceStoreTheChainDoesNotName()
    {
        var config = InMemory("test", "d5e0-b27f", "456687462751");
        config.Pipeline!.ChainEvidenceStore = "scu-test-deploy-evidence-0000-0000";
        var message = Assert.Throws<InvalidOperationException>(() => PlanOf(config)).Message;
        Assert.Contains("scu-test-deploy-evidence-0000-0000", message);
        Assert.Contains("scu-test-deploy-evidence-d5e0-b27f", message);
    }

    // ---- A4: an environment plans what it accepts ----

    [Fact]
    public void AnEnvironment_PlansOnlyTheRepositoriesWhoseClassItAccepts_AndIsNotRefusedForTheRest()
    {
        const string site = """
              - Repo: Scutara/ScutaraWebsite
                Class: site
                Artifacts: [site-dev, site-test]
            """;
        var config = Load("test", BuildSide(extraRepo: site), devPipeline: EnvPipeline("503947800380", "[image, client, site]"));

        Assert.Contains(config.Pipeline!.Repositories!, r => r.Class == "site");             // the build side keeps every repository
        Assert.DoesNotContain(config.Pipeline.AcceptedRepositories, r => r.Class == "site"); // test accepts no site
        Assert.Empty(DeployerPlanner.SiteApps(config));
    }

    [Fact]
    public void ARepositoryWhoseClassNoEnvironmentInTheChainAccepts_IsRefused()
    {
        const string assets = """
              - Repo: Scutara/ScutaraTenancies
                Class: assets
                Artifacts: [assets]
            """;
        var message = Refusal(() => Load("dev", BuildSide(extraRepo: assets)));
        Assert.Contains("Scutara/ScutaraTenancies", message);
        Assert.Contains("assets", message);
    }

    // ---- A2: two environments, one build side ----

    private static SystemConfig InMemory(string env, string suffix, string account)
    {
        var c = DeployerClientPlanTests.Config();
        c.Environment = env;
        c.SystemSuffix = suffix;
        c.Pipeline!.TargetAccountId = account;
        c.Pipeline.BuildSuffix = "4df6-b9c6";
        c.Pipeline.ChainEvidenceStore = $"scu-{env}-deploy-evidence-{suffix}";
        return c;
    }

    private static PipelineDeployer PlanOf(SystemConfig c)
        => DeployerPlanner.Plan(c, c.Pipeline!.TargetAccountId, clientInputs: DeployerClientPlanTests.Distributions);

    private static readonly SystemConfig Dev = InMemory("dev", "4df6-b9c6", "503947800380");
    private static readonly SystemConfig Test = InMemory("test", "d5e0-b27f", "456687462751");

    [Fact]
    public void TwoEnvironmentsWithDifferentSuffixes_PlanTheSameBuildSideStoresAndRepository()
    {
        var dev = PipelineBootstrapPlanner.Plan(Dev, "147440642635");
        var test = PipelineBootstrapPlanner.Plan(Test, "147440642635");
        Assert.Equal(("scu-artifacts-4df6-b9c6", "scu-build-records-4df6-b9c6"), (test.ArtifactStore, test.BuildRecordStore));
        Assert.Equal((dev.ArtifactStore, dev.BuildRecordStore), (test.ArtifactStore, test.BuildRecordStore));
        Assert.Equal("scu-4df6-b9c6-aiphost", EcrRepositoryNaming.For(Test, "aiphost"));
        Assert.Equal(EcrRepositoryNaming.For(Dev, "aiphost"), EcrRepositoryNaming.For(Test, "aiphost"));
    }

    [Fact]
    public void TheIntakesBuildAccountNames_FollowTheBuildSuffix()
    {
        Assert.Equal("scu-intake-evidence-4df6-b9c6", IntakeBuildPlanner.EvidenceStoreFor(Test));
        Assert.Equal("scu-intake-scratch-4df6-b9c6", IntakeBuildPlanner.ScratchBucketFor(Test));
        Assert.Equal("scu-intake-probe-4df6-b9c6", IntakePushProbe.RepositoryFor(Test));
    }

    [Fact]
    public void TestsDeployer_ReadsTheBuildSidesRecordsAndRepository_AndKeepsItsOwnEvidenceStore()
    {
        var plan = PlanOf(Test);
        var verify = plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify);

        Assert.Equal("scu-build-records-4df6-b9c6", verify.Environment[DeployerEnvironment.BuildRecordStore]);
        Assert.Equal("scu-4df6-b9c6-aiphost", verify.Environment[DeployerEnvironment.ImageRepositories]);
        Assert.Equal("scu-test-deploy-evidence-d5e0-b27f", plan.EvidenceStore);
    }

    // ---- B: bootstrappipeline from a second environment changes nothing of the first's ----

    private static (SystemConfig Dev, SystemConfig Test) Both()
    {
        (SystemConfig, SystemConfig) pair = (InMemory("dev", "4df6-b9c6", "503947800380"), InMemory("test", "d5e0-b27f", "456687462751"));
        foreach (var c in new[] { pair.Item1, pair.Item2 })
            c.Pipeline!.Repositories!.Add(new PipelineRepositoryConfig
            {
                Repo = "Scutara/ScutaraE2ETests", Class = "tests", RepositoryId = 1343104872, OwnerId = 320723031,
                WorkflowPath = ".github/workflows/publish-tests.yml", Intake = "live", TestBundle = "tests",
                Gate1 = new PipelineGate1Config { Kind = "collection" },
            });
        pair.Item1.Pipeline!.DeployOnBuildRecord = true;   // dev heads the chain
        return pair;
    }

    [Fact]
    public void TheIntake_IsPlannedTheSameFromEitherEnvironment()
    {
        var (dev, test) = Both();
        var fromDev = IntakeBuildPlanner.Plan(dev, "147440642635");
        Assert.NotNull(fromDev);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(fromDev), System.Text.Json.JsonSerializer.Serialize(IntakeBuildPlanner.Plan(test, "147440642635")));
    }

    [Fact]
    public void TheSigningRules_ArePlannedTheSameFromEitherEnvironment()
    {
        // MergeSigning keys a rule by its profile, which both environments share: a different repository filter from test
        // would replace dev's. Planned from one build side, the profile signs the same repository either way.
        var (dev, test) = Both();
        static string Signing(SystemConfig c) => string.Join(";", PipelineBootstrapPlanner.Plan(c, "147440642635").Roles
            .Where(r => r.SigningProfile is not null).Select(r => $"{r.SigningProfile}={string.Join(",", r.EcrRepositories)}"));
        Assert.NotEmpty(Signing(dev));
        Assert.Equal(Signing(dev), Signing(test));
    }

    [Fact]
    public void TheBuildAccountsGrants_ToEachEnvironment_NeverShareAStatementId()
    {
        // Each environment's grants are merged into the same store policies by Sid: a Sid both planned would let the second
        // run replace the first's grant.
        var (dev, test) = Both();
        static IEnumerable<string> Sids(SystemConfig c)
        {
            var plan = PipelineBootstrapPlanner.Plan(c, "147440642635");
            return (plan.BuildRecordReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>())
                .Concat(plan.ArtifactReadGrant ?? Array.Empty<System.Text.Json.Nodes.JsonObject>())
                .Select(s => s["Sid"]!.GetValue<string>());
        }
        Assert.NotEmpty(Sids(test));
        Assert.Empty(Sids(dev).Intersect(Sids(test)));
    }

    [Fact]
    public void TestsReplication_IsMergedBesideDevs_AndLeavesItAsItWas()
    {
        var (dev, test) = Both();
        var devRule = PipelineBootstrapPlanner.Plan(dev, "147440642635").Replication!;
        var merged = CrossAccount.MergeReplication(new Amazon.ECR.Model.ReplicationConfiguration { Rules = new() { devRule } },
            PipelineBootstrapPlanner.Plan(test, "147440642635").Replication!);
        Assert.Equal(2, merged.Rules.Count);
        Assert.Same(devRule.RepositoryFilters, merged.Rules.Single(r => r.Destinations.Single().RegistryId == "503947800380").RepositoryFilters);
        Assert.Equal(merged.Rules[0].RepositoryFilters.Select(f => f.Filter), merged.Rules[1].RepositoryFilters.Select(f => f.Filter));
    }

    [Fact]
    public void WithoutABuildSuffix_EveryNameIsTheSystemSuffixs_AsItWasBefore()
    {
        var c = DeployerClientPlanTests.Config();
        Assert.Null(c.Pipeline!.BuildSuffix);
        var build = PipelineBootstrapPlanner.Plan(c, "147440642635");
        Assert.Equal(("scu-artifacts-4df6-b9c6", "scu-build-records-4df6-b9c6"), (build.ArtifactStore, build.BuildRecordStore));
        Assert.Equal("scu-intake-scratch-4df6-b9c6", IntakeBuildPlanner.ScratchBucketFor(c));
    }
}
