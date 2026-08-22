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
    /// Walk up from <paramref name="startDirectory"/> and return the first ancestor that looks like
    /// a workspace root: it contains a <c>repos/</c> child, or is itself a git repository. The
    /// <c>repos/</c> test comes first so a workspace whose root is also a git repo still resolves
    /// to the root rather than to a nested repo the caller happens to be standing in.
    /// Falls back to the start directory when nothing matches.
    /// </summary>
    public static string FindWorkspaceRoot(string? startDirectory = null)
    {
        var start = startDirectory ?? Directory.GetCurrentDirectory();

        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ReposFolder)))
                return dir.FullName;
        }

        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (IsRepo(dir.FullName)) return dir.FullName;
        }

        return start;
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
