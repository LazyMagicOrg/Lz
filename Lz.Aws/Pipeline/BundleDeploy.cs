using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lz.Aws.Webapp;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  CLASS 2 — CLIENT BUNDLES (DecoupledCd.md §4.6, P4 stage C).
//
//  Verify checks a client record and HEADs its zip; DeployBundle mirrors the zip into the web app's bucket and
//  invalidates the app's path; VerifyBundle reads every object back. What they read and write is behind the three
//  interfaces below, which the Lambda package implements with the SDK and the tests with fakes, so the ORDER — nothing
//  written before the lease is held, no manifest before the assets it names, no delete before the manifests — is tested
//  rather than trusted. BCL only: the deployer's Lambda package compiles this file by link.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One web app a client bundle deploys into, resolved from configuration: a repository's <c>Artifacts</c> entry names the
/// app, <c>Behaviors.WebApps</c> gives its path, the system names its bucket, and the tenants' distributions are found
/// when the deployer is bootstrapped.
/// </summary>
/// <param name="Repo">The repository whose bundles, and only whose bundles, deploy here.</param>
/// <param name="BasePath">The path the app is served under, <c>seller/</c>, which its bundle is built for.</param>
/// <param name="Distributions">The CloudFront distributions whose cached copies a deploy clears.</param>
public sealed record ClientTarget(string Repo, string App, string Bucket, string BasePath, IReadOnlyList<string> Distributions)
{
    /// <summary>Everything under it is the app's: <c>wwwroot/seller/</c>.</summary>
    public string KeyPrefix => WebappSyncRules.StoragePrefix + BasePath;

    /// <summary>The one path a deploy invalidates, <c>/seller*</c> (DecoupledCd.md P-9).</summary>
    public string InvalidationPath => WebappSyncRules.InvalidationPath(BasePath);
}

/// <summary>Client targets as a function's environment and an execution's state carry them.</summary>
public static class ClientTargets
{
    /// <summary>What a function's environment can hold of them, well inside Lambda's four kilobytes for all variables.</summary>
    public const int MaxEncodedLength = 2000;

    public static string Encode(IReadOnlyList<ClientTarget> targets)
    {
        var json = new JsonArray(targets.Select(t => (JsonNode?)ToJson(t)).ToArray()).ToJsonString();
        return json.Length <= MaxEncodedLength
            ? json
            : throw new InvalidOperationException(
                $"the client targets are {json.Length} characters; a function's environment holds {MaxEncodedLength} of them here.");
    }

    public static IReadOnlyList<ClientTarget> Decode(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{DeployerEnvironment.ClientTargets} is not valid JSON: {ex.Message}");
        }

        return node is JsonArray array
            ? array.Select(FromJson).ToList()
            : throw new InvalidOperationException($"{DeployerEnvironment.ClientTargets} is not a JSON array.");
    }

    public static JsonObject ToJson(ClientTarget t) => new()
    {
        ["repo"] = t.Repo,
        ["app"] = t.App,
        ["bucket"] = t.Bucket,
        ["basePath"] = t.BasePath,
        ["keyPrefix"] = t.KeyPrefix,
        ["invalidationPath"] = t.InvalidationPath,
        ["distributions"] = new JsonArray(t.Distributions.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
    };

    /// <summary>Read one back. Throws on anything the planner would not have written.</summary>
    public static ClientTarget FromJson(JsonNode? node)
    {
        string Text(string name)
            => node is JsonObject o && o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>()
                : throw new InvalidOperationException($"a client target has no string '{name}'.");

        var distributions = node is JsonObject obj && obj["distributions"] is JsonArray a
                            && a.All(d => d is JsonValue dv && dv.GetValueKind() == JsonValueKind.String)
            ? a.Select(d => d!.GetValue<string>()).ToList()
            : throw new InvalidOperationException("a client target has no list of distributions.");

        var target = new ClientTarget(Text("repo"), Text("app"), Text("bucket"), Text("basePath"), distributions);

        // Derived, and checked rather than trusted, so a hand-edited target cannot aim the mirror at another prefix.
        if (Text("keyPrefix") != target.KeyPrefix || Text("invalidationPath") != target.InvalidationPath)
            throw new InvalidOperationException(
                $"client target '{target.App}' names keyPrefix '{Text("keyPrefix")}' and invalidationPath '{Text("invalidationPath")}', " +
                $"which its base path '{target.BasePath}' does not give.");

        if (distributions.Count == 0)
            throw new InvalidOperationException($"client target '{target.App}' names no distribution, so a deploy could clear no cache.");

        return target;
    }
}

