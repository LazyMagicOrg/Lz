using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE SITE THROUGH THE INTAKE (DecoupledCd.md V-24, §15.7 V-K step 1): the fetch stages each environment's bundle, each
/// artifact wrapping the one <c>bundle.zip</c> the build made; Gate 1 is lz's site check, run by a function over the staged
/// bytes, whose verdict binds them as Gate 1's does; and a live site's publish writes one zip and one record per
/// environment, each record naming its environment, with no tests record, since a site has no test bundle.
/// </summary>
public sealed class IntakeSiteTests : IDisposable
{
    private const long RepoId = 1342000001, OwnerId = 320723031, RunId = 39900000001;
    private const string Repo = "Scutara/ScutaraWebsite", Sha = "0455585a1b2c3d4e5f60718293a4b5c6d7e8f901";
    private const string Records = "scu-build-records-4df6-b9c6", Artifacts = "scu-artifacts-4df6-b9c6";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-site-" + Guid.NewGuid().ToString("N")[..8]);
    public IntakeSiteTests() => Directory.CreateDirectory(_work);
    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static string Exec(int retry = 0) => IntakeNames.For(RepoId, RunId, 1, retry);

    private static IntakeRepository Site(string mode = "live", params string[] environments)
    {
        var envs = environments.Length == 0 ? new[] { "dev", "test" } : environments;
        return new IntakeRepository(Repo, RepoId, OwnerId, ".github/workflows/publish.yml", mode, "site",
            envs.Select(e => $"site-{e}").ToArray(),
            Site: new IntakeSiteCheck(envs.ToDictionary(e => $"site-{e}", e => e), new[] { "prod" }, new[] { "link.msgsndr.com" }));
    }

