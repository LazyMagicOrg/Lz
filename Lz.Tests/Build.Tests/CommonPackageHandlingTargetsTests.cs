using System.Diagnostics;

namespace Lz.Tests.Build.Tests;

/// <summary>
/// Runs the Lz repo's <c>CommonPackageHandling.targets</c> for real, once, against a throwaway
/// class library — a real <c>dotnet build</c>, a fake global-packages folder, a fake feed — so
/// the two targets that MSBuild cannot unit-test any other way are pinned by behaviour:
///
/// <list type="bullet">
///   <item><b>DeletePackage / DeleteSpecificPackage</b> must evict the version the package was
///   ACTUALLY built with, and must report a failed eviction as a failure. The scratch project sets
///   its version inside a target, exactly as Nerdbank.GitVersioning does; the old child
///   <c>&lt;MSBuild&gt;</c> re-entry only ever saw the static value and would have evicted the wrong
///   folder (MigrationPlan §2), and the first draft of the honest messages keyed on
///   <c>rmdir</c>'s exit code, which is 0 even when a locked file stops the deletion.</item>
///   <item><b>CleanExistingPackages</b> must remove this id's superseded packages from the feed
///   and NOTHING else — not a neighbour whose id merely extends this one, and not the version it
///   has just produced.</item>
/// </list>
///
/// The same targets text is replicated into LazyMagic, Service and BaseAppLib; this harness
/// exercises the Lz copy. The id carries dots on purpose: every real id does, and the pattern's
/// <c>Regex.Escape</c> is what makes <c>Lz.Scratch.Pkg</c> not match <c>Lz.ScratchXPkg</c>.
/// </summary>
public sealed class PackageHandlingScratchBuild : IDisposable
{
    public const string Id = "Lz.Scratch.Pkg";
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

        // The feed: this id's superseded packages; a neighbour whose id EXTENDS this one; an id
        // that shares the prefix but not the dot; the id's own PREFIX; and a sibling package.
        foreach (var f in new[]
        {
            $"{Id}.{StaticVersion}.nupkg", $"{Id}.{StaticVersion}.snupkg", $"{Id}.2.0.0-beta.3.nupkg",
            $"{Id}.Extra.1.0.0.nupkg", $"{Id}X.1.0.0.nupkg", "Lz.Scratch.1.0.0.nupkg", "Lz.Core.0.11.1.nupkg",
        })
            File.WriteAllText(Path.Combine(Feed, f), "not a real package");

