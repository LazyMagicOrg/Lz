using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>Where one repository's image artifact is pushed, and the signing profile its pushes are signed under.</summary>
public sealed record PublishTarget(string Repo, string Artifact, string Repository, string SigningProfileArn);

/// <summary>An object in the artifact store: the version a record names, and its SHA-256 as stored with it.</summary>
public sealed record StoredArtifact(string VersionId, string? Sha256);

/// <summary>Reads a staged file back out of the scratch bucket.</summary>
public interface IScratchReader
{
    Task GetFileAsync(string key, string path);
}

/// <summary>ECR managed signing's answer for one image under one profile: COMPLETE, IN_PROGRESS, FAILED, or null for none yet.</summary>
public interface IImageSignatures
{
    Task<string?> StatusAsync(string repository, string digest, string profileArn);
}

/// <summary>What the publish writes: build records, write-once, and objects in the artifact store, write-once.</summary>
public interface IPublishStores
{
    Task<IReadOnlyList<string>> RecordKeysAsync(string prefix);
    Task<string?> RecordAsync(string key);

    /// <summary>A conditional PutObject (<c>If-None-Match</c>); false when the key exists.</summary>
    Task<bool> PutRecordOnceAsync(string key, string json);

    Task<StoredArtifact?> ArtifactAsync(string key);

    /// <summary>A single conditional PutObject with the file's SHA-256 as its checksum; the version written.</summary>
    Task<StoredArtifact> PutArtifactOnceAsync(string key, string path, string sha256);
}

/// <summary>The pushed image is not signed yet. RETRIED by the machine: no record names an unsigned image (§15.4).</summary>
public sealed class ImageNotYetSigned(string message) : Exception(message);

/// <summary>The publish's targets in one environment variable, as JSON: written by the planner, read by the function.</summary>
public static class PublishTargets
{
    public static string Encode(IReadOnlyList<PublishTarget> targets) => JsonSerializer.Serialize(targets);
    public static IReadOnlyList<PublishTarget> Decode(string json) => JsonSerializer.Deserialize<List<PublishTarget>>(json) ?? new();
}