/// <summary>A bundle in the artifact store, as a HEAD of one version reads it.</summary>
/// <param name="ChecksumSha256Base64">S3's own SHA-256 of the object, base64; null when it was written without one.</param>
/// <param name="ChecksumType"><c>FULL_OBJECT</c> or <c>COMPOSITE</c>.</param>
public sealed record ArtifactHead(string? VersionId, string? ChecksumSha256Base64, string? ChecksumType, long ContentLength);

/// <summary>The app's deploy marker as read: its text, and the ETag a conditional write names it by.</summary>
public sealed record MarkerRead(string Json, string ETag);

/// <summary>The artifact store, read by version.</summary>
public interface IArtifactObjects
{
    /// <summary>
    /// The HEAD of one version, or null when S3 has no such version: a 404, or the 400 S3 answers a HEAD naming a version
    /// id it never issued (measured on the build account's store, 2026-09-14). A denial throws.
    /// </summary>
    Task<ArtifactHead?> HeadAsync(string bucket, string key, string versionId);

    /// <summary>Write one version to a file; the version S3 served, and the bytes written.</summary>
    Task<(string? VersionId, long Bytes)> DownloadAsync(string bucket, string key, string versionId, string path);
}

/// <summary>A web app's bucket, which the deploy creates and hardens (DecoupledCd.md P-7) and mirrors into.</summary>
public interface IAppBucket
{
    /// <summary>Create the bucket when absent; block public access; write its policy whole; apply the durability decision.</summary>
    Task EnsureAsync(string bucket, string region, string policyJson, bool versioning, int? noncurrentExpirationDays);

    /// <summary>The marker, or null when there is no such object or no such bucket yet. A denial throws.</summary>
    Task<MarkerRead?> ReadMarkerAsync(string bucket, string key);

    /// <summary>
    /// Write the marker only if it is still what was read: <c>If-Match</c> the ETag, or <c>If-None-Match: *</c> when
    /// <paramref name="ifMatchETag"/> is null. The new ETag; null when the condition failed.
    /// </summary>
    Task<string?> WriteMarkerAsync(string bucket, string key, string json, string? ifMatchETag);

    /// <summary>Every key under the prefix, all pages.</summary>
    Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix);

    /// <summary>One object's checksum and headers, or null when it is gone.</summary>
    Task<StoredObject?> HeadAsync(string bucket, string key);

    /// <summary>Write one file with its headers and its SHA-256, which S3 checks the bytes against.</summary>
    Task PutAsync(string bucket, string key, string filePath, WebappObjectHeaders headers, string sha256Base64);

    /// <summary>Delete the keys. Throws when any one was not deleted.</summary>
    Task DeleteAsync(string bucket, IReadOnlyList<string> keys);
}

/// <summary>CloudFront invalidations.</summary>
public interface IInvalidations
{
    /// <summary>
    /// Create one, or get back the one this caller reference already created — CloudFront's own idempotency, which makes a
    /// retried deploy clear the path once. Its id.
    /// </summary>
    Task<string> CreateAsync(string distributionId, string path, string callerReference);

    /// <summary><c>InProgress</c> or <c>Completed</c>.</summary>
    Task<string?> StatusAsync(string distributionId, string invalidationId);
}

/// <summary>
/// The app's deploy marker, <c>lz/deploy.json</c> in its bucket: the build it serves, and the lease an execution holds
/// while it writes (DecoupledCd.md P4 stage C).
///
/// <para><b>THE BUILD TIME KEPT BESIDE THE APP</b>, as <see cref="DeployOrdering.BuiltAtTag"/> is kept on a task
/// definition, so a deploy naming an older bundle is refused. Outside <c>wwwroot/</c>, which is all CloudFront's origin
/// path reaches, so it is never served.</para>
///
/// <para><b>AND A LEASE, WHICH CLASS 1 DOES NOT NEED.</b> An image roll is one <c>UpdateService</c>; a mirror is hundreds
/// of writes and deletes. Two executions judging the same marker could both pass and interleave them — an older build's
/// <c>index.html</c> landing after a newer one's, or one deploy deleting what the other just wrote. So an execution
/// claims the marker with a conditional write before it writes anything, and releases it, conditionally again, with the
/// build it deployed. A claim by another execution is waited out, or refused when it is for a newer build; a claim older
/// than <see cref="Lease"/> belongs to a deploy that died, and is ignored. What remains: an execution whose own writes
/// outlast its lease, which its release then finds taken (<see cref="BundleDeployRaced"/>), and a claim that stays until
/// it expires when a deploy fails after taking it.</para>
/// </summary>
public static class BundleMarker
{
    public const string Key = "lz/deploy.json";
    public const int Schema = 1;