        (ExitCode, Output) = Build();
    }

    /// <summary>The fixture's build, repeatable: a second call is the same-version rebuild case.</summary>
    public (int ExitCode, string Output) Build() => RunDotnet(ProjectDir,
        "build", ProjectPath, "-nologo", "-v:m",
        $"-p:NuGetPackageRoot={FakeGlobalRoot}",
        $"-p:PackageRepoFolder={Feed}");

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

    private const string Id = PackageHandlingScratchBuild.Id;
    private static string LowerId => Id.ToLowerInvariant();
    private string Cached(string version) => Path.Combine(_b.FakeGlobalRoot, LowerId, version);
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
        Assert.True(File.Exists(InFeed($"{Id}.{PackageHandlingScratchBuild.DynamicVersion}.nupkg")),
            "the new package should have been copied into the feed" + Environment.NewLine + _b.Output);

        // Superseded packages of THIS id are gone, nupkg and snupkg alike.
        Assert.False(File.Exists(InFeed($"{Id}.{PackageHandlingScratchBuild.StaticVersion}.nupkg")));
        Assert.False(File.Exists(InFeed($"{Id}.{PackageHandlingScratchBuild.StaticVersion}.snupkg")));
        Assert.False(File.Exists(InFeed($"{Id}.2.0.0-beta.3.nupkg")));

        // The neighbour whose id merely EXTENDS ours is exactly what a bare glob would sweep; the
        // other three never matched the glob and prove the candidates are what we think they are.
        Assert.True(File.Exists(InFeed($"{Id}.Extra.1.0.0.nupkg")),
            "a package whose id extends this one must survive the clean");
        Assert.True(File.Exists(InFeed($"{Id}X.1.0.0.nupkg")));
        Assert.True(File.Exists(InFeed("Lz.Scratch.1.0.0.nupkg")));
        Assert.True(File.Exists(InFeed("Lz.Core.0.11.1.nupkg")));
    }

    [Fact]
    public void ReportsTheEviction_AndNeverTheOldFalseFailure()
    {
        Assert.Contains("Evicted ", _b.Output);
        Assert.DoesNotContain("Could not evict", _b.Output);
        Assert.DoesNotContain("Failed to delete package", _b.Output);
    }

    [Fact]
    public void ASameVersionRebuildRemovesNothingFromTheFeed()
    {
        // The ordinary edit-and-build loop: the version does not change, pack rewrites the same
        // file name, and the clean must not delete it (the version just produced is excluded), so
        // there is no window in which the feed lacks the id.
        var (exit, output) = _b.Build();

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("Removing superseded", output);
        Assert.True(File.Exists(InFeed($"{Id}.{PackageHandlingScratchBuild.DynamicVersion}.nupkg")));
        Assert.True(File.Exists(InFeed($"{Id}.Extra.1.0.0.nupkg")));
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
        Assert.False(Directory.Exists(Path.Combine(root, LowerId, folder)),
            $"expected {folder} to be evicted for PackageVersion={packageVersion}" + Environment.NewLine + output);
        // The lower-casing is pinned in the TEXT the target computed, not on the filesystem — NTFS
        // would resolve either case to the same folder and prove nothing.
        Assert.Contains(Path.Combine(LowerId, folder), output);
    }

    [Fact]
    public void AFailedEvictionIsReportedAsAFailure_NotAsEvicted()
    {
        // Windows only: `rmdir /s /q` exits 0 when a file inside is in use and it deleted only what
        // it could, so the outcome has to be decided by whether the folder still exists, not by
        // the exit code — keyed on the exit code, this scenario printed "Evicted". On Unix `rm -rf`
        // removes an open file outright, so there is nothing to observe.
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(_b.Root, "gpf-locked");
        PackageHandlingScratchBuild.SeedCacheFolder(root, "4.4.4");
        var lib = Path.Combine(root, LowerId, "4.4.4", "lib");
        Directory.CreateDirectory(lib);
        using var locked = new FileStream(Path.Combine(lib, "locked.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        locked.WriteByte(1); locked.Flush();

        var (exit, output) = PackageHandlingScratchBuild.RunDotnet(_b.ProjectDir,
            "msbuild", _b.ProjectPath, "-t:DeleteSpecificPackage", "-nologo", "-v:m",
            "-p:PackageVersion=4.4.4",
            $"-p:NuGetPackageRoot={root}");

        Assert.True(exit == 0, "a warning must not fail the build:" + Environment.NewLine + output);
        Assert.Contains("Could not evict", output);
        Assert.DoesNotContain("Evicted ", output);
        Assert.True(Directory.Exists(Path.Combine(root, LowerId, "4.4.4")), "the locked folder should have survived");
    }
}


/// <summary>
/// The same targets, exercised through a CROSS-TARGETING project — a <c>&lt;TargetFrameworks&gt;</c>
/// (plural) project, which the SDK builds in two passes: an outer pass that dispatches to the inner
/// builds, then one inner build per framework. A single-entry list still takes that outer pass, and
/// that is exactly the shape of the nine such projects in LazyMagic.
///
/// <para><b>Why this fixture exists.</b> A version tool sets the version inside a target, and the
/// outer pass runs none of those targets — so there <c>$(Version)</c> is the SDK default
/// <c>1.0.0</c>. Before the guard, the outer pass evicted <c>&lt;id&gt;/1.0.0</c>: a package the
/// build never produced, possibly a real published one. The 2026-09-05 NBGV spike logged seven such
/// lines per build; this is the same defect the child <c>&lt;MSBuild&gt;</c> re-entry used to cause,
/// reached by a different route.</para>
///
/// <para>The stand-in version tool below is deliberately crude: it models only the property
/// asymmetry between the two passes. It hooks <c>GenerateNuspec</c> as well as <c>BeforeBuild</c> so
/// that pack — which runs in the outer pass — still sees the real version, which is what the spike
/// observed of Nerdbank.GitVersioning (correct package file names, wrong eviction).</para>
/// </summary>
public sealed class CrossTargetingScratchBuild : IDisposable
{
    public const string Id = "Lz.Scratch.Multi";
    /// <summary>What the outer pass sees: the SDK's default, since no <c>&lt;Version&gt;</c> is set.</summary>
    public const string SdkDefaultVersion = "1.0.0";
    /// <summary>What the stand-in version tool sets inside a target, so only the inner build sees it.</summary>
    public const string DynamicVersion = "8.8.8-alpha.2";

    public string Root { get; }
    public string Feed { get; }
    public string FakeGlobalRoot { get; }
    public string ProjectDir { get; }
    public string ProjectPath { get; }
    public string Output { get; }
    public int ExitCode { get; }

    public CrossTargetingScratchBuild()
    {
        var lzRepoRoot = PackageHandlingScratchBuild.FindLzRepoRoot();
        Root = Path.Combine(Path.GetTempPath(), "lz-xtarget-" + Guid.NewGuid().ToString("N")[..8]);
        Feed = Path.Combine(Root, "feed");
        FakeGlobalRoot = Path.Combine(Root, "gpf");
        ProjectDir = Path.Combine(Root, "proj");
        foreach (var d in new[] { Feed, FakeGlobalRoot, ProjectDir }) Directory.CreateDirectory(d);

        ProjectPath = Path.Combine(ProjectDir, "Scratch.csproj");
        File.WriteAllText(Path.Combine(ProjectDir, "Class1.cs"), "namespace Scratch; public class Class1 { }");
        File.WriteAllText(ProjectPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <AssemblyName>{Id}</AssemblyName>
    <SolutionDir>{lzRepoRoot}{Path.DirectorySeparatorChar}</SolutionDir>
  </PropertyGroup>
  <Import Project=""$(SolutionDir)CommonPackageHandling.targets"" />
  <PropertyGroup>
    <!-- The post-migration state, which is what makes the defect reachable: MigrationPlan P0b
         deletes the static <Version> lines from the packaging targets, and an empty Version is
         filled in by the SDK from VersionPrefix - {SdkDefaultVersion}. Blanking it here reproduces
         that without touching the real targets file (which still supplies $(LzVersion) today, and
         is why the single-framework fixture above never sees 1.0.0). -->
    <Version></Version>
    <PackageVersion></PackageVersion>
  </PropertyGroup>
  <Target Name=""FakeVersionTool"" BeforeTargets=""BeforeBuild;GenerateNuspec"">
    <PropertyGroup>
      <Version>{DynamicVersion}</Version>
      <PackageVersion>{DynamicVersion}</PackageVersion>
    </PropertyGroup>
  </Target>
</Project>
");

        SeedCacheFolder(SdkDefaultVersion);
        SeedCacheFolder(DynamicVersion);

        (ExitCode, Output) = PackageHandlingScratchBuild.RunDotnet(ProjectDir,
            "build", ProjectPath, "-nologo", "-v:m",
            $"-p:NuGetPackageRoot={FakeGlobalRoot}",
            $"-p:PackageRepoFolder={Feed}");
    }

    private void SeedCacheFolder(string version)
    {
        var dir = Path.Combine(FakeGlobalRoot, Id.ToLowerInvariant(), version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".nupkg.metadata"), "{}");
    }

    public string Cached(string version) => Path.Combine(FakeGlobalRoot, Id.ToLowerInvariant(), version);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public class CrossTargetingEvictionTests : IClassFixture<CrossTargetingScratchBuild>
{
    private readonly CrossTargetingScratchBuild _b;
    public CrossTargetingEvictionTests(CrossTargetingScratchBuild b) => _b = b;

    [Fact]
    public void TheCrossTargetingBuildSucceeds()
        => Assert.True(_b.ExitCode == 0, "dotnet build failed:" + Environment.NewLine + _b.Output);

    [Fact]
    public void TheOuterPassEvictsNothing_SoTheSdkDefaultSurvives()
    {
        // THE GUARD. Without `Condition="'$(IsCrossTargetingBuild)' != 'true'"` on DeletePackage the
        // outer pass evicts <id>/1.0.0 — a version this build never produced. Deleting the condition
        // fails exactly this assertion.
        Assert.True(Directory.Exists(_b.Cached(CrossTargetingScratchBuild.SdkDefaultVersion)),
            "the SDK default 1.0.0 must not be evicted by the cross-targeting outer pass"
            + Environment.NewLine + _b.Output);
        Assert.DoesNotContain($"1.0.0; the next restore", _b.Output);
    }

    [Fact]
    public void TheInnerBuildStillEvictsTheVersionActuallyBuilt()
    {
        // Skipping the outer pass must not cost the eviction: the inner build does it, with the
        // version the version tool set.
        Assert.False(Directory.Exists(_b.Cached(CrossTargetingScratchBuild.DynamicVersion)),
            "the inner build should have evicted the version it actually built"
            + Environment.NewLine + _b.Output);
        Assert.Contains(CrossTargetingScratchBuild.DynamicVersion, _b.Output);
    }
}
