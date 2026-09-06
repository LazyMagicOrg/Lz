using System.Text.RegularExpressions;
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
    public void SupersededPackagesAreSweptFromTheBuildOutputToo_NotJustTheFeed()
    {
        // $(PackageOutputPath) had never been swept: pack leaves a nupkg/snupkg pair per version and
        // nothing removed the old ones, so under a derived version bin/ grows without bound (the
        // 2026-09-05 spike watched one project reach four pairs in an hour).
        var bin = Path.Combine(_b.ProjectDir, "bin", "Debug");
        var stale = Path.Combine(bin, $"{Id}.4.4.4.nupkg");
        var staleSymbols = Path.Combine(bin, $"{Id}.4.4.4.snupkg");
        var neighbour = Path.Combine(bin, $"{Id}.Extra.1.0.0.nupkg");
        File.WriteAllText(stale, "old"); File.WriteAllText(staleSymbols, "old"); File.WriteAllText(neighbour, "not ours");

        var (exit, output) = _b.Build();

        Assert.True(exit == 0, output);
        Assert.False(File.Exists(stale), "a superseded package should be swept from the build output" + Environment.NewLine + output);
        Assert.False(File.Exists(staleSymbols));
        Assert.True(File.Exists(neighbour), "an id that merely extends this one must survive in bin too");
        Assert.True(File.Exists(Path.Combine(bin, $"{Id}.{PackageHandlingScratchBuild.DynamicVersion}.nupkg")),
            "the version just built must NOT be swept from the build output");
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
    public void AnEmptyVersionRefusesToDelete_RatherThanWipingEveryCachedVersion()
    {
        // With no version the cache path collapses to the id root and rmdir takes EVERY cached
        // version of the package — verified 2026-09-05 on an unguarded copy, where four seeded
        // versions and the id folder itself went in one "Evicted …\lz.scratch.multi\". Global
        // properties cannot be overridden from inside the project, so -p:Version= holds it empty
        // through the whole evaluation, including the SDK's own defaulting.
        var root = Path.Combine(_b.Root, "gpf-empty");
        foreach (var v in new[] { "1.0.0", "2.2.2", "3.3.3" })
            PackageHandlingScratchBuild.SeedCacheFolder(root, v);

        var (exit, output) = PackageHandlingScratchBuild.RunDotnet(_b.ProjectDir,
            "msbuild", _b.ProjectPath, "-t:DeleteSpecificPackage", "-nologo", "-v:m",
            "-p:Version=", "-p:PackageVersion=",
            $"-p:NuGetPackageRoot={root}");

        Assert.True(exit == 0, "a warning must not fail the build:" + Environment.NewLine + output);
        Assert.Contains("No version could be determined", output);
        Assert.DoesNotContain("Evicted ", output);
        foreach (var v in new[] { "1.0.0", "2.2.2", "3.3.3" })
            Assert.True(Directory.Exists(Path.Combine(root, LowerId, v)),
                $"{v} must survive an eviction with no version" + Environment.NewLine + output);
        Assert.True(Directory.Exists(Path.Combine(root, LowerId)), "the id folder itself must survive");
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
    <!-- Mirror how a real version tool integrates: Nerdbank.GitVersioning registers its target in
         BOTH GenerateNuspecDependsOn and GetPackageVersionDependsOn
         (Nerdbank.GitVersioning.targets:16-23). GenerateNuspecDependsOn is the one that runs before
         _CalculateInputsOutputsForPack, which is why @(NuGetPackOutput) is correct under NBGV and
         wrong for a tool that only uses BeforeTargets=""GenerateNuspec"". -->
    <GenerateNuspecDependsOn>FakeVersionTool;$(GenerateNuspecDependsOn)</GenerateNuspecDependsOn>
    <GetPackageVersionDependsOn>FakeVersionTool;$(GetPackageVersionDependsOn)</GetPackageVersionDependsOn>
  </PropertyGroup>
  <Import Project=""$(SolutionDir)CommonPackageHandling.targets"" />
  <PropertyGroup>
    <!-- The post-migration state, which is what makes the defect reachable: MigrationPlan P0b
         deletes the static <Version> lines from the packaging targets, and an empty Version is
         filled in by the SDK from VersionPrefix - {SdkDefaultVersion}. Blanking it here reproduces
         that without touching the real targets file (which still supplies $(LzVersion) today, and
         is why the single-framework fixture above never sees 1.0.0). <Version> is the line that
         carries the signal; <PackageVersion> is inert here (pack defaults it from Version) and is
         kept only because P0b leaves neither behind. -->
    <Version></Version>
    <PackageVersion></PackageVersion>
  </PropertyGroup>
  <Target Name=""FakeVersionTool"" BeforeTargets=""BeforeBuild;GenerateNuspec"">
    <PropertyGroup>
      <Version>{DynamicVersion}</Version>
      <PackageVersion>{DynamicVersion}</PackageVersion>
    </PropertyGroup>
  </Target>
  <!-- The canary. Everything this fixture asserts is conditional on an outer pass having happened;
       nothing else here observes one, so without this a one-character edit ({{TargetFrameworks}} to
       {{TargetFramework}}) would retire the guard with all the tests still green. -->
  <Target Name=""ProveOuterPass"" AfterTargets=""Build"" Condition=""'$(IsCrossTargetingBuild)' == 'true'"">
    <Message Importance=""high"" Text=""OUTER PASS RAN"" />
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
    public void TheBuildReallyTookTheOuterPass()
        // The precondition every other assertion here rests on. A single-entry <TargetFrameworks>
        // list still takes the outer pass — that is the shape of all nine cross-targeting LazyMagic
        // projects — but nothing else in this fixture would notice if it stopped.
        => Assert.True(_b.Output.Contains("OUTER PASS RAN"),
            "the fixture did not take a cross-targeting outer pass, so it proves nothing about the guard:"
            + Environment.NewLine + _b.Output);

    [Fact]
    public void TheOuterPassEvictsNothing_SoTheSdkDefaultSurvives()
    {
        // THE GUARD. Without `Condition="'$(IsCrossTargetingBuild)' != 'true'"` on DeletePackage the
        // outer pass evicts <id>/1.0.0 — a version this build never produced. Deleting the condition
        // fails exactly this assertion.
        Assert.True(Directory.Exists(_b.Cached(CrossTargetingScratchBuild.SdkDefaultVersion)),
            "the SDK default 1.0.0 must not be evicted by the cross-targeting outer pass"
            + Environment.NewLine + _b.Output);
        // Both eviction messages name "<id> <version>", so this catches the destructive line and the
        // "nothing to evict" one alike — the signature the NBGV spike actually logged.
        Assert.DoesNotContain(
            $"{CrossTargetingScratchBuild.Id} {CrossTargetingScratchBuild.SdkDefaultVersion}", _b.Output);
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

/// <summary>
/// The four producers keep byte-identical copies of these targets, replicated by hand, and only the
/// Lz copy is exercised by a build test — no workflow runs Lz.Tests against LazyMagic, Service or
/// BaseAppLib. That is how a sentence claiming "every project in this repo is single-framework"
/// reached LazyMagic, the one repo where it is false. These are text assertions, so they need no
/// scaffold and no SDK: they only require the load-bearing lines to be present in whichever sibling
/// working copies exist beside this one.
/// </summary>
public class PackagingTargetsReplicationTests
{
    /// <summary>The sibling copies, skipped when the working copy is not beside this one.</summary>
    public static IEnumerable<string> Paths()
    {
        var repos = Directory.GetParent(PackageHandlingScratchBuild.FindLzRepoRoot())!.FullName;
        foreach (var rel in new[]
        {
            @"Lz\CommonPackageHandling.targets",
            @"LazyMagic\CommonPackageHandling.targets",
            @"Service\CommonPackageHandling.targets",
            @"BaseAppLib\MakePackage.targets",
        })
        {
            var path = Path.Combine(repos, rel);
            if (File.Exists(path)) yield return path;
        }
    }

    public static TheoryData<string> Copies()
    {
        var data = new TheoryData<string>();
        foreach (var p in Paths()) data.Add(p);
        return data;
    }

    [Theory]
    [MemberData(nameof(Copies))]
    public void EveryCopyGuardsTheCrossTargetingOuterPass(string path)
    {
        var text = File.ReadAllText(path);

        Assert.Contains("<Target Name=\"DeletePackage\"", text);
        Assert.Contains("Condition=\"'$(IsCrossTargetingBuild)' != 'true'\"", text);
    }

    [Theory]
    [MemberData(nameof(Copies))]
    public void EveryCopyRefusesToEvictWithNoVersion(string path)
    {
        var text = File.ReadAllText(path);

        // Both delete branches, and the warning that replaces them.
        Assert.Equal(2, Regex.Matches(text, @"'\$\(_EvictVersion\)' != ''\s+AND\s+Exists\('\$\(PackageCacheFolder\)'\)").Count);
        Assert.Contains("No version could be determined", text);
    }

    /// <summary>
    /// Every target the four copies share, compared verbatim. Not one contiguous span: the repos
    /// legitimately differ BETWEEN these targets (LazyMagic carries a Newtonsoft.Json reference,
    /// Service a project-reference packing target), so widening the slice to cover the copy and the
    /// clean would fail on differences that are not drift.
    /// </summary>
    [Theory]
    [InlineData("CopyPackage")]
    [InlineData("CleanExistingPackages")]
    [InlineData("DeleteSpecificPackage")]
    public void TheSharedTargetsHaveNotDrifted(string target)
    {
        var blocks = Paths().Select(p => Target(File.ReadAllText(p), target)).ToList();

        Assert.NotEmpty(blocks);
        Assert.All(blocks, b => Assert.Equal(blocks[0], b));
    }

    private static string Target(string text, string name)
    {
        var m = Regex.Match(text, $@"<Target Name=""{Regex.Escape(name)}"".*?</Target>", RegexOptions.Singleline);
        Assert.True(m.Success, $"target {name} was not found in a copy");
        return m.Value;
    }


}

/// <summary>
/// Pack names its output <c>&lt;PackageId&gt;.&lt;normalized PackageVersion&gt;</c>, which is NOT
/// <c>$(AssemblyName).$(Version)</c>. This fixture drives both ways they diverge at once: a
/// <c>PackageId</c> that differs from the assembly name, and a version carrying build metadata,
/// which pack drops from the file name. Before CopyPackage read pack's own output list this build
/// failed with MSB3030 and left the feed with no package for the id.
/// </summary>
public sealed class PackNamingScratchBuild : IDisposable
{
    public const string AssemblyName = "Lz.Scratch.Named";
    public const string PackageId = "Lz.Scratch.Renamed";
    public const string PackageVersion = "7.7.7-beta.1+gdeadbee";
    /// <summary>What pack puts in the file name: metadata dropped.</summary>
    public const string FileVersion = "7.7.7-beta.1";

    public string Root { get; }
    public string Feed { get; }
    public string Output { get; }
    public int ExitCode { get; }

    public PackNamingScratchBuild()
    {
        var lzRepoRoot = PackageHandlingScratchBuild.FindLzRepoRoot();
        Root = Path.Combine(Path.GetTempPath(), "lz-naming-" + Guid.NewGuid().ToString("N")[..8]);
        Feed = Path.Combine(Root, "feed");
        var gpf = Path.Combine(Root, "gpf");
        var proj = Path.Combine(Root, "proj");
        foreach (var d in new[] { Feed, gpf, proj }) Directory.CreateDirectory(d);

        // A stale package of the same id, to prove the feed clean still spares what was just written.
        File.WriteAllText(Path.Combine(Feed, $"{PackageId}.1.0.0.nupkg"), "old");

        File.WriteAllText(Path.Combine(proj, "Class1.cs"), "namespace Scratch; public class Class1 { }");
        var projPath = Path.Combine(proj, "Scratch.csproj");
        File.WriteAllText(projPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{AssemblyName}</AssemblyName>
    <PackageId>{PackageId}</PackageId>
    <SolutionDir>{lzRepoRoot}{Path.DirectorySeparatorChar}</SolutionDir>
  </PropertyGroup>
  <Import Project=""$(SolutionDir)CommonPackageHandling.targets"" />
  <PropertyGroup>
    <Version>{PackageVersion}</Version>
    <PackageVersion>{PackageVersion}</PackageVersion>
  </PropertyGroup>
</Project>
");
        (ExitCode, Output) = PackageHandlingScratchBuild.RunDotnet(proj,
            "build", projPath, "-nologo", "-v:m",
            $"-p:NuGetPackageRoot={gpf}", $"-p:PackageRepoFolder={Feed}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public class PackNamingTests : IClassFixture<PackNamingScratchBuild>
{
    private readonly PackNamingScratchBuild _b;
    public PackNamingTests(PackNamingScratchBuild b) => _b = b;

    [Fact]
    public void TheFixtureReallyExercisesBothDivergences()
    {
        // The force of every assertion below lives in two consts. If someone "simplifies" them the
        // suite would keep passing while testing nothing.
        Assert.NotEqual(PackNamingScratchBuild.AssemblyName, PackNamingScratchBuild.PackageId);
        Assert.Contains("+", PackNamingScratchBuild.PackageVersion);
        Assert.DoesNotContain("+", PackNamingScratchBuild.FileVersion);
    }

    [Fact]
    public void TheBuildSucceeds_EvenThoughTheFileNameIsNotAssemblyNameDotVersion()
        => Assert.True(_b.ExitCode == 0,
            "the copy rebuilt a file name pack never wrote:" + Environment.NewLine + _b.Output);

    [Fact]
    public void TheFeedGetsTheFilePackActuallyWrote()
    {
        // <PackageId>, not <AssemblyName>; and without the +metadata that $(Version) still carries.
        var expected = Path.Combine(_b.Feed, $"{PackNamingScratchBuild.PackageId}.{PackNamingScratchBuild.FileVersion}.nupkg");

        Assert.True(File.Exists(expected),
            $"expected {Path.GetFileName(expected)} in the feed, found: "
            + string.Join(", ", Directory.GetFiles(_b.Feed).Select(Path.GetFileName))
            + Environment.NewLine + _b.Output);
        Assert.False(File.Exists(Path.Combine(_b.Feed, $"{PackNamingScratchBuild.AssemblyName}.{PackNamingScratchBuild.PackageVersion}.nupkg")));
    }

    [Fact]
    public void TheFeedCleanStillSweepsTheOldVersionAndSparesTheNewOne()
    {
        Assert.False(File.Exists(Path.Combine(_b.Feed, $"{PackNamingScratchBuild.PackageId}.1.0.0.nupkg")),
            "the superseded package should have been swept" + Environment.NewLine + _b.Output);
        Assert.True(File.Exists(Path.Combine(_b.Feed, $"{PackNamingScratchBuild.PackageId}.{PackNamingScratchBuild.FileVersion}.nupkg")));
    }
}

/// <summary>
/// The copy reconstructs pack's file name, so it models NuGet's normalization and can be incomplete.
/// These pin both halves of that bargain: the rules that ARE modelled must produce the name pack
/// writes, and anything that is not must fail the build loudly rather than silently leaving the feed
/// on stale bytes. Measured 2026-09-05 against the real targets: 1.2 -&gt; pack writes 1.2.0,
/// 1.0.010 -&gt; pack writes 1.0.10.
/// </summary>
public class PackVersionNormalizationTests
{
    private const string Id = "Lz.Scratch.Norm";

    private static (int ExitCode, string Output, string[] Feed, string[] Bin) Build(string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "lz-norm-" + Guid.NewGuid().ToString("N")[..8]);
        var proj = Path.Combine(root, "proj");
        var feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(feed);
        try
        {
            File.WriteAllText(Path.Combine(proj, "Class1.cs"), "namespace Scratch; public class Class1 { }");
            var projPath = Path.Combine(proj, "Scratch.csproj");
            File.WriteAllText(projPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{Id}</AssemblyName>
    <SolutionDir>{PackageHandlingScratchBuild.FindLzRepoRoot()}{Path.DirectorySeparatorChar}</SolutionDir>
  </PropertyGroup>
  <Import Project=""$(SolutionDir)CommonPackageHandling.targets"" />
  <PropertyGroup>
    <Version>{version}</Version>
    <PackageVersion>{version}</PackageVersion>
  </PropertyGroup>
</Project>
");
            var (exit, output) = PackageHandlingScratchBuild.RunDotnet(proj,
                "build", projPath, "-nologo", "-v:m", $"-p:PackageRepoFolder={feed}");
            var bin = Path.Combine(proj, "bin", "Debug");
            return (exit, output,
                Directory.GetFiles(feed, "*.nupkg").Select(Path.GetFileName).ToArray()!,
                Directory.Exists(bin) ? Directory.GetFiles(bin, "*.nupkg").Select(Path.GetFileName).ToArray()! : Array.Empty<string>());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("7.7.7.0+gdeadbee", "7.7.7")]                 // both rules at once, at copy level
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]                              // padded to three parts
    [InlineData("3.2.1.0", "3.2.1")]                          // a fourth part of .0 dropped
    [InlineData("7.7.7-beta.1+gdeadbee", "7.7.7-beta.1")]     // build metadata dropped
    [InlineData("2.0.0-Rc.1", "2.0.0-Rc.1")]                  // prerelease case preserved
    public void TheModelledRulesProduceTheNamePackWrites(string version, string expected)
    {
        var r = Build(version);

        Assert.True(r.ExitCode == 0, r.Output);
        Assert.Equal(new[] { $"{Id}.{expected}.nupkg" }, r.Bin.Where(f => !f.EndsWith(".snupkg")).ToArray());
        Assert.Contains($"{Id}.{expected}.nupkg", r.Feed);
    }

    [Fact]
    public void AnUnmodelledVersionFailsTheBuild_RatherThanSilentlyLeavingTheFeedStale()
    {
        // NuGet strips leading zeros; this reconstruction does not. The point is not that it should
        // — it is that the miss must be loud. As a Warning this build was green, exit 0, with the
        // feed still serving whatever it held before.
        var r = Build("1.0.010");

        Assert.False(r.ExitCode == 0, "an unmodelled version must fail the build:" + Environment.NewLine + r.Output);
        Assert.Contains("Pack produced no", r.Output);
        Assert.Contains($"{Id}.1.0.10.nupkg", r.Bin);   // what pack actually wrote
        Assert.Empty(r.Feed);
    }
}
