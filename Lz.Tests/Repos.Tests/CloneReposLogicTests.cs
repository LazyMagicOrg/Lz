using Lz.Core.Repos;

namespace Lz.Tests.Repos.Tests;

/// <summary>
/// Pins the clonerepos decision rules. Everything here is pure — no git, no network, no filesystem:
/// the disk is supplied as a probe function, which is the whole reason the plan was split out of
/// the runner. A rule that needs a network to test does not get tested.
/// </summary>
public class CloneReposLogicTests
{
    private static RepoEntry Entry(
        string name = "Service",
        string path = "repos/Service",
        string? url = "git@github.com:Org/Service.git",
        string? dev = "main",
        string? test = "main",
        string? prod = "main") => new()
    {
        Name = name,
        Path = path,
        Url = url,
        Branches = new RepoBranches { Dev = dev, Test = test, Prod = prod },
    };

    private static RepoManifest Manifest(params RepoEntry[] entries) => new() { Repos = entries.ToList() };

    // ---------- the core contract: present means skip ----------

    [Fact]
    public void AnExistingGitRepo_IsSkipped_NotRecloned()
    {
        // THE contract of this command. Re-running it on a populated workspace must be a no-op,
        // because that is how someone fills in the two repos they are missing.
        var d = CloneReposLogic.Decide(Entry(), RepoEnvironment.Dev, TargetState.GitRepo);

        Assert.Equal(CloneAction.SkipPresent, d.Action);
    }

    [Fact]
    public void AMissingOrEmptyFolder_IsCloned()
    {
        Assert.Equal(CloneAction.Clone,
            CloneReposLogic.Decide(Entry(), RepoEnvironment.Dev, TargetState.Missing).Action);

        // An empty folder is a safe target — git clone into it succeeds, and this is what a
        // half-finished earlier run or a `mkdir` leaves behind.
        Assert.Equal(CloneAction.Clone,
            CloneReposLogic.Decide(Entry(), RepoEnvironment.Dev, TargetState.Empty).Action);
    }

