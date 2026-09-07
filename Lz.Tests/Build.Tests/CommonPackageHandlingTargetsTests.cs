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
         that without touching the real targets file. The real targets stopped supplying a version on
         2026-09-06, when Lz moved to Nerdbank.GitVersioning; the single-framework fixture above still
         never sees 1.0.0 because it sets its own <Version> AFTER the import, not because the targets
         supply one. <Version> is the line that
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
/// scaffold and no SDK: they only need the load-bearing lines present in the working copies.
///
/// <para><b>WHAT CI ACTUALLY COVERS.</b> <c>lz-ci.yml</c> checks out exactly two repos — this one at
/// <c>repos/Lz</c>, and the public <c>LazyMagicOrg/LazyMagic</c> at <c>repos/LazyMagic</c>. So on CI
/// these theories cover the <b>Lz and LazyMagic copies and nothing else</b>; Service's and
/// BaseAppLib's copies are verified only by a local run in a full workspace.</para>
///
/// <para><b>WHY CI CANNOT SIMPLY CHECK OUT THE OTHER TWO.</b> Service and BaseAppLib are PRIVATE
/// <c>Scutara</c> repos, and this workflow runs in <c>LazyMagicOrg</c>, which holds no credential for
/// them. The <c>scutara-ci</c> App is registered "Only on this account" precisely because every
/// cross-org checkout it performs today is public (GitHubApp.md §1). Widening CI to four would mean
/// re-registering that App as "Any account", installing it on LazyMagicOrg, and storing its private
/// key as a secret on a PUBLIC repo — a permanent read path into eleven private repos, bought to
/// widen a text assertion from two files to four. Deliberately not done, so the reduced coverage is
/// permanent and has to be MANAGED rather than fixed.</para>
///
/// <para><b>HOW THAT IS MANAGED, AND WHAT IT REPLACES.</b> Until 2026-09-07 the data source yielded
/// only files that happened to exist, so CI ran 2 cases per theory where a local run ran 4 — no
/// skips, no warning, a green tick. That is the whole 4-test gap between a local run (570) and CI
/// (566) on db7d249. It is an inert guard in its data-driven form: absent input produces no test
/// case, and no test case is indistinguishable from a passing one. It also could not tell "Service
/// is not checked out here" from "Service's copy was renamed and nobody noticed" — the second is
/// exactly the drift these assertions exist to catch, and it read green too.
///
/// <see cref="Roster"/> is now a constant four, so the case count cannot shrink, and an absent copy
/// must be <b>declared</b> rather than inferred:
/// <list type="bullet">
///   <item>copy present → checked, as before.</item>
///   <item>copy absent, its repo checked out → <b>FAIL</b>. Renamed, moved or deleted.</item>
///   <item>copy absent, its repo absent, and NOT named in <c>LZ_TARGETS_COPIES_ABSENT</c> →
///   <b>FAIL</b>. This is the case that used to vanish.</item>
///   <item>copy absent and named in <c>LZ_TARGETS_COPIES_ABSENT</c> → allowed. lz-ci.yml sets that
///   variable to <c>Service,BaseAppLib</c>, in plain sight beside the checkout steps that cause it.</item>
///   <item>copy named in <c>LZ_TARGETS_COPIES_ABSENT</c> but actually PRESENT → <b>FAIL</b>. A stale
///   declaration is caught from the other side, so the variable cannot outlive its reason.</item>
/// </list>
/// The declaration is the point: reduced coverage becomes a reviewed line in a workflow file that
/// someone had to write, instead of an accident of the filesystem that nothing records.
///
/// A useful side effect, and the cheapest way to check this stayed true: the case count no longer
/// depends on what is on disk, so CI and a full local workspace report the SAME suite total —
/// measured 2026-09-07, both 577, where they were 566 and 570 before. If they diverge again, some
/// suite has started varying with the checkout.</para>
///
/// <para><b>WHY NOT A SKIP.</b> A dynamically-skipped case would be the natural fit — the run summary
/// would go from <c>Passed: 12</c> to <c>Passed: 8, Skipped: 4</c> and say so in one line. It does not
/// work on the pinned toolchain. Both halves of that were measured on the assemblies actually
/// restored here, not assumed: the shipped <c>xunit.assert.dll</c> (2.9.2) exposes no
/// <c>Assert.Skip</c> at all — reflection over <c>Xunit.Assert</c> returns no such member, and the
/// call does not compile — while <c>Xunit.Sdk.SkipException.ForSkip</c> IS public; and throwing that
/// exception is reported by xunit.runner.visualstudio 2.8.2 under <c>dotnet test</c> as a plain
/// FAILURE, not a skip (measured 2026-09-07 with the two repos hidden: <c>Failed: 7, Skipped: 0</c>).
/// So there is no third outcome available here — a case either passes or fails — which is what forces
/// the declaration below rather than a softer report. Worth re-testing if Lz.Tests ever moves off
/// xunit 2.9.2; until then this is the loudest option the runner allows.</para>
/// </summary>
public class PackagingTargetsReplicationTests
{
    /// <summary>
    /// Comma-separated repo names whose copy this environment is KNOWN not to have. Set by lz-ci.yml.
    /// Anything absent and not named here fails.
    /// </summary>
    public const string AbsenceDeclarationVariable = "LZ_TARGETS_COPIES_ABSENT";

