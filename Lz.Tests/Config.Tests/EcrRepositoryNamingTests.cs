using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// What an ECR repository is called (DecoupledCd.md §8.5). The neutral name drops BOTH the
/// environment and the tenant; the reasons differ and both matter.
/// </summary>
public class EcrRepositoryNamingTests
{
    private static SystemConfig Config(string? naming) => new()
    {
        SystemKey = "scu", SystemSuffix = "4df6-b9c6", Environment = "dev", Region = "us-west-2",
        Pipeline = naming is null ? null : new PipelineConfig
        {
            Enabled = true,
            Registry = new PipelineRegistryConfig { RepositoryNaming = naming },
        },
    };

    [Fact]
    public void TheNeutralNameDropsEnvironmentAndTenant()
    {
        Assert.Equal("scu-4df6-b9c6-aiphost", EcrRepositoryNaming.For(Config("neutral"), "aiphost"));
    }

    [Fact]
    public void TheEnvironmentNameIsExactlyWhatExistsToday()
    {
        // The live repository on 2026-09-12 was scu-4df6-b9c6-dev-mp-aiphost. A system that has not
        // opted in must keep getting this name, byte for byte.
        Assert.Equal("scu-4df6-b9c6-dev-mp-aiphost",
            EcrRepositoryNaming.For(Config("environment"), "aiphost", "mp"));
    }

    [Fact]
    public void WithNoPipelineBlockTheNameIsUnchanged()
    {
        // The default is NOT neutral. An un-opted-in system's repository name cannot move, or the
        // deploy would look for an image that is not there.
        Assert.Equal("scu-4df6-b9c6-dev-mp-aiphost",
            EcrRepositoryNaming.For(Config(null), "aiphost", "mp"));
        Assert.False(EcrRepositoryNaming.IsNeutral(Config(null)));
    }

    [Fact]
    public void ThePipelineBlockAloneDoesNotMakeItNeutral()
    {
        // Only the explicit mode does. Enabling the pipeline without naming a mode leaves every
        // repository name where it is.
        var c = Config(null);
        c.Pipeline = new PipelineConfig { Enabled = true };

        Assert.False(EcrRepositoryNaming.IsNeutral(c));
    }

    [Fact]
    public void TheNeutralNameIsTheSameInEveryEnvironment()
    {
        // This is the property that makes promotion by replication meaningful — the thing that
        // arrives in prod is provably the thing that was built.
        var dev = Config("neutral");
        var prod = Config("neutral");
        prod.Environment = "prod";

        Assert.Equal(EcrRepositoryNaming.For(dev, "aiphost"), EcrRepositoryNaming.For(prod, "aiphost"));
    }

    [Fact]
    public void TheEnvironmentModeRefusesWithoutATenant_RatherThanInventingOne()
    {
        // A silently-defaulted tenant would produce a name that resolves to the wrong repository,
        // which surfaces as a pull failure at deploy time and names nothing.
        var ex = Assert.Throws<InvalidOperationException>(
            () => EcrRepositoryNaming.For(Config("environment"), "aiphost"));

        Assert.Contains("tenant key", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  PipelineImageSourceFor — the deploy path's only read of the block
    // ---------------------------------------------------------------------------------------

    private static SystemConfig Deployable()
    {
        var c = Config("neutral");
        c.Pipeline!.TargetAccountId = "503947800380";
        c.Pipeline.Classes = new List<string> { "image", "client" };
        c.Pipeline.Repositories = new List<PipelineRepositoryConfig>
        {
            new() { Repo = "Scutara/ScutaraService", Class = "image", Artifacts = new List<string> { "aiphost" } },
            new() { Repo = "Scutara/ScutaraSellerApp", Class = "client" },
        };
        return c;
    }

    [Fact]
    public void ThePipelinesRepositoryForAService_IsInTheTargetAccountsRegistry()
    {
        var source = EcrRepositoryNaming.PipelineImageSourceFor(Deployable(), "aiphost", "mp", "us-west-2");

        Assert.Equal(new Lz.Aws.Compute.PipelineImageSource("503947800380.dkr.ecr.us-west-2.amazonaws.com", "scu-4df6-b9c6-aiphost"), source);
    }

    [Fact]
    public void WithNoPipelineBlock_ThereIsNoPipelineSource()
    {
        // THE ABSENT PATH. Null is what makes deploytenant and updatecontainer the historic code exactly.
        Assert.Null(EcrRepositoryNaming.PipelineImageSourceFor(Config(null), "aiphost", "mp", "us-west-2"));
    }

    [Fact]
    public void EachConditionAlone_TakesThePipelineSourceAway()
    {
        var disabled = Deployable(); disabled.Pipeline!.Enabled = false;
        var noTarget = Deployable(); noTarget.Pipeline!.TargetAccountId = null;
        var noImages = Deployable(); noImages.Pipeline!.Classes = new List<string> { "client" };
        var notBuilt = Deployable(); notBuilt.Pipeline!.Repositories![0].Artifacts = new List<string> { "worker" };
        var notImage = Deployable(); notImage.Pipeline!.Repositories![0].Class = "client";

        foreach (var (why, config) in new[]
                 {
                     ("disabled", disabled), ("no TargetAccountId", noTarget), ("image not accepted", noImages),
                     ("service not an artifact", notBuilt), ("entry not class image", notImage),
                 })
            Assert.True(EcrRepositoryNaming.PipelineImageSourceFor(config, "aiphost", "mp", "us-west-2") is null, why);
    }

    [Fact]
    public void UnderEnvironmentNaming_ThePipelinesRepositoryIsTheWorkstationsOwn()
    {
        // Nothing to tell apart: an image in it is recognised as the workstation's, as it always was.
        var c = Deployable();
        c.Pipeline!.Registry!.RepositoryNaming = "environment";

        Assert.Equal("scu-4df6-b9c6-dev-mp-aiphost",
            EcrRepositoryNaming.PipelineImageSourceFor(c, "aiphost", "mp", "us-west-2")!.Repository);
    }
}
