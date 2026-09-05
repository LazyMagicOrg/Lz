namespace Lz.Core.Repos;

/// <summary>What the target folder looks like before anything is cloned.</summary>
public enum TargetState
{
    /// <summary>No such folder.</summary>
    Missing,
    /// <summary>The folder exists but holds nothing — a safe clone target.</summary>
    Empty,
    /// <summary>Already a git working copy. This is the SKIP case the command exists to honour.</summary>
    GitRepo,
    /// <summary>Populated, but not a git repo. Cloning would fail; overwriting would destroy data.</summary>
    Occupied,
}

/// <summary>What the command decided to do with one manifest entry.</summary>
public enum CloneAction
{
    /// <summary>Clone it.</summary>
    Clone,
    /// <summary>Already present as a git repo — skip, per the command's contract.</summary>
    SkipPresent,
    /// <summary>Present but not a repo — skip and say so loudly.</summary>
    SkipOccupied,
    /// <summary>The manifest entry is unusable; nothing is attempted.</summary>
    Invalid,
}

/// <summary>One resolved decision. Pure data — nothing here has touched the disk or the network.</summary>
public readonly record struct CloneDecision(
    string Name,
    string Path,
    string? Url,
    string? Branch,
    CloneAction Action,
    string Reason)
{
    public bool IsFailure => Action == CloneAction.Invalid;
}

