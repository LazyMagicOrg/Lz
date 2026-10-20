using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The v5 additions to the build-record format (DecoupledCd.md §4.3, §15.4, §15.7 V-A): the intake block, the run
/// attempt in an intake record's key, <c>builtAt</c> from the run's <c>created_at</c> (§15.8 item 11), and the intake's
/// execution names. Half of these tests pin what must NOT change: every record already in the stores was written by
/// today's push path, without an intake block, and must keep its key and its bytes.
/// </summary>
public class BuildRecordIntakeTests
{
    private const string Store = "scu-artifacts-abcd-1234";
    private const string Checksum = "5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private const string GithubDigest = "sha256:" + Checksum;
    private const string BaseDigest = "sha256:0d9cd7bfa6ad1c8a3e2e0d7d7e3a0bba06d3a2b1c4f5e6d7c8b9a0f1e2d3c4b5";

    private static BuildRecordIntake Intake(int attempt = 2, string runId = "34702974502") => new(
        runId, attempt, "981234", "Scutara/ScutaraService/.github/workflows/build-aiphost.yml@refs/heads/main",
        GithubDigest, new BuildRecordGate1("passed", "intake/gate1/34702974502-2.json"),
        ServiceVersion: "1.0.381", BaseImageDigest: BaseDigest,
        Sbom: new BuildRecordObjectRef("image/scutara/scutaraservice/20261020T150405Z-34702974502-2.sbom.json", "v1abc"));

    private static BuildRecord TodaysImage() => new(
        BuildRecordFormat.CurrentSchema, "image",
        new BuildRecordBuiltFrom("Scutara/ScutaraService", "bdf6e14e1c2a", "published", new Dictionary<string, string>()),
        new BuildRecordIdentity("image", Digest: "sha256:ba9773aa21a1"),
        "2026-10-20T15:04:05Z", "Scutara/ScutaraService", "34702974502");

    private static BuildRecord IntakeImage(int attempt = 2) => TodaysImage() with
    {
        BuiltBy = "arn:aws:sts::147440642635:assumed-role/scu-cicd-intake-push/x",
        Intake = Intake(attempt),
    };

