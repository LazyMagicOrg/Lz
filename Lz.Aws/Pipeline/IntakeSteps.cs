using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>What the intake reads from GitHub, through its App's installation token.</summary>
public interface IIntakeGitHub
{
    /// <summary>One attempt of a run, or null when GitHub has no such attempt.</summary>
    Task<GitHubRun?> RunAttemptAsync(string repo, long runId, int attempt);
    Task<IReadOnlyList<GitHubJob>> JobsAsync(string repo, long runId, int attempt);
    Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string repo, long runId);

    /// <summary>Downloads an artifact's zip to <paramref name="path"/>.</summary>
    Task DownloadAsync(string repo, long artifactId, string path);
}

/// <summary>The intake's scratch bucket: written once per key by the fetch function only (§3).</summary>
public interface IIntakeScratch
{
    Task PutFileAsync(string key, string path);
}

/// <summary>
/// The intake met a fault, not a refusal (DecoupledCd.md §15.4): GitHub or AWS failed, or the run is in a state the intake
/// should never see. Not caught by the steps; the machine records it under faults/ and the poller retries.
/// </summary>
public sealed class IntakeFault(string message) : Exception(message);

/// <summary>Reads the execution's input: what the poller (or a nudge) started it with. Ids only; none is trusted.</summary>
public sealed record IntakeInput(string Repo, long RepositoryId, long RunId, int RunAttempt, int Retry)
{
    public string AttemptName => IntakeNames.For(RepositoryId, RunId, RunAttempt);

    public static IntakeInput From(JsonObject state)
    {
        try
        {
            return new IntakeInput(
                state["repo"]!.GetValue<string>(), state["repositoryId"]!.GetValue<long>(),
                state["runId"]!.GetValue<long>(), state["runAttempt"]!.GetValue<int>(), state["retry"]!.GetValue<int>());
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or FormatException)
        {
            throw new IntakeFault($"the intake's input is not what the poller writes: {ex.Message}");
        }
    }

    /// <summary>The configured repository this input names, matched by name AND id; null when none is.</summary>
    public IntakeRepository? In(IReadOnlyList<IntakeRepository> repositories)
        => repositories.FirstOrDefault(r => r.Repo == Repo && r.RepositoryId == RepositoryId);
}

/// <summary>
/// CHECK THE RUN (DecoupledCd.md §15.4): the attempt read back from GitHub, its jobs, and V-A's <see cref="RunCheck"/>. A
/// repository the intake does not take is refused before GitHub is asked anything. The commit check (V-I) is not built.
/// </summary>
public static class IntakeCheckRunStep
{
    public static async Task<JsonObject> RunAsync(JsonObject state, IReadOnlyList<IntakeRepository> repositories, IIntakeGitHub github)
    {
        var input = IntakeInput.From(state);
        if (input.In(repositories) is not { } repository)
            return Refused($"{input.Repo} ({input.RepositoryId}) is not a repository this intake takes");

        var run = await github.RunAttemptAsync(input.Repo, input.RunId, input.RunAttempt)
            ?? throw new IntakeFault($"GitHub has no attempt {input.RunAttempt} of run {input.RunId} in {input.Repo}");
        var jobs = await github.JobsAsync(input.Repo, input.RunId, input.RunAttempt);

        var check = RunCheck.Check(repository.Source, run, jobs);
        return check.Kind switch
        {
            RunCheckKind.Passed => new JsonObject { ["verdict"] = "passed", ["headBranch"] = run.HeadBranch, ["updatedAt"] = run.UpdatedAt.ToString("O") },
            RunCheckKind.Refused => Refused(check.Reasons.ToArray()),
            // Only completed attempts are started; one that is not is a fault, and the retry looks again.
            _ => throw new IntakeFault($"attempt {input.RunAttempt} of run {input.RunId} has not completed"),
        };
    }

    internal static JsonObject Refused(params string[] reasons)
        => new() { ["verdict"] = "refused", ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)r).ToArray()) };
}

