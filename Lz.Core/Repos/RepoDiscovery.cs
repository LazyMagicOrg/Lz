namespace Lz.Core.Repos;

/// <summary>
/// Finds the workspace root and the repositories under it.
///
/// <para><b>Deliberately independent of lz config.</b> This must work in any multi-repo checkout,
/// including one with no <c>systemconfig.*.yaml</c> at all — that is what makes the command useful
/// beyond a deployed system. Do not add a config load here.</para>
/// </summary>
public static class RepoDiscovery
{
    /// <summary>Conventional folder holding sibling repositories.</summary>
    public const string ReposFolder = "repos";

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> and return the NEAREST ancestor that looks
    /// like a multi-repo workspace root. At each level (nearest first) two signals are tested:
    /// <list type="number">
    ///   <item>a git repository whose immediate children include other git repositories — a
    ///     self-contained multi-repo workspace (a monorepo whose sub-projects are their own
    ///     repos, e.g. an NTS-style checkout). Tested FIRST so the walk stops at the workspace
    ///     you are standing in rather than overshooting to a generic <c>repos/</c> folder higher
    ///     up the tree (e.g. <c>~/repos</c>, which would otherwise capture unrelated sibling
    ///     checkouts);</item>
    ///   <item>a directory with a <c>repos/</c> child that holds at least one git repository — the
    ///     sibling-repos layout.</item>
    /// </list>
    /// Because both are evaluated in one nearest-first pass, a distant <c>repos/</c> can no longer
    /// shadow the workspace you are in. Standing in a bare nested repo (one with no nested repos of
    /// its own) matches neither signal at that level, so the walk continues up to the enclosing
    /// workspace root — a nested repo never shadows the workspace it belongs to. Falls back to the
    /// nearest enclosing git repo, then to the start directory, when nothing looks like a workspace.
    /// </summary>
    public static string FindWorkspaceRoot(string? startDirectory = null)
    {
        var start = startDirectory ?? Directory.GetCurrentDirectory();

        string? nearestRepo = null;
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            var path = dir.FullName;
            var isRepo = IsRepo(path);

            // (1) Self-contained multi-repo workspace: a repo whose immediate children are repos.
            if (isRepo && SafeChildren(path).Any(IsRepo))
                return path;

            // (2) Sibling-repos layout: a dir with a repos/ folder that actually holds repos.
            if (HasReposFolderWithRepo(path))
                return path;

            nearestRepo ??= isRepo ? path : null;
        }

        return nearestRepo ?? start;
    }

    /// <summary>True when <paramref name="dir"/> has a <c>repos/</c> child containing ≥1 git repo.</summary>
    private static bool HasReposFolderWithRepo(string dir)
    {
        var reposDir = Path.Combine(dir, ReposFolder);
        return Directory.Exists(reposDir) && SafeChildren(reposDir).Any(IsRepo);
    }

    /// <summary>
    /// Every repository belonging to the workspace: the root itself when it is one, its immediate
    /// children, and everything one level under <c>repos/</c>. Ordered root-first then by path, so
    /// output is stable regardless of filesystem enumeration order or completion order upstream.
    /// </summary>
    public static IReadOnlyList<string> Discover(string root)
    {
        var found = new List<string>();

        if (IsRepo(root)) found.Add(root);

        found.AddRange(SafeChildren(root).Where(IsRepo));

        var reposDir = Path.Combine(root, ReposFolder);
        if (Directory.Exists(reposDir))
            found.AddRange(SafeChildren(reposDir).Where(IsRepo));

        return found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => !PathsEqual(p, root))                 // root first
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A worktree or a submodule both surface <c>.git</c> — as a directory or a file.</summary>
    public static bool IsRepo(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>Path relative to the workspace root, using forward slashes; "." for the root itself.</summary>
    public static string DisplayName(string root, string repoPath)
    {
        if (PathsEqual(root, repoPath)) return ".";
        var rel = Path.GetRelativePath(root, repoPath).Replace('\\', '/');
        return rel.StartsWith("..", StringComparison.Ordinal) ? repoPath : rel;
    }

    private static IEnumerable<string> SafeChildren(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