    private static BuildRecord Bundle(BuildRecordIntake? intake)
    {
        var record = new BuildRecord(
            BuildRecordFormat.CurrentSchema, "client",
            new BuildRecordBuiltFrom("Scutara/ScutaraSellerApp", "8104ea1b", "published",
                new Dictionary<string, string> { ["AipApi"] = "1.0.17" }),
            new BuildRecordIdentity("bundle"), "2026-10-20T15:04:05Z", "Scutara/ScutaraSellerApp", "34702974502",
            intake);
        return record with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: Store, Key: BuildRecordFormat.BundleKeyFor(record),
                VersionId: "3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY", Sha256: Checksum),
        };
    }

    private static BuildRecordIntake BundleIntake() => Intake() with { ServiceVersion = null, BaseImageDigest = null, Sbom = null };

    // ---- what must not change ------------------------------------------------------------------

    [Fact]
    public void TodaysRecordKeepsItsKey()
        => Assert.Equal("image/scutara/scutaraservice/20261020T150405Z-34702974502.json", BuildRecordFormat.KeyFor(TodaysImage()));

    [Fact]
    public void TodaysRecordIsWrittenWithoutAnIntakeField()
    {
        var json = BuildRecordFormat.Serialize(TodaysImage());
        Assert.DoesNotContain("intake", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(BuildRecordFormat.Parse(json).Intake);
    }

    [Fact]
    public void TodaysBundleStillPairsWithItsZip()
    {
        var r = Bundle(null);
        Assert.Equal("client/scutara/scutarasellerapp/20261020T150405Z-34702974502.zip", r.Identity.Key);
        Assert.Equal(r.Identity.Key, BuildRecordFormat.Parse(BuildRecordFormat.Serialize(r), Store).Identity.Key);
    }

    // ---- the intake record ---------------------------------------------------------------------

    [Fact]
    public void AnIntakeRecordsKeyCarriesItsAttempt()
        => Assert.Equal("image/scutara/scutaraservice/20261020T150405Z-34702974502-2.json", BuildRecordFormat.KeyFor(IntakeImage()));

    [Fact]
    public void TwoAttemptsOfOneRunHaveTwoKeys()
        => Assert.NotEqual(BuildRecordFormat.KeyFor(IntakeImage(1)), BuildRecordFormat.KeyFor(IntakeImage(2)));

    [Fact]
    public void AnIntakeImageRecordRoundTrips()
    {
        var json = BuildRecordFormat.Serialize(IntakeImage());
        var parsed = BuildRecordFormat.Parse(json);
        Assert.Equal(json, BuildRecordFormat.Serialize(parsed));
        Assert.Equal(2, parsed.Intake!.RunAttempt);
        Assert.Equal("passed", parsed.Intake.Gate1.Result);
        Assert.Equal("v1abc", parsed.Intake.Sbom!.VersionId);
        Assert.Equal("1.0.381", parsed.Intake.ServiceVersion);
    }

    [Fact]
    public void AnIntakeBundlesZipKeyCarriesTheAttempt()
    {
        var r = Bundle(BundleIntake());
        Assert.Equal("client/scutara/scutarasellerapp/20261020T150405Z-34702974502-2.zip", r.Identity.Key);
        Assert.NotNull(BuildRecordFormat.Parse(BuildRecordFormat.Serialize(r), Store).Intake);
    }

    [Fact]
    public void AnIntakeBundleNamingTodaysZipKeyIsRefused()
    {
        var withoutAttempt = Bundle(BundleIntake()) with { };
        withoutAttempt = withoutAttempt with
        {
            Identity = withoutAttempt.Identity with { Key = "client/scutara/scutarasellerapp/20261020T150405Z-34702974502.zip" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(withoutAttempt), Store));
        Assert.Contains("identity.key", ex.Message);
    }

    [Fact]
    public void ABundleNeedsNoImageFields()
        => Assert.Null(BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Bundle(BundleIntake())), Store).Intake!.ServiceVersion);

    // ---- refusals ------------------------------------------------------------------------------

    public static TheoryData<string, Func<BuildRecord, BuildRecord>> Broken => new()
    {
        { "intake.runId", r => r with { Intake = r.Intake! with { RunId = "999" } } },
        { "runAttempt", r => r with { Intake = r.Intake! with { RunAttempt = 0 } } },
        { "intake.jobId", r => r with { Intake = r.Intake! with { JobId = "" } } },
        { "intake.workflowRef", r => r with { Intake = r.Intake! with { WorkflowRef = " " } } },
        { "intake.githubDigest", r => r with { Intake = r.Intake! with { GithubDigest = Checksum } } },
        { "intake.githubDigest", r => r with { Intake = r.Intake! with { GithubDigest = "sha256:ABC" } } },
        { "intake.gate1", r => r with { Intake = r.Intake! with { Gate1 = null! } } },
        { "intake.gate1.result", r => r with { Intake = r.Intake! with { Gate1 = new BuildRecordGate1("failed", "e") } } },
        { "intake.gate1.evidence", r => r with { Intake = r.Intake! with { Gate1 = new BuildRecordGate1("passed", "") } } },
        { "intake.serviceVersion", r => r with { Intake = r.Intake! with { ServiceVersion = null } } },
        { "intake.baseImageDigest", r => r with { Intake = r.Intake! with { BaseImageDigest = null } } },
        { "intake.baseImageDigest", r => r with { Intake = r.Intake! with { BaseImageDigest = "mcr.microsoft.com/dotnet/aspnet:10.0" } } },
        { "sbom", r => r with { Intake = r.Intake! with { Sbom = null } } },
        { "intake.sbom.versionId", r => r with { Intake = r.Intake! with { Sbom = new BuildRecordObjectRef("k", "") } } },
        { "\"null\"", r => r with { Intake = r.Intake! with { Sbom = new BuildRecordObjectRef("k", "null") } } },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void AnIntakeImageRecordMissingWhatItMustSayIsRefused(string named, Func<BuildRecord, BuildRecord> breakIt)
    {
        var json = BuildRecordFormat.Serialize(breakIt(IntakeImage()));
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json));
        Assert.Contains(named, ex.Message);
    }

    [Fact]
    public void AnAttemptBelowOneHasNoKey()
        => Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.KeyFor(IntakeImage(0)));

    // ---- builtAt (§15.8 item 11) ---------------------------------------------------------------

    [Fact]
    public void BuiltAtIsTheRunsCreatedAtInUtc()
        => Assert.Equal("2026-10-20T15:04:05Z",
            BuildRecordFormat.BuiltAtFor(new DateTimeOffset(2026, 10, 20, 8, 4, 5, TimeSpan.FromHours(-7))));

    [Fact]
    public void BuiltAtHasTheFixedWidthTheKeysSortBy()
        => Assert.Equal("2026-01-02T03:04:05Z",
            BuildRecordFormat.BuiltAtFor(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 999, TimeSpan.Zero)));

    [Fact]
    public void AReRunOfAnOlderRunSortsBeforeALaterPush()
    {
        var olderRun = IntakeImage(attempt: 3) with { BuiltAt = BuildRecordFormat.BuiltAtFor(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)) };
        var laterPush = IntakeImage(attempt: 1) with
        {
            WorkflowRunId = "34702999999",
            Intake = Intake(1, "34702999999"),
            BuiltAt = BuildRecordFormat.BuiltAtFor(new DateTimeOffset(2026, 10, 20, 9, 5, 0, TimeSpan.Zero)),
        };
        Assert.True(string.CompareOrdinal(BuildRecordFormat.KeyFor(olderRun), BuildRecordFormat.KeyFor(laterPush)) < 0);
    }

    // ---- execution names -----------------------------------------------------------------------

    [Fact]
    public void TheFirstTryIsNamedForTheAttempt() => Assert.Equal("123-456-2", IntakeNames.For(123, 456, 2));

    [Fact]
    public void ARetryCarriesItsNumber() => Assert.Equal("123-456-2-r3", IntakeNames.For(123, 456, 2, 3));

    [Theory]
    [InlineData(0, 456, 1, 0)]
    [InlineData(123, 0, 1, 0)]
    [InlineData(123, 456, 0, 0)]
    [InlineData(123, 456, 1, -1)]
    [InlineData(123, 456, 1, 4)]
    public void ANameTheIntakeWouldNotProduceIsNotMade(long repo, long run, int attempt, int retry)
        => Assert.Throws<ArgumentOutOfRangeException>(() => IntakeNames.For(repo, run, attempt, retry));

    [Theory]
    [InlineData("123-456-2", 123, 456, 2, 0)]
    [InlineData("123-456-2-r1", 123, 456, 2, 1)]
    [InlineData("123-456-2-r3", 123, 456, 2, 3)]
    public void ANameReadsBack(string name, long repo, long run, int attempt, int retry)
    {
        var parsed = IntakeNames.Parse(name);
        Assert.Equal(new IntakeName(repo, run, attempt, retry), parsed);
        Assert.Equal($"{repo}-{run}-{attempt}", parsed!.Attempt);
    }

    [Theory]
    [InlineData("123-456-2-r0")]
    [InlineData("123-456-2-r4")]
    [InlineData("0123-456-2")]
    [InlineData("123-456-02")]
    [InlineData("123-456")]
    [InlineData("req-20261019T205821Z-39323449481-buyerapp-1")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAnIntakeName(string? name) => Assert.Null(IntakeNames.Parse(name));
}
