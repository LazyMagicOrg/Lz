using System.Reflection;
using Lz.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The opt-in Pipeline block (Docs/specs/DecoupledCd.md, punchlist P0).
///
/// <para>The spec is explicit that byte-identical-when-absent "is not a courtesy here; it is the
/// compatibility guarantee the sibling systems run on, so it is pinned by a test, not a comment".
/// <see cref="NothingOutsideTheConfigLayerReadsPipeline"/> is that pin, and its summary explains
/// why it takes the shape it does.</para>
/// </summary>
public class PipelineConfigTests
{
    private static SystemConfig ValidBase() => new()
    {
        SystemKey = "med", Environment = "dev", Profile = "p", Region = "us-west-2", SystemSuffix = "abcd-1234",
    };

    private static PipelineConfig Enabled() => new()
    {
        Enabled = true,
        Classes = new List<string> { "image", "config" },
    };

    /// <summary>A config that satisfies the Rollback cross-check, so other rules can be tested alone.</summary>
    private static SystemConfig WithPipeline(PipelineConfig p)
    {
        var c = ValidBase();
        c.Pipeline = p;
        c.Rollback = new RollbackConfig { PinImageDigest = true };
        return c;
    }

    // ---------------------------------------------------------------------------------------
    //  The compatibility guarantee
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE BYTE-IDENTICAL PIN, and it is worth reading why it is a source scan rather than a plan
    /// diff. P0 asks for "deploy the plan for a config without the block and assert it is
    /// unchanged". A real Pulumi plan diff is not available in a unit test, and — more to the point
    /// — it would be vacuous TODAY, because nothing reads the block at all. So this asserts the
    /// stronger and more useful property directly: outside the config layer, no code reads
    /// SystemConfig.Pipeline. Nothing that is never read can change an emitted plan, for an absent
    /// block or a present one.
    ///
    /// <para>WHEN THIS TEST FAILS, that is the design working. It means a component has begun
    /// consuming the block, and the guarantee stops being free at exactly that moment: the new
    /// consumer needs its own absent-path test, and the cross-workspace plan diff P0 also calls for
    /// becomes a real obligation rather than a formality. Add the consumer to the allowlist below
    /// only together with those.</para>
    /// </summary>
    [Fact]
    public void NothingOutsideTheConfigLayerReadsPipeline()
    {
        // Config layer only: the property's own declaration and the validator that checks it.
        //
        // PLUS ONE CONSUMER, added 2026-09-12 after this test did its job and failed on it.
        // PipelineBootstrapPlanner reads the block, and it is admitted here because it cannot move
        // any deploy's plan: it is reached ONLY from `lz bootstrappipeline`, it REFUSES a config
        // without the block (PipelineBootstrapPlannerTests.ItRefusesASystemWithNoPipelineBlock —
        // the absent-path test this message asks for), and no deploy path calls it at all. The
        // byte-identical guarantee therefore still holds for every command that deploys anything,
        // which was re-confirmed by `lz previewtenant` against dev reporting "no changes" on a
        // config that has no Pipeline block.
        //
        // THE NEXT CONSUMER WILL NOT BE THIS EASY. One that runs inside a deploy needs a real
        // absent-path comparison, not an argument — and the cross-workspace plan diff P0 names has
        // still never been run.
        var allowed = new[]
        {
            Path.Combine("Lz.Core", "Config", "SystemConfig.cs"),
            Path.Combine("Lz.Core", "Config", "PipelineConfig.cs"),
            Path.Combine("Lz.Core", "Config", "ConfigValidator.cs"),
            Path.Combine("Lz.Aws", "Pipeline", "PipelineBootstrapPlan.cs"),
        };

        var root = RepoRoot();
        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Lz.Tests{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(".Pipeline"))
            .Select(f => Path.GetRelativePath(root, f))
            .Where(rel => !allowed.Contains(rel))
            .OrderBy(rel => rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Something outside the config layer now reads SystemConfig.Pipeline:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nThat is allowed - it is the whole point of the block - but it ends the free " +
            "byte-identical guarantee. Before adding the file to this test's allowlist, give the new " +
            "consumer its own test that the ABSENT path emits what it emitted before, and run the " +
            "cross-workspace plan diff DecoupledCd.md P0 asks for.");
    }

