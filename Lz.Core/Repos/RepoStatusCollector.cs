namespace Lz.Core.Repos;

/// <summary>Options for one repo-status run.</summary>
public sealed class RepoStatusOptions
{
    /// <summary>Workspace root. Defaults to <see cref="RepoDiscovery.FindWorkspaceRoot"/>.</summary>
    public string? Root { get; init; }

    /// <summary>
    /// Fetch each repo before comparing. Costs a network round trip per repo and updates
    /// remote-tracking refs — the only thing in this command that writes anything.
    /// </summary>
    public bool Fetch { get; init; } = true;

    /// <summary>Release markers to report, in display order.</summary>
    public IReadOnlyList<string> Tags { get; init; } = ["prod", "test"];

    /// <summary>Concurrent repositories. Network-bound, so higher than CPU count is fine.</summary>
    public int MaxConcurrency { get; init; } = 8;
}

/// <summary>
/// Collects <see cref="RepoStatus"/> for every repository in a workspace.
///
/// <para>Repositories are processed <b>concurrently</b> — the work is independent and dominated by
/// two network round trips each (fetch, ls-remote), so wall clock falls from the sum of per-repo
/// latency to roughly the slowest single repo. Output order is restored afterwards from the
/// discovery order, never from completion order.</para>
///
/// <para><b>Read-only by design</b> apart from the optional fetch: remote marker positions come
/// from <c>ls-remote</c> (a query) rather than <c>fetch --tags --force</c> (a local write), which
/// is also what makes a re-pointed remote tag report correctly without touching local refs.</para>
/// </summary>
public sealed class RepoStatusCollector
{
    private readonly GitRunner _git;

    public RepoStatusCollector(GitRunner? git = null) => _git = git ?? new GitRunner();

    public async Task<IReadOnlyList<RepoStatus>> CollectAsync(
        RepoStatusOptions options, CancellationToken ct = default)
    {
        var root = options.Root ?? RepoDiscovery.FindWorkspaceRoot();
        var repos = RepoDiscovery.Discover(root);

        var results = new RepoStatus[repos.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));

