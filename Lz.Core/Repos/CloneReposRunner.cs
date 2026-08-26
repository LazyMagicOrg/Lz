using System.Text;
using System.Text.Json;

namespace Lz.Core.Repos;

/// <summary>Options for one clonerepos run.</summary>
public sealed class CloneReposOptions
{
    /// <summary>Workspace root. Defaults to <see cref="RepoDiscovery.FindWorkspaceRoot"/>.</summary>
    public string? Root { get; init; }

    /// <summary>Which environment's branch column to use.</summary>
    public RepoEnvironment Environment { get; init; } = RepoEnvironment.Dev;

    /// <summary>Plan and print, clone nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Limit the run to one manifest entry by Name.</summary>
    public string? OnlyRepo { get; init; }

    /// <summary>Clone with <c>--depth 1</c>. Fast, but the history is not there afterwards.</summary>
    public bool Shallow { get; init; }

    /// <summary>Per-clone timeout. Clones are far slower than the status command's git calls.</summary>
    public int TimeoutMs { get; init; } = 600_000;
}

/// <summary>What actually happened to one entry once the plan was executed.</summary>
public sealed record CloneOutcome
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? Url { get; init; }
    public string? Branch { get; init; }

    /// <summary>cloned · skipped · would-clone · failed · invalid</summary>
    public required string Status { get; init; }

    public required string Detail { get; init; }

    public bool IsError => Status is "failed" or "invalid";
}

