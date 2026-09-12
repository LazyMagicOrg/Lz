using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The build-record format (DecoupledCd.md §4.3, punchlist P1), frozen before the first record
/// exists — which is the only moment it can be. A record is written once under a conditional write
/// into a store its writer cannot delete from, so every field decision here is permanent for every
/// record ever written under it.
///
/// <para>Most of these tests are REFUSALS, and that is the point: each one is a state that would
/// otherwise reach Verify as a plausible-looking object, and be acted on.</para>
/// </summary>
public class BuildRecordFormatTests
{
    private static BuildRecordBuiltFrom BuiltFrom(params (string Id, string Version)[] packages) =>
        new("Scutara/ScutaraService", "bdf6e14e1c2a", "published",
            packages.ToDictionary(p => p.Id, p => p.Version));

    private static BuildRecord Image() => new(
        BuildRecordFormat.CurrentSchema, "image",
        BuiltFrom(("LazyMagic.Shared", "3.0.26-alpha")),
        new BuildRecordIdentity("image", Digest: "sha256:ba9773aa21a1"),
        "2026-09-12T15:04:05Z", "Scutara/ScutaraService", "34702974502");

    private static BuildRecord Bundle() => new(
        BuildRecordFormat.CurrentSchema, "client",
        new BuildRecordBuiltFrom("Scutara/ScutaraSellerApp", "8104ea1b", "published",
            new Dictionary<string, string> { ["AipApi"] = "1.0.17" }),
        new BuildRecordIdentity("bundle", Bucket: "scu-artifacts-abcd-1234",
            Key: "client/scutara/scutarasellerapp/x.zip", VersionId: "v1", Sha256: "abc123"),
        "2026-09-12T15:04:05Z", "Scutara/ScutaraSellerApp", "1");

