using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// The gate cannot find what it runs: no tests record, one written before Gate 1's lists were recorded, no browser tests, or a
/// client's gate while the service runs two images. Not a fault and not a decision: Gate2Start answers <c>incomplete</c>, and the
/// result state records TestsIncomplete with this reason, which is alerted.
/// </summary>
public sealed class Gate2NotResolvable(string message) : Exception(message);

/// <summary>The keys under a prefix of the build-record store, which the build account lets Gate2Start list.</summary>
public interface IGate2RecordList
{
    Task<IReadOnlyList<string>> KeysAsync(string bucket, string prefix);
}

/// <summary>A GET URL for one object VERSION, signed by Gate2Start's role, which the build account lets read it.</summary>
public interface IGate2Presigner
{
    string GetUrl(string bucket, string key, string versionId, TimeSpan lifetime);
}

/// <summary>
/// WHAT GATE 2 RUNS, FROM THE BUILD RECORDS (§15.5). The e2e tier: the tests record of the image member that runs — for an
/// image's gate the one the same run attempt wrote, at its record's key under <c>tests/</c>; for a client's, the running image's,
/// found by digest in this environment's deploy evidence. The browser tier: the newest tests record under the browser tests'
/// prefix. Each bundle by its object version, presigned for <see cref="UrlLifetime"/>, with its SHA-256, which the build checks;
/// the lists and the <c>@gate</c> count are the ones Gate 1 recorded on those records.
/// </summary>
public sealed class Gate2RecordBundles(
    IRecordStore records, IGate2RecordList list, IGate2EvidenceReader evidence, IGate2Presigner presigner,
    string buildRecordStore, string artifactStore, string evidenceBucket, string browserPrefix) : IGate2Bundles
{
    /// <summary>Long enough for the build to start and download both bundles; the build checks each one's SHA-256.</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(15);

    public async Task<Gate2Bundles> ResolveAsync(JsonObject state, Gate2Running running)
    {
        var e2eKey = await TestsKeyAsync(state, running);
        var (e2eRecord, e2eUrl) = await ReadAsync(e2eKey, "the e2e tier's");
        var e2eLists = e2eRecord.Intake?.Gate1.Lists
            ?? throw new Gate2NotResolvable($"{e2eKey} carries no Gate 1 lists: it was written before Gate 1's lists were recorded on tests records, so what ran cannot be held to them. A new build of it can be gated.");

        var browserKey = (await list.KeysAsync(buildRecordStore, browserPrefix)).Where(k => k.EndsWith(".json", StringComparison.Ordinal))
            .OrderByDescending(k => k, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new Gate2NotResolvable($"no browser tests record under {browserPrefix}: the browser tier has nothing to run");
        var (browserRecord, browserUrl) = await ReadAsync(browserKey, "the browser tier's");
        var gateCount = browserRecord.Intake?.Gate1.Lists?.GateCount
            ?? throw new Gate2NotResolvable($"{browserKey} carries no @gate count from its Gate 1, so the browser tier's result cannot be held to one");

        return new Gate2Bundles(
            new Dictionary<string, string>
            {
                ["GATE2_E2E_BUNDLE_URL"] = e2eUrl, ["GATE2_E2E_BUNDLE_SHA256"] = e2eRecord.Identity.Sha256!,
                ["GATE2_BROWSER_BUNDLE_URL"] = browserUrl, ["GATE2_BROWSER_BUNDLE_SHA256"] = browserRecord.Identity.Sha256!,
            },
            new Gate2Recorded(e2eLists.Precondition, e2eLists.Blocking, e2eLists.Advisory, gateCount));
    }

    /// <summary>The tests record of the image member that runs.</summary>
    private async Task<string> TestsKeyAsync(JsonObject state, Gate2Running running)
    {
        if (state["verified"]?["class"]?.GetValue<string>() != "client")
        {
            var key = state["record"]?["key"]?.GetValue<string>() ?? throw new Gate2NotResolvable("the state names no record");
            return ToTests(key);
        }

        var digests = running.Digests.Values.SelectMany(d => d).Distinct().ToList();
        if (digests.Count != 1 || digests[0] is not { } digest)
            throw new Gate2NotResolvable($"the services run more than one image ({string.Join(", ", digests.Select(d => d ?? "(none)"))}), so a client's gate cannot name its image member");

        foreach (var key in (await evidence.KeysAsync(evidenceBucket, "deploys/image/")).OrderByDescending(k => k, StringComparer.Ordinal))
        {
            if (await evidence.ReadAsync(evidenceBucket, key) is not { } body) continue;
            var deployed = JsonNode.Parse(body);
            if (deployed?["verified"]?["digest"]?.GetValue<string>() == digest && deployed["record"]?["key"]?.GetValue<string>() is { } record)
                return ToTests(record);
        }
        throw new Gate2NotResolvable($"no deploy of {digest} is in this environment's evidence, so the image it runs reached it outside the pipeline");
    }

    private static string ToTests(string recordKey)
    {
        var slash = recordKey.IndexOf('/');
        if (slash < 0) throw new Gate2NotResolvable($"{recordKey} is not a record key");
        return $"tests{recordKey[slash..]}";
    }

    private async Task<(BuildRecord Record, string Url)> ReadAsync(string key, string what)
    {
        var stored = await records.ReadAsync(buildRecordStore, key)
            ?? throw new Gate2NotResolvable($"{what} tests record {key} does not exist");
        BuildRecord record;
        try { record = BuildRecordFormat.Parse(stored.Json, artifactStore); }
        catch (InvalidOperationException ex) { throw new Gate2NotResolvable($"{key} is not a record Gate 2 can read: {ex.Message}"); }
        if (record.Class != "tests" || record.Identity is not { Bucket: { } bucket, Key: { } zip, VersionId: { } version, Sha256: { } })
            throw new Gate2NotResolvable($"{key} is not a tests record naming its bundle by version and SHA-256");
        return (record, presigner.GetUrl(bucket, zip, version, UrlLifetime));
    }
}
