using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The intake's artifact check and the staged image's parser (DecoupledCd.md §15.4, Fetch and stage; §15.7 V-A).
/// The archives are built here, in memory: one well-formed image layout, and one for each way a staged image is refused.
/// </summary>
public class IntakeArtifactsTests
{
    // ---- the artifact check --------------------------------------------------------------------

    private const string Digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private static readonly string[] Expected = { "image", "tests" };

    private static GitHubArtifact Art(string name, long run = 5000, bool expired = false, string? digest = Digest)
        => new(1, name, run, expired, digest, 1234);

    [Fact]
    public void TheAttemptsArtifactsPass()
    {
        var result = ArtifactCheck.Check(5000, 2, Expected,
            new[] { Art("image-1"), Art("tests-1"), Art("image-2"), Art("tests-2") });
        Assert.True(result.Passed);
        Assert.Equal("image-2", result.Found["image"].Name);
    }

    [Fact]
    public void AnEarlierAttemptsArtifactIsNotTakenForThisOnes()
    {
        var result = ArtifactCheck.Check(5000, 2, Expected, new[] { Art("image-1"), Art("tests-2") });
        Assert.Contains(result.Reasons, r => r.Contains("no artifact named 'image-2'"));
    }

    public static TheoryData<string, GitHubArtifact[]> Bad => new()
    {
        { "belongs to run 4999", new[] { Art("image-2", run: 4999), Art("tests-2") } },
        { "has expired", new[] { Art("image-2", expired: true), Art("tests-2") } },
        { "no sha256 digest", new[] { Art("image-2", digest: null), Art("tests-2") } },
        { "no sha256 digest", new[] { Art("image-2", digest: "md5:abc"), Art("tests-2") } },
        { "2 artifacts named 'image-2'", new[] { Art("image-2"), Art("image-2"), Art("tests-2") } },
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void EachBadArtifactIsRefusedAndNamed(string named, GitHubArtifact[] listed)
    {
        var result = ArtifactCheck.Check(5000, 2, Expected, listed);
        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains(named));
    }

    [Fact]
    public void NothingExpectedIsNotAPass() => Assert.False(ArtifactCheck.Check(5000, 1, Array.Empty<string>(), new[] { Art("image-1") }).Passed);

    [Fact]
    public void TheDigestMustBeTheBytes()
    {
        Assert.True(ArtifactCheck.DigestMatches(Digest, Digest["sha256:".Length..].ToUpperInvariant()));
        Assert.False(ArtifactCheck.DigestMatches(Digest, new string('0', 64)));
        Assert.False(ArtifactCheck.DigestMatches(null, Digest["sha256:".Length..]));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("image", 0)]
    public void ANameNeedsAnArtifactAndAnAttempt(string artifact, int attempt)
        => Assert.ThrowsAny<ArgumentException>(() => ArtifactCheck.NameFor(artifact, attempt));

    // ---- the staged image ----------------------------------------------------------------------

    private static string Sha(byte[] b) => "sha256:" + Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static string Hex(string digest) => digest["sha256:".Length..];

    public sealed class Layout
    {
        public byte[] Config = Encoding.UTF8.GetBytes("{\"architecture\":\"amd64\",\"os\":\"linux\"}");
        public byte[] Layer = Encoding.UTF8.GetBytes(new string('x', 3000));
        public string ManifestMediaType = OciArchive.OciManifest;
        public string IndexEntryMediaType = OciArchive.OciManifest;
        public long? ConfigSize, LayerSize;
        public bool OmitLayout, OmitLayer, ExtraIndexEntry, NoLayers, DuplicateLayer, Symlink, WrongBlobName, StrayBlob;
        public string LayoutVersion = "1.0.0";

