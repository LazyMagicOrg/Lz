using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>The dry intake's steps (DecoupledCd.md §15.4; V-B): check the run, fetch and stage, record.</summary>
public sealed class IntakeStepsTests : IDisposable
{
    private const long RepoId = 1001, OwnerId = 2002, RunId = 9001;
    private static readonly IntakeRepository Service = new("Example/ExampleService", RepoId, OwnerId, ".github/workflows/build-aiphost.yml", "dry", "image", new[] { "aiphost" });
    private static readonly IReadOnlyList<IntakeRepository> Repos = new[] { Service };
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-intake-" + Guid.NewGuid().ToString("N")[..8]);
    private string Exec(int retry = 0) => IntakeNames.For(RepoId, RunId, 1, retry);

    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private static JsonObject Input(string repo = "Example/ExampleService", long repoId = RepoId) => new()
    {
        ["repo"] = repo, ["repositoryId"] = repoId, ["runId"] = RunId, ["runAttempt"] = 1, ["retry"] = 0,
    };

    private static GitHubRun Run(string conclusion = "success", string status = "completed", string branch = "main") => new(
        RunId, 1, RepoId, OwnerId, RepoId, Service.WorkflowPath, branch, "push", status, conclusion, Now.AddMinutes(-9), Now.AddMinutes(-2));