    /// <summary>One replicated copy: the repo that owns it, and the file inside it.</summary>
    public readonly record struct Copy(string Repo, string File)
    {
        public override string ToString() => Path.Combine(Repo, File);
    }

    /// <summary>
    /// THE FULL EXPECTED ROSTER — a constant, deliberately. Adding a fifth producer means adding a
    /// line here, and until then that producer's copy is uncovered everywhere and nothing says so.
    /// </summary>
    public static readonly Copy[] Roster =
    [
        new("Lz",         "CommonPackageHandling.targets"),
        new("LazyMagic",  "CommonPackageHandling.targets"),
        new("Service",    "CommonPackageHandling.targets"),
        new("BaseAppLib", "MakePackage.targets"),
    ];

    /// <summary>Every roster entry, always — present or not. Four cases, locally and on CI alike.</summary>
    public static TheoryData<Copy> Copies()
    {
        var data = new TheoryData<Copy>();
        foreach (var c in Roster) data.Add(c);
        return data;
    }

    private static string LzRoot => PackageHandlingScratchBuild.FindLzRepoRoot();

    /// <summary>Where the repo owning this copy would live, checked out or not.</summary>
    public static string RepoDir(Copy c)
        // Lz's own root is KNOWN rather than guessed from a folder name: a clone that is not
        // literally named "Lz" — a worktree, a second clone, `git clone <url> lz-fix` — still
        // resolves here, where the sibling-name form silently found nothing and dropped the one
        // copy this suite is certain of.
        => c.Repo == "Lz" ? LzRoot : Path.Combine(Directory.GetParent(LzRoot)!.FullName, c.Repo);

    public static string CopyPath(Copy c) => Path.Combine(RepoDir(c), c.File);

    /// <summary>The repos this environment declares it does not have.</summary>
    public static IReadOnlySet<string> DeclaredAbsent()
        => DeclaredAbsent(Environment.GetEnvironmentVariable(AbsenceDeclarationVariable));