/// <summary>
/// FETCH AND STAGE (DecoupledCd.md §15.4): each artifact the repository's config expects, named for this attempt, checked by
/// V-A's <see cref="ArtifactCheck"/>, downloaded, its SHA-256 compared with GitHub's digest, and written to the scratch bucket
/// at <c>{executionName}/{artifact}.zip</c>. For an image the zip's one OCI archive is read by V-A's
/// <see cref="OciArchive"/> (exactly one manifest, every blob's hash its name), and its manifest digest recorded. What was
/// staged is written to the evidence under <c>staged/</c>. Nothing is pushed and no record is written.
///
/// <para>A wrong artifact, a digest that does not match, or an image archive in the wrong form is a REFUSAL, decided; a
/// download or write that fails is a FAULT, retried.</para>
/// </summary>
public static class IntakeFetchStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IReadOnlyList<IntakeRepository> repositories,
        IIntakeGitHub github, IIntakeScratch scratch, IIntakeEvidenceStore evidence, string workDirectory)
    {
        var input = IntakeInput.From(state);
        var repository = input.In(repositories) ?? throw new IntakeFault($"{input.Repo} is not configured; CheckRun should have refused it");

        // Check names each expected artifact for this attempt itself (ArtifactCheck.NameFor), and keys what it found by
        // the configured name. The test bundle, where Gate 1 is configured, is fetched the same way (V-C).
        var expected = repository.Gate1 is { } gate1 ? repository.Artifacts.Append(gate1.Artifact).ToList() : repository.Artifacts.ToList();
        if (repository.BuiltFrom is { } builtFromArtifact) expected = expected.Append(builtFromArtifact).ToList();   // V-D
        var listed = await github.ArtifactsAsync(input.Repo, input.RunId);
        var check = ArtifactCheck.Check(input.RunId, input.RunAttempt, expected, listed);
        if (!check.Passed) return IntakeCheckRunStep.Refused(check.Reasons.ToArray());

        var staged = new JsonArray();
        string? manifestDigest = null;
        Directory.CreateDirectory(workDirectory);
        JsonObject? tests = null;
        JsonObject? builtFrom = null;
        foreach (var (configured, artifact) in expected.Select(n => (n, check.Found[n])))
        {
            var path = Path.Combine(workDirectory, $"{artifact.Name}.zip");
            try
            {
                await github.DownloadAsync(input.Repo, artifact.Id, path);
                var sha256 = await Sha256Async(path);
                if (!ArtifactCheck.DigestMatches(artifact.Digest, sha256))
                    return IntakeCheckRunStep.Refused($"{artifact.Name}: the bytes downloaded hash to sha256:{sha256}, not GitHub's {artifact.Digest}");

                var isTests = repository.Gate1?.Artifact == configured;
                var isBuiltFrom = repository.BuiltFrom == configured;
                if (isBuiltFrom)
                {
                    try
                    {
                        builtFrom = ReadBuiltFrom(path);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                    {
                        return IntakeCheckRunStep.Refused($"{artifact.Name}: {ex.Message}");
                    }
                }
                else if (repository.Class == "image" && !isTests)
                {
                    try
                    {
                        manifestDigest = ReadImage(path).ManifestDigest;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return IntakeCheckRunStep.Refused($"{artifact.Name}: {ex.Message}");
                    }
                }

                var key = $"{executionName}/{artifact.Name}.zip";
                await scratch.PutFileAsync(key, path);
                staged.Add(new JsonObject { ["artifact"] = artifact.Name, ["key"] = key, ["sha256"] = sha256, ["bytes"] = new FileInfo(path).Length });
                if (isTests) tests = new JsonObject { ["artifact"] = artifact.Name, ["key"] = key, ["sha256"] = sha256 };
            }
            finally
            {
                File.Delete(path);
            }
        }

        var result = new JsonObject { ["verdict"] = "staged", ["staged"] = staged };
        if (manifestDigest is not null) result["manifestDigest"] = manifestDigest;
        if (tests is not null) result["tests"] = tests;
        if (builtFrom is not null) result["builtFrom"] = builtFrom;
        await evidence.PutOnceAsync(IntakeEvidence.Staged(executionName), result.ToJsonString());   // already there: this try's retry wrote it
        return result;
    }

    /// <summary>
    /// The build's <c>builtfrom.json</c> (V-D), its zip's one entry: <c>{"packages": {name: version}}</c> and nothing else, every
    /// name and version a plain string, at least one, as the push path's record requires; returned as <c>{"packages": …}</c>.
    /// </summary>
    public static JsonObject ReadBuiltFrom(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "builtfrom.json")
            throw new InvalidOperationException($"the artifact holds {string.Join(", ", zip.Entries.Select(e => e.FullName))}, not one builtfrom.json");
        if (zip.Entries[0].Length > 1 << 20) throw new InvalidOperationException("builtfrom.json is over a megabyte");
        using var stream = zip.Entries[0].Open();
        var root = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("builtfrom.json is not an object");
        if (root.Count != 1 || root["packages"] is not JsonObject packages)
            throw new InvalidOperationException("builtfrom.json must be {\"packages\": {name: version}} and nothing else");
        if (packages.Count == 0) throw new InvalidOperationException("builtfrom.json names no packages; the build consumes first-party packages");
        var clean = new JsonObject();
        foreach (var (name, version) in packages)
        {
            var text = version is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
            if (text is null || !Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,200}$") || !Regex.IsMatch(text, @"^[0-9A-Za-z.+-]{1,100}$"))
                throw new InvalidOperationException($"builtfrom.json's package '{name}' is not a package name and a version");
            clean[name] = text;
        }
        return new JsonObject { ["packages"] = clean };
    }

    /// <summary>The image in an artifact's zip: exactly one <c>.tar</c> entry, an OCI archive.</summary>
    public static OciImage ReadImage(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var tars = zip.Entries.Where(e => e.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tars.Count != 1)
            throw new InvalidOperationException($"staged image refused: the artifact holds {tars.Count} .tar files, not one OCI archive");
        using var tar = tars[0].Open();
        return OciArchive.Read(tar);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
    }
}