    private static byte[] ZipOf(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    private static string Page(string host, string path) =>
        $"<html><head><meta name=robots content=noindex><link rel=canonical href=https://{host}{path}></head><body><a href=/>Home</a></body></html>";

    /// <summary>One environment's site, as the build zips it, inside the artifact GitHub zips again.</summary>
    private static (byte[] Bundle, byte[] Artifact) SiteArtifact(string host, string? extraPage = null)
    {
        var entries = new List<(string, byte[])>
        {
            ("index.html", Encoding.UTF8.GetBytes(Page(host, "/"))),
            ("404.html", Encoding.UTF8.GetBytes(Page(host, "/404.html"))),
            ("park.html", Encoding.UTF8.GetBytes("<html><head><meta name=robots content=noindex></head><body>Back soon</body></html>")),
        };
        if (extraPage is not null) entries.Add(("extra/index.html", Encoding.UTF8.GetBytes(extraPage)));
        var bundle = ZipOf(entries.ToArray());
        return (bundle, ZipOf(("bundle.zip", bundle)));
    }

    private sealed class Fakes : IIntakeGitHub, IIntakeScratch, IIntakeEvidenceStore, IScratchReader, IPublishStores
    {
        public List<GitHubArtifact> Listed = new();
        public Dictionary<long, byte[]> Downloads = new();
        public readonly Dictionary<string, byte[]> Scratch = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Evidence = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> RecordStore = new(StringComparer.Ordinal);
        public readonly List<string> RecordWrites = new();
        public readonly Dictionary<string, (byte[] Bytes, string Version)> ArtifactStore = new(StringComparer.Ordinal);

        public Task<GitHubRun?> RunAttemptAsync(string repo, long runId, int attempt) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubJob>> JobsAsync(string repo, long runId, int attempt) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string repo, long runId) => Task.FromResult<IReadOnlyList<GitHubArtifact>>(Listed);
        public Task DownloadAsync(string repo, long artifactId, string path) => File.WriteAllBytesAsync(path, Downloads[artifactId]);
        public async Task PutFileAsync(string key, string path) => Scratch[key] = await File.ReadAllBytesAsync(path);
        public Task GetFileAsync(string key, string path) => File.WriteAllBytesAsync(path, Scratch[key]);
        public Task<IReadOnlyList<string>> KeysAsync(string prefix) => Task.FromResult<IReadOnlyList<string>>(Evidence.Keys.ToList());
        public Task<bool> PutOnceAsync(string key, string body) => Task.FromResult(Evidence.TryAdd(key, body));

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
            ArtifactStore.Add(key, (bytes, version));
            return Task.FromResult(new StoredArtifact(version, sha256));
        }
    }

    private static JsonObject Input() => new() { ["repo"] = Repo, ["repositoryId"] = RepoId, ["runId"] = RunId, ["runAttempt"] = 1, ["retry"] = 0 };

    private static Fakes Listing(params (string Name, byte[] Artifact)[] artifacts)
    {
        var f = new Fakes();
        long id = 70;
        foreach (var (name, artifact) in artifacts)
        {
            f.Listed.Add(new GitHubArtifact(++id, name, RunId, false, "sha256:" + Hex(artifact), artifact.Length));
            f.Downloads[id] = artifact;
        }
        return f;
    }

    // ---- fetch ----

    [Fact]
    public async Task EachEnvironmentsBundle_IsStaged_WithItsEnvironment()
    {
        var (devBundle, dev) = SiteArtifact("aiproxydev.click");
        var (_, test) = SiteArtifact("scutara-test.click");
        var f = Listing(("site-dev-1", dev), ("site-test-1", test));

        var result = await IntakeFetchStep.RunAsync(Input(), Exec(), new[] { Site() }, f, f, f, _work);

        Assert.Equal("staged", result["verdict"]!.GetValue<string>());
        var sites = result["sites"]!.AsArray().Select(s => s!.AsObject()).ToList();
        Assert.Equal(new[] { "dev", "test" }, sites.Select(s => s["environment"]!.GetValue<string>()));
        Assert.Equal($"{Exec()}/site-dev-1.zip", sites[0]["key"]!.GetValue<string>());
        Assert.Equal(Hex(dev), sites[0]["sha256"]!.GetValue<string>());
        Assert.Equal(Hex(devBundle), sites[0]["bundleSha256"]!.GetValue<string>());
        Assert.Null(result["tests"]);
    }

    [Fact]
    public async Task ASiteArtifactNotHoldingOneBundleZip_IsRefused()
    {
        var (bundle, _) = SiteArtifact("aiproxydev.click");
        var f = Listing(("site-dev-1", ZipOf(("site.zip", bundle))), ("site-test-1", SiteArtifact("scutara-test.click").Artifact));
        Assert.Equal("refused", (await IntakeFetchStep.RunAsync(Input(), Exec(), new[] { Site() }, f, f, f, _work))["verdict"]!.GetValue<string>());
    }

    // ---- Gate 1: the site check ----

    private async Task<(Fakes F, JsonObject State)> Staged(string? testExtra = null, string? devExtra = null, string mode = "live")
    {
        var f = Listing(("site-dev-1", SiteArtifact("aiproxydev.click", devExtra).Artifact), ("site-test-1", SiteArtifact("scutara-test.click", testExtra).Artifact));
        var state = Input();
        state["check"] = new JsonObject
        {
            ["verdict"] = "passed", ["headBranch"] = "main", ["headSha"] = Sha, ["createdAt"] = "2026-10-23T09:00:00.0000000+00:00",
            ["actor"] = "tmay57", ["jobIds"] = "301", ["workflowRef"] = $"{Repo}/.github/workflows/publish.yml@refs/heads/main",
        };
        state["fetch"] = await IntakeFetchStep.RunAsync(Input(), Exec(), new[] { Site(mode) }, f, f, f, _work);
        return (f, state);
    }

    private Task<JsonObject> SiteCheck(Fakes f, JsonObject state, IntakeRepository? repo = null)
        => IntakeSiteCheckStep.RunAsync(state, Exec(), new[] { repo ?? Site() }, f, f, _work, new DateTimeOffset(2026, 10, 23, 9, 5, 0, TimeSpan.Zero));

    [Fact]
    public async Task SoundBundles_Pass_AndTheVerdictBindsEveryStagedSha()
    {
        var (f, state) = await Staged();
        var verdict = await SiteCheck(f, state);

        Assert.Equal("passed", verdict["verdict"]!.GetValue<string>());
        Assert.Equal("site", verdict["kind"]!.GetValue<string>());
        var bound = verdict["artifactSha256"]!.AsObject();
        Assert.Equal(new[] { "site-dev-1", "site-test-1" }, bound.Select(kv => kv.Key).Order());
        Assert.Equal(state["fetch"]!["sites"]![0]!["sha256"]!.GetValue<string>(), bound["site-dev-1"]!.GetValue<string>());
        Assert.Equal("aiproxydev.click", verdict["hosts"]!["dev"]!.GetValue<string>());
        Assert.True(f.Evidence.ContainsKey(IntakeEvidence.Gate1(Exec())));
    }

    [Fact]
    public async Task ABrokenSite_FailsGate1_WithItsReasons()
    {
        var (f, state) = await Staged(devExtra: Page("aiproxydev.click", "/extra/").Replace("<a href=/>", "<a href=/gone/>"),
                                      testExtra: Page("scutara-test.click", "/extra/").Replace("<a href=/>", "<a href=/gone/>"));
        var verdict = await SiteCheck(f, state);

        Assert.Equal("failed", verdict["verdict"]!.GetValue<string>());
        var reasons = verdict["reasons"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Contains(reasons, r => r.Contains("dev: extra/index.html links to /gone/"));
    }

    [Fact]
    public async Task BundlesThatDifferBeyondTheirHost_FailGate1()
    {
        var (f, state) = await Staged(testExtra: Page("scutara-test.click", "/extra/"));
        var verdict = await SiteCheck(f, state);
        Assert.Equal("failed", verdict["verdict"]!.GetValue<string>());
        Assert.Contains(verdict["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("test: extra/index.html is not in dev's bundle"));
    }

    [Fact]
    public async Task AStagedFileThatNoLongerHashesAsFetched_IsAFault()
    {
        var (f, state) = await Staged();
        f.Scratch[$"{Exec()}/site-test-1.zip"] = new byte[] { 1, 2, 3 };
        await Assert.ThrowsAsync<IntakeFault>(() => SiteCheck(f, state));
    }

    [Fact]
    public async Task ARepositoryWithoutASiteCheck_IsAFault()
    {
        var (f, state) = await Staged();
        await Assert.ThrowsAsync<IntakeFault>(() => SiteCheck(f, state, Site() with { Site = null }));
    }

    // ---- publish ----

    private async Task<(Fakes F, JsonObject State)> Passed(string mode = "live")
    {
        var (f, state) = await Staged(mode: mode);
        state["gate1"] = await SiteCheck(f, state);
        Assert.Equal("passed", state["gate1"]!["verdict"]!.GetValue<string>());
        return (f, state);
    }

    private Task<JsonObject> Publish(Fakes f, JsonObject state, IntakeRepository? repo = null)
        => IntakePublishStep.RunAsync(state, Exec(), new[] { repo ?? Site() }, Array.Empty<PublishTarget>(), Records, Artifacts,
            f, new NoRegistry(), new NoRegistry(), f, _work);

    private sealed class NoRegistry : IImageRegistry, IImageSignatures
    {
        public Task<string?> DigestAtTagAsync(string repository, string tag) => throw new InvalidOperationException("a site pushes no image");
        public Task<IReadOnlySet<string>> MissingBlobsAsync(string repository, IReadOnlyList<string> digests) => throw new InvalidOperationException("a site pushes no image");
        public Task UploadBlobAsync(string repository, string digest, long size, Stream content) => throw new InvalidOperationException("a site pushes no image");
        public Task<string> PutImageAsync(string repository, string tag, string manifest, string mediaType, string imageDigest) => throw new InvalidOperationException("a site pushes no image");
        public Task<string?> StatusAsync(string repository, string digest, string profileArn) => throw new InvalidOperationException("a site pushes no image");
    }

    [Fact]
    public async Task ALiveSitesPass_WritesAZipAndARecordPerEnvironment_AndNoTestsRecord()
    {
        var (f, state) = await Passed();
        var result = await Publish(f, state);

        Assert.Equal("published", result["outcome"]!.GetValue<string>());
        const string stem = "20261023T090000Z-39900000001-1";
        Assert.Equal(new[] { $"site/scutara/scutarawebsite/dev/{stem}.json", $"site/scutara/scutarawebsite/test/{stem}.json" }, f.RecordWrites);

        foreach (var env in new[] { "dev", "test" })
        {
            var record = BuildRecordFormat.Parse(f.RecordStore[$"site/scutara/scutarawebsite/{env}/{stem}.json"], Artifacts);
            Assert.Equal(("site", env, "refs/heads/main"), (record.Class, record.Intake!.Environment, record.BuiltFrom.Ref));
            Assert.Empty(record.BuiltFrom.Packages);
            Assert.Equal(f.ArtifactStore[record.Identity.Key!].Version, record.Identity.VersionId);
            var staged = state["fetch"]!["sites"]!.AsArray().Single(s => s!["environment"]!.GetValue<string>() == env)!;
            Assert.Equal(staged["bundleSha256"]!.GetValue<string>(), record.Identity.Sha256);   // the build's own zip, unchanged
            Assert.Equal("sha256:" + staged["sha256"]!.GetValue<string>(), record.Intake.GithubDigest);
        }
    }

    [Fact]
    public async Task ADrySite_PublishesNothing()
    {
        var (f, state) = await Passed(mode: "dry");
        Assert.Equal("dry", (await Publish(f, state, Site("dry")))["outcome"]!.GetValue<string>());
        Assert.Empty(f.RecordWrites);
    }

    [Fact]
    public async Task ABundleGate1DidNotPass_IsNeverPublished()
    {
        var (f, state) = await Passed();
        state["gate1"]!["artifactSha256"]!["site-test-1"] = new string('0', 64);
        await Assert.ThrowsAsync<IntakeFault>(() => Publish(f, state));
        Assert.Empty(f.RecordWrites);
        Assert.Empty(f.ArtifactStore);   // checked before anything is written
    }

    [Fact]
    public async Task ARetry_FindsItsOwnWrites_AndWritesNothingTwice()
    {
        var (f, state) = await Passed();
        await Publish(f, state);
        var writes = f.RecordWrites.Count;
        Assert.Equal("published", (await Publish(f, state))["outcome"]!.GetValue<string>());
        Assert.Equal(writes, f.RecordWrites.Count);
    }

    // ---- the plan ----

    private const string Account = "147440642635";

    private static SystemConfig Config(bool withService = false)
    {
        var repos = new List<PipelineRepositoryConfig>
        {
            new()
            {
                Repo = Repo, Class = "site", Artifacts = new() { "site-dev" }, RepositoryId = RepoId, OwnerId = OwnerId,
                WorkflowPath = ".github/workflows/publish.yml", Intake = "dry",
                Gate1 = new PipelineGate1Config { Kind = "site", Indexed = new() { "prod" }, ThirdPartyHosts = new() { "link.msgsndr.com" } },
            },
        };
        if (withService)
            repos.Add(new()
            {
                Repo = "Example/ExampleService", Class = "image", Artifacts = new() { "aiphost" }, RepositoryId = 1001, OwnerId = 2002,
                WorkflowPath = ".github/workflows/build-aiphost.yml", Intake = "dry", TestBundle = "tests",
                Gate1 = new PipelineGate1Config { Run = "a.dll", Filter = "f", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128) },
            });
        return new SystemConfig
        {
            SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
            Pipeline = new PipelineConfig { Enabled = true, Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" }, Repositories = repos },
        };
    }

    [Fact]
    public void TheSitesCheck_IsCarriedToTheIntake_WithEachArtifactsEnvironment()
    {
        var repo = Assert.Single(IntakeBuildPlanner.RepositoriesFor(Config()));
        Assert.Null(repo.Gate1);
        Assert.Equal("dev", repo.Site!.Environments["site-dev"]);
        Assert.Equal(new[] { "prod" }, repo.Site.Indexed);
        Assert.Equal(new[] { "link.msgsndr.com" }, repo.Site.ThirdPartyHosts);
    }

    [Fact]
    public void ASiteOnlyIntake_HasTheSiteCheckAndThePublish_AndNoCodeBuild()
    {
        var plan = IntakeBuildPlanner.Plan(Config(), Account)!;
        Assert.Null(plan.Gate1);
        Assert.Contains(plan.Functions, f => f.Handler == DeployerHandlers.IntakeSiteCheck);
        Assert.Contains(plan.Functions, f => f.Handler == DeployerHandlers.IntakePublish);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.IntakeGate1Start);
    }

    [Fact]
    public void TheSiteCheckFunction_ReadsTheScratchBucket_AndWritesOnlyTheVerdict()
    {
        var plan = IntakeBuildPlanner.Plan(Config(), Account)!;
        var fn = plan.Functions.Single(f => f.Handler == DeployerHandlers.IntakeSiteCheck);
        var grants = Grants(fn.Policy).Where(g => g.Action.StartsWith("s3:", StringComparison.Ordinal)).ToList();
        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{plan.ScratchBucket}/*"), grants);
        Assert.Equal(new[] { $"arn:aws:s3:::{plan.EvidenceStore}/gate1/*" }, grants.Where(g => g.Action == "s3:PutObject").Select(g => g.Resource));
        Assert.Equal(IntakeEnvironment.Repositories, fn.Environment.Keys.Single(k => k == IntakeEnvironment.Repositories));
    }

    [Fact]
    public void ASitesPublish_WritesOnlyItsOwnSitePrefix_NotATestsOne()
    {
        var plan = IntakeBuildPlanner.Plan(Config(), Account)!;
        var writes = Grants(plan.Functions.Single(f => f.Handler == DeployerHandlers.IntakePublish).Policy)
            .Where(g => g.Action == "s3:PutObject").Select(g => g.Resource).ToList();
        Assert.Contains($"arn:aws:s3:::scu-build-records-4df6-b9c6/site/scutara/scutarawebsite/*", writes);
        Assert.DoesNotContain(writes, w => w.Contains("/tests/scutara/scutarawebsite/"));
    }

    [Fact]
    public void TheMachine_ChecksASiteWhereTheFetchStagedOne_AndRunsGate1WhereItStagedTests()
    {
        var definition = JsonDocument.Parse(IntakeBuildPlanner.Plan(Config(withService: true), Account)!.Definition).RootElement.GetProperty("States");
        var choices = definition.GetProperty("HasGate1").GetProperty("Choices").EnumerateArray()
            .Select(c => (c.GetProperty("Variable").GetString(), c.GetProperty("Next").GetString())).ToList();
        Assert.Contains(("$.fetch.tests", "Gate1Start"), choices);
        Assert.Contains(("$.fetch.sites", "SiteCheck"), choices);

        var siteCheck = definition.GetProperty("SiteCheck");
        Assert.Equal("$.gate1", siteCheck.GetProperty("ResultPath").GetString());
        Assert.Equal("Gate1Outcome", siteCheck.GetProperty("Next").GetString());
    }

    [Fact]
    public void ASiteOnlyMachine_HasNoCodeBuildStates()
    {
        var states = JsonDocument.Parse(IntakeBuildPlanner.Plan(Config(), Account)!.Definition).RootElement.GetProperty("States");
        Assert.False(states.TryGetProperty("Gate1Start", out _));
        Assert.True(states.TryGetProperty("SiteCheck", out _));
        Assert.True(states.TryGetProperty("Publish", out _));
    }

    private static IEnumerable<(string Action, string Resource)> Grants(string policy)
    {
        static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };
        return JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray()
            .Where(s => s.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(s => Strings(s.GetProperty("Action")).SelectMany(a => Strings(s.GetProperty("Resource")).Select(r => (a, r))));
    }
}