    [Fact]
    public void AConfigWithNoPipelineBlock_ValidatesAndInventsNothing()
    {
        var config = ValidBase();

        ConfigValidator.Validate(config, "test.yaml"); // no throw

        Assert.Null(config.Pipeline); // no default is materialised anywhere
    }

    [Fact]
    public void APresentButDisabledBlock_IsNotValidatedAtAll()
    {
        // Present-but-false records "this environment considered the pipeline and is not using it".
        // Every contents rule is gated on Enabled, so writing that down must not require filling in
        // Classes, a topic ARN, or the Rollback cross-check.
        var config = ValidBase();
        config.Pipeline = new PipelineConfig
        {
            Enabled = false,
            Classes = new List<string> { "not-a-class" },
            Approval = new PipelineApprovalConfig { Required = true }, // no NotifyTopicArn
        };

        ConfigValidator.Validate(config, "test.yaml"); // no throw
    }

    // ---------------------------------------------------------------------------------------
    //  The Rollback cross-check
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Enabled_WithoutDigestPinning_IsRefused()
    {
        // The pipeline deploys the image by digest, and that mechanism lives under Rollback. It is
        // refused rather than implied: one opt-in block silently changing another's emitted plan is
        // what these blocks promise not to do.
        var config = ValidBase();
        config.Pipeline = Enabled();

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config, "test.yaml"));

        Assert.Contains("Rollback.PinImageDigest", ex.Message);
    }

    [Fact]
    public void Enabled_WithDigestPinning_IsAccepted()
    {
        ConfigValidator.Validate(WithPipeline(Enabled()), "test.yaml"); // no throw
    }

    // ---------------------------------------------------------------------------------------
    //  Classes — an allowlist, so a typo fails closed and is therefore rejected
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Enabled_WithNoClasses_IsRefused()
    {
        var p = Enabled();
        p.Classes = new List<string>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("Pipeline.Classes is empty", ex.Message);
    }

    [Fact]
    public void Enabled_WithNullClasses_IsRefused()
    {
        var p = Enabled();
        p.Classes = null;

        Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("client")]
    [InlineData("site")]
    [InlineData("assets")]
    [InlineData("config")]
    [InlineData("tooling")]
    public void EveryKnownClass_IsAccepted(string name)
    {
        // Six names for eight classes: 5-7 are all the config bundle. Pinned per name so a future
        // edit to KnownClasses cannot quietly drop one.
        var p = Enabled();
        p.Classes = new List<string> { name };

        ConfigValidator.Validate(WithPipeline(p), "test.yaml"); // no throw
    }

    [Fact]
    public void AnUnknownClass_IsRefused_AndTheMessageNamesIt()
    {
        var p = Enabled();
        p.Classes = new List<string> { "image", "consoles" }; // plausible typo for 'client'

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("'consoles'", ex.Message);
        Assert.Contains("client", ex.Message); // the valid set is offered
    }

    // ---------------------------------------------------------------------------------------
    //  Approval
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RequiredApproval_WithNoTopic_IsRefused()
    {
        // An approval nobody is told about is a deploy that waits a day and then fails.
        var p = Enabled();
        p.Approval = new PipelineApprovalConfig { Required = true };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("NotifyTopicArn", ex.Message);
    }

    [Fact]
    public void RequiredApproval_WithATopic_IsAccepted()
    {
        var p = Enabled();
        p.Approval = new PipelineApprovalConfig
        {
            Required = true,
            NotifyTopicArn = "arn:aws:sns:us-west-2:123456789012:deploys",
        };

        ConfigValidator.Validate(WithPipeline(p), "test.yaml"); // no throw
    }

    [Fact]
    public void UnrequiredApproval_NeedsNoTopic()
    {
        // dev: Required false is the normal case and must not drag a topic in with it.
        var p = Enabled();
        p.Approval = new PipelineApprovalConfig { Required = false };

        ConfigValidator.Validate(WithPipeline(p), "test.yaml"); // no throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveHeartbeat_IsRefused(int seconds)
    {
        var p = Enabled();
        p.Approval = new PipelineApprovalConfig { HeartbeatSeconds = seconds };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("HeartbeatSeconds", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The remaining scalars
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ANonPositiveReconcilerInterval_IsRefused()
    {
        var p = Enabled();
        p.Reconciler = new PipelineReconcilerConfig { IntervalMinutes = 0 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("IntervalMinutes", ex.Message);
    }

    [Theory]
    [InlineData("12345678901")]      // 11 digits
    [InlineData("1234567890123")]    // 13
    [InlineData("12345678901a")]
    public void AMalformedArtifactAccountId_IsRefused(string id)
    {
        var p = Enabled();
        p.ArtifactAccountId = id;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("ArtifactAccountId", ex.Message);
    }

    [Fact]
    public void AWellFormedArtifactAccountId_IsAccepted()
    {
        var p = Enabled();
        p.ArtifactAccountId = "503947800380";

        ConfigValidator.Validate(WithPipeline(p), "test.yaml"); // no throw
    }

    [Fact]
    public void AnUnknownRepositoryNaming_IsRefused()
    {
        var p = Enabled();
        p.Registry = new PipelineRegistryConfig { RepositoryNaming = "Neutral" }; // wrong case

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("RepositoryNaming", ex.Message);
    }

    [Fact]
    public void AnUnknownScanSeverity_IsRefused()
    {
        var p = Enabled();
        p.Scan = new PipelineScanConfig { BlockOn = new List<string> { "Critical" } }; // ECR reports CRITICAL

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(WithPipeline(p), "test.yaml"));

        Assert.Contains("BlockOn", ex.Message);
    }

    [Fact]
    public void PostDeployTests_AreNotPolicedHere()
    {
        // DecoupledCd.md expects this empty in prod, but that is the operator's call per
        // environment. A validator in a library every sibling system shares must not encode one
        // system's policy as a rule.
        var p = Enabled();
        p.PostDeployTests = new List<string> { "Aws", "E2E" };

        ConfigValidator.Validate(WithPipeline(p), "test.yaml"); // no throw
    }

    // ---------------------------------------------------------------------------------------
    //  The YAML the spec documents actually deserializes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheDocumentedYaml_RoundTrips()
    {
        // Verbatim shape from DecoupledCd.md section 3. A schema whose own documentation does not
        // parse is the failure this catches.
        const string yaml = """
            SystemKey: med
            Environment: dev
            Profile: p
            Region: us-west-2
            SystemSuffix: abcd-1234
            Rollback:
              PinImageDigest: true
            Pipeline:
              Enabled: true
              ArtifactAccountId: "503947800380"
              Classes: [image, client, site, assets, config]
              Registry:
                RepositoryNaming: neutral
                TagImmutability: true
              Approval:
                Required: true
                HeartbeatSeconds: 86400
                NotifyTopicArn: "arn:aws:sns:us-west-2:503947800380:deploys"
                RefuseDestructivePlan: true
              Reconciler:
                IntervalMinutes: 15
              Scan:
                BlockOn: [CRITICAL]
              PostDeployTests: [Aws, E2E]
            """;

        var config = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<SystemConfig>(yaml);

        ConfigValidator.Validate(config, "test.yaml"); // the documented example is also VALID

        var p = config.Pipeline!;
        Assert.True(p.Enabled);
        Assert.Equal("503947800380", p.ArtifactAccountId);
        Assert.Equal(new[] { "image", "client", "site", "assets", "config" }, p.Classes);
        Assert.Equal("neutral", p.Registry!.RepositoryNaming);
        Assert.True(p.Registry.TagImmutability);
        Assert.True(p.Approval!.Required);
        Assert.Equal(86400, p.Approval.HeartbeatSeconds);
        Assert.True(p.Approval.RefuseDestructivePlan);
        Assert.Equal(15, p.Reconciler!.IntervalMinutes);
        Assert.Equal(new[] { "CRITICAL" }, p.Scan!.BlockOn);
        Assert.Equal(new[] { "Aws", "E2E" }, p.PostDeployTests);
    }

    /// <summary>Walk up from the test assembly to the directory holding Lz.slnx.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir); // the scan is meaningless if the root was not found
        return dir!.FullName;
    }
}