    private static byte[] ZipOf(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    private static byte[] ImageZip(IntakeArtifactsTests.Layout? layout = null) => ZipOf(("aiphost.oci.tar", (layout ?? new()).Tar().ToArray()));
    private static string Digest(byte[] b) => "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private sealed class Fakes : IIntakeGitHub, IIntakeScratch, IIntakeEvidenceStore, IIntakeAlerts
    {
        public GitHubRun? Attempt;
        public IReadOnlyList<GitHubJob> Jobs = new[] { new GitHubJob(1, "GitHub Actions", new[] { "ubuntu-latest" }) };
        public List<GitHubArtifact> Artifacts = new();
        public Dictionary<long, byte[]> Bytes = new();
        public int GitHubCalls;
        public readonly Dictionary<string, long> Scratch = new();
        public readonly Dictionary<string, string> Evidence = new(StringComparer.Ordinal);
        public readonly List<string> Alerts = new();

        public Task<GitHubRun?> RunAttemptAsync(string repo, long runId, int attempt) { GitHubCalls++; return Task.FromResult(Attempt); }
        public Task<IReadOnlyList<GitHubJob>> JobsAsync(string repo, long runId, int attempt) { GitHubCalls++; return Task.FromResult(Jobs); }
        public Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string repo, long runId) => Task.FromResult<IReadOnlyList<GitHubArtifact>>(Artifacts);
        public Task DownloadAsync(string repo, long artifactId, string path) => File.WriteAllBytesAsync(path, Bytes[artifactId]);
        public Task PutFileAsync(string key, string path) { Scratch[key] = new FileInfo(path).Length; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> KeysAsync(string prefix) => Task.FromResult<IReadOnlyList<string>>(Evidence.Keys.ToList());
        public Task<bool> PutOnceAsync(string key, string body) => Task.FromResult(Evidence.TryAdd(key, body));
        public Task PublishAsync(string subject, string message) { Alerts.Add(message); return Task.CompletedTask; }
    }

    private Fakes WithImage(byte[] zip, string? digest = null)
    {
        var f = new Fakes();
        f.Artifacts.Add(new GitHubArtifact(31, "aiphost-1", RunId, false, digest ?? Digest(zip), zip.Length));
        f.Bytes[31] = zip;
        return f;
    }

    private Task<JsonObject> Fetch(Fakes f) => IntakeFetchStep.RunAsync(Input(), Exec(), Repos, f, f, f, _work);

    // ---- check the run ----

    [Fact]
    public async Task ASuccessfulRunOnMain_Passes()
    {
        var f = new Fakes { Attempt = Run() };
        Assert.Equal("passed", (await IntakeCheckRunStep.RunAsync(Input(), Repos, f))["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARunTheCheckRefuses_IsRefusedWithItsReasons()
    {
        var f = new Fakes { Attempt = Run(branch: "feature") };
        var result = await IntakeCheckRunStep.RunAsync(Input(), Repos, f);
        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("feature"));
    }

    [Theory]
    [InlineData("Example/Other", RepoId)]
    [InlineData("Example/ExampleService", 1234)]   // the right name with another repository's id
    public async Task ARepositoryTheIntakeDoesNotTake_IsRefused_BeforeGitHubIsAsked(string repo, long id)
    {
        var f = new Fakes { Attempt = Run() };
        var result = await IntakeCheckRunStep.RunAsync(Input(repo, id), Repos, f);
        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Equal(0, f.GitHubCalls);
    }

    [Fact]
    public async Task AnAttemptNotThere_OrNotCompleted_IsAFault_NotARefusal()
    {
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeCheckRunStep.RunAsync(Input(), Repos, new Fakes()));
        await Assert.ThrowsAsync<IntakeFault>(() => IntakeCheckRunStep.RunAsync(Input(), Repos, new Fakes { Attempt = Run(status: "in_progress", conclusion: "") }));
    }

    [Fact]
    public async Task AnInputThePollerDidNotWrite_IsAFault()
        => await Assert.ThrowsAsync<IntakeFault>(() => IntakeCheckRunStep.RunAsync(new JsonObject { ["repo"] = "x" }, Repos, new Fakes()));

    // ---- fetch and stage ----

    [Fact]
    public async Task AGoodImage_IsStagedUnderTheExecution_WithItsShaAndManifestDigest()
    {
        var layout = new IntakeArtifactsTests.Layout();
        var zip = ImageZip(layout);
        var f = WithImage(zip);

        var result = await Fetch(f);

        Assert.Equal("staged", result["verdict"]!.GetValue<string>());
        var staged = result["staged"]!.AsArray().Single()!.AsObject();
        Assert.Equal($"{Exec()}/aiphost-1.zip", staged["key"]!.GetValue<string>());
        Assert.Equal(Digest(zip), "sha256:" + staged["sha256"]!.GetValue<string>());
        Assert.Equal(Digest(layout.Manifest()), result["manifestDigest"]!.GetValue<string>());
        Assert.Equal(zip.Length, f.Scratch[$"{Exec()}/aiphost-1.zip"]);
        Assert.True(f.Evidence.ContainsKey(IntakeEvidence.Staged(Exec())));
        Assert.Empty(Directory.GetFiles(_work));   // nothing left on the function's disk
    }

    // ---- builtFrom (V-D) ----

    private static readonly IReadOnlyList<IntakeRepository> WithBuiltFrom = new[] { Service with { BuiltFrom = "builtfrom" } };

    private Fakes WithImageAndBuiltFrom(string json, string entry = "builtfrom.json")
    {
        var f = WithImage(ImageZip());
        var zip = ZipOf((entry, System.Text.Encoding.UTF8.GetBytes(json)));
        f.Artifacts.Add(new GitHubArtifact(32, "builtfrom-1", RunId, false, Digest(zip), zip.Length));
        f.Bytes[32] = zip;
        return f;
    }

    [Fact]
    public async Task TheBuildsBuiltFrom_IsStaged_AndItsPackagesRead()
    {
        var f = WithImageAndBuiltFrom("{\"packages\":{\"LazyMagic.Shared\":\"3.0.35\",\"Lz.Core\":\"0.13.70\"}}");

        var result = await IntakeFetchStep.RunAsync(Input(), Exec(), WithBuiltFrom, f, f, f, _work);

        Assert.Equal("staged", result["verdict"]!.GetValue<string>());
        Assert.Equal("3.0.35", result["builtFrom"]!["packages"]!["LazyMagic.Shared"]!.GetValue<string>());
        Assert.Contains($"{Exec()}/builtfrom-1.zip", f.Scratch.Keys);
    }

    [Theory]
    [InlineData("{\"packages\":{}}", "builtfrom.json")]                                   // names no packages
    [InlineData("{\"packages\":{\"A\":\"1\"},\"commit\":\"x\"}", "builtfrom.json")]      // more than the packages
    [InlineData("{\"packages\":{\"A\":1}}", "builtfrom.json")]                            // a version that is not a string
    [InlineData("{\"packages\":{\"A\":\"1 2\"}}", "builtfrom.json")]                      // not a version
    [InlineData("[]", "builtfrom.json")]
    [InlineData("{\"packages\":{\"A\":\"1\"}}", "other.json")]                            // not the one entry
    public async Task ABuiltFromNotInItsForm_IsRefused(string json, string entry)
    {
        var f = WithImageAndBuiltFrom(json, entry);
        var result = await IntakeFetchStep.RunAsync(Input(), Exec(), WithBuiltFrom, f, f, f, _work);
        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().StartsWith("builtfrom-1"));
    }

    [Fact]
    public async Task AMissingBuiltFrom_IsRefused_WhereTheRepositoryNamesOne()
    {
        var result = await IntakeFetchStep.RunAsync(Input(), Exec(), WithBuiltFrom, WithImage(ImageZip()), WithImage(ImageZip()), new Fakes(), _work);
        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnArtifactNamedForAnotherAttempt_IsRefused_AndNothingIsStaged()
    {
        var zip = ImageZip();
        var f = new Fakes();
        f.Artifacts.Add(new GitHubArtifact(31, "aiphost-2", RunId, false, Digest(zip), zip.Length));
        f.Bytes[31] = zip;

        var result = await Fetch(f);

        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Empty(f.Scratch);
    }

    [Fact]
    public async Task BytesThatDoNotMatchGitHubsDigest_AreRefused_AndNotStaged()
    {
        var f = WithImage(ImageZip(), digest: "sha256:" + new string('0', 64));
        var result = await Fetch(f);
        Assert.Contains(result["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("not GitHub's"));
        Assert.Empty(f.Scratch);
    }

    [Fact]
    public async Task AnImageArchiveInTheWrongForm_IsRefused_AndNotStaged()
    {
        var f = WithImage(ImageZip(new IntakeArtifactsTests.Layout { ExtraIndexEntry = true }));
        var result = await Fetch(f);
        Assert.Equal("refused", result["verdict"]!.GetValue<string>());
        Assert.Empty(f.Scratch);
        Assert.Empty(Directory.GetFiles(_work));
    }

    [Fact]
    public async Task AZipWithoutExactlyOneArchive_IsRefused()
    {
        var two = ZipOf(("a.tar", new IntakeArtifactsTests.Layout().Tar().ToArray()), ("b.tar", new IntakeArtifactsTests.Layout().Tar().ToArray()));
        Assert.Equal("refused", (await Fetch(WithImage(two)))["verdict"]!.GetValue<string>());
        Assert.Equal("refused", (await Fetch(WithImage(ZipOf(("readme.txt", new byte[] { 1 })))))["verdict"]!.GetValue<string>());
    }

    // ---- record ----

    [Fact]
    public async Task TakenIn_IsWrittenOnceUnderTheAttempt_AndSaysADryIntakePushedNothing()
    {
        var f = new Fakes();
        var state = Input();
        state["fetch"] = new JsonObject { ["verdict"] = "staged" };

        var first = await IntakeRecordStep.TakenInAsync(state, "dry", f, Now);
        var again = await IntakeRecordStep.TakenInAsync(state, "dry", f, Now);

        Assert.True(first["written"]!.GetValue<bool>());
        Assert.False(again["written"]!.GetValue<bool>());
        Assert.Contains("nothing pushed", f.Evidence[IntakeEvidence.TakenIn(Exec())]);
    }

    [Fact]
    public async Task ARefusal_IsWrittenUnderTheAttempt_WithItsReasons_AndAlertsOnce()
    {
        var f = new Fakes();
        var state = Input();
        state["check"] = new JsonObject { ["verdict"] = "refused", ["reasons"] = new JsonArray("head_branch is feature") };

        await IntakeRecordStep.RefusedAsync(state, f, f, Now);
        await IntakeRecordStep.RefusedAsync(state, f, f, Now);

        Assert.Contains("head_branch is feature", f.Evidence[IntakeEvidence.Refused(Exec())]);
        Assert.Single(f.Alerts);
    }

    [Fact]
    public async Task AFault_IsWrittenUnderTheTry_AndAlertsOnlyOnTheLastRetry()
    {
        var f = new Fakes();
        var state = Input();
        state["error"] = new JsonObject { ["Error"] = "GitHubApiException" };

        await IntakeRecordStep.FaultAsync(state, Exec(), f, f, Now);
        Assert.True(f.Evidence.ContainsKey(IntakeEvidence.Fault(Exec())));
        Assert.Empty(f.Alerts);

        await IntakeRecordStep.FaultAsync(state, Exec(IntakeNames.MaxRetries), f, f, Now);
        Assert.Single(f.Alerts);
    }
}
