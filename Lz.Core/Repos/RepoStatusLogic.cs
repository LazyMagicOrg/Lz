namespace Lz.Core.Repos;

/// <summary>
/// The decision logic of the repo report, as pure functions over already-captured git output.
/// Kept free of process invocation so every rule below is unit-testable — these rules are subtle,
/// and two of them encode bugs that were found the hard way (see <see cref="SelectUpstream"/> and
/// <see cref="ParseLsRemoteTags"/>).
/// </summary>
public static class RepoStatusLogic
{
    /// <summary>
    /// Choose the ref to compare against, in strict preference order.
    ///
    /// <para><b>The order is load-bearing.</b> <c>@{u}</c> comes first because it is the branch's own
    /// tracking config, so it survives a remote default-branch rename. <c>origin/HEAD</c> is only a
    /// local pointer written at clone time and is NOT updated by a fetch — after a master→main
    /// rename it can still name a pruned <c>origin/master</c>, which yields phantom "N ahead"
    /// counts. Preferring <c>@{u}</c> is what prevents that.</para>
    ///
    /// <para>Consequence worth knowing: on a feature branch with its own remote this reports
    /// ahead/behind versus THAT branch, not versus the default — which is usually what you want.</para>
    /// </summary>
    public static string? SelectUpstream(
        string? trackedUpstream,
        string? originHead,
        bool hasOriginMain,
        bool hasOriginMaster)
    {
        if (!string.IsNullOrWhiteSpace(trackedUpstream)) return trackedUpstream.Trim();
        if (!string.IsNullOrWhiteSpace(originHead)) return originHead.Trim();
        if (hasOriginMain) return "origin/main";
        if (hasOriginMaster) return "origin/master";
        return null;
    }

    /// <summary>
    /// Parse <c>git ls-remote origin refs/tags/&lt;name&gt;</c> output into name → commit sha.
    ///
    /// <para><b>Annotated tags emit two lines</b>: the tag-object sha, and a second line suffixed
    /// <c>^{}</c> carrying the commit it dereferences to. The dereferenced line must win, or an
    /// annotated tag compares against a tag object that is not in the commit graph and every
    /// ancestry test comes back "diverged". Lightweight tags emit only the plain form.</para>
    ///
    /// <para>ls-remote is used rather than local tag refs on purpose: a plain <c>git fetch</c> does
    /// not move a local tag that was re-pointed on the remote, so local refs can silently report a
    /// stale position.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseLsRemoteTags(
        IEnumerable<string> lines, IEnumerable<string> wantedTags)
    {
        var wanted = new HashSet<string>(wantedTags, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dereferenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var sha = parts[0].Trim();
            var refName = parts[1].Trim();
            const string prefix = "refs/tags/";
            if (!refName.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var name = refName[prefix.Length..];
            var isDeref = name.EndsWith("^{}", StringComparison.Ordinal);
            if (isDeref) name = name[..^3];

            if (!wanted.Contains(name)) continue;

            // A dereferenced entry always wins, and once seen must not be overwritten by a
            // plain entry arriving later.
            if (isDeref) { result[name] = sha; dereferenced.Add(name); }
            else if (!dereferenced.Contains(name)) result[name] = sha;
        }

        return result;
    }

    /// <summary>
    /// Classify a marker's position from the ancestry facts git reported.
    /// <paramref name="presentLocally"/> false short-circuits: without the commit locally, no
    /// ancestry question can be answered.
    /// </summary>
    public static TagPosition ClassifyTag(
        string tagSha, string headSha, bool presentLocally,
        bool isAncestorOfHead, bool headIsAncestorOfTag, int distance)
    {
        if (string.Equals(tagSha, headSha, StringComparison.OrdinalIgnoreCase))
            return new TagPosition(TagRelation.AtHead, 0, tagSha);
        if (!presentLocally)
            return new TagPosition(TagRelation.NotFetched, 0, tagSha);
        if (isAncestorOfHead)
            return new TagPosition(TagRelation.Ahead, distance, tagSha);
        if (headIsAncestorOfTag)
            return new TagPosition(TagRelation.Behind, distance, tagSha);
        return new TagPosition(TagRelation.Diverged, 0, tagSha);
    }

    /// <summary>Render one marker position: "@HEAD", "+3", "-2", "&lt;&gt;abc1234", "→abc1234(not-fetched)".</summary>
    public static string FormatTagPosition(TagPosition p) => p.Relation switch
    {
        TagRelation.AtHead => "@HEAD",
        TagRelation.Ahead => $"+{p.Count}",
        TagRelation.Behind => $"-{p.Count}",
        TagRelation.Diverged => $"<>{Short(p.Sha)}",
        TagRelation.NotFetched => $"→{Short(p.Sha)}(not-fetched)",
        _ => "?",
    };

    /// <summary>
    /// Render the marker column for an ordered set of markers, collapsing them into one entry when
    /// they all agree ("prod, test @HEAD") and listing them separately when they do not
    /// ("prod @HEAD, test +2"). Absent markers are omitted; none at all renders "(none)".
    /// </summary>
    public static string FormatTags(IReadOnlyList<(string Name, TagPosition? Position)> markers)
    {
        var present = markers.Where(m => m.Position.HasValue)
                             .Select(m => (m.Name, Text: FormatTagPosition(m.Position!.Value)))
                             .ToList();
        if (present.Count == 0) return "(none)";

        var first = present[0].Text;
        if (present.Count > 1 && present.All(p => p.Text == first))
            return $"{string.Join(", ", present.Select(p => p.Name))} {first}";

        return string.Join(", ", present.Select(p => $"{p.Name} {p.Text}"));
    }

    /// <summary>
    /// Parse <c>git rev-list --left-right --count &lt;upstream&gt;...HEAD</c>, which prints
    /// "&lt;behind&gt;\t&lt;ahead&gt;" — left is commits only the upstream has.
    /// </summary>
    public static bool TryParseLeftRight(string? output, out int behind, out int ahead)
    {
        behind = ahead = 0;
        if (string.IsNullOrWhiteSpace(output)) return false;

        var parts = output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], out behind)
            && int.TryParse(parts[1], out ahead);
    }

    /// <summary>
    /// Render the sync column. The "(no fetch)" annotation appears only when a fetch was
    /// REQUESTED and failed — not when the caller passed --no-fetch, where stale counts are the
    /// explicitly chosen trade rather than a degraded result.
    /// </summary>
    public static string FormatSync(
        string? upstream, int behind, int ahead, bool fetchRequested, bool fetchSucceeded)
    {
        if (upstream is null)
            return fetchRequested && !fetchSucceeded ? "n/a (no fetch)" : "n/a";

        var core = behind == 0 && ahead == 0
            ? $"in sync vs {upstream}"
            : $"{ahead} ahead / {behind} behind vs {upstream}";

        return fetchRequested && !fetchSucceeded ? $"{core} (no fetch)" : core;
    }

    /// <summary>Render the working-tree column from a `git status --porcelain` line count.</summary>
    public static string FormatTree(int changedCount) =>
        changedCount == 0 ? "clean" : $"{changedCount} changed";

    /// <summary>Truncate a commit subject for the table, preserving the short sha prefix.</summary>
    public static string TruncateCommit(string? commit, int max = 48)
    {
        if (string.IsNullOrEmpty(commit)) return string.Empty;
        return commit.Length <= max ? commit : commit[..max] + "…";
    }

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? sha : (sha.Length <= 7 ? sha : sha[..7]);
}