        await Task.WhenAll(repos.Select(async (repoPath, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await CollectOneAsync(root, repoPath, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One unreadable repo must not lose the whole report.
                results[index] = new RepoStatus
                {
                    Repo = RepoDiscovery.DisplayName(root, repoPath),
                    Error = ex.Message,
                };
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return results;
    }

    private async Task<RepoStatus> CollectOneAsync(
        string root, string repo, RepoStatusOptions options, CancellationToken ct)
    {
        var name = RepoDiscovery.DisplayName(root, repo);

        var fetched = false;
        if (options.Fetch)
            fetched = await _git.SucceedsAsync(repo, "fetch", "--quiet").ConfigureAwait(false);

        var branch = (await _git.RunAsync(repo, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Value;
        var commit = (await _git.RunAsync(repo, "log", "-1", "--format=%h %s").ConfigureAwait(false)).Value;
        var headSha = (await _git.RunAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false)).Value;

        var statusResult = await _git.RunAsync(repo, "status", "--porcelain").ConfigureAwait(false);
        var dirty = statusResult.Ok ? statusResult.Lines.Count() : 0;

        var upstream = await ResolveUpstreamAsync(repo).ConfigureAwait(false);

        int? ahead = null, behind = null;
        var sync = RepoStatusLogic.FormatSync(upstream, 0, 0, options.Fetch, fetched);
        if (upstream is not null)
        {
            var lr = await _git.RunAsync(repo, "rev-list", "--left-right", "--count", $"{upstream}...HEAD")
                               .ConfigureAwait(false);
            if (RepoStatusLogic.TryParseLeftRight(lr.Value, out var b, out var a))
            {
                behind = b; ahead = a;
                sync = RepoStatusLogic.FormatSync(upstream, b, a, options.Fetch, fetched);
            }
        }

        var tags = await ResolveTagsAsync(repo, headSha, options, fetched).ConfigureAwait(false);

        return new RepoStatus
        {
            Repo = name,
            Branch = branch,
            Commit = RepoStatusLogic.TruncateCommit(commit),
            Tree = RepoStatusLogic.FormatTree(dirty),
            DirtyCount = dirty,
            Tags = tags,
            Sync = sync,
            Fetched = fetched,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
        };
    }

    /// <summary>The four-step fallback described on <see cref="RepoStatusLogic.SelectUpstream"/>.</summary>
    private async Task<string?> ResolveUpstreamAsync(string repo)
    {
        var tracked = (await _git.RunAsync(repo, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")
                                 .ConfigureAwait(false)).Value;

        string? originHead = null;
        if (tracked is null)
        {
            // Best-effort refresh; a no-op offline, which is why the probes below still matter.
            await _git.RunAsync(repo, "remote", "set-head", "origin", "-a").ConfigureAwait(false);
            originHead = (await _git.RunAsync(repo, "rev-parse", "--abbrev-ref", "origin/HEAD")
                                    .ConfigureAwait(false)).Value;
        }

        var hasMain = tracked is null && originHead is null
            && await _git.SucceedsAsync(repo, "show-ref", "--verify", "--quiet", "refs/remotes/origin/main")
                         .ConfigureAwait(false);
        var hasMaster = tracked is null && originHead is null && !hasMain
            && await _git.SucceedsAsync(repo, "show-ref", "--verify", "--quiet", "refs/remotes/origin/master")
                         .ConfigureAwait(false);

        return RepoStatusLogic.SelectUpstream(tracked, originHead, hasMain, hasMaster);
    }

    private async Task<string> ResolveTagsAsync(
        string repo, string? headSha, RepoStatusOptions options, bool fetched)
    {
        if (options.Tags.Count == 0 || headSha is null) return "(none)";

        // --no-fetch means OFFLINE. ls-remote is a network round trip like the fetch, so honouring
        // the flag means skipping it too. Render "(offline)" rather than "(none)" — the latter
        // would claim no markers exist when in truth we never looked.
        if (!options.Fetch) return "(offline)";

        // A failed fetch means the remote is unreachable; skip the query rather than making every
        // repo wait out its own connection timeout.
        if (!fetched) return "(unreachable)";

        var refArgs = new List<string> { "ls-remote", "origin" };
        refArgs.AddRange(options.Tags.Select(t => $"refs/tags/{t}"));

        var lsRemote = await _git.RunAsync(repo, refArgs.ToArray()).ConfigureAwait(false);
        if (!lsRemote.Ok) return "(none)";

        var shas = RepoStatusLogic.ParseLsRemoteTags(lsRemote.Lines, options.Tags);

        var markers = new List<(string, TagPosition?)>();
        foreach (var tag in options.Tags)
        {
            if (!shas.TryGetValue(tag, out var sha)) { markers.Add((tag, null)); continue; }

            var present = await _git.SucceedsAsync(repo, "cat-file", "-e", $"{sha}^{{commit}}").ConfigureAwait(false);
            var isAncestor = present
                && await _git.SucceedsAsync(repo, "merge-base", "--is-ancestor", sha, "HEAD").ConfigureAwait(false);
            var headBefore = present && !isAncestor
                && await _git.SucceedsAsync(repo, "merge-base", "--is-ancestor", "HEAD", sha).ConfigureAwait(false);

            var distance = 0;
            if (isAncestor || headBefore)
            {
                var range = isAncestor ? $"{sha}..HEAD" : $"HEAD..{sha}";
                var count = (await _git.RunAsync(repo, "rev-list", "--count", range).ConfigureAwait(false)).Value;
                _ = int.TryParse(count, out distance);
            }

            markers.Add((tag, RepoStatusLogic.ClassifyTag(sha, headSha, present, isAncestor, headBefore, distance)));
        }

        return RepoStatusLogic.FormatTags(markers);
    }
}