/// <summary>
/// Executes a <see cref="CloneReposLogic"/> plan: probes the disk, runs git, collects outcomes.
///
/// <para><b>Sequential, unlike the status command.</b> That command fans out because its work is
/// two small round trips per repo; a clone is a sustained transfer, and running eleven at once
/// mostly means eleven of them contending for the same link while the output interleaves. It also
/// keeps a partial run readable when one repo fails.</para>
///
/// <para><b>One repo's failure never stops the others.</b> A missing branch or an unreachable
/// remote is reported and the run continues — a half-populated workspace with a named failure is
/// more useful than an abort, and re-running skips whatever already landed.</para>
/// </summary>
public static class CloneReposRunner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Observe what is at <paramref name="absolutePath"/> right now.</summary>
    public static TargetState Probe(string absolutePath)
    {
        if (!Directory.Exists(absolutePath)) return TargetState.Missing;
        if (Directory.Exists(Path.Combine(absolutePath, ".git")) ||
            File.Exists(Path.Combine(absolutePath, ".git")))   // worktree/submodule: .git is a file
            return TargetState.GitRepo;

        var hasAnything = Directory.EnumerateFileSystemEntries(absolutePath).Any();
        return hasAnything ? TargetState.Occupied : TargetState.Empty;
    }

    /// <summary>Plan and (unless dry-run) execute. Returns one outcome per considered entry.</summary>
    public static async Task<IReadOnlyList<CloneOutcome>> RunAsync(
        RepoManifest manifest, string root, CloneReposOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var plan = CloneReposLogic.Plan(
            manifest,
            options.Environment,
            rel => Probe(Path.GetFullPath(Path.Combine(root, rel))),
            options.OnlyRepo);

        var git = new GitRunner(options.TimeoutMs);
        var outcomes = new List<CloneOutcome>(plan.Count);

        foreach (var d in plan)
        {
            switch (d.Action)
            {
                case CloneAction.Invalid:
                    log?.Invoke($"  invalid   {d.Name,-14} {d.Reason}");
                    outcomes.Add(Outcome(d, "invalid", d.Reason));
                    continue;

                case CloneAction.SkipPresent:
                    log?.Invoke($"  skip      {d.Name,-14} {d.Path} (already cloned)");
                    outcomes.Add(Outcome(d, "skipped", d.Reason));
                    continue;

                case CloneAction.SkipOccupied:
                    log?.Invoke($"  skip      {d.Name,-14} {d.Path} — NOT a git repository, left untouched");
                    outcomes.Add(Outcome(d, "skipped", d.Reason));
                    continue;
            }

            if (options.DryRun)
            {
                log?.Invoke($"  would     {d.Name,-14} {d.Url} #{d.Branch} -> {d.Path}");
                outcomes.Add(Outcome(d, "would-clone", $"would clone {d.Branch}"));
                continue;
            }

            // Clone from the ROOT so the manifest's relative Path lands where it says. The parent
            // must exist first: `git clone a/b/c` fails when a/b does not.
            var target = Path.GetFullPath(Path.Combine(root, CloneReposLogic.Normalize(d.Path)));
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            var args = new List<string>(CloneReposLogic.CloneArguments(d.Url!, d.Branch!, CloneReposLogic.Normalize(d.Path)));
            if (options.Shallow) args.InsertRange(1, new[] { "--depth", "1" });

            log?.Invoke($"  cloning   {d.Name,-14} {d.Url} #{d.Branch} -> {d.Path}");
            var result = await git.RunAsync(root, args.ToArray()).ConfigureAwait(false);

            if (result.Ok)
            {
                outcomes.Add(Outcome(d, "cloned", $"cloned {d.Branch}"));
            }
            else
            {
                // git writes clone progress to stderr, so the last line is the useful part; the
                // whole stream would bury the reason under transfer chatter.
                var why = LastLine(result.StdErr) ?? LastLine(result.StdOut) ?? $"git exited {result.ExitCode}";
                log?.Invoke($"  FAILED    {d.Name,-14} {why}");
                outcomes.Add(Outcome(d, "failed", why));
            }
        }

        return outcomes;
    }

    private static CloneOutcome Outcome(CloneDecision d, string status, string detail) => new()
    {
        Name = d.Name,
        Path = d.Path,
        Url = d.Url,
        Branch = d.Branch,
        Status = status,
        Detail = detail,
    };

    private static string? LastLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r').Trim())
                     .Where(l => l.Length > 0)
                     .ToList();
        return lines.Count == 0 ? null : lines[^1];
    }

    /// <summary>One JSON document on stdout — the `--json` convention `lz repos`/`lz verify` use.</summary>
    public static string ToJson(IReadOnlyList<CloneOutcome> outcomes, string root, RepoEnvironment env) =>
        JsonSerializer.Serialize(new
        {
            root,
            environment = env.ToString().ToLowerInvariant(),
            count = outcomes.Count,
            cloned = outcomes.Count(o => o.Status == "cloned"),
            skipped = outcomes.Count(o => o.Status == "skipped"),
            failed = outcomes.Count(o => o.IsError),
            repos = outcomes,
        }, Json);

    /// <summary>The closing one-line summary.</summary>
    public static string Summarize(IReadOnlyList<CloneOutcome> outcomes)
    {
        var sb = new StringBuilder();
        var cloned = outcomes.Count(o => o.Status == "cloned");
        var would = outcomes.Count(o => o.Status == "would-clone");
        var failed = outcomes.Count(o => o.IsError);

        // "already present" and "not a git repo" are BOTH skips, and lumping them together
        // understates the second — a populated non-repo folder sitting where a repo belongs is
        // something the operator needs to look at, not a quiet success.
        var present = outcomes.Count(o => o.Status == "skipped" && !o.Detail.Contains("not a git repository", StringComparison.OrdinalIgnoreCase));
        var occupied = outcomes.Count(o => o.Status == "skipped" && o.Detail.Contains("not a git repository", StringComparison.OrdinalIgnoreCase));

        sb.Append(cloned > 0 ? $"{cloned} cloned" : would > 0 ? $"{would} would be cloned" : "nothing to clone");
        if (present > 0) sb.Append($", {present} already present");
        if (occupied > 0) sb.Append($", {occupied} NOT a git repository (left untouched)");
        if (failed > 0) sb.Append($", {failed} FAILED");
        return sb.ToString();
    }
}
