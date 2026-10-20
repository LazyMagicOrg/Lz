using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>A run artifact as GitHub's artifacts API lists it: only the fields the intake reads.</summary>
public sealed record GitHubArtifact(long Id, string Name, long WorkflowRunId, bool Expired, string? Digest, long SizeInBytes);

/// <summary>The artifact check's answer: each expected artifact found, or every reason it was not.</summary>
public sealed record ArtifactCheckResult(IReadOnlyDictionary<string, GitHubArtifact> Found, IReadOnlyList<string> Reasons)
{
    public bool Passed => Reasons.Count == 0;
}

/// <summary>
/// THE ARTIFACT CHECK (DecoupledCd.md §15.4, Fetch and stage). GitHub lists a run's artifacts across all its attempts,
/// so the build workflow names each one <c>{name}-{runAttempt}</c> and the intake takes only the current attempt's: each
/// expected name must exist exactly once, belong to this run, be unexpired, and carry a <c>sha256:</c> digest, which
/// the fetch then compares with the bytes it downloaded (<see cref="DigestMatches"/>).
/// </summary>
public static class ArtifactCheck
{
    private static readonly Regex Sha256Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>The name the workflow gives one artifact of one attempt.</summary>
    public static string NameFor(string artifact, int runAttempt)
    {
        if (string.IsNullOrWhiteSpace(artifact)) throw new ArgumentException("an artifact has a name", nameof(artifact));
        if (runAttempt < 1) throw new ArgumentOutOfRangeException(nameof(runAttempt), "GitHub numbers attempts from 1");
        return $"{artifact}-{runAttempt}";
    }

    public static ArtifactCheckResult Check(
        long runId, int runAttempt, IReadOnlyList<string> expected, IReadOnlyList<GitHubArtifact> listed)
    {
        var found = new Dictionary<string, GitHubArtifact>(StringComparer.Ordinal);
        var reasons = new List<string>();
        if (expected.Count == 0)
            reasons.Add("the repository's config expects no artifacts, so there is nothing to take in");

        foreach (var artifact in expected)
        {
            var name = NameFor(artifact, runAttempt);
            var matches = listed.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0) { reasons.Add($"no artifact named '{name}' (attempt {runAttempt})"); continue; }
            if (matches.Count > 1) { reasons.Add($"{matches.Count} artifacts named '{name}'; one is expected"); continue; }

            var a = matches[0];
            if (a.WorkflowRunId != runId) { reasons.Add($"'{name}' belongs to run {a.WorkflowRunId}, not {runId}"); continue; }
            if (a.Expired) { reasons.Add($"'{name}' has expired"); continue; }
            if (a.Digest is null || !Sha256Digest.IsMatch(a.Digest))
            { reasons.Add($"'{name}' carries no sha256 digest to compare the bytes with ('{a.Digest ?? "(none)"}')"); continue; }
            found[artifact] = a;
        }

        return new ArtifactCheckResult(found, reasons);
    }

    /// <summary>Whether bytes whose SHA-256 is <paramref name="sha256Hex"/> are the ones GitHub's digest names.</summary>
    public static bool DigestMatches(string? githubDigest, string sha256Hex)
        => githubDigest is not null && Sha256Digest.IsMatch(githubDigest)
           && string.Equals(githubDigest, "sha256:" + sha256Hex.ToLowerInvariant(), StringComparison.Ordinal);
}

/// <summary>A blob an OCI manifest names: its digest, its size and its media type.</summary>
public sealed record OciDescriptor(string MediaType, string Digest, long Size);

/// <summary>A staged image, read and checked: its one manifest's bytes and digest, and the blobs that manifest names.</summary>
public sealed record OciImage(
    string ManifestDigest, byte[] ManifestBytes, string ManifestMediaType,
    OciDescriptor Config, IReadOnlyList<OciDescriptor> Layers, IReadOnlyDictionary<string, long> Blobs);

/// <summary>
/// THE STAGED IMAGE'S PARSER (DecoupledCd.md §15.4, §15.8 item 3): an OCI image layout as a tar archive, which
/// <c>docker buildx --output type=oci</c> writes. Refused unless it is exactly one image: an <c>oci-layout</c> file,
/// an <c>index.json</c> naming exactly one manifest (an index of manifests, which buildx writes when it adds a
/// provenance attestation, is refused, so the build passes <c>--provenance=false</c>), every blob's SHA-256 equal to its
/// name, and the manifest's config and layers present at their declared sizes. The manifest's digest is the image's
/// identity; the push sends these exact manifest bytes, so the digest in ECR is the one the build produced (§2).
/// </summary>
public static class OciArchive
{
    public const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    public const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private static readonly Regex BlobPath = new("^blobs/sha256/([0-9a-f]{64})$", RegexOptions.Compiled);

