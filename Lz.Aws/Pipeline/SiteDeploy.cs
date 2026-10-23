using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lz.Aws.Webapp;

namespace Lz.Aws.Pipeline;

// ---------------------------------------------------------------------------------------------
//  CLASS 3 — THE SITE (DecoupledCd.md V-24, §15.7 V-K).
//
//  One bundle per environment, each built for that environment's domain. Verify checks a site record, that it was built for
//  THIS environment, and HEADs its zip; DeploySite mirrors the zip into wwwroot/ of the environment's website bucket under
//  the same lease DeployBundle takes (lz/deploy.json), after holding the bundle's canonical host to the environment's domain;
//  VerifySite reads every object back. It differs from a client bundle in three ways, each deliberate: NO INVALIDATION (the
//  host-keyed cache policy honours Cache-Control, so a page converges within its 60 s), the BUCKET IS NOT CREATED OR ITS
//  POLICY WRITTEN (it is the tenant's, made by `lz bootstrapwebsiteci`, and read by CloudFront under a policy this step has no
//  reason to restate), and TWO CACHE CLASSES (HTML 60 s, everything else an hour), the split the tag-driven publish used.
//  BCL only: the deployer's Lambda package compiles this file by link.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The site this environment deploys, resolved from configuration: the site repository, this environment's name (a record
/// must have been built for it), the tenant's website bucket, and the tenant's root domain, which the bundle's canonical URLs
/// must name.
/// </summary>
public sealed record SiteTarget(string Repo, string Environment, string Bucket, string Domain)
{
    /// <summary>Everything the site serves is under it; the deploy marker is outside it.</summary>
    public string KeyPrefix => WebappSyncRules.StoragePrefix;

    /// <summary>The records a route starts deploys for: this repository's, for this environment only.</summary>
    public string RecordPrefix => $"{BuildRecordFormat.PrefixFor(BuildRecordFormat.SiteClass, Repo)}{Environment}/";
}