    /// <summary>Longer than one DeployBundle invocation may run (its timeout is ten minutes), so a live one is never taken over.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>The build the app serves, as the deploy that wrote it recorded.</summary>
    public sealed record DeployedBuild(
        string BuiltAt, string Execution, string At, string BundleKey, string BundleVersionId, string BundleSha256, string ManifestSha256);

    /// <summary>An execution's lease on the app's bucket.</summary>
    public sealed record LeaseClaim(string BuiltAt, string Execution, string At, string ExpiresAt);

    public sealed record MarkerState(DeployedBuild? Deployed, LeaseClaim? Claim);

    public enum Verdict { Proceed, Superseded, Wait }

    /// <summary>
    /// Read a marker. Refuses anything this does not write — only the deployer writes it, so a marker it cannot read
    /// means something else did, and guessing which build the app serves is the one thing it exists not to do.
    /// </summary>
    public static MarkerState Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["schema"]?.GetValue<int>() != Schema)
                throw new InvalidOperationException($"it is not a schema-{Schema} object");

            DeployedBuild? deployed = root["deployed"] is JsonObject d
                ? new DeployedBuild(Req(d, "builtAt"), Req(d, "execution"), Req(d, "at"), Req(d, "bundleKey"),
                    Req(d, "bundleVersionId"), Req(d, "bundleSha256"), Req(d, "manifestSha256"))
                : null;
            LeaseClaim? claim = root["claim"] is JsonObject c
                ? new LeaseClaim(Req(c, "builtAt"), Req(c, "execution"), Req(c, "at"), Req(c, "expiresAt"))
                : null;

            return new MarkerState(deployed, claim);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeployRefused("marker",
                $"the app's deploy marker ({Key}) could not be read ({ex.Message}). Only the deployer writes it; refusing rather " +
                "than guessing which build the app serves.");
        }

        static string Req(JsonObject o, string name)
            => o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s
                ? s
                : throw new InvalidOperationException($"'{name}' is missing");
    }

    public static string Serialize(MarkerState state)
    {
        var root = new JsonObject { ["schema"] = Schema };
        if (state.Deployed is { } d)
            root["deployed"] = new JsonObject
            {
                ["builtAt"] = d.BuiltAt, ["execution"] = d.Execution, ["at"] = d.At, ["bundleKey"] = d.BundleKey,
                ["bundleVersionId"] = d.BundleVersionId, ["bundleSha256"] = d.BundleSha256, ["manifestSha256"] = d.ManifestSha256,
            };
        if (state.Claim is { } c)
            root["claim"] = new JsonObject { ["builtAt"] = c.BuiltAt, ["execution"] = c.Execution, ["at"] = c.At, ["expiresAt"] = c.ExpiresAt };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// May this execution deploy its build now?
    /// <list type="bullet">
    ///   <item>another execution's live claim: <see cref="Verdict.Superseded"/> when its build is newer, since this one would
    ///   follow it with an older build; <see cref="Verdict.Wait"/> otherwise;</item>
    ///   <item>a newer build deployed: <see cref="Verdict.Superseded"/>, unless <paramref name="allowOlderBuild"/>;</item>
    ///   <item>anything else — no marker, an expired claim, this execution's own claim: <see cref="Verdict.Proceed"/>.</item>
    /// </list>
    /// </summary>
    public static (Verdict Verdict, string Reason) Decide(
        MarkerState? marker, string builtAt, string execution, DateTimeOffset now, bool allowOlderBuild)
    {
        var ours = DeployOrdering.ParseUtc(builtAt)
            ?? throw new DeployRefused("verified.builtAt",
                $"the record's builtAt is '{builtAt}', which is not a UTC time; the build's age cannot be judged.");

        if (marker?.Claim is { } claim && !string.Equals(claim.Execution, execution, StringComparison.Ordinal))
        {
            var expires = Time(claim.ExpiresAt, "claim.expiresAt");
            if (expires > now)
            {
                var theirs = Time(claim.BuiltAt, "claim.builtAt");
                if (theirs > ours && !allowOlderBuild)
                    return (Verdict.Superseded,
                        $"execution {claim.Execution} is deploying a newer build of this app ({claim.BuiltAt}) than this record's " +
                        $"({builtAt}); following it would put the older build back. Nothing was written.");

                return (Verdict.Wait,
                    $"execution {claim.Execution} holds this app's deploy lease for build {claim.BuiltAt} until {claim.ExpiresAt}.");
            }
        }

        if (marker?.Deployed is not { } deployed)
            return (Verdict.Proceed,
                $"the app records no build deployed by the deployer, so this build ({builtAt}) is not compared.");

        var serving = Time(deployed.BuiltAt, "deployed.builtAt");
        if (serving <= ours)
            return (Verdict.Proceed, $"this build ({builtAt}) is not older than the one the app serves ({deployed.BuiltAt}).");

        return allowOlderBuild
            ? (Verdict.Proceed,
                $"this build ({builtAt}) is older than the one the app serves ({deployed.BuiltAt}), and the input says allowOlderBuild.")
            : (Verdict.Superseded,
                $"the app already serves a newer build ({deployed.BuiltAt}, deployed by {deployed.Execution}) than this record's " +
                $"({builtAt}); deploying it would put the older bundle back. Nothing was written. To deploy an older build on " +
                "purpose, start the execution with \"allowOlderBuild\": true.");

        static DateTimeOffset Time(string value, string field)
            => DeployOrdering.ParseUtc(value)
               ?? throw new DeployRefused("marker",
                   $"the app's deploy marker has {field} '{value}', which is not a UTC time. Only the deployer writes it; refusing " +
                   "rather than guessing.");
    }

    /// <summary>The marker with this execution's claim on it, and the deployed build kept.</summary>
    public static MarkerState Claimed(MarkerState? marker, string builtAt, string execution, DateTimeOffset now)
        => new(marker?.Deployed, new LeaseClaim(builtAt, execution, Utc(now), Utc(now + Lease)));

    /// <summary>
    /// A time as the marker writes it: UTC with a <c>Z</c>, the form <see cref="Decide"/> reads back. The round-trip format of
    /// a <see cref="DateTimeOffset"/> writes <c>+00:00</c> instead, which the reader refuses — the first draft wrote exactly
    /// that, and every lease it claimed would have been refused as unreadable by the next execution to read it.
    /// </summary>
    public static string Utc(DateTimeOffset time)
        => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The marker a finished deploy leaves: its build, and no claim.</summary>
    public static MarkerState Released(DeployedBuild deployed) => new(deployed, null);
}