    [Fact]
    public void APopulatedNonRepoFolder_IsSkippedLoudly_NeverOverwritten()
    {
        // The dangerous case. Cloning would fail anyway, but the reason must reach the operator:
        // silently reporting "skipped" alongside genuinely-cloned repos would hide real data
        // sitting where a repo is supposed to be.
        var d = CloneReposLogic.Decide(Entry(), RepoEnvironment.Dev, TargetState.Occupied);

        Assert.Equal(CloneAction.SkipOccupied, d.Action);
        Assert.Contains("not a git repository", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- per-environment branch selection ----------

    [Theory]
    [InlineData(RepoEnvironment.Dev, "develop")]
    [InlineData(RepoEnvironment.Test, "staging")]
    [InlineData(RepoEnvironment.Prod, "release")]
    public void TheBranchComesFromTheEnvironmentColumn(RepoEnvironment env, string expected)
    {
        var d = CloneReposLogic.Decide(
            Entry(dev: "develop", test: "staging", prod: "release"), env, TargetState.Missing);

        Assert.Equal(expected, d.Branch);
    }

    [Fact]
    public void AMissingEnvironmentColumn_IsAnError_NotADefaultToDev()
    {
        // The rule this exists to prevent: quietly falling back to Dev would clone the wrong branch
        // for prod and surface as a build or deploy failure with nothing pointing back at the file.
        var d = CloneReposLogic.Decide(Entry(prod: null), RepoEnvironment.Prod, TargetState.Missing);

        Assert.Equal(CloneAction.Invalid, d.Action);
        Assert.Contains("prod", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dev", RepoEnvironment.Dev)]
    [InlineData("TEST", RepoEnvironment.Test)]
    [InlineData(" prod ", RepoEnvironment.Prod)]
    public void EnvironmentParsing_IsCaseAndWhitespaceTolerant(string input, RepoEnvironment expected)
    {
        Assert.True(CloneReposLogic.TryParseEnvironment(input, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownEnvironment_IsRejected_NotCoercedToDev(string? input)
    {
        Assert.False(CloneReposLogic.TryParseEnvironment(input, out _));
    }

    // ---------- path safety ----------

    [Theory]
    [InlineData(".")]
    [InlineData("repos/Service")]
    [InlineData("repos\\Service")]
    [InlineData("a/b/../c")]
    public void SafePaths_AreAccepted(string path) =>
        Assert.True(CloneReposLogic.IsSafeRelativePath(path));

    [Theory]
    [InlineData("../outside")]
    [InlineData("repos/../../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows")]
    [InlineData("~/somewhere")]
    [InlineData("")]
    [InlineData(null)]
    public void PathsThatEscapeTheWorkspace_AreRejected(string? path)
    {
        // The manifest is a file, so Path is untrusted input: without this the command would clone
        // outside the root the operator named.
        Assert.False(CloneReposLogic.IsSafeRelativePath(path));
    }

    [Fact]
    public void AnEscapingPath_IsInvalid_AndIsNeverProbed()
    {
        var probed = new List<string>();
        var plan = CloneReposLogic.Plan(
            Manifest(Entry(path: "../../elsewhere")),
            RepoEnvironment.Dev,
            p => { probed.Add(p); return TargetState.Missing; });

        Assert.Equal(CloneAction.Invalid, Assert.Single(plan).Action);
        Assert.Empty(probed);   // the disk is never touched on behalf of a rejected path
    }

    // ---------- required fields ----------

    [Fact]
    public void MissingNameUrlOrPath_AreEachInvalid()
    {
        Assert.Equal(CloneAction.Invalid,
            CloneReposLogic.Decide(Entry(name: ""), RepoEnvironment.Dev, TargetState.Missing).Action);
        Assert.Equal(CloneAction.Invalid,
            CloneReposLogic.Decide(Entry(path: ""), RepoEnvironment.Dev, TargetState.Missing).Action);
        Assert.Equal(CloneAction.Invalid,
            CloneReposLogic.Decide(Entry(url: null), RepoEnvironment.Dev, TargetState.Missing).Action);
    }

    // ---------- manifest-level rules ----------

    [Fact]
    public void DuplicatePathsAndNames_AreCaughtInTheManifest_NotByGit()
    {
        // Left to git, the second entry fails with "destination path already exists", which reads
        // like a disk problem rather than a typo in the file.
        var byPath = CloneReposLogic.Plan(
            Manifest(Entry(name: "A", path: "repos/X"), Entry(name: "B", path: "repos/X")),
            RepoEnvironment.Dev, _ => TargetState.Missing);

        Assert.Equal(CloneAction.Clone, byPath[0].Action);
        Assert.Equal(CloneAction.Invalid, byPath[1].Action);
        Assert.Contains("duplicate Path", byPath[1].Reason, StringComparison.OrdinalIgnoreCase);

        var byName = CloneReposLogic.Plan(
            Manifest(Entry(name: "A", path: "repos/X"), Entry(name: "A", path: "repos/Y")),
            RepoEnvironment.Dev, _ => TargetState.Missing);

        Assert.Contains("duplicate Name", byName[1].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyRepo_NarrowsThePlanToOneEntry()
    {
        var plan = CloneReposLogic.Plan(
            Manifest(Entry(name: "A", path: "repos/A"), Entry(name: "B", path: "repos/B")),
            RepoEnvironment.Dev, _ => TargetState.Missing, onlyRepo: "b");   // case-insensitive

        Assert.Equal("B", Assert.Single(plan).Name);
    }

    [Fact]
    public void PathsAreNormalizedBeforeComparison()
    {
        Assert.Equal("repos/Service", CloneReposLogic.Normalize("repos\\Service\\"));
        Assert.Equal(".", CloneReposLogic.Normalize(""));
    }

    // ---------- the git command line ----------

    [Fact]
    public void CloneArguments_PinTheBranch_AndTerminateOptions()
    {
        var args = CloneReposLogic.CloneArguments("git@github.com:Org/R.git", "main", "repos/R");

        Assert.Equal(new[] { "clone", "--branch", "main", "--", "git@github.com:Org/R.git", "repos/R" }, args);

        // The `--` matters: without it a URL or path that begins with a dash would be parsed by git
        // as an option. It costs nothing and removes the class entirely.
        Assert.Contains("--", args);
    }

    // ---------- the manifest parser ----------

    [Fact]
    public void Manifest_ParsesTheDocumentedShape()
    {
        var manifest = RepoManifest.Parse("""
            Repos:
              - Name: Service
                Path: repos/Service
                Url: git@github.com:Org/Service.git
                Branches:
                  Dev: main
                  Test: release/1.0
                  Prod: release/1.0
            """);

        var entry = Assert.Single(manifest.Repos);
        Assert.Equal("Service", entry.Name);
        Assert.Equal("repos/Service", entry.Path);
        Assert.Equal("release/1.0", entry.Branches!.For(RepoEnvironment.Prod));
        Assert.Equal("main", entry.Branches!.For(RepoEnvironment.Dev));
    }

    [Fact]
    public void Manifest_BlankBranchIsTreatedAsAbsent()
    {
        var branches = new RepoBranches { Dev = "  ", Test = "main", Prod = null };

        Assert.Null(branches.For(RepoEnvironment.Dev));
        Assert.Equal("main", branches.For(RepoEnvironment.Test));
        Assert.Null(branches.For(RepoEnvironment.Prod));
    }

    [Fact]
    public void Manifest_MalformedYaml_NamesTheFileAndThePosition()
    {
        // An operator who mistyped an indent should get a line number, not a stack trace.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RepoManifest.Parse("Repos:\n  - Name: A\n   Path: bad-indent\n", "repos.yaml"));

        Assert.Contains("repos.yaml", ex.Message);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
