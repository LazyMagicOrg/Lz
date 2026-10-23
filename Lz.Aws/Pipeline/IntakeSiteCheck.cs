using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lz.Aws.Webapp;

namespace Lz.Aws.Pipeline;

/// <summary>
/// A SITE'S GATE 1, DECIDED (DecoupledCd.md V-24, §15.7 V-K): each environment's bundle the fetch staged, read back from the
/// scratch bucket and held to the SHA-256 the fetch recorded, expanded in memory, and judged by <see cref="SiteCheck"/>. The
/// verdict is written once to the intake's evidence under <c>gate1/{executionName}</c>, in the shape a test bundle's is,
/// BINDING THE BYTES IT RAN FOR: every staged artifact's SHA-256. A refusal is decided (<c>failed</c>), recorded and alerted,
/// like a failed test; bytes that changed in the scratch bucket are a fault.
/// </summary>
public static class IntakeSiteCheckStep
{
    /// <summary>The most one environment's site may expand to: a Hugo site is a few megabytes, and this one is read in memory.</summary>
    public const long MaxExpandedBytes = 200L * 1024 * 1024;

    public static async Task<JsonObject> RunAsync(
        JsonObject state, string executionName, IReadOnlyList<IntakeRepository> repositories, IScratchReader scratch,
        IIntakeEvidenceStore evidence, string workDirectory, DateTimeOffset now)
    {
        var input = IntakeInput.From(state);
        var site = input.In(repositories)?.Site ?? throw new IntakeFault($"{input.Repo} has no site check; the machine should not have run one");
        var fetch = state["fetch"] as JsonObject ?? throw new IntakeFault("nothing was staged");
        var staged = (fetch["sites"] as JsonArray ?? throw new IntakeFault("the fetch staged no site bundle")).OfType<JsonObject>().ToList();

        Directory.CreateDirectory(workDirectory);
        var bundles = new List<SiteBundle>();
        var refusals = new List<string>();
        foreach (var entry in staged)
        {
            string Text(string name) => entry[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new IntakeFault($"a staged site names no {name}");
            var path = Path.Combine(workDirectory, Path.GetFileName(Text("key")));
            try
            {
                await scratch.GetFileAsync(Text("key"), path);
                if (await Sha256Async(path) != Text("sha256"))
                    throw new IntakeFault($"{Text("key")} no longer hashes to {Text("sha256")}, the bytes the fetch staged");
                try
                {
                    bundles.Add(new SiteBundle(Text("environment"), Expand(path)));
                }
                catch (InvalidOperationException ex)
                {
                    refusals.Add($"{Text("environment")}: {ex.Message}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        var result = refusals.Count == 0
            ? SiteCheck.Check(bundles, new SiteCheckSettings(site.Indexed, site.ThirdPartyHosts))
            : new SiteCheckResult(false, refusals, new Dictionary<string, string>());
        var summary = result.Passed
            ? $"the site check passed {bundles.Count} bundle(s): {string.Join(", ", result.Hosts.Select(h => $"{h.Key} {h.Value}"))}"
            : $"the site check refused: {result.Reasons.Count} reason(s)";

        var verdict = new JsonObject
        {
            ["verdict"] = result.Passed ? "passed" : "failed",
            ["kind"] = "site",
            ["reasons"] = result.Passed
                ? null
                : new JsonArray(new[] { "Gate 1 failed: the site check refused" }.Concat(result.Reasons).Select(r => (JsonNode?)r).ToArray()),
            ["summary"] = summary,
            ["hosts"] = new JsonObject(result.Hosts.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
            ["manifestDigest"] = null,
            ["artifactSha256"] = new JsonObject((fetch["staged"] as JsonArray ?? new JsonArray())
                .Select(a => KeyValuePair.Create(a!["artifact"]!.GetValue<string>(), (JsonNode?)a["sha256"]!.GetValue<string>()))),
            ["at"] = now.ToString("O"),
        };
        await evidence.PutOnceAsync(IntakeEvidence.Gate1(executionName), verdict.ToJsonString());   // there already: a retry of this step
        return verdict;
    }

    /// <summary>
    /// The site inside an artifact: its one <c>bundle.zip</c>, every file of it by path. Refused when an entry could not be
    /// mirrored (the deploy's own rule) or the site expands past <see cref="MaxExpandedBytes"/>.
    /// </summary>
    private static Dictionary<string, byte[]> Expand(string artifactPath)
    {
        using var outer = ZipFile.OpenRead(artifactPath);
        if (outer.Entries.Count != 1 || outer.Entries[0].FullName != IntakeFetchStep.BundleEntry)
            throw new InvalidOperationException($"the artifact holds {string.Join(", ", outer.Entries.Select(e => e.FullName))}, not one {IntakeFetchStep.BundleEntry}");
        using var inner = new MemoryStream();
        using (var s = outer.Entries[0].Open()) s.CopyTo(inner);
        inner.Position = 0;

        using var bundle = new ZipArchive(inner, ZipArchiveMode.Read);
        var refusals = WebappMirrorPlanner.RefusalsForEntryNames(bundle.Entries.Select(e => e.FullName));
        if (refusals.Count > 0) throw new InvalidOperationException("the site cannot be mirrored: " + string.Join("; ", refusals));

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in bundle.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var data = new MemoryStream();
            using (var s = entry.Open())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxExpandedBytes) throw new InvalidOperationException($"the site expands past {MaxExpandedBytes} bytes");
                    data.Write(buffer, 0, read);
                }
            }
            files[entry.FullName] = data.ToArray();
        }
        return files;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
    }
}