/// <summary>Expanding a bundle into the function's scratch space.</summary>
public static class BundleArchive
{
    /// <summary>The largest zip a deploy reads: six times today's 24.6 MB, and with the expansion inside Lambda's 512 MB.</summary>
    public const long MaxZipBytes = 150L * 1024 * 1024;

    /// <summary>The most a bundle may expand to, counted as it is written rather than as the zip claims.</summary>
    public const long MaxExpandedBytes = 300L * 1024 * 1024;

    /// <summary>
    /// Expand <paramref name="zipPath"/> under <paramref name="directory"/>, hashing each file as it is written. Refuses a
    /// bundle whose entry names could leave the directory or the app's prefix, that collide when case is ignored, or that
    /// expands past <see cref="MaxExpandedBytes"/>.
    /// </summary>
    public static async Task<IReadOnlyList<BundleFile>> ExpandAsync(string zipPath, string directory)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        var refusals = WebappMirrorPlanner.RefusalsForEntryNames(names).ToList();
        var files = names.Where(n => !n.EndsWith('/')).ToList();
        var collision = files.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (collision != null)
            refusals.Add($"entries {string.Join(" and ", collision.Distinct(StringComparer.Ordinal).Take(2))} differ only in case");
        if (refusals.Count > 0)
            throw new DeployRefused("bundle", "the bundle cannot be mirrored: " + string.Join("; ", refusals) + ".");

        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        var expanded = new List<BundleFile>();
        var buffer = new byte[81920];
        long total = 0;

        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new DeployRefused("bundle", $"entry '{entry.FullName}' expands outside the bundle's directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var source = entry.Open())
            await using (var target = File.Create(path))
            {
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    size += read;
                    total += read;
                    if (total > MaxExpandedBytes)
                        throw new DeployRefused("bundle", $"the bundle expands past {MaxExpandedBytes} bytes.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            expanded.Add(new BundleFile(entry.FullName, Convert.ToHexStringLower(hash.GetHashAndReset()), size));
        }

        return expanded;
    }

    /// <summary>A file's SHA-256, lowercase hex.</summary>
    public static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}

/// <summary>What DeployBundle is configured with.</summary>
/// <param name="TargetAccountId">The account the app's bucket policy admits CloudFront from.</param>
/// <param name="WorkRoot">Where bundles are expanded: Lambda's only writable directory is <c>/tmp</c>.</param>
public sealed record DeployBundleSettings(
    string Region, string TargetAccountId, bool BucketVersioning, int? NoncurrentExpirationDays, string WorkRoot)
{
    /// <summary>
    /// The lifecycle rule's id, which <c>BucketDurabilityEnsurer.LifecycleRuleId</c> writes for <c>deploywebapp</c> — the same
    /// rule, so a bucket either deploy touched has one. Repeated here because that file needs lz's credential chain, which
    /// the Lambda package does not carry; a test holds the two equal.
    /// </summary>
    public const string NoncurrentExpiryRuleId = "lz-hygiene-noncurrent-expire";

    public static DeployBundleSettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.Required(env, DeployerEnvironment.Region),
        DeployerEnvironment.Required(env, DeployerEnvironment.TargetAccount),
        DeployerEnvironment.Required(env, DeployerEnvironment.BucketVersioning) switch
        {
            "true" => true,
            "false" => false,
            var other => throw new InvalidOperationException(
                $"environment variable {DeployerEnvironment.BucketVersioning} is '{other}'; it must be true or false."),
        },
        // Absent means no expiry rule, as BucketDurabilityDecision's null does; the planner writes it only with a value.
        env(DeployerEnvironment.NoncurrentExpirationDays) is { Length: > 0 } days
            ? int.TryParse(days, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new InvalidOperationException($"environment variable {DeployerEnvironment.NoncurrentExpirationDays} is '{days}', not a number of days.")
            : null,
        "/tmp/lz-bundle");
}

/// <summary>
/// The DeployBundle state (§4.6, class 2): mirror the verified bundle into the web app's bucket and invalidate the app's
/// path, in dev too (P-9).
/// </summary>
public static class DeployBundleStep
{
    /// <summary>Concurrent reads and writes against the app's bucket.</summary>
    public const int Concurrency = 16;

    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, DeployBundleSettings settings, IArtifactObjects artifacts, IAppBucket bucket,
        IInvalidations invalidations, Func<DateTimeOffset> clock)
    {
        var (identity, target, builtAt) = BundleState.Verified(state, "DeployBundle");
        var allowOlder = DeployerInput.AllowsOlderBuild(state);

        // 1. THE MARKER, BEFORE ANYTHING IS READ OR WRITTEN. A refusal here leaves the bucket as it was; a missing bucket has
        // no marker, so there is nothing to refuse against and it is created below.
        var read = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key);
        var marker = read is null ? null : BundleMarker.Parse(read.Json);
        var (verdict, ordering) = BundleMarker.Decide(marker, builtAt, executionName, clock(), allowOlder);
        switch (verdict)
        {
            case BundleMarker.Verdict.Proceed:
                break;
            case BundleMarker.Verdict.Superseded:
                throw new DeploySuperseded(ordering);
            default:
                throw new BundleDeployInProgress(ordering);
        }