    public static OciImage Read(Stream tar)
    {
        var blobs = new Dictionary<string, byte[]?>(StringComparer.Ordinal);   // digest -> bytes for small JSON blobs, null for others
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        byte[]? index = null, layout = null;

        using var reader = new TarReader(tar);
        while (reader.GetNextEntry() is { } entry)
        {
            var path = entry.Name.Replace('\\', '/').TrimStart('.', '/');
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw Refuse($"'{path}' is a {entry.EntryType}, and an image layout holds only files and directories");

            if (path == "index.json") { index = ReadAll(entry); continue; }
            if (path == "oci-layout") { layout = ReadAll(entry); continue; }

            var m = BlobPath.Match(path);
            if (!m.Success)
            {
                if (path.StartsWith("blobs/", StringComparison.Ordinal))
                    throw Refuse($"'{path}' is not a sha256 blob");
                continue;   // buildx may add files the layout does not define (manifest.json); they name nothing
            }

            var digest = "sha256:" + m.Groups[1].Value;
            if (sizes.ContainsKey(digest)) throw Refuse($"blob {digest} appears twice");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var keep = entry.Length <= 1 << 20 ? new MemoryStream() : null;
            long length = 0;
            var buffer = new byte[81920];
            var data = entry.DataStream ?? Stream.Null;
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                keep?.Write(buffer, 0, read);
                length += read;
            }
            var actual = "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != digest) throw Refuse($"blob '{path}' hashes to {actual}, not its name");
            sizes[digest] = length;
            blobs[digest] = keep?.ToArray();
        }

        if (layout is null) throw Refuse("there is no oci-layout file");
        using (var doc = JsonDocument.Parse(layout))
            if (!doc.RootElement.TryGetProperty("imageLayoutVersion", out var v) || v.GetString() != "1.0.0")
                throw Refuse("oci-layout does not declare imageLayoutVersion 1.0.0");
        if (index is null) throw Refuse("there is no index.json");

        OciDescriptor manifestRef;
        using (var doc = JsonDocument.Parse(index))
        {
            if (!doc.RootElement.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
                throw Refuse("index.json has no manifests");
            if (manifests.GetArrayLength() != 1)
                throw Refuse($"index.json names {manifests.GetArrayLength()} manifests; exactly one image is staged");
            manifestRef = Descriptor(manifests[0], "index.json's manifest");
        }
        if (manifestRef.MediaType != OciManifest && manifestRef.MediaType != DockerManifest)
            throw Refuse($"index.json's one entry is '{manifestRef.MediaType}', not an image manifest - an index of manifests " +
                         "(buildx's provenance attestation) is refused; build with --provenance=false");

        var manifestBytes = Blob(blobs, sizes, manifestRef, "the manifest");
        OciDescriptor config;
        var layers = new List<OciDescriptor>();
        using (var doc = JsonDocument.Parse(manifestBytes))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("mediaType", out var mt) && mt.GetString() != manifestRef.MediaType)
                throw Refuse($"the manifest says it is '{mt.GetString()}' but index.json says '{manifestRef.MediaType}'");
            if (!root.TryGetProperty("config", out var c)) throw Refuse("the manifest names no config");
            config = Descriptor(c, "the manifest's config");
            if (!root.TryGetProperty("layers", out var ls) || ls.ValueKind != JsonValueKind.Array || ls.GetArrayLength() == 0)
                throw Refuse("the manifest names no layers");
            foreach (var l in ls.EnumerateArray()) layers.Add(Descriptor(l, "a layer"));
        }

        RequirePresent(sizes, config, "the config");
        foreach (var l in layers) RequirePresent(sizes, l, $"layer {l.Digest}");

        return new OciImage(manifestRef.Digest, manifestBytes, manifestRef.MediaType, config, layers, sizes);
    }

    private static OciDescriptor Descriptor(JsonElement e, string what)
    {
        var mediaType = e.TryGetProperty("mediaType", out var m) ? m.GetString() : null;
        var digest = e.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : -1;
        if (string.IsNullOrEmpty(mediaType) || digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$") || size < 0)
            throw Refuse($"{what} is not a descriptor with a media type, a sha256 digest and a size");
        return new OciDescriptor(mediaType!, digest, size);
    }

    private static byte[] Blob(Dictionary<string, byte[]?> blobs, Dictionary<string, long> sizes, OciDescriptor d, string what)
    {
        RequirePresent(sizes, d, what);
        return blobs[d.Digest] ?? throw Refuse($"{what} is larger than a manifest can be");
    }

    private static void RequirePresent(Dictionary<string, long> sizes, OciDescriptor d, string what)
    {
        if (!sizes.TryGetValue(d.Digest, out var size)) throw Refuse($"{what} ({d.Digest}) is not in the archive");
        if (size != d.Size) throw Refuse($"{what} ({d.Digest}) is {size} bytes; its descriptor says {d.Size}");
    }

    private static byte[] ReadAll(TarEntry entry)
    {
        using var ms = new MemoryStream();
        entry.DataStream?.CopyTo(ms);
        return ms.ToArray();
    }

    private static InvalidOperationException Refuse(string why) => new($"staged image refused: {why}");
}
