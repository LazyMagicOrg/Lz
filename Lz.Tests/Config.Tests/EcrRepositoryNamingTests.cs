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
}