    /// <summary>Parsing split out from the environment read, so it can be tested directly.</summary>
    public static IReadOnlySet<string> DeclaredAbsent(string? value)
        => new HashSet<string>(
            (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The text of one copy, or a failure naming which of the three absences this is. Returns null
    /// only for a copy this environment has DECLARED absent — the one case a caller may pass over.
    /// See the class remarks for why absence is declared rather than inferred.
    /// </summary>
    private static string? ReadDeclaredCopy(Copy c)
    {
        var repo = RepoDir(c);
        var path = CopyPath(c);
        if (File.Exists(path)) return File.ReadAllText(path);

        Assert.False(Directory.Exists(repo),
            $"{repo} IS checked out, but {c} is missing from it. The replicated targets file was "
            + "renamed, moved or deleted — that is the drift this suite exists to catch. Restore the "
            + $"file, or update {nameof(Roster)} in this file.");

        Assert.True(DeclaredAbsent().Contains(c.Repo),
            $"{c} is not covered by this run: {repo} is not checked out, and {c.Repo} is not named in "
            + $"{AbsenceDeclarationVariable}. Either check the repo out beside this one, or declare "
            + $"the gap by setting {AbsenceDeclarationVariable} (lz-ci.yml sets it to "
            + "\"Service,BaseAppLib\", which is exactly the coverage that workflow can have). "
            + "Silently running fewer cases is what this assertion replaced.");

        return null;
    }

    /// <summary>
    /// The roster, checked against what this environment declares — in BOTH directions, so neither
    /// the coverage nor the declaration can drift without a red test. This is the one place the
    /// count is asserted rather than implied by a case total nobody reads.
    /// </summary>
    [Fact]
    public void TheCoverageRosterMatchesWhatThisEnvironmentDeclares()
    {
        var declared = DeclaredAbsent();
        var covered = Roster.Where(c => File.Exists(CopyPath(c))).ToList();
        var report = Environment.NewLine
            + $"covered: {string.Join(", ", covered)}" + Environment.NewLine
            + $"{AbsenceDeclarationVariable}: "
            + (declared.Count == 0 ? "(unset)" : string.Join(", ", declared));

        // A copy missing from a repo that IS here is drift, never a coverage gap.
        var drifted = Roster
            .Where(c => !File.Exists(CopyPath(c)) && Directory.Exists(RepoDir(c)))
            .ToList();
        Assert.True(drifted.Count == 0,
            "a checked-out repo has lost its replicated copy: " + string.Join(", ", drifted) + report);

        // Absent and undeclared: the case that used to disappear without trace.
        var undeclared = Roster
            .Where(c => !File.Exists(CopyPath(c)) && !declared.Contains(c.Repo))
            .ToList();
        Assert.True(undeclared.Count == 0,
            "these copies are not covered and the gap is not declared: "
            + string.Join(", ", undeclared) + report);

        // Declared absent but actually present: a stale declaration would let a REAL gap hide behind
        // it later, so the variable is not allowed to outlive its reason.
        var stale = declared.Where(r => Roster.Any(c => c.Repo == r && File.Exists(CopyPath(c)))).ToList();
        Assert.True(stale.Count == 0,
            $"{AbsenceDeclarationVariable} names repos that ARE checked out here: "
            + string.Join(", ", stale) + ". Remove them from the declaration." + report);

        // Every declared name must be a roster repo — a typo would otherwise excuse nothing while
        // looking like it excused something.
        var unknown = declared.Where(r => !Roster.Any(c => c.Repo == r)).ToList();
        Assert.True(unknown.Count == 0,
            $"{AbsenceDeclarationVariable} names repos that are not on the roster: "
            + string.Join(", ", unknown) + report);

        // The floor: two copies is what lz-ci has and the least a run may compare. One copy compares
        // nothing against anything, which is how TheSharedTargetsHaveNotDrifted below would pass
        // while checking a single file against itself.
        Assert.True(covered.Count >= 2, "fewer than two copies are visible." + report);
    }

    [Theory]
    [MemberData(nameof(Copies))]
    public void EveryCopyGuardsTheCrossTargetingOuterPass(Copy copy)
    {
        var text = ReadDeclaredCopy(copy);
        if (text is null) return;   // declared absent; the roster test above pins the declaration

        Assert.Contains("<Target Name=\"DeletePackage\"", text);
        Assert.Contains("Condition=\"'$(IsCrossTargetingBuild)' != 'true'\"", text);
    }

    [Theory]
    [MemberData(nameof(Copies))]
    public void EveryCopyRefusesToEvictWithNoVersion(Copy copy)
    {
        var text = ReadDeclaredCopy(copy);
        if (text is null) return;   // declared absent; the roster test above pins the declaration

        // Both delete branches, and the warning that replaces them.
        Assert.Equal(2, Regex.Matches(text, @"'\$\(_EvictVersion\)' != ''\s+AND\s+Exists\('\$\(PackageCacheFolder\)'\)").Count);
        Assert.Contains("No version could be determined", text);
    }

    /// <summary>
    /// Every target the four copies share, compared verbatim. Not one contiguous span: the repos
    /// legitimately differ BETWEEN these targets (LazyMagic carries a Newtonsoft.Json reference,
    /// Service a project-reference packing target), so widening the slice to cover the copy and the
    /// clean would fail on differences that are not drift.
    ///
    /// This one compares copies AGAINST EACH OTHER, so it works on the present set rather than one
    /// entry at a time — but it runs the same per-entry rules over the whole roster first, so an
    /// undeclared absence reddens it here exactly as it does above, and it names the copies it
    /// actually compared so a green tick on two is never mistaken for a green tick on four.
    /// </summary>
    [Theory]
    [InlineData("CopyPackage")]
    [InlineData("CleanExistingPackages")]
    [InlineData("DeleteSpecificPackage")]
    public void TheSharedTargetsHaveNotDrifted(string target)
    {
        var blocks = Roster
            .Select(c => (Copy: c, Text: ReadDeclaredCopy(c)))
            .Where(t => t.Text is not null)
            .Select(t => (t.Copy, Block: Target(t.Text!, target)))
            .ToList();

        var compared = string.Join(", ", blocks.Select(b => b.Copy));
        Assert.True(blocks.Count >= 2, $"nothing to compare {target} against; copies seen: {compared}");
        Assert.All(blocks, b => Assert.True(b.Block == blocks[0].Block,
            $"{target} has drifted in {b.Copy} away from {blocks[0].Copy}. Copies compared: {compared}"));
    }

    private static string Target(string text, string name)
    {
        var m = Regex.Match(text, $@"<Target Name=""{Regex.Escape(name)}"".*?</Target>", RegexOptions.Singleline);
        Assert.True(m.Success, $"target {name} was not found in a copy");
        // Normalise line endings. They are a git checkout artefact on Windows, not drift: with
        // core.autocrlf on, whichever of the four files git most recently materialised comes back
        // CRLF while the others stay LF, so a raw comparison fails for a reason that has nothing to
        // do with the targets. Observed 2026-09-06 - committing a version bump to LazyMagic turned
        // its copy CRLF and reddened all three cases at once.
        return m.Value.Replace("\r\n", "\n");
    }
}

/// <summary>
/// The declaration parser itself, which is load-bearing: every "this gap is allowed" verdict above
/// runs through it, and a parser that quietly matched nothing would turn the whole roster check back
/// into the permissive thing it replaced.
/// </summary>
public class AbsenceDeclarationTests
{
    private static IReadOnlySet<string> Parse(string? v)
        => PackagingTargetsReplicationTests.DeclaredAbsent(v);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",, ,")]
    public void NothingDeclaredExcusesNothing(string? value)
        => Assert.Empty(Parse(value));

    [Fact]
    public void TheCiDeclarationNamesExactlyTheTwoPrivateRepos()
    {
        // The literal lz-ci.yml sets. If that workflow's checkout list changes, this is the line that
        // has to change with it.
        var declared = Parse("Service,BaseAppLib");
        Assert.Equal(2, declared.Count);
        Assert.Contains("Service", declared);
        Assert.Contains("BaseAppLib", declared);
        Assert.DoesNotContain("LazyMagic", declared);
    }

    [Fact]
    public void SurroundingWhitespaceAndCaseDoNotDefeatTheDeclaration()
    {
        // A YAML author writing "Service, BaseAppLib" must not get a red gate for the space, and a
        // case mismatch must not silently fail to excuse a gap it was meant to excuse.
        var declared = Parse(" service ,  BASEAPPLIB ");
        Assert.Contains("Service", declared);
        Assert.Contains("BaseAppLib", declared);
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
