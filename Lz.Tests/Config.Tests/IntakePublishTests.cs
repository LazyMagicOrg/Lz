using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Xunit;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE LIVE INTAKE'S PUBLISH (DecoupledCd.md §15.4, V-D): after Gate 1's pass, a run the push path already recorded is taken
/// in dry; otherwise the image is pushed into its pipeline repository, its signature waited for, the tests zip and the SBOM
/// written to the artifact store, and the tests record written before the image record. Every write reads its key first.
/// </summary>
public class IntakePublishTests : IDisposable
{
    private const long RepoId = 1035592016, RunId = 39651406296;
    private const string Repo = "Scutara/ScutaraService", Sha = "806ff5a3d1c2b4e5f60718293a4b5c6d7e8f9012";
    private const string Records = "scu-build-records-4df6-b9c6", Artifacts = "scu-artifacts-4df6-b9c6";
    private const string EcrRepo = "scu-4df6-b9c6-aiphost";
    private const string Profile = "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutara_scutaraservice";
    private static readonly string BaseDigest = "sha256:" + new string('b', 64);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-publish-" + Guid.NewGuid().ToString("N")[..8]);
    public IntakePublishTests() => Directory.CreateDirectory(_work);
    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static IntakeRepository Service(string mode = "live") => new(Repo, RepoId, 320723031, ".github/workflows/build-aiphost.yml", mode, "image",
        new[] { "aiphost" }, new IntakeGate1("tests", "ServiceTest.dll", "Category!=Aws", "10.0.301", new string('a', 128), Array.Empty<IntakeImage>()),
        "builtfrom", "sbom");

    private static readonly IReadOnlyList<PublishTarget> Targets = new[] { new PublishTarget(Repo, "aiphost", EcrRepo, Profile) };

    private sealed class Fakes : IScratchReader, IImageRegistry, IImageSignatures, IPublishStores
    {
        public readonly Dictionary<string, byte[]> Scratch = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> RecordStore = new(StringComparer.Ordinal);
        public readonly List<string> RecordWrites = new();
        public readonly Dictionary<string, (byte[] Bytes, string Version)> ArtifactStore = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Tags = new(StringComparer.Ordinal);
        public string Signing = "COMPLETE";
        public int Pushes;

        public Task GetFileAsync(string key, string path) => File.WriteAllBytesAsync(path, Scratch[key]);

        public Task<string?> DigestAtTagAsync(string repository, string tag) => Task.FromResult(Tags.GetValueOrDefault($"{repository}:{tag}"));
        public Task<IReadOnlySet<string>> MissingBlobsAsync(string repository, IReadOnlyList<string> digests)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(digests));
        public Task UploadBlobAsync(string repository, string digest, long size, Stream content) { content.CopyTo(Stream.Null); return Task.CompletedTask; }
        public Task<string> PutImageAsync(string repository, string tag, string manifest, string mediaType, string imageDigest)
        {
            Pushes++;
            Tags[$"{repository}:{tag}"] = imageDigest;
            return Task.FromResult(imageDigest);
        }

        public Task<string?> StatusAsync(string repository, string digest, string profileArn) => Task.FromResult<string?>(Signing);