    // ---------------------------------------------------------------------------------------
    //  Round trip
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AnImageRecordRoundTrips()
    {
        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Image()));

        Assert.Equal("image", parsed.Class);
        Assert.Equal("sha256:ba9773aa21a1", parsed.Identity.Digest);
        Assert.Equal("published", parsed.BuiltFrom.Lane);
        Assert.Equal("3.0.26-alpha", parsed.BuiltFrom.Packages["LazyMagic.Shared"]);
    }

    [Fact]
    public void ABundleRecordRoundTrips()
    {
        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Bundle()));

        Assert.Equal("bundle", parsed.Identity.Kind);
        Assert.Equal("v1", parsed.Identity.VersionId);
    }

    [Fact]
    public void TheWireFormatIsCamelCase_MatchingTheSpecExample()
    {
        // The spec documents this shape; a record that does not match it is a record nothing
        // written against the spec can read.
        var json = BuildRecordFormat.Serialize(Image());

        foreach (var field in new[] { "\"schema\"", "\"class\"", "\"builtFrom\"", "\"identity\"",
                                      "\"builtAt\"", "\"builtBy\"", "\"workflowRunId\"" })
            Assert.Contains(field, json);
    }

    // ---------------------------------------------------------------------------------------
    //  The key layout
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheKeyIsClassThenRepositoryThenTime()
    {
        // Class first satisfies §4.3's "sorts by time within its class"; repository second is what
        // lets a writer role be scoped to its own prefix and nothing else.
        Assert.Equal(
            "image/scutara/scutaraservice/20260912T150405Z-34702974502.json",
            BuildRecordFormat.KeyFor(Image()));
    }

    [Fact]
    public void KeysSortByTimeWithinOneRepository()
    {
        var early = Image() with { BuiltAt = "2026-09-12T09:00:00Z", WorkflowRunId = "2" };
        var late = Image() with { BuiltAt = "2026-09-12T17:00:00Z", WorkflowRunId = "1" };

        // Ordinal comparison is what S3 listing does, so the fixed-width RFC 3339 stamp has to
        // sort correctly as a STRING — note the later record has the LOWER run id, so this is
        // testing the timestamp rather than an accident of the suffix.
        Assert.True(string.CompareOrdinal(
            BuildRecordFormat.KeyFor(early), BuildRecordFormat.KeyFor(late)) < 0);
    }

    [Fact]
    public void TwoRepositoriesOfOneClass_GetDifferentPrefixes()
    {
        Assert.NotEqual(
            BuildRecordFormat.PrefixFor("client", "Scutara/ScutaraSellerApp"),
            BuildRecordFormat.PrefixFor("client", "Scutara/ScutaraAdminApp"));
    }

    // ---------------------------------------------------------------------------------------
    //  Refusals — each one a record that would otherwise be acted on
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ARecordWithNoBuiltFrom_IsRefused()
    {
        // The distinction this preserves is the entire reason builtFrom was specified before any
        // record existed: an empty `packages` map is a statement, an absent block is a fault.
        const string json = """
            { "schema": 1, "class": "image",
              "identity": { "kind": "image", "digest": "sha256:ab" },
              "builtAt": "2026-09-12T15:04:05Z", "builtBy": "x", "workflowRunId": "1" }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json));
        Assert.Contains("builtFrom", ex.Message);
    }

    [Fact]
    public void ARecordWithBuiltFromButNoPackages_IsRefused()
    {
        const string json = """
            { "schema": 1, "class": "image",
              "builtFrom": { "repo": "a/b", "commit": "c", "lane": "published" },
              "identity": { "kind": "image", "digest": "sha256:ab" },
              "builtAt": "2026-09-12T15:04:05Z", "builtBy": "x", "workflowRunId": "1" }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse(json));
        Assert.Contains("empty map", ex.Message);
    }

    [Fact]
    public void AnEmptyPackagesMap_IsAccepted_BecauseItIsAStatement()
    {
        // Classes 3 and 4 genuinely consume none. Their {} is load-bearing: it is what lets a
        // MISSING block be a fault for every class rather than only some.
        var assets = Bundle() with
        {
            Class = "assets",
            BuiltFrom = new BuildRecordBuiltFrom(
                "Scutara/ScutaraTenancies", "fa63754", "published", new Dictionary<string, string>()),
        };

        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(assets));
        Assert.Empty(parsed.BuiltFrom.Packages);
    }

    [Fact]
    public void ABundleIdentityWithNoVersionId_IsRefused()
    {
        // The VERSION is the identity, not the key. Without it a deployer fetches whatever the key
        // currently points at — the mutable-pointer problem digests exist to avoid, one storage
        // layer down.
        var bad = Bundle() with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: "b", Key: "k", Sha256: "s"),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(bad)));
        Assert.Contains("versionId", ex.Message);
    }

    [Fact]
    public void AnImageIdentityNamingATag_IsRefused()
    {
        var bad = Image() with { Identity = new BuildRecordIdentity("image", Digest: "latest") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(bad)));
        Assert.Contains("not a sha256", ex.Message);
    }

    [Theory]
    [InlineData("image", "bundle")]
    [InlineData("client", "image")]
    [InlineData("tooling", "bundle")]
    public void AClassCarryingTheWrongIdentityKind_IsRefused(string cls, string kind)
    {
        // Class and identity kind are not independent. `tooling` is an image like `image` is;
        // everything else is a bundle. A mismatch is a record nothing can deploy.
        var bad = Image() with
        {
            Class = cls,
            Identity = kind == "image"
                ? new BuildRecordIdentity("image", Digest: "sha256:ab")
                : new BuildRecordIdentity("bundle", Bucket: "b", Key: "k", VersionId: "v", Sha256: "s"),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(bad)));
        Assert.Contains("identity", ex.Message);
    }

    [Fact]
    public void ARecordFromAFutureSchema_IsRefused()
    {
        // A record is immutable, so the writer cannot be asked to try again — reading it with
        // today's assumptions is the one option that must not be taken silently.
        var future = Image() with { Schema = BuildRecordFormat.CurrentSchema + 1 };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(future)));
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void MalformedJson_IsRefusedWithTheReason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildRecordFormat.Parse("{ not json"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("builtAt")]
    [InlineData("builtBy")]
    [InlineData("workflowRunId")]
    public void EveryRequiredScalar_IsRefusedWhenBlank(string field)
    {
        var r = Image();
        var blanked = field switch
        {
            "class" => r with { Class = "" },
            "builtAt" => r with { BuiltAt = "" },
            "builtBy" => r with { BuiltBy = "" },
            _ => r with { WorkflowRunId = "" },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(blanked)));
        Assert.Contains(field, ex.Message);
    }
}