/// <summary>Site targets as a function's environment and an execution's state carry them.</summary>
public static class SiteTargets
{
    private static readonly Regex Bucket = new(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex Domain = new(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$", RegexOptions.CultureInvariant);

    public static string Encode(IReadOnlyList<SiteTarget> targets)
        => new JsonArray(targets.Select(t => (JsonNode?)ToJson(t)).ToArray()).ToJsonString();

    public static IReadOnlyList<SiteTarget> Decode(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{DeployerEnvironment.SiteTargets} is not valid JSON: {ex.Message}");
        }
        return (node as JsonArray ?? throw new InvalidOperationException($"{DeployerEnvironment.SiteTargets} is not a JSON array."))
            .Select(FromJson).ToList();
    }

    public static JsonObject ToJson(SiteTarget t) => new()
    {
        ["repo"] = t.Repo, ["environment"] = t.Environment, ["bucket"] = t.Bucket, ["domain"] = t.Domain, ["keyPrefix"] = t.KeyPrefix,
    };

    public static SiteTarget FromJson(JsonNode? node)
    {
        var o = node as JsonObject ?? throw new InvalidOperationException("a site target is not a JSON object.");
        string Text(string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : throw new InvalidOperationException($"a site target has no {name}.");
        var target = new SiteTarget(Text("repo"), Text("environment"), Text("bucket"), Text("domain"));
        if (!Regex.IsMatch(target.Environment, BuildRecordFormat.EnvironmentPattern))
            throw new InvalidOperationException($"site target environment '{target.Environment}' is not an environment's name.");
        if (!Bucket.IsMatch(target.Bucket)) throw new InvalidOperationException($"site target bucket '{target.Bucket}' is not a bucket name.");
        if (!Domain.IsMatch(target.Domain)) throw new InvalidOperationException($"site target domain '{target.Domain}' is not a domain.");
        if (o["keyPrefix"] is not null && Text("keyPrefix") != target.KeyPrefix)
            throw new InvalidOperationException($"site target '{target.Bucket}' names keyPrefix '{Text("keyPrefix")}', not {target.KeyPrefix}.");
        return target;
    }
}

/// <summary>
/// The headers a site's objects carry (V-24): the type by extension, as a client bundle's, and two cache lifetimes — a page a
/// minute, everything else an hour — which CloudFront honours, so no deploy invalidates.
/// </summary>
public static class SiteSyncRules
{
    public const string PageCacheControl = "public, max-age=60";
    public const string AssetCacheControl = "public, max-age=3600";

    public static bool IsPage(string path)
        => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    public static WebappObjectHeaders For(string path)
        => new(IsPage(path) ? PageCacheControl : AssetCacheControl, WebappSyncRules.ContentTypeFor(path), null);

    /// <summary>
    /// The mirror of a site into <c>wwwroot/</c>: what is new or changed (bytes or headers) is put, assets before pages, so no
    /// page a browser reads names an asset not there yet; then what the site lacks is deleted. The pages ride in the plan's
    /// <c>Manifests</c>, which DeploySite writes second.
    /// </summary>
    public static MirrorPlan Plan(IReadOnlyList<BundleFile> site, IReadOnlyList<StoredObject> stored)
    {
        if (site.Count == 0)
            throw new InvalidOperationException("the site holds no files; mirroring it would delete the site.");
        var prefix = WebappSyncRules.StoragePrefix;
        var strays = stored.Where(o => !o.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(o => o.Key).Take(3).ToList();
        if (strays.Count > 0)
            throw new InvalidOperationException(
                $"the bucket's listing holds keys outside '{prefix}' ({string.Join(", ", strays)}), so it was not listed under the " +
                "site's prefix and cannot decide what to delete.");

        var byKey = stored.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var assets = new List<MirrorPut>();
        var pages = new List<MirrorPut>();
        var unchanged = 0;
        foreach (var file in site.OrderBy(f => f.Path, WebappSyncRules.Utf8Ordinal.Instance))
        {
            var key = prefix + file.Path;
            var headers = For(file.Path);
            string? reason = !byKey.TryGetValue(key, out var current) ? "absent"
                : !string.Equals(current.Sha256Hex, file.Sha256Hex, StringComparison.Ordinal) ? "content"
                : !WebappSyncRules.Carries(headers, current.CacheControl, current.ContentType, current.ContentEncoding) ? "headers"
                : null;
            if (reason is null) { unchanged++; continue; }
            (IsPage(file.Path) ? pages : assets).Add(new MirrorPut(key, file, headers, reason));
        }

        var keys = site.Select(f => prefix + f.Path).ToHashSet(StringComparer.Ordinal);
        var deletes = stored.Select(o => o.Key).Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new MirrorPlan("", prefix, assets, pages, deletes, unchanged,
            WebappSyncRules.ManifestSha256(site.Select(f => (f.Path, f.Sha256Hex))), site.Sum(f => f.Size));
    }
}

/// <summary>
/// The DeploySite state (V-24): the verified bundle, held to the environment's domain, mirrored into its website bucket under
/// the deploy lease. Nothing is invalidated and the bucket is neither created nor re-policied.
/// </summary>
public static class DeploySiteStep
{
    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, string workRoot, IArtifactObjects artifacts, IAppBucket bucket, Func<DateTimeOffset> clock)
    {
        var (identity, target, builtAt) = SiteState.Verified(state, "DeploySite");
        var allowOlder = DeployerInput.AllowsOlderBuild(state);

        // 1. THE MARKER, BEFORE ANYTHING IS READ OR WRITTEN.
        var read = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key);
        var marker = read is null ? null : BundleMarker.Parse(read.Json);
        var (verdict, ordering) = BundleMarker.Decide(marker, builtAt, executionName, clock(), allowOlder);
        switch (verdict)
        {
            case BundleMarker.Verdict.Proceed: break;
            case BundleMarker.Verdict.Superseded: throw new DeploySuperseded(ordering);
            default: throw new BundleDeployInProgress(ordering);
        }

        var work = Path.Combine(workRoot, DeployEvidence.RequireSafeExecutionName(executionName));
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            // 2. THE BYTES VERIFY CHECKED, hashed again as read.
            var zipPath = Path.Combine(work, "site.zip");
            var (servedVersion, _) = await artifacts.DownloadAsync(identity.Bucket, identity.Key, identity.VersionId, zipPath);
            if (!string.Equals(servedVersion, identity.VersionId, StringComparison.Ordinal))
                throw new DeployRefused("identity.versionId",
                    $"asked for version {identity.VersionId} of s3://{identity.Bucket}/{identity.Key}, S3 served '{servedVersion}'. Nothing was written.");
            var zipSha256 = await BundleArchive.Sha256Async(zipPath);
            if (!string.Equals(zipSha256, identity.Sha256, StringComparison.Ordinal))
                throw new DeployRefused("identity.sha256",
                    $"the bytes read from s3://{identity.Bucket}/{identity.Key} hash to {zipSha256}, not the {identity.Sha256} Verify checked. Nothing was written.");

            var filesDirectory = Path.Combine(work, "files");
            var files = await BundleArchive.ExpandAsync(zipPath, filesDirectory);

            // 3. BUILT FOR THIS DOMAIN, judged before the claim: the home page's canonical host is the one the site's absolute
            // URLs carry, and a bundle built for another environment's domain would send its visitors there.
            var index = Path.Combine(filesDirectory, "index.html");
            var host = File.Exists(index)
                ? SiteCheck.HostOf(new Dictionary<string, byte[]> { ["index.html"] = await File.ReadAllBytesAsync(index) })
                : null;
            if (!string.Equals(host, target.Domain, StringComparison.OrdinalIgnoreCase))
                throw new DeployRefused("bundle",
                    $"the site's index.html names {(host is null ? "no canonical host" : $"host {host}")}, and {target.Environment} serves " +
                    $"{target.Domain}. It was built for another domain. Nothing was written.");

            // 4. THE CLAIM, conditional on the marker being what step 1 read.
            var claimTag = await bucket.WriteMarkerAsync(target.Bucket, BundleMarker.Key,
                    BundleMarker.Serialize(BundleMarker.Claimed(marker, builtAt, executionName, clock())), read?.ETag)
                ?? throw new BundleDeployInProgress(
                    "another execution wrote the site's deploy marker between this one's read and its claim. Nothing of the site was " +
                    "written; the marker is read again when this is retried.");

            // 5. WHAT THE BUCKET HOLDS NOW, read under the claim.
            var keys = await bucket.ListAsync(target.Bucket, target.KeyPrefix);
            var stored = (await DeployBundleStep.ForEachAsync(keys, key => bucket.HeadAsync(target.Bucket, key))).OfType<StoredObject>().ToList();
            MirrorPlan plan;
            try
            {
                plan = SiteSyncRules.Plan(files, stored);
            }
            catch (InvalidOperationException ex)
            {
                throw new DeployRefused("bundle", ex.Message);
            }

            // 6. ASSETS, THEN PAGES, THEN DELETES.
            string FileFor(MirrorPut put) => Path.Combine(filesDirectory, put.File.Path);
            await ForEachAsync(plan.Assets, put => Put(bucket, target.Bucket, put, FileFor(put)));
            await ForEachAsync(plan.Manifests, put => Put(bucket, target.Bucket, put, FileFor(put)));
            if (plan.Deletes.Count > 0)
                await bucket.DeleteAsync(target.Bucket, plan.Deletes);

            // 7. THE RELEASE, conditional on the claim still being this execution's.
            var deployed = new BundleMarker.DeployedBuild(
                builtAt, executionName, BundleMarker.Utc(clock()), identity.Key, identity.VersionId, identity.Sha256, plan.ManifestSha256);
            var releasedTag = await bucket.WriteMarkerAsync(target.Bucket, BundleMarker.Key,
                    BundleMarker.Serialize(BundleMarker.Released(deployed)), claimTag)
                ?? throw new BundleDeployRaced(
                    $"this execution's lease on {target.Bucket} was taken by another while it deployed, so both wrote to the site. What " +
                    "the bucket holds is not known; the newer build's deploy, or a new execution, settles it.");

            var puts = plan.Assets.Concat(plan.Manifests).ToList();
            return new JsonObject
            {
                ["bucket"] = target.Bucket,
                ["keyPrefix"] = plan.KeyPrefix,
                ["domain"] = target.Domain,
                ["manifestSha256"] = plan.ManifestSha256,
                ["files"] = files.Count,
                ["bytes"] = plan.Bytes,
                ["put"] = new JsonObject
                {
                    ["assets"] = plan.Assets.Count,
                    ["pages"] = plan.Manifests.Count,
                    ["absent"] = puts.Count(p => p.Reason == "absent"),
                    ["content"] = puts.Count(p => p.Reason == "content"),
                    ["headers"] = puts.Count(p => p.Reason == "headers"),
                },
                ["deleted"] = plan.Deletes.Count,
                ["unchanged"] = plan.Unchanged,
                ["invalidations"] = new JsonArray(),
                ["ordering"] = ordering,
                ["marker"] = new JsonObject { ["key"] = BundleMarker.Key, ["etag"] = releasedTag },
            };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* the next run of this step clears it */ }
        }
    }

    private static Task Put(IAppBucket bucket, string bucketName, MirrorPut put, string path)
        => bucket.PutAsync(bucketName, put.Key, path, put.Headers, DeployVerification.Sha256Base64(put.File.Sha256Hex));

    private static Task ForEachAsync<TItem>(IReadOnlyList<TItem> items, Func<TItem, Task> action)
        => Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = DeployBundleStep.Concurrency }, async (item, _) => await action(item));
}

