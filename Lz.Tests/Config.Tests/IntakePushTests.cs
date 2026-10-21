using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lz.Aws.Pipeline;
using Xunit;

namespace Lz.Tests.Config.Tests;

/// <summary>The intake's push (DecoupledCd.md §15.4, V-12) and V-C's probe's name check, without AWS.</summary>
public class IntakePushTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lz-push-" + Guid.NewGuid().ToString("N"));
    public IntakePushTests() => Directory.CreateDirectory(_work);
    public void Dispose() { if (Directory.Exists(_work)) Directory.Delete(_work, true); }

    private static string Digest(byte[] b) => "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private string StagedZip(IntakeArtifactsTests.Layout layout)
    {
        var path = Path.Combine(_work, "aiphost-1.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var s = zip.CreateEntry("aiphost.oci.tar").Open()) layout.Tar().CopyTo(s);
        return path;
    }

    private sealed class Registry : IImageRegistry
    {
        public string? AtTag;
        public HashSet<string> Held = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte[]> Uploaded = new(StringComparer.Ordinal);
        public (string Tag, string Manifest, string MediaType, string ImageDigest)? Put;

        public Task<string?> DigestAtTagAsync(string repository, string tag) => Task.FromResult(AtTag);
        public Task<IReadOnlySet<string>> MissingBlobsAsync(string repository, IReadOnlyList<string> digests)
            => Task.FromResult<IReadOnlySet<string>>(digests.Where(d => !Held.Contains(d)).ToHashSet(StringComparer.Ordinal));
        public Task UploadBlobAsync(string repository, string digest, long size, Stream content)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Assert.Equal(size, ms.Length);
            Uploaded[digest] = ms.ToArray();
            return Task.CompletedTask;
        }
        public Task<string> PutImageAsync(string repository, string tag, string manifest, string mediaType, string imageDigest)
        {
            Put = (tag, manifest, mediaType, imageDigest);
            return Task.FromResult(imageDigest);
        }
    }

    [Fact]
    public async Task APush_UploadsWhatIsMissing_ThenPutsTheExactManifestUnderItsOwnDigest()
    {
        var layout = new IntakeArtifactsTests.Layout();
        var registry = new Registry { Held = { Digest(layout.Config) } };   // the config is there already; the layer is not

        var result = await IntakePushStep.RunAsync(StagedZip(layout), "repo", "t1", registry);

        Assert.Equal("pushed", result["outcome"]!.GetValue<string>());
        Assert.Equal(layout.Layer, Assert.Single(registry.Uploaded, u => u.Key == Digest(layout.Layer)).Value);
        Assert.DoesNotContain(Digest(layout.Config), registry.Uploaded.Keys);
        Assert.Equal(Encoding.UTF8.GetString(layout.Manifest()), registry.Put!.Value.Manifest);   // the bytes, unchanged
        Assert.Equal(Digest(layout.Manifest()), registry.Put.Value.ImageDigest);
        Assert.Equal(OciArchive.OciManifest, registry.Put.Value.MediaType);
    }

    [Fact]
    public async Task TheTagNamingThisDigest_IsAlreadyDone_AndPushesNothing()
    {
        var layout = new IntakeArtifactsTests.Layout();
        var registry = new Registry { AtTag = Digest(layout.Manifest()) };

        var result = await IntakePushStep.RunAsync(StagedZip(layout), "repo", "t1", registry);

        Assert.Equal("already-there", result["outcome"]!.GetValue<string>());
        Assert.Null(registry.Put);
        Assert.Empty(registry.Uploaded);
    }

    [Fact]
    public async Task TheTagNamingAnotherDigest_IsRefused_NeverMoved()
    {
        var registry = new Registry { AtTag = "sha256:" + new string('9', 64) };
        await Assert.ThrowsAsync<IntakeFault>(() => IntakePushStep.RunAsync(StagedZip(new()), "repo", "t1", registry));
        Assert.Null(registry.Put);
    }

    [Fact]
    public async Task TheProbe_CanSkipTheTagRead_AndGiveItsOwnImageDigest()
    {
        var registry = new Registry { AtTag = "sha256:" + new string('9', 64) };
        var result = await IntakePushStep.RunAsync(StagedZip(new()), "repo", "t1", registry, readTagFirst: false, imageDigest: IntakePushProbe.WrongDigest);
        Assert.Equal(IntakePushProbe.WrongDigest, registry.Put!.Value.ImageDigest);
        Assert.Null(result["digestAtTag"]);
    }

    [Theory]
    [InlineData("PREFIX_MATCH", "scu-4df6-b9c6-aiphost", "scu-intake-probe-4df6-b9c6", false)]
    [InlineData("WILDCARD_MATCH", "scu-4df6-b9c6-aiphost", "scu-intake-probe-4df6-b9c6", false)]
    [InlineData("PREFIX_MATCH", "scu-intake", "scu-intake-probe-4df6-b9c6", true)]           // a prefix over-matches (§14.4)
    [InlineData("WILDCARD_MATCH", "scu-*", "scu-intake-probe-4df6-b9c6", true)]
    [InlineData("WILDCARD_MATCH", "scu-intake-probe-4df6-b9c6", "scu-intake-probe-4df6-b9c6", true)]
    public void TheProbesName_IsCheckedAgainstTheRegistrysFilters(string type, string filter, string repository, bool matches)
        => Assert.Equal(matches, IntakePushProbe.FilterMatches(type, filter, repository));
}
