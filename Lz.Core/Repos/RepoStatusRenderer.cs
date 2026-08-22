using System.Text;
using System.Text.Json;

namespace Lz.Core.Repos;

/// <summary>Renders the collected statuses. Hand-rolled: Lz.Cli carries no table dependency.</summary>
public static class RepoStatusRenderer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One JSON document on stdout — matches the `--json` convention used by `lz verify`.</summary>
    public static string ToJson(IReadOnlyList<RepoStatus> rows, string root) =>
        JsonSerializer.Serialize(new { root, count = rows.Count, repos = rows }, Json);

    /// <summary>Fixed-width table, columns sized to content.</summary>
    public static string ToTable(IReadOnlyList<RepoStatus> rows)
    {
        string[] headers = ["Repo", "Branch", "Commit", "Tree", "Tags", "Sync"];
        var cells = rows.Select(r => new[]
        {
            r.Repo,
            r.Error is null ? r.Branch ?? "-" : "-",
            r.Error is null ? r.Commit ?? "-" : $"ERROR: {r.Error}",
            r.Error is null ? r.Tree : "-",
            r.Error is null ? r.Tags : "-",
            r.Error is null ? r.Sync : "-",
        }).ToList();

        var width = new int[headers.Length];
        for (var c = 0; c < headers.Length; c++)
            width[c] = Math.Max(headers[c].Length, cells.Count == 0 ? 0 : cells.Max(row => row[c]?.Length ?? 0));

        var sb = new StringBuilder();
        AppendRow(sb, headers, width);
        AppendRow(sb, width.Select(w => new string('-', w)).ToArray(), width);
        foreach (var row in cells) AppendRow(sb, row, width);
        return sb.ToString();
    }

    /// <summary>
    /// The one-line callouts a reader actually acts on. Everything here is a deviation from the
    /// steady state — a quiet workspace prints nothing but the "all clean" line.
    /// </summary>
    public static string ToSummary(IReadOnlyList<RepoStatus> rows)
    {
        var notes = new List<string>();

        var dirty = rows.Where(r => r.DirtyCount > 0).Select(r => r.Repo).ToList();
        if (dirty.Count > 0) notes.Add($"uncommitted changes: {string.Join(", ", dirty)}");

        var behind = rows.Where(r => r.Behind is > 0).Select(r => $"{r.Repo} (-{r.Behind})").ToList();
        if (behind.Count > 0) notes.Add($"behind upstream, pull needed: {string.Join(", ", behind)}");

        var ahead = rows.Where(r => r.Ahead is > 0).Select(r => $"{r.Repo} (+{r.Ahead})").ToList();
        if (ahead.Count > 0) notes.Add($"unpushed commits: {string.Join(", ", ahead)}");

        var errored = rows.Where(r => r.Error is not null).Select(r => r.Repo).ToList();
        if (errored.Count > 0) notes.Add($"unreadable: {string.Join(", ", errored)}");

        var unfetched = rows.Where(r => r.Error is null && r.Sync.Contains("(no fetch)", StringComparison.Ordinal))
                            .Select(r => r.Repo).ToList();
        if (unfetched.Count > 0) notes.Add($"fetch failed (counts are as of last fetch): {string.Join(", ", unfetched)}");

        return notes.Count == 0
            ? $"All {rows.Count} repos clean, in sync with their upstreams."
            : string.Join(Environment.NewLine, notes.Select(n => "- " + n));
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] width)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            sb.Append((cells[c] ?? string.Empty).PadRight(width[c]));
            if (c < cells.Length - 1) sb.Append("  ");
        }
        sb.AppendLine();
    }
}