/// <summary>
/// THE INTAKE'S RECORD (DecoupledCd.md §15.4, §5.3 rule 1): taken in or refused under the attempt's first-try name, a fault
/// under the try's own name; each written once, so a retry of the write after one that landed is not a second record. A
/// dry intake's "taken in" says what was staged and that nothing was pushed. A refusal and a fault alert.
/// </summary>
public static class IntakeRecordStep
{
    public static async Task<JsonObject> TakenInAsync(JsonObject state, string mode, IIntakeEvidenceStore evidence, DateTimeOffset now)
    {
        var input = IntakeInput.From(state);
        var body = new JsonObject
        {
            ["outcome"] = "taken-in",
            ["mode"] = mode,
            ["note"] = mode == "dry" ? "dry intake: staged and checked; nothing pushed, no build record written" : null,
            ["at"] = now.ToString("O"),
            ["run"] = Run(input),
            ["staged"] = state["fetch"]?.DeepClone(),
            ["gate1"] = state["gate1"]?.DeepClone(),
        };
        return Written(IntakeEvidence.TakenIn(input.AttemptName), await evidence.PutOnceAsync(IntakeEvidence.TakenIn(input.AttemptName), body.ToJsonString()));
    }

    public static async Task<JsonObject> RefusedAsync(JsonObject state, IIntakeEvidenceStore evidence, IIntakeAlerts alerts, DateTimeOffset now)
    {
        var input = IntakeInput.From(state);
        // Whichever step refused: the run check, the fetch, or Gate 1 (V-C).
        var refusing = new[] { "check", "fetch", "gate1" }.Select(s => state[s]).FirstOrDefault(n => n?["verdict"]?.GetValue<string>() is "refused" or "failed");
        var reasons = refusing?["reasons"]?.DeepClone();
        var key = IntakeEvidence.Refused(input.AttemptName);
        var written = await evidence.PutOnceAsync(key, new JsonObject
        {
            ["outcome"] = "refused", ["at"] = now.ToString("O"), ["run"] = Run(input), ["reasons"] = reasons,
        }.ToJsonString());
        if (written)
            await alerts.PublishAsync("intake refused a run", $"{input.Repo} run {input.RunId} attempt {input.RunAttempt} was refused: {reasons?.ToJsonString()}");
        return Written(key, written);
    }

    public static async Task<JsonObject> FaultAsync(JsonObject state, string executionName, IIntakeEvidenceStore evidence, IIntakeAlerts alerts, DateTimeOffset now)
    {
        var key = IntakeEvidence.Fault(executionName);
        var error = state["error"]?.DeepClone();
        var written = await evidence.PutOnceAsync(key, new JsonObject
        {
            ["outcome"] = "fault", ["at"] = now.ToString("O"), ["execution"] = executionName, ["error"] = error,
        }.ToJsonString());
        if (written && IntakeNames.Parse(executionName) is { Retry: >= IntakeNames.MaxRetries })
            await alerts.PublishAsync("intake faulted on its last retry", $"{executionName}: {error?.ToJsonString()}");
        return Written(key, written);
    }

    private static JsonObject Run(IntakeInput input) => new()
    {
        ["repo"] = input.Repo, ["repositoryId"] = input.RepositoryId, ["runId"] = input.RunId, ["runAttempt"] = input.RunAttempt, ["retry"] = input.Retry,
    };

    private static JsonObject Written(string key, bool written) => new() { ["key"] = key, ["written"] = written };
}