        var work = Path.Combine(settings.WorkRoot, DeployEvidence.RequireSafeExecutionName(executionName));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        try
        {
            // 2. THE BYTES VERIFY CHECKED. Verify compared S3's checksum with the record's; these are hashed again as read,
            // so what is expanded is what was verified, not merely what the same key served later.
            var zipPath = Path.Combine(work, "bundle.zip");
            var (servedVersion, _) = await artifacts.DownloadAsync(identity.Bucket, identity.Key, identity.VersionId, zipPath);
            if (!string.Equals(servedVersion, identity.VersionId, StringComparison.Ordinal))
                throw new DeployRefused("identity.versionId",
                    $"asked for version {identity.VersionId} of s3://{identity.Bucket}/{identity.Key}, S3 served '{servedVersion}'. Nothing was written.");

            var zipSha256 = await BundleArchive.Sha256Async(zipPath);
            if (!string.Equals(zipSha256, identity.Sha256, StringComparison.Ordinal))
                throw new DeployRefused("identity.sha256",
                    $"the bytes read from s3://{identity.Bucket}/{identity.Key} hash to {zipSha256}, not the {identity.Sha256} Verify " +
                    "checked. Nothing was written.");

            var files = await BundleArchive.ExpandAsync(zipPath, Path.Combine(work, "files"));

            // BUILT FOR THIS APP, judged before the claim, so a bundle that does not belong here holds no lease.
            try
            {
                WebappMirrorPlanner.Plan(files, Array.Empty<StoredObject>(), target.BasePath);
            }
            catch (InvalidOperationException ex)
            {
                throw new DeployRefused("bundle", ex.Message + " Nothing was written.");
            }

            // 3. THE BUCKET, created and hardened by the deploy (P-7). Idempotent, so it is written before the claim.
            await bucket.EnsureAsync(target.Bucket, settings.Region,
                WebappSyncRules.CloudFrontReadPolicy(target.Bucket, settings.TargetAccountId),
                settings.BucketVersioning, settings.NoncurrentExpirationDays);

            // 4. THE CLAIM, conditional on the marker being what step 1 read.
            var claimTag = await bucket.WriteMarkerAsync(target.Bucket, BundleMarker.Key,
                    BundleMarker.Serialize(BundleMarker.Claimed(marker, builtAt, executionName, clock())), read?.ETag)
                ?? throw new BundleDeployInProgress(
                    "another execution wrote this app's deploy marker between this one's read and its claim. Nothing of the " +
                    "bundle was written; the marker is read again when this is retried.");

            // 5. WHAT THE APP HOLDS NOW, read under the claim.
            var keys = await bucket.ListAsync(target.Bucket, target.KeyPrefix);
            var stored = (await ForEachAsync(keys, key => bucket.HeadAsync(target.Bucket, key))).OfType<StoredObject>().ToList();

            MirrorPlan plan;
            try
            {
                plan = WebappMirrorPlanner.Plan(files, stored, target.BasePath);
            }
            catch (InvalidOperationException ex)
            {
                throw new DeployRefused("bundle", ex.Message);
            }

            // 6. ASSETS, THEN MANIFESTS, THEN DELETES — so no manifest a browser reads names a file that is not there.
            string FileFor(MirrorPut put) => Path.Combine(work, "files", put.File.Path);
            await ForEachAsync(plan.Assets, put => Put(bucket, target.Bucket, put, FileFor(put)));
            await ForEachAsync(plan.Manifests, put => Put(bucket, target.Bucket, put, FileFor(put)));
            if (plan.Deletes.Count > 0)
                await bucket.DeleteAsync(target.Bucket, plan.Deletes);

            // 7. THE APP'S PATH ON EVERY DISTRIBUTION, in dev too (P-9). The execution name is the caller reference, so a
            // retry gets back the invalidation the first attempt made.
            var created = new JsonArray();
            foreach (var distribution in target.Distributions)
            {
                var id = await invalidations.CreateAsync(distribution, target.InvalidationPath, executionName);
                created.Add(new JsonObject { ["distribution"] = distribution, ["path"] = target.InvalidationPath, ["id"] = id });
            }

            // 8. THE RELEASE, conditional on the claim still being this execution's.
            var deployed = new BundleMarker.DeployedBuild(
                builtAt, executionName, BundleMarker.Utc(clock()), identity.Key, identity.VersionId, identity.Sha256, plan.ManifestSha256);
            var releasedTag = await bucket.WriteMarkerAsync(target.Bucket, BundleMarker.Key,
                    BundleMarker.Serialize(BundleMarker.Released(deployed)), claimTag)
                ?? throw new BundleDeployRaced(
                    $"this execution's lease on {target.Bucket} was taken by another while it deployed, so both wrote to the app. " +
                    "What the bucket holds is not known; the newer build's deploy, or a new execution, settles it.");

            var puts = plan.Assets.Concat(plan.Manifests).ToList();
            return new JsonObject
            {
                ["bucket"] = target.Bucket,
                ["keyPrefix"] = plan.KeyPrefix,
                ["basePath"] = plan.BasePath,
                ["manifestSha256"] = plan.ManifestSha256,
                ["files"] = files.Count,
                ["bytes"] = plan.Bytes,
                ["put"] = new JsonObject
                {
                    ["assets"] = plan.Assets.Count,
                    ["manifests"] = plan.Manifests.Count,
                    ["absent"] = puts.Count(p => p.Reason == "absent"),
                    ["content"] = puts.Count(p => p.Reason == "content"),
                    ["headers"] = puts.Count(p => p.Reason == "headers"),
                },
                ["deleted"] = plan.Deletes.Count,
                ["unchanged"] = plan.Unchanged,
                ["invalidations"] = created,
                ["ordering"] = ordering,
                ["marker"] = new JsonObject { ["key"] = BundleMarker.Key, ["etag"] = releasedTag },
            };
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // /tmp outlives an invocation only within one execution environment, and the next run of this step clears it.
            }
        }
    }

    private static Task Put(IAppBucket bucket, string bucketName, MirrorPut put, string path)
        => bucket.PutAsync(bucketName, put.Key, path, put.Headers, DeployVerification.Sha256Base64(put.File.Sha256Hex));

    internal static async Task<IReadOnlyList<TResult>> ForEachAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, Task<TResult>> action)
    {
        var results = new TResult[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            async (i, _) => results[i] = await action(items[i]));
        return results;
    }

    private static Task ForEachAsync<TItem>(IReadOnlyList<TItem> items, Func<TItem, Task> action)
        => Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Concurrency }, async (item, _) => await action(item));
}