        public byte[] Manifest() => Encoding.UTF8.GetBytes(
            $"{{\"schemaVersion\":2,\"mediaType\":\"{ManifestMediaType}\"," +
            $"\"config\":{{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\"{Sha(Config)}\",\"size\":{ConfigSize ?? Config.Length}}}," +
            $"\"layers\":[{(NoLayers ? "" : $"{{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar+gzip\",\"digest\":\"{Sha(Layer)}\",\"size\":{LayerSize ?? Layer.Length}}}")}]}}");

        public MemoryStream Tar()
        {
            var manifest = Manifest();
            var entry = $"{{\"mediaType\":\"{IndexEntryMediaType}\",\"digest\":\"{Sha(manifest)}\",\"size\":{manifest.Length}}}";
            var index = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":2,\"manifests\":[{entry}{(ExtraIndexEntry ? "," + entry : "")}]}}");

            var ms = new MemoryStream();
            using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
            {
                void File(string name, byte[] data) =>
                    w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
                if (!OmitLayout) File("oci-layout", Encoding.UTF8.GetBytes($"{{\"imageLayoutVersion\":\"{LayoutVersion}\"}}"));
                File("index.json", index);
                File($"blobs/sha256/{Hex(Sha(manifest))}", manifest);
                File($"blobs/sha256/{Hex(Sha(Config))}", Config);
                if (!OmitLayer) File($"blobs/sha256/{(WrongBlobName ? new string('a', 64) : Hex(Sha(Layer)))}", Layer);
                if (DuplicateLayer) File($"blobs/sha256/{Hex(Sha(Layer))}", Layer);
                if (StrayBlob) File("blobs/md5/abc", Layer);
                if (Symlink) w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "blobs/sha256/link") { LinkName = "/etc/passwd" });
                File("manifest.json", Encoding.UTF8.GetBytes("[]"));
            }
            ms.Position = 0;
            return ms;
        }
    }

    [Fact]
    public void AWellFormedLayoutIsOneImage()
    {
        var layout = new Layout();
        var image = OciArchive.Read(layout.Tar());
        Assert.Equal(Sha(layout.Manifest()), image.ManifestDigest);
        Assert.Equal(layout.Manifest(), image.ManifestBytes);
        Assert.Equal(Sha(layout.Config), image.Config.Digest);
        Assert.Equal(Sha(layout.Layer), Assert.Single(image.Layers).Digest);
    }

    [Fact]
    public void ADockerManifestIsAnImageToo()
    {
        var layout = new Layout { ManifestMediaType = OciArchive.DockerManifest, IndexEntryMediaType = OciArchive.DockerManifest };
        Assert.Equal(Sha(layout.Manifest()), OciArchive.Read(layout.Tar()).ManifestDigest);
    }

    public static TheoryData<string, Layout> Refused => new()
    {
        { "no oci-layout", new Layout { OmitLayout = true } },
        { "imageLayoutVersion", new Layout { LayoutVersion = "2.0.0" } },
        { "names 2 manifests", new Layout { ExtraIndexEntry = true } },
        { "--provenance=false", new Layout { IndexEntryMediaType = "application/vnd.oci.image.index.v1+json" } },
        { "but index.json says", new Layout { ManifestMediaType = OciArchive.DockerManifest } },
        { "not in the archive", new Layout { OmitLayer = true } },
        { "hashes to", new Layout { WrongBlobName = true } },
        { "appears twice", new Layout { DuplicateLayer = true } },
        { "its descriptor says", new Layout { LayerSize = 10 } },
        { "its descriptor says", new Layout { ConfigSize = 1 } },
        { "names no layers", new Layout { NoLayers = true } },
        { "is a SymbolicLink", new Layout { Symlink = true } },
        { "is not a sha256 blob", new Layout { StrayBlob = true } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void EachMalformedLayoutIsRefusedAndNamed(string named, Layout layout)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OciArchive.Read(layout.Tar()));
        Assert.StartsWith("staged image refused:", ex.Message);
        Assert.Contains(named, ex.Message);
    }
}
