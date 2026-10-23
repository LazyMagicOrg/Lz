using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// A SITE'S BUILD RECORD (DecoupledCd.md V-24, §15.7 V-K): one per environment's bundle, each naming the environment it was
/// built for in <c>intake.environment</c>. The environment is a directory under the repository's prefix, so one run's
/// records do not share a key, a route can be told which of them is its environment's, and the writer's prefix is
/// unchanged. Only the intake writes one, so a site record without an intake block is refused; and no other class names an
/// environment.
/// </summary>
public class SiteRecordTests
{
    private const string Store = "scu-artifacts-abcd-1234";
    private const string Checksum = "5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";

    private static BuildRecordIntake Intake(string? environment = "dev") => new(
        "39900000001", 1, "981234", "Scutara/ScutaraWebsite/.github/workflows/publish.yml@refs/heads/main",
        "sha256:" + Checksum, new BuildRecordGate1("passed", "gate1/1-39900000001-1.json"), Environment: environment);

    private static BuildRecord Site(BuildRecordIntake? intake, string cls = "site")
    {
        var record = new BuildRecord(
            BuildRecordFormat.CurrentSchema, cls,
            new BuildRecordBuiltFrom("Scutara/ScutaraWebsite", "0455585aa", "published", new Dictionary<string, string>(), "refs/heads/main"),
            new BuildRecordIdentity("bundle"), "2026-10-23T15:04:05Z", "tmay57", "39900000001", intake);
        return record with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: Store, Key: BuildRecordFormat.BundleKeyFor(record),
                VersionId: "3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY", Sha256: Checksum),
        };
    }

    [Fact]
    public void ASiteRecordsKeyIsUnderItsEnvironment()
    {
        var r = Site(Intake("dev"));
        Assert.Equal("site/scutara/scutarawebsite/dev/20261023T150405Z-39900000001-1.json", BuildRecordFormat.KeyFor(r));
        Assert.Equal("site/scutara/scutarawebsite/dev/20261023T150405Z-39900000001-1.zip", r.Identity.Key);
    }

    [Fact]
    public void TwoEnvironmentsOfOneRun_DoNotShareAKey()
        => Assert.NotEqual(BuildRecordFormat.KeyFor(Site(Intake("dev"))), BuildRecordFormat.KeyFor(Site(Intake("test"))));

    [Fact]
    public void TheWritersPrefixIsTheRepositorysAsForEveryClass()
        => Assert.StartsWith(BuildRecordFormat.PrefixFor("site", "Scutara/ScutaraWebsite"), BuildRecordFormat.KeyFor(Site(Intake("dev"))));

    [Fact]
    public void ASiteRecordRoundTrips_WithItsEnvironmentAndNoPackages()
    {
        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Site(Intake("dev"))), Store);
        Assert.Equal("dev", parsed.Intake!.Environment);
        Assert.Empty(parsed.BuiltFrom.Packages);
    }

    [Fact]
    public void TheEnvironmentIsReadFromTheRecord()
        => Assert.Equal("test", BuildRecordFormat.EnvironmentOf(Site(Intake("test"))));

    [Fact]
    public void ASiteRecordWithoutAnIntakeBlock_IsRefused()
    {
        // Built with the intake for its key, then the block removed: the push path never wrote a site record.
        var json = BuildRecordFormat.Serialize(Site(Intake("dev")) with { Intake = null });
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json, Store));
        Assert.Contains("a site record is written by the intake only", ex.Message);
    }

    [Theory]
    [InlineData(null, "names no intake.environment")]
    [InlineData("Dev", "intake.environment 'Dev' is not an environment's name")]
    [InlineData("../prod", "intake.environment '../prod' is not an environment's name")]
    public void ASiteRecordMustNameItsEnvironment(string? environment, string problem)
    {
        var record = Site(Intake("dev"));
        var json = BuildRecordFormat.Serialize(record with { Intake = record.Intake! with { Environment = environment } });
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json, Store));
        Assert.Contains(problem, ex.Message);
    }

    [Fact]
    public void ARecordFromAnotherEnvironmentsDirectory_IsRefused()
    {
        // The record says dev, its zip key says test: the key is not the one its contents imply.
        var record = Site(Intake("dev"));
        var json = BuildRecordFormat.Serialize(record with { Identity = record.Identity with { Key = record.Identity.Key!.Replace("/dev/", "/test/") } });
        Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json, Store));
    }

    [Fact]
    public void NoOtherClassNamesAnEnvironment()
    {
        var client = Site(Intake("dev"), cls: "client");
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(client), Store));
        Assert.Contains("only a site record names intake.environment", ex.Message);
    }

    [Fact]
    public void TheEnvironmentPatternIsTheConfigsToo()
        => Assert.Equal(Lz.Core.Config.SiteArtifacts.EnvironmentPattern, BuildRecordFormat.EnvironmentPattern);

    [Fact]
    public void AnotherClassesKeyIsUnchanged()
    {
        var client = Site(Intake(null), cls: "client");
        Assert.Equal("client/scutara/scutarawebsite/20261023T150405Z-39900000001-1.json", BuildRecordFormat.KeyFor(client));
        Assert.Null(BuildRecordFormat.EnvironmentOf(client));
    }
}