/// <summary>
/// PURE decision rules for <c>lz clonerepos</c>: manifest + environment + what is on disk, in;
/// a plan, out. No git, no filesystem, no console — the same split
/// <see cref="RepoStatusLogic"/> uses, and for the same reason: the interesting behaviour is the
/// rules, and rules that need a network to test do not get tested.
/// </summary>
public static class CloneReposLogic
{
    /// <summary>
    /// Map lz's environment string onto the manifest's three. Returns false for anything else
    /// rather than guessing — <c>--env staging</c> should be told the manifest has no such column,
    /// not quietly served the dev branch.
    /// </summary>
    public static bool TryParseEnvironment(string? env, out RepoEnvironment parsed)
    {
        parsed = RepoEnvironment.Dev;
        if (string.IsNullOrWhiteSpace(env)) return false;
        switch (env.Trim().ToLowerInvariant())
        {
            case "dev": parsed = RepoEnvironment.Dev; return true;
            case "test": parsed = RepoEnvironment.Test; return true;
            case "prod": parsed = RepoEnvironment.Prod; return true;
            default: return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> starts with a Windows drive designator — the absolute
    /// form (<c>C:/Windows</c>) or the drive-RELATIVE form (<c>C:Windows</c>), which resolves
    /// against that drive's current directory and escapes just as effectively.
    ///
    /// <para><b>Public and separate on purpose, so it can be tested at all.</b> Inside
    /// <see cref="IsSafeRelativePath"/> this check is invisible on Windows:
    /// <see cref="System.IO.Path.IsPathRooted"/> already rejects every drive form there, so
    /// deleting the check leaves the end-to-end tests GREEN on a Windows runner while the Linux
    /// behaviour it exists for silently regresses. Verified by mutation on 2026-09-05 — with the
    /// call removed, all seven drive cases still passed. Asserting this predicate directly is what
    /// makes the rule pinned rather than incidentally satisfied, because it is pure string logic
    /// and reaches the same verdict on every platform.</para>
    ///
    /// <para>Deliberately narrow: a single ASCII letter then a colon. <c>a:b</c> IS a designator
    /// (drive A:, relative); <c>ab:cd</c> is not. A colon deeper in the path is left alone — it is
    /// a legal POSIX filename character and not an escape.</para>
    /// </summary>
    public static bool HasDriveDesignator(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        return p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':';
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> is a safe target: relative, and inside the
    /// workspace root once normalised.
    ///
    /// <para><b>This is a real guard, not defensive noise.</b> The manifest is a file, and a
    /// <c>Path</c> of <c>../../somewhere</c> or <c>C:\Windows</c> would have the command clone
    /// outside the workspace it was pointed at. Rejecting it here keeps every write under the root
    /// the operator named.</para>
    ///
    /// <para><b>Platform independence is part of the contract.</b> The checks below must reach the
    /// same verdict on every OS, because the guard reads untrusted input and the deployer is
    /// expected to run off-Windows. Two of the three already do: normalising <c>\</c> to <c>/</c>
    /// makes <c>..\..\outside</c> traverse identically everywhere, and it turns a UNC
    /// <c>\\server\share</c> into <c>//server/share</c>, which is rooted on both. The exception was
    /// the DRIVE DESIGNATOR — <see cref="System.IO.Path.IsPathRooted"/> is <c>true</c> for
    /// <c>C:/Windows</c> on Windows and <c>false</c> on Linux, so that one form was accepted
    /// off-Windows and is now rejected explicitly. Surfaced by running the suite on an ubuntu
    /// runner (2026-09-05).
    /// </para>
    /// </summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var p = relativePath.Trim().Replace('\\', '/');
        if (p == ".") return true;

        // Checked BEFORE IsPathRooted and independently of it, because that call is the one line
        // here whose answer varies by platform. See HasDriveDesignator for why it is a separate
        // public predicate rather than an inline condition.
        if (HasDriveDesignator(p)) return false;

        if (System.IO.Path.IsPathRooted(p)) return false;
        if (p.StartsWith("~", StringComparison.Ordinal)) return false;

        var depth = 0;
        foreach (var segment in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (--depth < 0) return false; continue; }
            depth++;
        }
        return true;
    }

    /// <summary>Decide one entry. <paramref name="state"/> is what the caller observed on disk.</summary>
    public static CloneDecision Decide(RepoEntry entry, RepoEnvironment env, TargetState state)
    {
        var name = string.IsNullOrWhiteSpace(entry?.Name) ? "(unnamed)" : entry!.Name!.Trim();
        var path = entry?.Path?.Trim() ?? string.Empty;

        if (entry is null)
            return new CloneDecision(name, path, null, null, CloneAction.Invalid, "entry is empty");

        if (string.IsNullOrWhiteSpace(entry.Name))
            return new CloneDecision(name, path, entry.Url, null, CloneAction.Invalid, "Name is required");

        if (string.IsNullOrWhiteSpace(entry.Path))
            return new CloneDecision(name, path, entry.Url, null, CloneAction.Invalid, "Path is required");

        if (!IsSafeRelativePath(entry.Path))
            return new CloneDecision(name, path, entry.Url, null, CloneAction.Invalid,
                $"Path '{entry.Path}' must be relative and stay inside the workspace root");

        var branch = entry.Branches?.For(env);
        if (branch is null)
            return new CloneDecision(name, path, entry.Url, null, CloneAction.Invalid,
                $"no branch declared for '{env.ToString().ToLowerInvariant()}' — add it under Branches");

        if (string.IsNullOrWhiteSpace(entry.Url))
            return new CloneDecision(name, path, null, branch, CloneAction.Invalid, "Url is required");

        // The contract: already there means leave it alone. Reporting the branch it WOULD have
        // used keeps a mismatch visible without acting on it.
        return state switch
        {
            TargetState.GitRepo => new CloneDecision(name, path, entry.Url, branch,
                CloneAction.SkipPresent, "already cloned"),
            TargetState.Occupied => new CloneDecision(name, path, entry.Url, branch,
                CloneAction.SkipOccupied, "folder exists and is not a git repository — left untouched"),
            _ => new CloneDecision(name, path, entry.Url, branch, CloneAction.Clone, "missing"),
        };
    }

    /// <summary>
    /// Build the whole plan. <paramref name="probe"/> maps a relative path to what is on disk, so
    /// tests supply a dictionary and the runner supplies the filesystem.
    /// </summary>
    public static IReadOnlyList<CloneDecision> Plan(
        RepoManifest manifest,
        RepoEnvironment env,
        Func<string, TargetState> probe,
        string? onlyRepo = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(probe);

        var decisions = new List<CloneDecision>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.Repos ?? new List<RepoEntry>())
        {
            if (onlyRepo is not null &&
                !string.Equals(entry?.Name?.Trim(), onlyRepo.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            // Probe only once the entry is worth probing — an invalid Path must not reach the disk.
            var path = entry?.Path?.Trim();
            var state = IsSafeRelativePath(path) ? probe(Normalize(path!)) : TargetState.Missing;

            var decision = Decide(entry!, env, state);

            // Duplicates are a manifest bug that would otherwise present as a confusing git error
            // ("destination path already exists") on the SECOND entry only.
            if (decision.Action != CloneAction.Invalid)
            {
                if (!seenNames.Add(decision.Name))
                    decision = decision with { Action = CloneAction.Invalid, Reason = "duplicate Name in the manifest" };
                else if (!seenPaths.Add(Normalize(decision.Path)))
                    decision = decision with { Action = CloneAction.Invalid, Reason = "duplicate Path in the manifest" };
            }

            decisions.Add(decision);
        }

        return decisions;
    }

    /// <summary>Canonical form for comparing manifest paths: forward slashes, no trailing slash.</summary>
    public static string Normalize(string path)
    {
        var p = (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        return p.Length == 0 ? "." : p;
    }

    /// <summary>The git arguments for one clone. Public so a test can pin them without running git.</summary>
    public static string[] CloneArguments(string url, string branch, string targetPath) =>
        new[] { "clone", "--branch", branch, "--", url, targetPath };
}
