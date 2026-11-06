using System.IO.Compression;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <c>lz infraplan</c>'s parts that are not the previews (DecoupledCd.md P3 D, the read-only slice): the config bundle the
/// deployer names, fetched by its version and refused unless its SHA-256 is the one the build record carries, expanded into an
/// empty working directory; and the plan written once to the evidence store under <see cref="PlansPrefix"/>.
///
/// <para>THE TASK IS STARTED BY THE DEPLOYER, never by a person: the bucket, key, version and hash come from the verified
/// config record, passed as the container's command, and the plan key from the execution. Nothing here trusts the bundle
/// before its hash matches, and nothing is extracted into a directory that already holds files, so a plan is always of
/// exactly the recorded tree.</para>
/// </summary>
public static class InfraPlan
{
    /// <summary>Where plans live in the evidence store. A plan key outside it is refused.</summary>
    public const string PlansPrefix = "plans/";

    /// <summary>The exit code for a destructive plan, the same as <c>--fail-on-replace</c>'s.</summary>
    public const int DestructiveExitCode = 2;

    /// <summary>The lower-case hex SHA-256 of a file.</summary>
    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>
    /// Check <paramref name="zipPath"/> against <paramref name="expectedSha256"/>, then expand it into
    /// <paramref name="targetDir"/>, which must be empty or absent. Returns the number of files written. Throws
    /// <see cref="InvalidDataException"/> on a hash that does not match, and <see cref="IOException"/> on a non-empty target or
    /// an entry that would land outside it (the framework's own check).
    /// </summary>
    public static int ExtractVerified(string zipPath, string expectedSha256, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidDataException("no SHA-256 to check the config bundle against; refusing to expand it");
        var actual = Sha256Of(zipPath);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the config bundle's SHA-256 is {actual}, not the recorded {expectedSha256.Trim()}; refusing to expand it");

        Directory.CreateDirectory(targetDir);
        if (Directory.EnumerateFileSystemEntries(targetDir).Any())
            throw new IOException($"{targetDir} is not empty; a plan is made from the recorded tree alone");

        ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: false);
        return Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Count();
    }

    /// <summary>Why a plan key is refused, or null. Under <see cref="PlansPrefix"/>, a <c>.json</c> name, no <c>..</c>.</summary>
    public static string? WhyNotAPlanKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "no plan key";
        if (!key.StartsWith(PlansPrefix, StringComparison.Ordinal)) return $"the plan key {key} is not under {PlansPrefix}";
        if (key.Contains("..", StringComparison.Ordinal) || key.Contains('\\')) return $"the plan key {key} is not a plain key";
        if (!key.EndsWith(".json", StringComparison.Ordinal)) return $"the plan key {key} is not a .json name";
        return null;
    }

    /// <summary>Fetch one version of the bundle to <paramref name="path"/>.</summary>
    public static async Task FetchAsync(IAmazonS3 s3, string bucket, string key, string? versionId, string path, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = string.IsNullOrEmpty(versionId) ? null : versionId,
        }, ct);
        await response.WriteResponseStreamToFileAsync(path, append: false, ct);
    }

    /// <summary>Write the plan once: <c>If-None-Match: *</c>, which the evidence store's policy demands anyway.</summary>
    public static Task PutPlanAsync(IAmazonS3 s3, string bucket, string key, string json, CancellationToken ct)
        => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = json,
            ContentType = "application/json",
            IfNoneMatch = "*",
        }, ct);
}