/// <summary>
/// The VerifySite state (V-24): the marker names this deploy, and every object under <c>wwwroot/</c>, read back, is the site —
/// by S3's own SHA-256 and by the headers <see cref="SiteSyncRules"/> gives it.
/// </summary>
public static class VerifySiteStep
{
    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, IAppBucket bucket, DateTimeOffset now)
    {
        var (_, target, _) = SiteState.Verified(state, "VerifySite");
        var verified = (JsonObject)state["verified"]!;
        var record = DeployerInput.RecordFrom(state);
        var deploy = state["deploy"] as JsonObject
            ?? throw new DeployRefused("deploy", "the state has no DeploySite result; there is nothing to verify.");
        var manifest = deploy["manifestSha256"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m
            : throw new DeployRefused("deploy", "the state has no deploy.manifestSha256.");
        var fileCount = deploy["files"] is JsonValue fv && fv.TryGetValue<int>(out var f) ? f
            : throw new DeployRefused("deploy", "the state has no deploy.files.");

        var before = await RequireOursAsync(bucket, target, executionName, manifest);

        var keys = await bucket.ListAsync(target.Bucket, target.KeyPrefix);
        var objects = (await DeployBundleStep.ForEachAsync(keys, key => bucket.HeadAsync(target.Bucket, key))).OfType<StoredObject>().ToList();

        var problems = new List<string>();
        var unhashed = objects.Where(o => o.Sha256Hex is null).Select(o => o.Key).ToList();
        if (unhashed.Count > 0)
            problems.Add($"{unhashed.Count} object(s) carry no SHA-256 of their own, e.g. {string.Join(", ", unhashed.Take(3))}");
        var readBack = WebappSyncRules.ManifestSha256(objects.Where(o => o.Sha256Hex != null)
            .Select(o => (o.Key[target.KeyPrefix.Length..], o.Sha256Hex!)));
        if (unhashed.Count == 0 && (objects.Count != fileCount || !string.Equals(readBack, manifest, StringComparison.Ordinal)))
            problems.Add($"the {objects.Count} object(s) under {target.KeyPrefix} hash to manifest {readBack}, not the site's {manifest} " +
                         $"({fileCount} files): something is missing, extra or different");

        var wrongHeaders = objects
            .Select(o => (Object: o, Expected: SiteSyncRules.For(o.Key[target.KeyPrefix.Length..])))
            .Where(x => !WebappSyncRules.Carries(x.Expected, x.Object.CacheControl, x.Object.ContentType, x.Object.ContentEncoding))
            .ToList();
        if (wrongHeaders.Count > 0)
            problems.Add($"{wrongHeaders.Count} object(s) carry headers other than the site's rules give, e.g. " +
                         string.Join("; ", wrongHeaders.Take(3).Select(x =>
                             $"{x.Object.Key} is '{x.Object.CacheControl}' {x.Object.ContentType}, not '{x.Expected.CacheControl}' {x.Expected.ContentType}")));

        if (problems.Count > 0)
            throw new BundleNotDeployed($"s3://{target.Bucket}/{target.KeyPrefix} is not the site this execution deployed: " +
                                        string.Join("; ", problems) + ".");

        var after = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key);
        if (!string.Equals(after?.ETag, before.ETag, StringComparison.Ordinal))
            throw new BundleNotDeployed(
                $"the site's deploy marker changed while its objects were read back, so another deploy wrote to {target.Bucket} meanwhile " +
                "and what was read cannot be trusted as this deploy's.");

        var repo = (verified["builtFrom"] as JsonObject)?["repo"]?.GetValue<string>()
            ?? throw new DeployRefused("verified", "the state has no verified.builtFrom.repo.");
        var verification = new JsonObject
        {
            ["objects"] = objects.Count,
            ["manifestSha256"] = readBack,
            ["domain"] = target.Domain,
            ["marker"] = new JsonObject { ["key"] = BundleMarker.Key, ["etag"] = before.ETag },
        };
        return new JsonObject
        {
            ["verdict"] = "Landed",
            ["objects"] = objects.Count,
            ["manifestSha256"] = readBack,
            ["evidence"] = new JsonObject
            {
                ["key"] = DeployEvidence.DeployedKey(BuildRecordFormat.SiteClass, repo, executionName),
                ["body"] = DeployEvidence.BundleDeployed(executionName, now, record, verified, deploy, verification),
            },
        };
    }

    private static async Task<MarkerRead> RequireOursAsync(IAppBucket bucket, SiteTarget target, string executionName, string manifest)
    {
        var read = await bucket.ReadMarkerAsync(target.Bucket, BundleMarker.Key)
            ?? throw new BundleNotDeployed($"{target.Bucket} has no deploy marker, so nothing records that this execution deployed it.");
        var marker = BundleMarker.Parse(read.Json);
        if (marker.Deployed is not { } deployed
            || !string.Equals(deployed.Execution, executionName, StringComparison.Ordinal)
            || !string.Equals(deployed.ManifestSha256, manifest, StringComparison.Ordinal))
            throw new BundleNotDeployed(
                $"the site's deploy marker records {(marker.Deployed is { } d ? $"execution {d.Execution}'s build {d.BuiltAt}" : "no deployed build")}, " +
                "not this execution's: a later deploy superseded it.");
        if (marker.Claim is { } claim)
            throw new BundleNotDeployed(
                $"execution {claim.Execution} has claimed the site's deploy lease since, for build {claim.BuiltAt}; the objects are changing.");
        return read;
    }
}

/// <summary>What Verify established for a site, read back out of an execution's state.</summary>
internal static class SiteState
{
    public static (BundleState.Identity Identity, SiteTarget Target, string BuiltAt) Verified(JsonObject state, string step)
    {
        var verified = state["verified"] as JsonObject
            ?? throw new DeployRefused("verified", $"the state has no Verify result; {step} cannot run before Verify.");
        if (verified["class"]?.GetValue<string>() != BuildRecordFormat.SiteClass)
            throw new DeployRefused("verified.class", $"{step} deploys class 'site'; the verified record is not one.");

        SiteTarget target;
        try
        {
            target = SiteTargets.FromJson(verified["target"]);
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
        return (new BundleState.Identity(Text(identity, "bucket"), Text(identity, "key"), Text(identity, "versionId"), Text(identity, "sha256")),
            target, Text(verified, "builtAt"));
    }
}