/// <summary>
/// PUSH AND WRITE, ONLY ON A PASS (DecoupledCd.md §15.4, V-12, V-D). For a repository that is <c>Intake: live</c>:
/// <list type="number">
/// <item>a run the push path already recorded — a key under <c>{class}/{repo}/</c> ending <c>-{runId}.json</c>, which the
/// workflow writes before its run completes — is taken in dry, as evidence only, so no run is recorded twice;</item>
/// <item>nothing leaves the scratch bucket without Gate 1's pass, and then only the bytes it names: each staged file's
/// SHA-256 is held to the verdict's before it is used;</item>
/// <item>the image is pushed into its pipeline repository under <c>{sha}-{runId}-{runAttempt}</c> (the tag read first), and
/// its signature waited for, since no record may name an unsigned image;</item>
/// <item>the tests zip and the SBOM are written to the artifact store, each with one conditional PutObject; then the
/// <c>tests</c> record, then the image record, so no record that starts a deploy exists before the bundle Gate 2 reads.</item>
/// </list>
/// Every write reads its key first: what this attempt wrote counts as done, anything else there is refused, never
/// overwritten. A retry therefore continues where an earlier try stopped. Its <c>builtAt</c> is the run's
/// <c>created_at</c> (§15.8 item 11), so every try computes the same keys.
/// </summary>
public static class IntakePublishStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IReadOnlyList<IntakeRepository> repositories, IReadOnlyList<PublishTarget> targets,
        string recordStore, string artifactStore,
        IScratchReader scratch, IImageRegistry registry, IImageSignatures signatures, IPublishStores stores, string workDirectory)
    {
        var input = IntakeInput.From(state);
        var repository = input.In(repositories) ?? throw new IntakeFault($"{input.Repo} is not configured; CheckRun should have refused it");
        if (repository.Mode != "live") return new JsonObject { ["outcome"] = "dry" };

        // 1. THE PUSH PATH'S RECORD, while the push role exists (V-D): that run is recorded already.
        var prefix = BuildRecordFormat.PrefixFor(repository.Class, repository.Repo);
        var pushPath = (await stores.RecordKeysAsync(prefix)).FirstOrDefault(k => k.EndsWith($"-{input.RunId}.json", StringComparison.Ordinal));
        if (pushPath is not null) return new JsonObject { ["outcome"] = "push-path", ["record"] = pushPath };

        // 2. ONLY ON A PASS, AND ONLY THE BYTES IT NAMES.
        var gate1 = state["gate1"] as JsonObject;
        if (gate1?["verdict"]?.GetValue<string>() != "passed")
            throw new IntakeFault($"{executionName}: Gate 1 has not passed this attempt, so nothing is published");
        var passedSha = gate1["artifactSha256"] as JsonObject ?? throw new IntakeFault("Gate 1's verdict names no artifact SHA-256s");
        var manifest = gate1["manifestDigest"]?.GetValue<string>();
        var fetch = state["fetch"] as JsonObject ?? throw new IntakeFault("nothing was staged");
        var check = state["check"] as JsonObject ?? throw new IntakeFault("the run was not checked");

        string Text(JsonObject o, string name) => o[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new IntakeFault($"{name} is missing");
        var headSha = Text(check, "headSha");
        var builtAt = BuildRecordFormat.BuiltAtFor(DateTimeOffset.Parse(Text(check, "createdAt"), System.Globalization.CultureInfo.InvariantCulture));
        // A site consumes no first-party package (V-24): its records say so with an empty map rather than leaving it out.
        var builtFromNode = repository.Class == BuildRecordFormat.SiteClass
            ? new JsonObject { ["packages"] = new JsonObject() }
            : fetch["builtFrom"] as JsonObject ?? throw new IntakeFault($"{input.Repo} staged no builtFrom, which its records carry");
        var packages = (builtFromNode["packages"] as JsonObject ?? new JsonObject()).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
        var builtFrom = new BuildRecordBuiltFrom(repository.Repo, headSha, "published", packages, $"refs/heads/{Text(check, "headBranch")}");
        var gate1Evidence = IntakeEvidence.Gate1(executionName);
        // Gate 1's lists of the test bundle, which Gate 2 holds what ran to (§15.5): carried on the tests record, beside its zip.
        var testsLists = gate1?["gate2Lists"] is JsonObject recordedLists
            ? new BuildRecordGate1Lists(ListOf(recordedLists["precondition"]), ListOf(recordedLists["blocking"]), ListOf(recordedLists["advisory"]))
            : null;
        static IReadOnlyList<string> ListOf(JsonNode? node) => (node as JsonArray ?? new JsonArray()).Select(n => n!.GetValue<string>()).ToList();

        (string Key, string Sha256) Passed(string artifact)
        {
            var name = $"{artifact}-{input.RunAttempt}";
            var staged = (fetch["staged"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(s => s["artifact"]?.GetValue<string>() == name) ?? throw new IntakeFault($"{name} was not staged");
            var sha = staged["sha256"]!.GetValue<string>();
            if (passedSha[name]?.GetValue<string>() != sha)
                throw new IntakeFault($"{name}'s staged SHA-256 {sha} is not the one Gate 1 passed; nothing is published");
            return (staged["key"]!.GetValue<string>(), sha);
        }

        async Task<string> Fetched((string Key, string Sha256) staged, string name)
        {
            var path = Path.Combine(workDirectory, name);
            await scratch.GetFileAsync(staged.Key, path);
            if (await Sha256Async(path) != staged.Sha256)
                throw new IntakeFault($"{staged.Key} no longer hashes to {staged.Sha256}");
            return path;
        }

        BuildRecordIntake Intake(string artifact, string sha256) => new(
            input.RunId.ToString(), input.RunAttempt, Text(check, "jobIds"), Text(check, "workflowRef"), "sha256:" + sha256,
            new BuildRecordGate1("passed", gate1Evidence));

        // Everything a record needs is checked BEFORE anything is pushed or written, so a refusal leaves nothing behind.
        var isImage = BuildRecordFormat.IsImageClass(repository.Class);
        if (!isImage && repository.Class is not ("client" or BuildRecordFormat.SiteClass))
            throw new IntakeFault($"{repository.Class} records are not published yet; an image's, a client's and a site's are (V-D, V-24)");

        // A SITE (V-24): one zip and one record per environment's bundle, each record naming its environment, and no tests record,
        // since a site has no test bundle. Every bundle is held to Gate 1's pass before anything is written.
        if (repository.Class == BuildRecordFormat.SiteClass)
        {
            var site = repository.Site ?? throw new IntakeFault($"{input.Repo} is a site with no site check, so nothing of it passed Gate 1");
            var bundles = repository.Artifacts
                .Select(a => (Artifact: a,
                    Environment: site.Environments.TryGetValue(a, out var e) ? e : throw new IntakeFault($"{input.Repo}'s site check names no environment for {a}"),
                    Staged: Passed(a)))
                .ToList();
            Directory.CreateDirectory(workDirectory);
            var siteRecords = new JsonArray();
            var siteBundles = new JsonArray();
            foreach (var (artifact, environment, siteStaged) in bundles)
            {
                var bundlePath = Path.Combine(workDirectory, $"{artifact}-{IntakeFetchStep.BundleEntry}");
                string bundleSha;
                try
                {
                    var outer = await Fetched(siteStaged, $"{artifact}.zip");
                    bundleSha = IntakeFetchStep.ReadBundle(outer);
                    using var zip = ZipFile.OpenRead(outer);
                    zip.Entries.Single().ExtractToFile(bundlePath, overwrite: true);
                }
                catch (InvalidOperationException ex)
                {
                    throw new IntakeFault($"{artifact}-{input.RunAttempt}: {ex.Message}");
                }

                var provisional = new BuildRecord(BuildRecordFormat.CurrentSchema, BuildRecordFormat.SiteClass, builtFrom, new BuildRecordIdentity("bundle"),
                    builtAt, Text(check, "actor"), input.RunId.ToString(), Intake(artifact, siteStaged.Sha256) with { Environment = environment });
                var key = BuildRecordFormat.BundleKeyFor(provisional);
                var stored = await PutArtifactAsync(stores, key, bundlePath, bundleSha);
                siteRecords.Add(await WriteRecordAsync(stores, provisional with
                {
                    Identity = new BuildRecordIdentity("bundle", Bucket: artifactStore, Key: key, VersionId: stored.VersionId, Sha256: bundleSha),
                }, artifactStore));
                siteBundles.Add(new JsonObject { ["environment"] = environment, ["key"] = key, ["versionId"] = stored.VersionId, ["sha256"] = bundleSha });
            }

            return new JsonObject { ["outcome"] = "published", ["sites"] = siteBundles, ["records"] = siteRecords };
        }

        if (repository.Artifacts.Count != 1)
            throw new IntakeFault($"{input.Repo} names {repository.Artifacts.Count} artifacts; a record names one");
        var artifactName = repository.Artifacts[0];
        var staged = Passed(artifactName);
        var testsStaged = repository.Gate1 is { } g ? Passed(g.Artifact) : default;
        Directory.CreateDirectory(workDirectory);
        var written = new JsonArray();

        BuildRecord Record(string cls, BuildRecordIdentity identity, string artifact, string sha256) => new(
            BuildRecordFormat.CurrentSchema, cls, builtFrom, identity, builtAt, Text(check, "actor"), input.RunId.ToString(), Intake(artifact, sha256));

        // THE TESTS ZIP AND ITS RECORD come first, so no record that starts a deploy exists before the bundle Gate 2 reads.
        async Task WriteTestsAsync()
        {
            if (repository.Gate1 is not { } gate) return;
            var provisional = Record("tests", new BuildRecordIdentity("bundle"), gate.Artifact, testsStaged.Sha256);
            provisional = provisional with { Intake = provisional.Intake! with { Gate1 = provisional.Intake.Gate1 with { Lists = testsLists } } };
            var zipKey = BuildRecordFormat.BundleKeyFor(provisional);
            var stored = await PutArtifactAsync(stores, zipKey, await Fetched(testsStaged, "tests.zip"), testsStaged.Sha256);
            written.Add(await WriteRecordAsync(stores, provisional with
            {
                Identity = new BuildRecordIdentity("bundle", Bucket: artifactStore, Key: zipKey, VersionId: stored.VersionId, Sha256: testsStaged.Sha256),
            }, artifactStore));
        }

        if (!isImage)
        {
            // A CLIENT (V-D): its bundle is the one zip the build made, inside the artifact; the artifact store keeps it as it is.
            var bundlePath = Path.Combine(workDirectory, IntakeFetchStep.BundleEntry);
            string bundleSha;
            try
            {
                var outer = await Fetched(staged, "artifact.zip");
                bundleSha = IntakeFetchStep.ReadBundle(outer);
                using var zip = ZipFile.OpenRead(outer);
                zip.Entries.Single().ExtractToFile(bundlePath, overwrite: true);
            }
            catch (InvalidOperationException ex)
            {
                throw new IntakeFault($"{artifactName}-{input.RunAttempt}: {ex.Message}");
            }

            await WriteTestsAsync();
            var provisional = Record(repository.Class, new BuildRecordIdentity("bundle"), artifactName, staged.Sha256);
            var bundleKey = BuildRecordFormat.BundleKeyFor(provisional);
            var stored = await PutArtifactAsync(stores, bundleKey, bundlePath, bundleSha);
            written.Add(await WriteRecordAsync(stores, provisional with
            {
                Identity = new BuildRecordIdentity("bundle", Bucket: artifactStore, Key: bundleKey, VersionId: stored.VersionId, Sha256: bundleSha),
            }, artifactStore));

            return new JsonObject
            {
                ["outcome"] = "published",
                ["bundle"] = new JsonObject { ["key"] = bundleKey, ["versionId"] = stored.VersionId, ["sha256"] = bundleSha },
                ["records"] = written,
            };
        }

        // AN IMAGE also needs (V-19) its version, its base image and its SBOM, and a pipeline repository to go into.
        var serviceVersion = builtFromNode["serviceVersion"]?.GetValue<string>() ?? throw new IntakeFault("builtFrom has no serviceVersion, which an image record needs (V-19)");
        var baseImage = builtFromNode["baseImageDigest"]?.GetValue<string>() ?? throw new IntakeFault("builtFrom has no baseImageDigest, which an image record needs (V-19)");
        var sbomArtifact = repository.Sbom ?? throw new IntakeFault($"{input.Repo} uploads no SBOM, which an image record needs (V-19)");
        var target = targets.SingleOrDefault(t => string.Equals(t.Repo, repository.Repo, StringComparison.OrdinalIgnoreCase) && t.Artifact == artifactName)
            ?? throw new IntakeFault($"no pipeline repository is configured for {input.Repo}'s {artifactName}");
        var sbomStaged = Passed(sbomArtifact);

        // THE IMAGE, PUSHED AND SIGNED.
        var tag = $"{headSha}-{input.RunId}-{input.RunAttempt}";
        var pushed = await IntakePushStep.RunAsync(await Fetched(staged, "image.zip"), target.Repository, tag, registry);
        var digest = pushed["manifestDigest"]!.GetValue<string>();
        if (digest != manifest) throw new IntakeFault($"the staged image is {digest}, not the {manifest} Gate 1 passed");
        switch (await signatures.StatusAsync(target.Repository, digest, target.SigningProfileArn))
        {
            case "COMPLETE": break;
            case "FAILED": throw new IntakeFault($"{target.Repository}@{digest}: managed signing FAILED under {target.SigningProfileArn}");
            case var other: throw new ImageNotYetSigned($"{target.Repository}@{digest} is not signed yet ({other ?? "no status"})");
        }

        await WriteTestsAsync();
        var imageRecord = Record(repository.Class, new BuildRecordIdentity("image", Digest: digest), artifactName, staged.Sha256);
        imageRecord = imageRecord with { Intake = imageRecord.Intake! with { ServiceVersion = serviceVersion, BaseImageDigest = baseImage } };
        var sbomKey = BuildRecordFormat.KeyFor(imageRecord)[..^".json".Length] + ".sbom.json";
        var sbomPath = Path.Combine(workDirectory, "sbom.json");
        using (var zip = ZipFile.OpenRead(await Fetched(sbomStaged, "sbom.zip")))
            zip.Entries.Single().ExtractToFile(sbomPath, overwrite: true);
        var sbom = await PutArtifactAsync(stores, sbomKey, sbomPath, await Sha256Async(sbomPath));
        imageRecord = imageRecord with { Intake = imageRecord.Intake! with { Sbom = new BuildRecordObjectRef(sbomKey, sbom.VersionId) } };
        written.Add(await WriteRecordAsync(stores, imageRecord, artifactStore));

        return new JsonObject
        {
            ["outcome"] = "published",
            ["image"] = new JsonObject { ["repository"] = target.Repository, ["tag"] = tag, ["digest"] = digest, ["push"] = pushed["outcome"]!.DeepClone() },
            ["records"] = written,
        };
    }

    /// <summary>One conditional PutObject; an object already at the key counts as written only if it holds these bytes.</summary>
    private static async Task<StoredArtifact> PutArtifactAsync(IPublishStores stores, string key, string path, string sha256)
    {
        switch (IntakeWrites.Zip((await stores.ArtifactAsync(key))?.Sha256, sha256))
        {
            case WriteDecision.AlreadyDone: return (await stores.ArtifactAsync(key))!;
            case WriteDecision.Refuse: throw new IntakeFault($"{key} already holds other bytes; it is never overwritten");
            default: return await stores.PutArtifactOnceAsync(key, path, sha256);
        }
    }

    /// <summary>The record, checked by the same parser every reader uses, written once; this attempt's own record there counts as done.</summary>
    private static async Task<JsonNode> WriteRecordAsync(IPublishStores stores, BuildRecord record, string artifactStore)
    {
        var key = BuildRecordFormat.KeyFor(record);
        var json = BuildRecordFormat.Serialize(record);
        BuildRecordFormat.Parse(json, artifactStore);   // a record this step cannot read back is never written

        for (var tries = 0; tries < 2; tries++)
        {
            if (await stores.RecordAsync(key) is { } existing)
            {
                BuildRecord? there;
                try { there = BuildRecordFormat.Parse(existing, artifactStore); }
                catch (InvalidOperationException) { there = null; }
                return IntakeWrites.Record(there ?? record with { Intake = null }, record) == WriteDecision.AlreadyDone
                    ? key
                    : throw new IntakeFault($"{key} already holds a record for another attempt or artifact; it is never overwritten");
            }
            if (await stores.PutRecordOnceAsync(key, json)) return key;
        }
        throw new IntakeFault($"{key} could not be written or read");
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
    }
}