/// <summary>
/// The VerifyBundle state (§4.7, class 2): the invalidations finished, the marker names this deploy, and every object under
/// the app's prefix, read back, is the bundle — by S3's own SHA-256 and by its headers.
/// </summary>
public static class VerifyBundleStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IAppBucket bucket, IInvalidations invalidations, DateTimeOffset now)
    {
        var (_, target, _) = BundleState.Verified(state, "VerifyBundle");
        var verified = (JsonObject)state["verified"]!;
        var record = DeployerInput.RecordFrom(state);
        var repo = (verified["builtFrom"] as JsonObject)?["repo"]?.GetValue<string>()
            ?? throw new DeployRefused("verified", "the state has no verified.builtFrom.repo.");

        var deploy = state["deploy"] as JsonObject
            ?? throw new DeployRefused("deploy", "the state has no DeployBundle result; there is nothing to verify.");
        var manifest = deploy["manifestSha256"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m
            : throw new DeployRefused("deploy", "the state has no deploy.manifestSha256.");
        var fileCount = deploy["files"] is JsonValue fv && fv.TryGetValue<int>(out var f) ? f
            : throw new DeployRefused("deploy", "the state has no deploy.files.");

        // 1. THE CACHE CLEARED, first and cheaply, so waiting for it does not re-read the whole app each time.
        var statuses = new JsonArray();
        foreach (var node in deploy["invalidations"] as JsonArray ?? new JsonArray())
        {
            var distribution = node?["distribution"]?.GetValue<string>();
            var id = node?["id"]?.GetValue<string>();
            if (distribution is null || id is null)
                throw new DeployRefused("deploy.invalidations", "an invalidation in the state names no distribution or id.");

            var status = await invalidations.StatusAsync(distribution, id);
            if (!string.Equals(status, "Completed", StringComparison.Ordinal))
                throw new BundleInvalidationInProgress($"invalidation {id} of {target.InvalidationPath} on {distribution} is {status ?? "unreadable"}.");
            statuses.Add(new JsonObject { ["distribution"] = distribution, ["id"] = id, ["status"] = status });
        }

        if (statuses.Count != target.Distributions.Count)
            throw new BundleNotDeployed(
                $"the deploy invalidated {statuses.Count} distribution(s), and the app is served by {target.Distributions.Count}.");

        // 2. THE MARKER NAMES THIS DEPLOY, before and after the reads, so nothing wrote to the app while it was read.
        var before = await RequireOursAsync(bucket, target, executionName, manifest);

        // 3. EVERY OBJECT, READ BACK. Exit codes are not evidence; these reads are.
        var keys = await bucket.ListAsync(target.Bucket, target.KeyPrefix);
        var objects = (await DeployBundleStep.ForEachAsync(keys, key => bucket.HeadAsync(target.Bucket, key))).OfType<StoredObject>().ToList();

        var problems = new List<string>();
        var unhashed = objects.Where(o => o.Sha256Hex is null).Select(o => o.Key).ToList();
        if (unhashed.Count > 0)
            problems.Add($"{unhashed.Count} object(s) carry no SHA-256 of their own, e.g. {string.Join(", ", unhashed.Take(3))}");

        var paths = objects.Select(o => o.Key[WebappSyncRules.StoragePrefix.Length..]).ToList();
        var readBack = WebappSyncRules.ManifestSha256(objects.Where(o => o.Sha256Hex != null)
            .Select(o => (o.Key[WebappSyncRules.StoragePrefix.Length..], o.Sha256Hex!)));
        if (unhashed.Count == 0 && (objects.Count != fileCount || !string.Equals(readBack, manifest, StringComparison.Ordinal)))
            problems.Add(
                $"the {objects.Count} object(s) under {target.KeyPrefix} hash to manifest {readBack}, not the bundle's {manifest} " +
                $"({fileCount} files): something is missing, extra or different");

        var rules = new WebappHeaderRules(target.BasePath, paths);
        var wrongHeaders = objects
            .Select(o => (Object: o, Expected: rules.For(o.Key[WebappSyncRules.StoragePrefix.Length..])))
            .Where(x => !WebappSyncRules.Carries(x.Expected, x.Object.CacheControl, x.Object.ContentType, x.Object.ContentEncoding))
            .ToList();
        if (wrongHeaders.Count > 0)
            problems.Add($"{wrongHeaders.Count} object(s) carry headers other than the bundle's rules give, e.g. " +
                         string.Join("; ", wrongHeaders.Take(3).Select(x =>
                             $"{x.Object.Key} is '{x.Object.CacheControl}' {x.Object.ContentType} {x.Object.ContentEncoding ?? "(no encoding)"}, " +
                             $"not '{x.Expected.CacheControl}' {x.Expected.ContentType} {x.Expected.ContentEncoding ?? "(no encoding)"}")));

        if (problems.Count > 0)
            throw new BundleNotDeployed($"s3://{target.Bucket}/{target.KeyPrefix} is not the bundle this execution deployed: " +
                                        string.Join("; ", problems) + ".");

        var after = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key);
        if (!string.Equals(after?.ETag, before.ETag, StringComparison.Ordinal))
            throw new BundleNotDeployed(
                $"the app's deploy marker changed while its objects were read back, so another deploy wrote to {target.Bucket} " +
                "meanwhile and what was read cannot be trusted as this deploy's.");

        var verification = new JsonObject
        {
            ["objects"] = objects.Count,
            ["manifestSha256"] = readBack,
            ["invalidations"] = statuses,
            ["marker"] = new JsonObject { ["key"] = BundleMarker.Key, ["etag"] = before.ETag },
        };

        return new JsonObject
        {
            ["verdict"] = "Landed",
            ["objects"] = objects.Count,
            ["manifestSha256"] = readBack,
            ["evidence"] = new JsonObject
            {
                ["key"] = DeployEvidence.DeployedKey("client", repo, executionName),
                ["body"] = DeployEvidence.BundleDeployed(executionName, now, record, verified, deploy, verification),
            },
        };
    }

    private static async Task<MarkerRead> RequireOursAsync(IAppBucket bucket, ClientTarget target, string executionName, string manifest)
    {
        var read = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key)
            ?? throw new BundleNotDeployed($"{target.Bucket} has no deploy marker, so nothing records that this execution deployed it.");

        var marker = BundleMarker.Parse(read.Json);
        if (marker.Deployed is not { } deployed
            || !string.Equals(deployed.Execution, executionName, StringComparison.Ordinal)
            || !string.Equals(deployed.ManifestSha256, manifest, StringComparison.Ordinal))
            throw new BundleNotDeployed(
                $"the app's deploy marker records {(marker.Deployed is { } d ? $"execution {d.Execution}'s build {d.BuiltAt}" : "no deployed build")}, " +
                "not this execution's: a later deploy superseded it.");

        if (marker.Claim is { } claim)
            throw new BundleNotDeployed(
                $"execution {claim.Execution} has claimed the app's deploy lease since, for build {claim.BuiltAt}; the objects are changing.");

        return read;
    }
}

/// <summary>What Verify established for a client bundle, read back out of an execution's state.</summary>
internal static class BundleState
{
    public sealed record Identity(string Bucket, string Key, string VersionId, string Sha256);

    public static (Identity Identity, ClientTarget Target, string BuiltAt) Verified(JsonObject state, string step)
    {
        var verified = state["verified"] as JsonObject
            ?? throw new DeployRefused("verified", $"the state has no Verify result; {step} cannot run before Verify.");

        if (verified["class"]?.GetValue<string>() != "client")
            throw new DeployRefused("verified.class", $"{step} deploys class 'client'; the verified record is not one.");

        ClientTarget target;
        try
        {
            target = ClientTargets.FromJson(verified["target"]);
        }
        catch (InvalidOperationException ex)
        {
            throw new DeployRefused("verified.target", ex.Message);
        }

        string Text(JsonNode? o, string name)
            => o is JsonObject obj && obj[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s
                ? s
                : throw new DeployRefused("verified", $"the state has no verified {name}; {step} acts only on what Verify checked.");

        var identity = verified["identity"];
        return (new Identity(Text(identity, "bucket"), Text(identity, "key"), Text(identity, "versionId"), Text(identity, "sha256")),
            target, Text(verified, "builtAt"));
    }
}
