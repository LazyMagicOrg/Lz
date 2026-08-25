using Lz.Core.Repos;

namespace Lz.Tests.Repos.Tests;

/// <summary>
/// Filesystem-backed tests for workspace-root discovery. The regression these pin: FindWorkspaceRoot
/// used to walk up looking for a <c>repos/</c> folder UNCONDITIONALLY and overshoot a self-contained
/// multi-repo git workspace (an NTS-style <c>_Dev_NTS</c>) whenever a generic <c>~/repos</c> existed
/// anywhere higher up — reporting unrelated sibling checkouts instead of the workspace's own repos.
///
/// <para>Every FindWorkspaceRoot case here resolves WITHIN its temp tree (it matches a workspace
/// signal before the walk could escape upward), so the assertions do not depend on whatever real
/// directories sit above the system temp folder.</para>
/// </summary>
public sealed class RepoDiscoveryTests : IDisposable
{
    private readonly string _root;

    public RepoDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lz-repos-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Marks a directory as a git repo by creating the <c>.git</c> folder IsRepo looks for.</summary>
    private string Repo(params string[] parts)
    {
        var p = Dir(parts);
        Directory.CreateDirectory(Path.Combine(p, ".git"));
        return p;
    }

    private static string N(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    [Fact]
    public void FindWorkspaceRoot_ResolvesToTheMultiRepoWorkspace_NotADistantReposFolder()
    {
        // A generic repos/ folder holds unrelated sibling checkouts AND (deep under it) the
        // NTS-style workspace. Standing in the workspace must resolve to the workspace itself.
        Repo("home", "repos", "sibling");                          // ~/repos/sibling (unrelated)
        var workspace = Repo("home", "repos", "_NTS", "_Dev_NTS");  // itself a git repo...
        Repo("home", "repos", "_NTS", "_Dev_NTS", "subA");         // ...with nested repos -> a workspace
        Repo("home", "repos", "_NTS", "_Dev_NTS", "subB");

        Assert.Equal(N(workspace), N(RepoDiscovery.FindWorkspaceRoot(workspace)), ignoreCase: true);
    }

    [Fact]
    public void FindWorkspaceRoot_FromBareNestedRepo_ResolvesToEnclosingWorkspace()
    {
        var workspace = Repo("home", "repos", "_NTS", "_Dev_NTS");
        var nested = Repo("home", "repos", "_NTS", "_Dev_NTS", "lz"); // bare: no nested repos of its own
        Repo("home", "repos", "_NTS", "_Dev_NTS", "subB");

        // A nested repo never shadows the workspace it belongs to.
        Assert.Equal(N(workspace), N(RepoDiscovery.FindWorkspaceRoot(nested)), ignoreCase: true);
    }

    [Fact]
    public void FindWorkspaceRoot_SiblingReposLayout_ResolvesToReposParent()
    {
        var home = Dir("home");
        Repo("home", "repos", "repoA");   // bare siblings under repos/, no nested repos
        Repo("home", "repos", "repoB");

        // No self-contained workspace above repoA, so resolve to the repos/ parent (not repoA).
        Assert.Equal(N(home), N(RepoDiscovery.FindWorkspaceRoot(Path.Combine(home, "repos", "repoA"))),
            ignoreCase: true);
    }

    [Fact]
    public void FindWorkspaceRoot_EmptyReposFolderIsNotAWorkspace()
    {
        // A repos/ folder with no repos in it must NOT be treated as a workspace root; fall through
        // to the enclosing self-contained workspace instead.
        var workspace = Repo("home", "ws");
        Repo("home", "ws", "subA");
        Dir("home", "ws", "repos");        // empty repos/ (a red herring)
        var nested = Repo("home", "ws", "subB");

        Assert.Equal(N(workspace), N(RepoDiscovery.FindWorkspaceRoot(nested)), ignoreCase: true);
    }

    [Fact]
    public void Discover_ReturnsRootFirstThenNestedRepoChildren()
    {
        var workspace = Repo("ws");
        var subA = Repo("ws", "subA");
        var subB = Repo("ws", "subB");
        Dir("ws", "not-a-repo");           // plain folder -> ignored

        var found = RepoDiscovery.Discover(workspace).Select(N).ToList();

        Assert.Equal(N(workspace), found[0]);                       // root first
        Assert.Contains(N(subA), found);
        Assert.Contains(N(subB), found);
        Assert.Equal(3, found.Count);
    }
}
