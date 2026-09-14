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

    /// <summary>The artifact store the bundle records below name, as the build-side planner names it.</summary>
    private const string Store = "scu-artifacts-abcd-1234";

    /// <summary>A SHA-256 as the writer records it: 64 lowercase hex characters.</summary>
    private const string Checksum = "5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";

    /// <summary>
    /// A bundle record whose identity is what a correct writer produces: the artifact store, the key mirroring the
    /// record's own, a real object version and a hex checksum. The refusal tests each break exactly one of those.
    /// </summary>
    private static BuildRecord Bundle(
        string cls = "client", string repo = "Scutara/ScutaraSellerApp", Dictionary<string, string>? packages = null)
    {
        var record = new BuildRecord(
            BuildRecordFormat.CurrentSchema, cls,
            new BuildRecordBuiltFrom(repo, "8104ea1b", "published",
                packages ?? new Dictionary<string, string> { ["AipApi"] = "1.0.17" }),
            new BuildRecordIdentity("bundle"),
            "2026-09-12T15:04:05Z", repo, "1");

        return record with
        {
            Identity = new BuildRecordIdentity("bundle", Bucket: Store,
                Key: BuildRecordFormat.BundleKeyFor(record), VersionId: "3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY", Sha256: Checksum),
        };
    }

    private static BuildRecord WithIdentity(BuildRecord r, string? bucket = null, string? key = null,
        string? versionId = null, string? sha256 = null) => r with
    {
        Identity = r.Identity with
        {
            Bucket = bucket ?? r.Identity.Bucket,
            Key = key ?? r.Identity.Key,
            VersionId = versionId ?? r.Identity.VersionId,
            Sha256 = sha256 ?? r.Identity.Sha256,
        },
    };

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
    public void TheRef_RoundTrips_AndARecordWithoutOneStillParses()
    {
        // builtFrom.ref (P2 stage D2) is the one optional field of schema 1: every record written before it exists
        // lacks it, and a record is immutable, so absence is read as absence — never refused, never invented.
        var withRef = Image() with { BuiltFrom = Image().BuiltFrom with { Ref = "refs/heads/main" } };
        var json = BuildRecordFormat.Serialize(withRef);

        Assert.Contains("\"ref\": \"refs/heads/main\"", json);
        Assert.Equal("refs/heads/main", BuildRecordFormat.Parse(json).BuiltFrom.Ref);

        Assert.DoesNotContain("\"ref\"", BuildRecordFormat.Serialize(Image()));
        Assert.Null(BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Image())).BuiltFrom.Ref);
        Assert.Null(BuildRecordFormat.Parse(WorkflowEmitted).BuiltFrom.Ref);
    }

    [Fact]
    public void ABundleRecordRoundTrips()
    {
        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Bundle()), Store);

        Assert.Equal("bundle", parsed.Identity.Kind);
        Assert.Equal("3HL4kqtJlcpXroDTDmJ.rmSpXd3dIbrHY", parsed.Identity.VersionId);
    }

    [Theory]
    [InlineData("client", "Scutara/ScutaraSellerApp")]
    [InlineData("client", "Scutara/ScutaraAdminApp")]
    [InlineData("site", "Scutara/ScutaraWebsite")]
    [InlineData("assets", "Scutara/ScutaraTenancies")]
    [InlineData("config", "Scutara/Scutara")]
    public void ABundleRecordOfEachBundleClass_RoundTrips(string cls, string repo)
    {
        // P4 A's done-when: a record of each class round-trips before any is written.
        var record = Bundle(cls, repo);
        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(record), Store);

        Assert.Equal(cls, parsed.Class);
        Assert.Equal(record.Identity, parsed.Identity);
        Assert.StartsWith(BuildRecordFormat.PrefixFor(cls, repo), parsed.Identity.Key);
    }

    [Fact]
    public void AToolingRecord_IsAnImage_AndNeedsNoArtifactStore()
    {
        // tooling is class 8, an image named by digest like class 1: nothing in it lives in the artifact store.
        var tooling = Image() with { Class = "tooling" };

        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(tooling));

        Assert.Equal("image", parsed.Identity.Kind);
        Assert.True(BuildRecordFormat.IsImageClass("tooling"));
        Assert.False(BuildRecordFormat.IsImageClass("config"));
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
    public void TheBundleKeyMirrorsTheRecordKey()
    {
        // One stem for both objects of a build, so the zip sits beside its record's name in the other store, and under
        // the same {class}/{repo}/ prefix the writer's role may write there.
        var record = Bundle();

        Assert.Equal("client/scutara/scutarasellerapp/20260912T150405Z-1.zip", BuildRecordFormat.BundleKeyFor(record));
        Assert.Equal(BuildRecordFormat.KeyFor(record)[..^".json".Length] + ".zip", BuildRecordFormat.BundleKeyFor(record));
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
        var assets = Bundle("assets", "Scutara/ScutaraTenancies", new Dictionary<string, string>());

        var parsed = BuildRecordFormat.Parse(BuildRecordFormat.Serialize(assets), Store);
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
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(bad), Store));
        Assert.Contains("versionId", ex.Message);
    }

    [Fact]
    public void ABundleRecordReadWithoutTheArtifactStore_IsRefused()
    {
        // A reader that cannot say which bucket is the artifact store cannot tell a record naming it from one naming
        // any bucket it can read. Fail closed: a bundle record is only ever parsed against the store.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildRecordFormat.Parse(BuildRecordFormat.Serialize(Bundle())));

        // Its own refusal, not the bucket check's comparison against nothing: the reader is told what it forgot.
        Assert.Contains("read without the artifact store", ex.Message);
    }

    [Theory]
    [InlineData("scu-build-records-abcd-1234")]   // the record store, which every reader can read
    [InlineData("scu-artifacts-abcd-12345")]       // a near miss
    [InlineData("SCU-ARTIFACTS-ABCD-1234")]        // bucket names are exact
    [InlineData("someone-elses-bucket")]
    public void ABundleInAnyOtherBucket_IsRefused(string bucket)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildRecordFormat.Parse(BuildRecordFormat.Serialize(WithIdentity(Bundle(), bucket: bucket)), Store));

        Assert.Contains("identity.bucket", ex.Message);
        Assert.Contains(Store, ex.Message);
    }

    [Theory]
    [InlineData("client/scutara/scutaraadminapp/20260912T150405Z-1.zip")]    // another repository's prefix
    [InlineData("site/scutara/scutarasellerapp/20260912T150405Z-1.zip")]     // another class's prefix
    [InlineData("client/scutara/scutarasellerapp/20260912T150406Z-1.zip")]   // another time
    [InlineData("client/scutara/scutarasellerapp/20260912T150405Z-2.zip")]   // another run
    [InlineData("client/scutara/scutarasellerapp/20260912T150405Z-1.json")]  // the record's own name
    [InlineData("client/Scutara/ScutaraSellerApp/20260912T150405Z-1.zip")]   // keys are exact, and lowercase
    [InlineData("x.zip")]                                                     // outside every prefix
    public void ABundleKeyThatIsNotTheMirror_IsRefused(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildRecordFormat.Parse(BuildRecordFormat.Serialize(WithIdentity(Bundle(), key: key)), Store));

        Assert.Contains("identity.key", ex.Message);
        Assert.Contains("client/scutara/scutarasellerapp/20260912T150405Z-1.zip", ex.Message);
    }

    [Fact]
    public void S3sNullVersion_IsRefused()
    {
        // S3 reports the version of an object written to a bucket without versioning as the string "null", and the
        // next write to that key replaces it: the mutable pointer again, dressed as a version id.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildRecordFormat.Parse(BuildRecordFormat.Serialize(WithIdentity(Bundle(), versionId: "null")), Store));

        Assert.Contains("identity.versionId", ex.Message);
        Assert.Contains("null", ex.Message);
    }

    [Theory]
    [InlineData("5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e9")]    // 63
    [InlineData("5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e955")]  // 65
    [InlineData("5E4F504E5BA9A0D90D25F1F111DA78E1CE16CA5B40410332FB242A192A4F9E95")]   // one spelling only
    [InlineData("ge4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95")]   // not hex
    [InlineData("Xk9QTluaoNkNJfHxEdp44c4WyltAQQMy+yQqGSpPnpU=")]                     // S3's base64 form
    [InlineData("abc123")]
    public void AChecksumThatIsNot64LowercaseHex_IsRefused(string sha256)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildRecordFormat.Parse(BuildRecordFormat.Serialize(WithIdentity(Bundle(), sha256: sha256)), Store));

        Assert.Contains("identity.sha256", ex.Message);
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

    // ---------------------------------------------------------------------------------------
    //  The contract with the build workflow, which lives in another repository
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// VERBATIM a record the workflow ACTUALLY WROTE — run 34707373278, 2026-09-12, read back
    /// from s3://scu-build-records-4df6-b9c6/. It is real data rather than my approximation of it.
    /// The workflow is in a different repository, written in Python, and nothing compiles the two
    /// together — so the contract between writer and reader is exactly the kind that drifts
    /// silently and is discovered by a deployer refusing a record months later.
    /// </summary>
    internal const string WorkflowEmitted = """
        {
          "schema": 1,
          "class": "image",
          "builtFrom": {
            "repo": "Scutara/ScutaraService",
            "commit": "57d2d67e5413b1c40e02ccdae6838947a4ebd656",
            "lane": "published",
            "packages": {
              "LazyMagic.OIDC.Bff": "3.0.26-alpha",
              "LazyMagic.Service.Authorization": "3.0.26-alpha",
              "LazyMagic.Service.DynamoDBRepo": "3.0.26-alpha",
              "LazyMagic.Service.Shared": "3.0.26-alpha",
              "LazyMagic.Shared": "3.0.26-alpha"
            }
          },
          "identity": {
            "kind": "image",
            "digest": "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95"
          },
          "builtAt": "2026-09-12T17:11:47Z",
          "builtBy": "tmay57",
          "workflowRunId": "34707373278"
        }
        """;

    [Fact]
    public void TheRecordTheWorkflowEmits_Parses()
    {
        var r = BuildRecordFormat.Parse(WorkflowEmitted);

        Assert.Equal("image", r.Class);
        Assert.Equal("published", r.BuiltFrom.Lane);
        Assert.Equal(5, r.BuiltFrom.Packages.Count);
        Assert.StartsWith("sha256:", r.Identity.Digest);
    }

    [Fact]
    public void TheKeyTheWorkflowComputes_MatchesKeyFor()
    {
        // The workflow builds its key in bash: `tr -d ':-'` over builtAt, then
        // {prefix}{stamp}-{runId}.json. If KeyFor and that shell line ever disagree, records land
        // where the reconciler does not look and where the role may not even write.
        var r = BuildRecordFormat.Parse(WorkflowEmitted);

        Assert.Equal(
            "image/scutara/scutaraservice/20260912T171147Z-34707373278.json",
            BuildRecordFormat.KeyFor(r));
    }

    [Fact]
    public void TheWorkflowsKeyStaysInsideThePrefixItsRoleMayWrite()
    {
        // The role holds s3:PutObject on image/scutara/scutaraservice/* and nothing else, so a key
        // outside it is an AccessDenied at the last step of a build that already pushed an image.
        var r = BuildRecordFormat.Parse(WorkflowEmitted);

        Assert.StartsWith(
            BuildRecordFormat.PrefixFor("image", "Scutara/ScutaraService"),
            BuildRecordFormat.KeyFor(r));
    }
}
