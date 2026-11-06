using System.IO.Compression;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Orchestration.Tests;

/// <summary><c>lz infraplan</c>'s bundle and plan-key checks (DecoupledCd.md P3 D): a plan is only ever of the recorded tree.</summary>
public sealed class InfraPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lz-infraplan-{Guid.NewGuid():N}");

    public InfraPlanTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, text) in entries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
                    w.Write(text);
        return path;
    }

    [Fact]
    public void ABundleWhoseHashMatches_IsExpanded()
    {
        var zip = Zip(("systemconfig.scu.dev.yaml", "SystemKey: scu"), ("Deploy/x.cs", "class X {}"));
        var target = Path.Combine(_dir, "work");
        Assert.Equal(2, InfraPlan.ExtractVerified(zip, InfraPlan.Sha256Of(zip).ToUpperInvariant(), target));
        Assert.Equal("SystemKey: scu", File.ReadAllText(Path.Combine(target, "systemconfig.scu.dev.yaml")));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("")]
    public void ABundleWhoseHashDoesNotMatch_IsNotExpanded(string sha)
    {
        var zip = Zip(("a.yaml", "a"));
        var target = Path.Combine(_dir, "work");
        Assert.Throws<InvalidDataException>(() => InfraPlan.ExtractVerified(zip, sha, target));
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
    }

    [Fact]
    public void ATargetThatAlreadyHoldsFiles_IsRefused()
    {
        var zip = Zip(("a.yaml", "a"));
        var target = Path.Combine(_dir, "work");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "stale.yaml"), "x");
        Assert.Throws<IOException>(() => InfraPlan.ExtractVerified(zip, InfraPlan.Sha256Of(zip), target));
        Assert.False(File.Exists(Path.Combine(target, "a.yaml")));
    }

    [Fact]
    public void AnEntryThatEscapesTheTarget_IsRefused()
    {
        var zip = Zip(("../escaped.yaml", "x"));
        var target = Path.Combine(_dir, "work");
        Assert.Throws<IOException>(() => InfraPlan.ExtractVerified(zip, InfraPlan.Sha256Of(zip), target));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.yaml")));
    }

    [Theory]
    [InlineData("plans/dev/exec-1.json", null)]
    [InlineData(null, "no plan key")]
    [InlineData("records/x.json", "not under plans/")]
    [InlineData("plans/../records/x.json", "not a plain key")]
    [InlineData("plans/x.txt", "not a .json name")]
    public void OnlyAJsonKeyUnderPlans_IsAPlanKey(string? key, string? why)
    {
        var refusal = InfraPlan.WhyNotAPlanKey(key);
        if (why is null) Assert.Null(refusal);
        else Assert.Contains(why, refusal);
    }
}
