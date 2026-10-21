using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>What the intake's push asks of ECR, through its API: no Docker, no registry login.</summary>
public interface IImageRegistry
{
    /// <summary>The digest the tag names in the repository; null when the tag is not there.</summary>
    Task<string?> DigestAtTagAsync(string repository, string tag);

    /// <summary>Which of these blobs the repository does not hold yet.</summary>
    Task<IReadOnlySet<string>> MissingBlobsAsync(string repository, IReadOnlyList<string> digests);

    /// <summary>Uploads one blob of <paramref name="size"/> bytes under <paramref name="digest"/>, which ECR checks.</summary>
    Task UploadBlobAsync(string repository, string digest, long size, Stream content);

    /// <summary>PutImage with these exact manifest bytes, under this tag and <paramref name="imageDigest"/>; the digest ECR answers with.</summary>
    Task<string> PutImageAsync(string repository, string tag, string manifest, string mediaType, string imageDigest);
}

/// <summary>
/// THE INTAKE'S PUSH (DecoupledCd.md §15.4, V-12; measured by V-C's probe, §15.8 item 3): the staged image, from its OCI
/// archive, into an ECR repository through the API. The tag is read first (<see cref="IntakeWrites.Push"/>): absent, push;
/// the digest being pushed, already done; another, refused. Every blob the repository lacks is uploaded from the archive,
/// streamed, and then PutImage is given the manifest's exact bytes and its digest as <c>imageDigest</c>, so ECR holds the
/// digest the build produced (§2) and refuses bytes that are not it.
/// </summary>
public static class IntakePushStep
{
    private static readonly Regex BlobPath = new("^blobs/sha256/([0-9a-f]{64})$", RegexOptions.Compiled);

    /// <param name="readTagFirst">False only for the probe, which measures ECR's own answer to a repeated push.</param>
    /// <param name="imageDigest">The digest PutImage is told; the manifest's own unless the probe passes a wrong one.</param>
    public static async Task<JsonObject> RunAsync(
        string zipPath, string repository, string tag, IImageRegistry registry, bool readTagFirst = true, string? imageDigest = null)
    {
        var clock = Stopwatch.StartNew();
        var image = IntakeFetchStep.ReadImage(zipPath);
        var result = new JsonObject
        {
            ["repository"] = repository, ["tag"] = tag, ["manifestDigest"] = image.ManifestDigest,
            ["imageDigest"] = imageDigest ?? image.ManifestDigest,
        };

        if (readTagFirst)
        {
            var atTag = await registry.DigestAtTagAsync(repository, tag);
            result["digestAtTag"] = atTag;
            switch (IntakeWrites.Push(atTag, image.ManifestDigest))
            {
                case WriteDecision.AlreadyDone:
                    result["outcome"] = "already-there";
                    result["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                    return result;
                case WriteDecision.Refuse:
                    throw new IntakeFault($"{repository}:{tag} already names {atTag}, not {image.ManifestDigest}; a tag is never moved");
            }
        }

        var blobs = new[] { image.Config }.Concat(image.Layers).Select(b => b.Digest).Distinct(StringComparer.Ordinal).ToList();
        var missing = await registry.MissingBlobsAsync(repository, blobs);
        long uploadedBytes = 0;
        if (missing.Count > 0)
        {
            // A second pass over the archive, streaming each missing blob as it comes: nothing is held in memory but a part.
            using var zip = ZipFile.OpenRead(zipPath);
            await using var tar = zip.Entries.Single(e => e.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)).Open();
            using var reader = new TarReader(tar);
            var left = new HashSet<string>(missing, StringComparer.Ordinal);
            while (left.Count > 0 && await reader.GetNextEntryAsync() is { } entry)
            {
                var m = BlobPath.Match(entry.Name.Replace('\\', '/').TrimStart('.', '/'));
                var digest = m.Success ? "sha256:" + m.Groups[1].Value : null;
                if (digest is null || !left.Remove(digest) || entry.DataStream is null) continue;
                await registry.UploadBlobAsync(repository, digest, entry.Length, entry.DataStream);
                uploadedBytes += entry.Length;
            }
            if (left.Count > 0) throw new IntakeFault($"the archive no longer holds {string.Join(", ", left)}");
        }

        var put = await registry.PutImageAsync(repository, tag, Encoding.UTF8.GetString(image.ManifestBytes), image.ManifestMediaType,
            imageDigest ?? image.ManifestDigest);
        result["outcome"] = "pushed";
        result["ecrDigest"] = put;
        result["blobs"] = blobs.Count;
        result["blobsUploaded"] = missing.Count;
        result["bytesUploaded"] = uploadedBytes;
        result["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        return result;
    }
}
