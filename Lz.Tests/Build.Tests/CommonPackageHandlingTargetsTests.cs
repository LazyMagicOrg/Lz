using System.Diagnostics;

namespace Lz.Tests.Build.Tests;

/// <summary>
/// Runs the Lz repo's <c>CommonPackageHandling.targets</c> for real, once, against a throwaway
/// class library — a real <c>dotnet build</c>, a fake global-packages folder, a fake feed — so
/// the two targets that MSBuild cannot unit-test any other way are pinned by behaviour:
///
/// <list type="bullet">
///   <item><b>DeletePackage / DeleteSpecificPackage</b> must evict the version the package was
///   ACTUALLY built with. The scratch project sets its version inside a target, exactly as
///   Nerdbank.GitVersioning does; the old child <c>&lt;MSBuild&gt;</c> re-entry only ever saw the
///   static value and would have evicted the wrong folder (MigrationPlan §2).</item>
///   <item><b>CleanExistingPackages</b> must remove this id's superseded packages from the feed
///   and NOTHING else — in particular not a neighbour whose id merely extends this one.</item>
/// </list>
///
/// The same targets text is replicated into LazyMagic, Service and BaseAppLib; this harness
/// exercises the Lz copy, which lz-ci builds and tests on every push.
/// </summary>
public sealed class PackageHandlingScratchBuild : IDisposable
{
    public const string Id = "LzScratchPkg";
    /// <summary>What static evaluation sees — set in the project body, after the targets import.</summary>
    public const string StaticVersion = "5.5.5";
    /// <summary>What the stand-in version tool sets INSIDE a target, before Build.</summary>
    public const string DynamicVersion = "9.9.9-alpha.7";

    public string Root { get; }
    public string Feed { get; }
    public string FakeGlobalRoot { get; }
    public string ProjectDir { get; }
    public string ProjectPath { get; }
    public string LzRepoRoot { get; }
    public string Output { get; }
    public int ExitCode { get; }

    public PackageHandlingScratchBuild()
    {
        LzRepoRoot = FindLzRepoRoot();
        Root = Path.Combine(Path.GetTempPath(), "lz-targets-" + Guid.NewGuid().ToString("N")[..8]);
        Feed = Path.Combine(Root, "feed");
        FakeGlobalRoot = Path.Combine(Root, "gpf");
        ProjectDir = Path.Combine(Root, "proj");
        foreach (var d in new[] { Feed, FakeGlobalRoot, ProjectDir }) Directory.CreateDirectory(d);

        ProjectPath = Path.Combine(ProjectDir, "Scratch.csproj");
        File.WriteAllText(Path.Combine(ProjectDir, "Class1.cs"), "namespace Scratch; public class Class1 { }");
        File.WriteAllText(ProjectPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{Id}</AssemblyName>
    <SolutionDir>{LzRepoRoot}{Path.DirectorySeparatorChar}</SolutionDir>
  </PropertyGroup>
  <Import Project=""$(SolutionDir)CommonPackageHandling.targets"" />
  <PropertyGroup>
    <Version>{StaticVersion}</Version>
  </PropertyGroup>
  <!-- Stand-in for a version tool: sets the version inside a TARGET, after static evaluation,
       exactly as Nerdbank.GitVersioning does. -->
  <Target Name=""FakeVersionTool"" BeforeTargets=""BeforeBuild"">
    <PropertyGroup>
      <Version>{DynamicVersion}</Version>
      <PackageVersion>{DynamicVersion}</PackageVersion>
    </PropertyGroup>
  </Target>
</Project>
");

        // The global-packages folder: three cached copies of this id. Only the one for the
        // version actually built may be evicted.
        SeedCacheFolder(FakeGlobalRoot, StaticVersion);
        SeedCacheFolder(FakeGlobalRoot, DynamicVersion);
        SeedCacheFolder(FakeGlobalRoot, "1.0.0");   // the SDK default — the plan's exact failure case

        // The feed: this id's superseded packages, a neighbour whose id EXTENDS this one, an
        // unrelated id sharing the prefix, and a sibling package.
        foreach (var f in new[]
        {
            $"{Id}.{StaticVersion}.nupkg", $"{Id}.{StaticVersion}.snupkg", $"{Id}.2.0.0-beta.3.nupkg",
            $"{Id}.Extra.1.0.0.nupkg", $"{Id}Other.1.0.0.nupkg", "Lz.Core.0.11.1.nupkg",
        })
            File.WriteAllText(Path.Combine(Feed, f), "not a real package");

        (ExitCode, Output) = RunDotnet(ProjectDir,
            "build", ProjectPath, "-nologo", "-v:m",
            $"-p:NuGetPackageRoot={FakeGlobalRoot}",
            $"-p:PackageRepoFolder={Feed}");
    }

    public static void SeedCacheFolder(string globalRoot, string version)
    {
        var dir = Path.Combine(globalRoot, Id.ToLowerInvariant(), version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".nupkg.metadata"), "{}");
    }

