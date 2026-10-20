using System.Text.Json;
using Lz.Aws.Config;
using Lz.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The build-side pipeline file, <c>pipelineconfig.{sk}.yaml</c> (DecoupledCd.md V-21; §15.7 V-A): what it holds, how
/// <see cref="ConfigLoader.LoadSystemConfig"/> fills it into an environment's Pipeline block, what it refuses, and - the
/// test V-A names - that a config split across the two files yields exactly the Pipeline block the single file did.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class BuildSideConfigTests : IDisposable
{
    private readonly string _dir;

    public BuildSideConfigTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _dir = Path.Combine(Path.GetTempPath(), "lz-buildside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string Head = "Platform: aws\nTopology: lambda-cognito-dynamodb\nSystemSuffix: test\nProfile: dummy\nRegion: us-west-2\nRollback:\n  PinImageDigest: true\n";

    private static string EnvPipeline(string target = "503947800380", string extra = "")
        => "Pipeline:\n  Enabled: true\n  TargetAccountId: \"" + target + "\"\n  EnforceSignatures: true\n  Alerts: true\n  Classes: [image, client]\n" + extra;

    private const string BuildSide = """
        BuildAccountId: "147440642635"
        Registry:
          RepositoryNaming: neutral
          TagImmutability: true
        Repositories:
          - Repo: Scutara/ScutaraService
            Class: image
            Artifacts: [aiphost]
          - Repo: Scutara/ScutaraSellerApp
            Class: client
            Artifacts: [sellerapp]
        Chain:
          - Environment: dev
            AccountId: "503947800380"
          - Environment: test
            AccountId: "456687462751"
        """;

    private string Write(string env, string body, string? buildSide = BuildSide)
    {
        if (buildSide is not null) File.WriteAllText(Path.Combine(_dir, "pipelineconfig.t.yaml"), buildSide);
        var path = Path.Combine(_dir, $"systemconfig.t.{env}.yaml");
        File.WriteAllText(path, Head + body);
        return path;
    }

    private static string Json(PipelineConfig p) => JsonSerializer.Serialize(p);

    // ---- the move is exact ---------------------------------------------------------------------

    [Fact]
    public void ASplitConfigYieldsTheSamePipelineBlockAsTheSingleFileDid()
    {
        // Dev's shape before the move: everything in the environment's own Pipeline block.
        const string before = """
            Pipeline:
              Enabled: true
              ArtifactAccountId: "147440642635"
              TargetAccountId: "503947800380"
              EnforceSignatures: true
              DeployOnBuildRecord: true
              Alerts: true
              Classes: [image, client]
              Repositories:
                - Repo: Scutara/ScutaraService
                  Class: image
                  Artifacts: [aiphost]
                - Repo: Scutara/ScutaraSellerApp
                  Class: client
                  Artifacts: [sellerapp]
              Registry:
                RepositoryNaming: neutral
                TagImmutability: true
            """;
        var old = new DeserializerBuilder().WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build().Deserialize<SystemConfig>(before).Pipeline!;

        var merged = ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline())).Pipeline!;

        Assert.Equal(Json(old), Json(merged));
    }

    [Fact]
    public void TheChainsFirstEnvironmentIsFedByBuildRecordsAndOnlyIt()
    {
        Assert.True(ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline())).Pipeline!.DeployOnBuildRecord);
        ConfigLoader.ResetForTests(); ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        Assert.False(ConfigLoader.LoadSystemConfig(Write("test", EnvPipeline("456687462751"))).Pipeline!.DeployOnBuildRecord);
        ConfigLoader.ResetForTests(); ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        Assert.False(ConfigLoader.LoadSystemConfig(Write("prod", EnvPipeline("982408502448"))).Pipeline!.DeployOnBuildRecord);
    }

    // ---- the absent paths ----------------------------------------------------------------------

    [Fact]
    public void NoPipelineBlock_NothingIsReadOrInvented()
        => Assert.Null(ConfigLoader.LoadSystemConfig(Write("dev", "")).Pipeline);

    [Fact]
    public void NoBuildSideFile_ADisabledOrAbsentBlockLoadsAsBefore()
    {
        Assert.Null(ConfigLoader.LoadSystemConfig(Write("dev", "", buildSide: null)).Pipeline);
        var disabled = ConfigLoader.LoadSystemConfig(Write("dev", "Pipeline:\n  Enabled: false\n", buildSide: null)).Pipeline!;
        Assert.Null(disabled.ArtifactAccountId);
        Assert.Null(disabled.Repositories);
        Assert.False(disabled.DeployOnBuildRecord);
    }

    // ---- refusals ------------------------------------------------------------------------------

    [Theory]
    [InlineData("  ArtifactAccountId: \"147440642635\"\n", "ArtifactAccountId")]
    [InlineData("  DeployOnBuildRecord: false\n", "DeployOnBuildRecord")]
    [InlineData("  Registry:\n    RepositoryNaming: neutral\n", "Registry")]
    [InlineData("  Repositories: []\n", "Repositories")]
    public void AnEnvironmentConfigStillNamingAMovedFieldIsRefused(string extra, string field)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline(extra: extra))));
        Assert.Contains(field, ex.Message);
        Assert.Contains("pipelineconfig.t.yaml", ex.Message);
    }

    [Fact]
    public void EveryMovedFieldIsNamedNotOnlyTheFirst()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(
            Write("dev", EnvPipeline(extra: "  ArtifactAccountId: \"147440642635\"\n  DeployOnBuildRecord: true\n"))));
        Assert.Contains("ArtifactAccountId, DeployOnBuildRecord", ex.Message);
    }

    [Fact]
    public void AnEnabledPipelineWithoutTheBuildSideFileIsRefused()
        => Assert.Contains("no pipelineconfig.t.yaml",
            Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline(), buildSide: null))).Message);

    [Fact]
    public void TheChainAndTheEnvironmentMustAgreeOnItsAccount()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline("111111111111"))));
        Assert.Contains("503947800380", ex.Message);
        Assert.Contains("111111111111", ex.Message);
    }

    [Fact]
    public void AMalformedBuildSideFileIsRefusedWithEveryProblem()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(Write("dev", EnvPipeline(), """
            BuildAccountId: "12345"
            Chain:
              - Environment: dev
                AccountId: "503947800380"
              - Environment: dev
                AccountId: "503947800380"
            """)));
        Assert.Contains("BuildAccountId is '12345'", ex.Message);
        Assert.Contains("names 'dev' twice", ex.Message);
    }

    public static TheoryData<string, BuildSideConfig> Unsound => new()
    {
        { "BuildAccountId is required", new BuildSideConfig() },
        { "not a 12-digit", new BuildSideConfig { BuildAccountId = "147440642635", Chain = new() { new() { Environment = "dev", AccountId = "x" } } } },
        { "has no Environment", new BuildSideConfig { BuildAccountId = "147440642635", Chain = new() { new() { AccountId = "503947800380" } } } },
        { "is the build account", new BuildSideConfig { BuildAccountId = "147440642635", Chain = new() { new() { Environment = "dev", AccountId = "147440642635" } } } },
        { "has no Repo", new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { new() { Class = "image" } } } },
        { "Intake is 'on'", new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { new() { Repo = "a/b", Intake = "on", RepositoryId = 1, OwnerId = 2, WorkflowPath = "w" } } } },
        { "Verify is 'yes'", new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { new() { Repo = "a/b", Verify = "yes" } } } },
        { "Verify goes live last", new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { new() { Repo = "a/b", Intake = "dry", Verify = "live", RepositoryId = 1, OwnerId = 2, WorkflowPath = "w" } } } },
        { "needs RepositoryId, OwnerId and WorkflowPath", new BuildSideConfig { BuildAccountId = "147440642635", Repositories = new() { new() { Repo = "a/b", Intake = "dry" } } } },
    };

    [Theory]
    [MemberData(nameof(Unsound))]
    public void EachUnsoundBuildSideFileIsNamed(string problem, BuildSideConfig config)
        => Assert.Contains(config.Problems(), p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void ASoundFileWithAnIntakeEntryHasNoProblems()
        => Assert.Empty(new BuildSideConfig
        {
            BuildAccountId = "147440642635",
            Repositories = new() { new() { Repo = "a/b", Intake = "live", Verify = "live", RepositoryId = 1, OwnerId = 2, WorkflowPath = ".github/workflows/b.yml" } },
            Chain = new() { new() { Environment = "dev", AccountId = "503947800380" } },
        }.Problems());

    [Fact]
    public void OnlyKeysInsideThePipelineBlockCount()
    {
        Assert.Empty(BuildSideConfig.MovedFieldsNamedIn("Registry: x\nRepositories: []\nPipeline:\n  Enabled: true\n"));
        Assert.Empty(BuildSideConfig.MovedFieldsNamedIn(""));
        Assert.Equal(new[] { "Registry" }, BuildSideConfig.MovedFieldsNamedIn("Pipeline:\n  Registry:\n    RepositoryNaming: neutral\n"));
    }
}