        public Task<IReadOnlyList<string>> RecordKeysAsync(string prefix)
            => Task.FromResult<IReadOnlyList<string>>(RecordStore.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
        public Task<string?> RecordAsync(string key) => Task.FromResult(RecordStore.GetValueOrDefault(key));
        public Task<bool> PutRecordOnceAsync(string key, string json)
        {
            if (!RecordStore.TryAdd(key, json)) return Task.FromResult(false);
            RecordWrites.Add(key);
            return Task.FromResult(true);
        }
        public Task<StoredArtifact?> ArtifactAsync(string key)
            => Task.FromResult(ArtifactStore.TryGetValue(key, out var a) ? new StoredArtifact(a.Version, Hex(a.Bytes)) : null);
        public Task<StoredArtifact> PutArtifactOnceAsync(string key, string path, string sha256)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(sha256, Hex(bytes));
            var version = $"v{ArtifactStore.Count + 1}";
            ArtifactStore.Add(key, (bytes, version));   // throws on a second write, as If-None-Match refuses
            return Task.FromResult(new StoredArtifact(version, sha256));
        }
    }

    private static byte[] ZipOf(string name, byte[] data)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    /// <summary>A state as the machine holds it after Gate 1 passed: the check, the staged artifacts, and the verdict.</summary>
    private (Fakes Fakes, JsonObject State, string Manifest) Passed(int attempt = 1, bool withServiceVersion = true)
    {
        var f = new Fakes();
        var layout = new IntakeArtifactsTests.Layout();
        var image = ZipOf("aiphost.oci.tar", layout.Tar().ToArray());
        var tests = ZipOf("ServiceTest.dll", Encoding.UTF8.GetBytes("tests"));
        var sbom = ZipOf("sbom.spdx.json", Encoding.UTF8.GetBytes("{\"spdxVersion\":\"SPDX-2.3\"}"));
        var exec = IntakeNames.For(RepoId, RunId, attempt, 0);
        f.Scratch[$"{exec}/aiphost-{attempt}.zip"] = image;
        f.Scratch[$"{exec}/tests-{attempt}.zip"] = tests;
        f.Scratch[$"{exec}/sbom-{attempt}.zip"] = sbom;
        var manifest = "sha256:" + Hex(layout.Manifest());
        JsonObject Staged(string name, byte[] b) => new() { ["artifact"] = $"{name}-{attempt}", ["key"] = $"{exec}/{name}-{attempt}.zip", ["sha256"] = Hex(b) };
        var builtFrom = new JsonObject { ["packages"] = new JsonObject { ["LazyMagic.Shared"] = "3.0.43-alpha" }, ["baseImageDigest"] = BaseDigest };
        if (withServiceVersion) builtFrom["serviceVersion"] = "1.0.180";

        var state = new JsonObject
        {
            ["repo"] = Repo, ["repositoryId"] = RepoId, ["runId"] = RunId, ["runAttempt"] = attempt, ["retry"] = 0,
            ["check"] = new JsonObject
            {
                ["verdict"] = "passed", ["headBranch"] = "main", ["headSha"] = Sha, ["createdAt"] = "2026-10-21T18:17:39.0000000+00:00",
                ["actor"] = "tmay57", ["jobIds"] = "101,102", ["workflowRef"] = $"{Repo}/.github/workflows/build-aiphost.yml@refs/heads/main",
            },
            ["fetch"] = new JsonObject
            {
                ["verdict"] = "staged", ["manifestDigest"] = manifest,
                ["staged"] = new JsonArray(Staged("aiphost", image), Staged("tests", tests), Staged("sbom", sbom)),
                ["tests"] = Staged("tests", tests), ["sbom"] = Staged("sbom", sbom), ["builtFrom"] = builtFrom,
            },
            ["gate1"] = new JsonObject
            {
                ["verdict"] = "passed", ["manifestDigest"] = manifest,
                ["artifactSha256"] = new JsonObject { [$"aiphost-{attempt}"] = Hex(image), [$"tests-{attempt}"] = Hex(tests), [$"sbom-{attempt}"] = Hex(sbom) },
            },
        };
        return (f, state, manifest);
    }

    private Task<JsonObject> Publish(Fakes f, JsonObject state, IntakeRepository? repo = null)
        => IntakePublishStep.RunAsync(state, IntakeNames.For(RepoId, RunId, state["runAttempt"]!.GetValue<int>(), 0), new[] { repo ?? Service() },
            Targets, Records, Artifacts, f, f, f, f, _work);

    [Fact]
    public async Task ADryRepository_PublishesNothing()
    {
        var (f, state, _) = Passed();
        var result = await Publish(f, state, Service("dry"));
        Assert.Equal("dry", result["outcome"]!.GetValue<string>());
        Assert.Equal(0, f.Pushes);
        Assert.Empty(f.RecordWrites);
    }

    [Fact]
    public async Task ARunThePushPathAlreadyRecorded_IsTakenInDry_AndNothingIsPushed()
    {
        var (f, state, _) = Passed();
        f.RecordStore[$"image/scutara/scutaraservice/20261021T181740Z-{RunId}.json"] = "{}";

        var result = await Publish(f, state);

        Assert.Equal("push-path", result["outcome"]!.GetValue<string>());
        Assert.Equal(0, f.Pushes);
        Assert.Empty(f.RecordWrites);
    }

    [Fact]
    public async Task APass_PushesTheImage_UnderItsAttemptsTag_ThenRecordsTestsBeforeTheImage()
    {
        var (f, state, manifest) = Passed();

        var result = await Publish(f, state);

        Assert.Equal("published", result["outcome"]!.GetValue<string>());
        Assert.Equal(manifest, f.Tags[$"{EcrRepo}:{Sha}-{RunId}-1"]);
        const string stem = "20261021T181739Z-39651406296-1";
        Assert.Equal(new[] { $"tests/scutara/scutaraservice/{stem}.json", $"image/scutara/scutaraservice/{stem}.json" }, f.RecordWrites);
        Assert.Contains($"tests/scutara/scutaraservice/{stem}.zip", f.ArtifactStore.Keys);
        Assert.Contains($"image/scutara/scutaraservice/{stem}.sbom.json", f.ArtifactStore.Keys);

        // Both records are what the parser and every deployer accept, and name what was published.
        var image = BuildRecordFormat.Parse(f.RecordStore[$"image/scutara/scutaraservice/{stem}.json"]);
        Assert.Equal(manifest, image.Identity.Digest);
        Assert.Equal("2026-10-21T18:17:39Z", image.BuiltAt);
        Assert.Equal(Sha, image.BuiltFrom.Commit);
        Assert.Equal("refs/heads/main", image.BuiltFrom.Ref);
        Assert.Equal("3.0.43-alpha", image.BuiltFrom.Packages["LazyMagic.Shared"]);
        Assert.Equal("1.0.180", image.Intake!.ServiceVersion);
        Assert.Equal(BaseDigest, image.Intake.BaseImageDigest);
        Assert.Equal($"image/scutara/scutaraservice/{stem}.sbom.json", image.Intake.Sbom!.Key);
        Assert.Equal("passed", image.Intake.Gate1.Result);
        Assert.Equal($"image/scutara/scutaraservice/{stem}.json", BuildRecordFormat.KeyFor(image));

        var tests = BuildRecordFormat.Parse(f.RecordStore[$"tests/scutara/scutaraservice/{stem}.json"], Artifacts);
        Assert.Equal($"tests/scutara/scutaraservice/{stem}.zip", tests.Identity.Key);
        Assert.Equal(f.ArtifactStore[tests.Identity.Key!].Version, tests.Identity.VersionId);
    }

    [Fact]
    public async Task TheTestsRecord_CarriesTheListsGate1Recorded_ForGate2_AndTheImageRecordDoesNot()
    {
        var (f, state, _) = Passed();
        state["gate1"]!["gate2Lists"] = new JsonObject
        {
            ["precondition"] = new JsonArray("ServiceTest.E2E.E2ELiveSmokeTests.Config_ServesAllThreePools"),
            ["blocking"] = new JsonArray("ServiceTest.E2E.E2ELeadRowsTests.AStaffRebuild"),
            ["advisory"] = new JsonArray(),
        };

        await Publish(f, state);

        const string stem = "20261021T181739Z-39651406296-1";
        var lists = BuildRecordFormat.Parse(f.RecordStore[$"tests/scutara/scutaraservice/{stem}.json"], Artifacts).Intake!.Gate1.Lists!;
        Assert.Equal(new[] { "ServiceTest.E2E.E2ELiveSmokeTests.Config_ServesAllThreePools" }, lists.Precondition);
        Assert.Equal(new[] { "ServiceTest.E2E.E2ELeadRowsTests.AStaffRebuild" }, lists.Blocking);
        Assert.Empty(lists.Advisory);
        Assert.Null(BuildRecordFormat.Parse(f.RecordStore[$"image/scutara/scutaraservice/{stem}.json"]).Intake!.Gate1.Lists);
    }

    /// <summary>The browser tests (DecoupledCd.md V-E): a tests-class repository, its Gate 1 the collection check.</summary>
    private static IntakeRepository BrowserTests() => new("Scutara/ScutaraE2ETests", 1343104872, 320723031, ".github/workflows/publish-tests.yml", "live",
        "tests", Array.Empty<string>(), new IntakeGate1("tests", "", "", "", "", Array.Empty<IntakeImage>(), Kind: "collection"), null, null);

    [Fact]
    public async Task ATestsRepository_PublishesItsTestsZipAndRecordAlone_WithTheGateCount_AndNoPackages()
    {
        var f = new Fakes();
        var tests = ZipOf("package.json", Encoding.UTF8.GetBytes("{\"name\":\"scutara-browser-e2e\"}"));
        var exec = IntakeNames.For(1343104872, 77, 1, 0);
        f.Scratch[$"{exec}/tests-1.zip"] = tests;
        var staged = new JsonObject { ["artifact"] = "tests-1", ["key"] = $"{exec}/tests-1.zip", ["sha256"] = Hex(tests) };
        var state = new JsonObject
        {
            ["repo"] = "Scutara/ScutaraE2ETests", ["repositoryId"] = 1343104872L, ["runId"] = 77L, ["runAttempt"] = 1, ["retry"] = 0,
            ["check"] = new JsonObject
            {
                ["verdict"] = "passed", ["headBranch"] = "main", ["headSha"] = Sha, ["createdAt"] = "2026-10-24T14:00:00.0000000+00:00",
                ["actor"] = "tmay57", ["jobIds"] = "201", ["workflowRef"] = "Scutara/ScutaraE2ETests/.github/workflows/publish-tests.yml@refs/heads/main",
            },
            ["fetch"] = new JsonObject { ["verdict"] = "staged", ["staged"] = new JsonArray(staged.DeepClone()), ["tests"] = staged },
            ["gate1"] = new JsonObject { ["verdict"] = "passed", ["gateCount"] = 12, ["artifactSha256"] = new JsonObject { ["tests-1"] = Hex(tests) } },
        };

        var result = await IntakePublishStep.RunAsync(state, exec, new[] { BrowserTests() }, Targets, Records, Artifacts, f, f, f, f, _work);

        Assert.Equal("published", result["outcome"]!.GetValue<string>());
        const string stem = "20261024T140000Z-77-1";
        Assert.Equal(new[] { $"tests/scutara/scutarae2etests/{stem}.json" }, f.RecordWrites);
        Assert.Equal(new[] { $"tests/scutara/scutarae2etests/{stem}.zip" }, f.ArtifactStore.Keys);
        var record = BuildRecordFormat.Parse(f.RecordStore[$"tests/scutara/scutarae2etests/{stem}.json"], Artifacts);
        Assert.Equal("tests", record.Class);
        Assert.Equal(12, record.Intake!.Gate1.Lists!.GateCount);
        Assert.Empty(record.BuiltFrom.Packages);   // a test bundle consumes no first-party package, and says so
        Assert.Equal(0, f.Pushes);
    }

    [Fact]
    public async Task ACollectionChecksGateCount_IsCarriedOnTheTestsRecord()
    {
        var (f, state, _) = Passed();
        state["gate1"]!["gateCount"] = 12;

        await Publish(f, state);

        var lists = BuildRecordFormat.Parse(f.RecordStore["tests/scutara/scutaraservice/20261021T181739Z-39651406296-1.json"], Artifacts).Intake!.Gate1.Lists!;
        Assert.Equal(12, lists.GateCount);
    }

    [Fact]
    public async Task AnUnsignedImage_IsWaitedFor_AndNothingIsRecordedMeanwhile()
    {
        var (f, state, _) = Passed();
        f.Signing = "IN_PROGRESS";

        await Assert.ThrowsAsync<ImageNotYetSigned>(() => Publish(f, state));
        Assert.Empty(f.RecordWrites);

        // The machine's retry continues: the tag already names the digest, so nothing is pushed twice.
        f.Signing = "COMPLETE";
        var result = await Publish(f, state);
        Assert.Equal("published", result["outcome"]!.GetValue<string>());
        Assert.Equal(1, f.Pushes);
    }

    [Fact]
    public async Task AFailedSignature_IsAFault()
    {
        var (f, state, _) = Passed();
        f.Signing = "FAILED";
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.Empty(f.RecordWrites);
    }

    [Fact]
    public async Task ARetryAfterEverythingWasWritten_WritesNothingAgain()
    {
        var (f, state, _) = Passed();
        await Publish(f, state);
        var writes = f.RecordWrites.Count;

        var again = await Publish(f, state);

        Assert.Equal("published", again["outcome"]!.GetValue<string>());
        Assert.Equal(writes, f.RecordWrites.Count);
        Assert.Equal(1, f.Pushes);
    }

    [Fact]
    public async Task StagedBytesThatAreNotTheOnesGate1Passed_AreRefused_BeforeAnythingIsPushed()
    {
        var (f, state, _) = Passed();
        state["gate1"]!["artifactSha256"]!["aiphost-1"] = new string('0', 64);
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.Equal(0, f.Pushes);
    }

    [Fact]
    public async Task NoPassFromGate1_PublishesNothing()
    {
        var (f, state, _) = Passed();
        state["gate1"]!["verdict"] = "failed";
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.Equal(0, f.Pushes);
    }

    [Fact]
    public async Task AnImageWithoutItsServiceVersion_IsNotRecorded()
    {
        var (f, state, _) = Passed(withServiceVersion: false);
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.Equal(0, f.Pushes);
    }

    // ---- a client (V-D, SellerApp) ----

    private const string ClientRepo = "Scutara/ScutaraSellerApp";
    private const long ClientRepoId = 1341231897, ClientRunId = 39700000001;

    private static IntakeRepository Seller(string mode = "live") => new(ClientRepo, ClientRepoId, 320723031, ".github/workflows/build-bundle.yml", mode, "client",
        new[] { "sellerapp" }, new IntakeGate1("tests", "ViewModelsTest.dll", "FullyQualifiedName~ViewModelsTest", "10.0.301", new string('a', 128), Array.Empty<IntakeImage>()),
        "builtfrom");

    /// <summary>A client's state after Gate 1 passed: its artifact wraps the one bundle.zip the build made.</summary>
    private (Fakes Fakes, JsonObject State, byte[] Bundle, byte[] Artifact) ClientPassed(string entry = "bundle.zip")
    {
        var f = new Fakes();
        var bundle = ZipOf("seller/index.html", Encoding.UTF8.GetBytes("<html>seller</html>"));
        var artifact = ZipOf(entry, bundle);
        var tests = ZipOf("ViewModelsTest.dll", Encoding.UTF8.GetBytes("tests"));
        var exec = IntakeNames.For(ClientRepoId, ClientRunId, 1, 0);
        f.Scratch[$"{exec}/sellerapp-1.zip"] = artifact;
        f.Scratch[$"{exec}/tests-1.zip"] = tests;
        JsonObject Staged(string name, byte[] b) => new() { ["artifact"] = $"{name}-1", ["key"] = $"{exec}/{name}-1.zip", ["sha256"] = Hex(b) };
        var state = new JsonObject
        {
            ["repo"] = ClientRepo, ["repositoryId"] = ClientRepoId, ["runId"] = ClientRunId, ["runAttempt"] = 1, ["retry"] = 0,
            ["check"] = new JsonObject
            {
                ["verdict"] = "passed", ["headBranch"] = "main", ["headSha"] = Sha, ["createdAt"] = "2026-10-22T09:00:00.0000000+00:00",
                ["actor"] = "tmay57", ["jobIds"] = "201", ["workflowRef"] = $"{ClientRepo}/.github/workflows/build-bundle.yml@refs/heads/main",
            },
            ["fetch"] = new JsonObject
            {
                ["verdict"] = "staged",
                ["staged"] = new JsonArray(Staged("sellerapp", artifact), Staged("tests", tests)),
                ["tests"] = Staged("tests", tests),
                ["builtFrom"] = new JsonObject { ["packages"] = new JsonObject { ["BaseApp.ViewModels"] = "1.0.49" } },
            },
            ["gate1"] = new JsonObject
            {
                ["verdict"] = "passed",
                ["artifactSha256"] = new JsonObject { ["sellerapp-1"] = Hex(artifact), ["tests-1"] = Hex(tests) },
            },
        };
        return (f, state, bundle, artifact);
    }

    private Task<JsonObject> PublishClient(Fakes f, JsonObject state, IntakeRepository? repo = null)
        => IntakePublishStep.RunAsync(state, IntakeNames.For(ClientRepoId, ClientRunId, 1, 0), new[] { repo ?? Seller() },
            Targets, Records, Artifacts, f, f, f, f, _work);

    [Fact]
    public async Task AClientsPass_WritesItsTestsThenItsBundleAndRecord_TheBundleBeingTheBuildsOwnZip()
    {
        var (f, state, bundle, artifact) = ClientPassed();

        var result = await PublishClient(f, state);

        Assert.Equal("published", result["outcome"]!.GetValue<string>());
        Assert.Equal(0, f.Pushes);   // no registry for a client
        const string stem = "20261022T090000Z-39700000001-1";
        Assert.Equal(new[] { $"tests/scutara/scutarasellerapp/{stem}.json", $"client/scutara/scutarasellerapp/{stem}.json" }, f.RecordWrites);
        Assert.Equal(bundle, f.ArtifactStore[$"client/scutara/scutarasellerapp/{stem}.zip"].Bytes);   // the inner zip, unchanged

        var client = BuildRecordFormat.Parse(f.RecordStore[$"client/scutara/scutarasellerapp/{stem}.json"], Artifacts);
        Assert.Equal(("bundle", Artifacts, $"client/scutara/scutarasellerapp/{stem}.zip"), (client.Identity.Kind, client.Identity.Bucket, client.Identity.Key));
        Assert.Equal(Hex(bundle), client.Identity.Sha256);
        Assert.Equal(f.ArtifactStore[client.Identity.Key!].Version, client.Identity.VersionId);
        Assert.Equal("sha256:" + Hex(artifact), client.Intake!.GithubDigest);   // what GitHub reported, and Gate 1 passed
        Assert.Equal("1.0.49", client.BuiltFrom.Packages["BaseApp.ViewModels"]);
        Assert.Null(client.Intake.ServiceVersion);
    }

    [Fact]
    public async Task AClientRunThePushPathRecorded_IsTakenInDry()
    {
        var (f, state, _, _) = ClientPassed();
        f.RecordStore[$"client/scutara/scutarasellerapp/20261022T090001Z-{ClientRunId}.json"] = "{}";
        Assert.Equal("push-path", (await PublishClient(f, state))["outcome"]!.GetValue<string>());
        Assert.Empty(f.RecordWrites);
    }

    [Theory]
    [InlineData("bundle.tar")]
    [InlineData("wwwroot/bundle.zip")]
    public async Task AClientArtifactNotHoldingOneBundleZip_PublishesNothing(string entry)
    {
        var (f, state, _, _) = ClientPassed(entry);
        await Assert.ThrowsAsync<IntakeFault>(() => PublishClient(f, state));
        Assert.Empty(f.RecordWrites);
        Assert.Empty(f.ArtifactStore);
    }

    [Fact]
    public async Task AClientsRetry_WritesNothingAgain()
    {
        var (f, state, _, _) = ClientPassed();
        await PublishClient(f, state);
        var (records, artifacts) = (f.RecordWrites.Count, f.ArtifactStore.Count);
        Assert.Equal("published", (await PublishClient(f, state))["outcome"]!.GetValue<string>());
        Assert.Equal((records, artifacts), (f.RecordWrites.Count, f.ArtifactStore.Count));
    }

    [Fact]
    public async Task ARecordForAnotherAttemptAtTheKey_IsRefused_NeverOverwritten()
    {
        var (f, state, _) = Passed();
        f.RecordStore["tests/scutara/scutaraservice/20261021T181739Z-39651406296-1.json"] = "{\"schema\":1}";
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.DoesNotContain(f.RecordWrites, k => k.StartsWith("image/"));
    }
}
