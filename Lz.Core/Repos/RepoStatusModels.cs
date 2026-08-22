namespace Lz.Core.Repos;

/// <summary>Where a release marker sits relative to the working copy's HEAD.</summary>
public enum TagRelation
{
    /// <summary>The marker points at HEAD — the steady state after a release.</summary>
    AtHead,
    /// <summary>HEAD has advanced N commits past the marker (unreleased work).</summary>
    Ahead,
    /// <summary>HEAD is N commits behind the marker (this working copy is stale).</summary>
    Behind,
    /// <summary>Neither ancestor nor descendant — the marker is on a diverged branch.</summary>
    Diverged,
    /// <summary>The marker names a commit not present locally (e.g. on an unfetched branch).</summary>
    NotFetched,
}

/// <summary>A release marker's position, ready to render.</summary>
public readonly record struct TagPosition(TagRelation Relation, int Count, string Sha);

/// <summary>One repository's status. Every field is already rendered for display.</summary>
public sealed record RepoStatus
{
    /// <summary>Path relative to the workspace root, or the absolute path if outside it.</summary>
    public required string Repo { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }

    /// <summary>"clean" or "N changed".</summary>
    public string Tree { get; init; } = "unknown";

    /// <summary>Rendered marker column, e.g. "prod, test @HEAD" or "(none)".</summary>
    public string Tags { get; init; } = "(none)";

    /// <summary>Rendered sync column, e.g. "in sync vs origin/main".</summary>
    public string Sync { get; init; } = "n/a";

    /// <summary>True when a fetch was requested AND succeeded.</summary>
    public bool Fetched { get; init; }

    /// <summary>Set when the repo could not be read at all; other fields are best-effort.</summary>
    public string? Error { get; init; }

    // Structured duplicates of the rendered columns, for --json consumers that would
    // otherwise have to parse the display strings.
    public string? Upstream { get; init; }
    public int? Ahead { get; init; }
    public int? Behind { get; init; }
    public int DirtyCount { get; init; }
}