    public static (int ExitCode, string Output) RunDotnet(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("dotnet " + string.Join(' ', args) + " did not finish in 5 minutes");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>Walk up from the test bin for <c>Lz.slnx</c>, as the lifecycle harness does.</summary>
    public static string FindLzRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("Lz.slnx").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not locate the Lz repo root (no Lz.slnx walking up from {AppContext.BaseDirectory}).");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public class CommonPackageHandlingTargetsTests : IClassFixture<PackageHandlingScratchBuild>
{
    private readonly PackageHandlingScratchBuild _b;
    public CommonPackageHandlingTargetsTests(PackageHandlingScratchBuild b) => _b = b;

    private string Cached(string version) => Path.Combine(_b.FakeGlobalRoot, PackageHandlingScratchBuild.Id.ToLowerInvariant(), version);
    private string InFeed(string file) => Path.Combine(_b.Feed, file);

    [Fact]
    public void TheScratchBuildSucceeds()
        => Assert.True(_b.ExitCode == 0, "dotnet build failed:" + Environment.NewLine + _b.Output);

    [Fact]
    public void EvictsTheVersionActuallyBuilt_NotTheStaticOne_AndNotTheSdkDefault()
    {
        // The whole point of running the target in the build's own project instance. Under the
        // old child <MSBuild> re-entry the static 5.5.5 folder went and the 9.9.9-alpha.7 one —
        // the version the package really carries — survived.
        Assert.False(Directory.Exists(Cached(PackageHandlingScratchBuild.DynamicVersion)),
            "the built version's cache folder should have been evicted" + Environment.NewLine + _b.Output);
        Assert.True(Directory.Exists(Cached(PackageHandlingScratchBuild.StaticVersion)),
            "the static-evaluation version must not be evicted");
        Assert.True(Directory.Exists(Cached("1.0.0")),
            "the SDK default 1.0.0 — possibly a real published package — must not be evicted");
    }

    [Fact]
    public void TheFeedHoldsOneVersionOfThisId_AndTheNeighboursSurvive()
    {
        Assert.True(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}.{PackageHandlingScratchBuild.DynamicVersion}.nupkg")),
            "the new package should have been copied into the feed" + Environment.NewLine + _b.Output);

        // Superseded packages of THIS id are gone, nupkg and snupkg alike.
        Assert.False(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}.{PackageHandlingScratchBuild.StaticVersion}.nupkg")));
        Assert.False(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}.{PackageHandlingScratchBuild.StaticVersion}.snupkg")));
        Assert.False(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}.2.0.0-beta.3.nupkg")));

        // The neighbour whose id merely EXTENDS ours is exactly what the bare glob used to sweep.
        Assert.True(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}.Extra.1.0.0.nupkg")),
            "a package whose id extends this one must survive the clean");
        Assert.True(File.Exists(InFeed($"{PackageHandlingScratchBuild.Id}Other.1.0.0.nupkg")));
        Assert.True(File.Exists(InFeed("Lz.Core.0.11.1.nupkg")));
    }

    [Fact]
    public void ReportsTheEviction_AndNeverTheOldFalseFailure()
    {
        Assert.Contains("Evicted", _b.Output);
        Assert.DoesNotContain("Failed to delete package", _b.Output);
    }

    [Theory]
    [InlineData("9.8.7-Rc.1+gABCDEF", "9.8.7-rc.1")]   // metadata dropped, lower-cased, like NuGet's folder
    [InlineData("3.2.1.0", "3.2.1")]                    // a fourth part of 0 is not in the folder name
    [InlineData("1.2.3.4", "1.2.3.4")]                  // a real fourth part is
    public void NormalizesTheCacheFolderNameAsNuGetDoes(string packageVersion, string folder)
    {
        // Run the eviction target alone, with the version handed in as a global property, against
        // its own fake root so the Theory cases cannot disturb each other or the fixture's folders.
        var root = Path.Combine(_b.Root, "gpf-" + Guid.NewGuid().ToString("N")[..6]);
        PackageHandlingScratchBuild.SeedCacheFolder(root, folder);

        var (exit, output) = PackageHandlingScratchBuild.RunDotnet(_b.ProjectDir,
            "msbuild", _b.ProjectPath, "-t:DeleteSpecificPackage", "-nologo", "-v:m",
            $"-p:PackageVersion={packageVersion}",
            $"-p:NuGetPackageRoot={root}");

        Assert.True(exit == 0, output);
        Assert.False(Directory.Exists(Path.Combine(root, PackageHandlingScratchBuild.Id.ToLowerInvariant(), folder)),
            $"expected {folder} to be evicted for PackageVersion={packageVersion}" + Environment.NewLine + output);
    }
}
