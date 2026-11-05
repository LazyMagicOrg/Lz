namespace Lz.Aws.Pipeline;

/// <summary>
/// THE INTAKE APP'S INSTALLATION TOKENS, ONE PER REPOSITORY (owner, 2026-11-05).
///
/// <para>The intake used to mint one token narrowed to every configured repository, finding the installation from the first.
/// GitHub refuses such a token whole (422) when the App is not installed on any one of them - and on 2026-11-05, with the root
/// added before the App was installed on it, every poll failed and no build from any of the eight repositories was taken in
/// for eight minutes. Each repository now has its own installation lookup and its own token, narrowed to it alone, so a
/// repository the App cannot see fails its own calls and nobody else's.</para>
///
/// <para>Kept for fifty minutes of GitHub's hour, as the single token was. Entries of one repository (an entry per class) share
/// its token: the token is the repository's, not the entry's.</para>
/// </summary>
public sealed class IntakeTokens(
    Func<string, Task<long>> installationOf,
    Func<long, IReadOnlyList<long>, Task<string>> mint,
    IReadOnlyList<IntakeRepository> repositories,
    Func<DateTimeOffset> now)
{
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(50);

    private readonly Dictionary<string, (string Token, DateTimeOffset Until)> _byRepo = new(StringComparer.Ordinal);

    /// <summary>A token for <paramref name="repo"/> alone. Throws what GitHub answered when it will not give one.</summary>
    public async Task<string> ForAsync(string repo)
    {
        if (_byRepo.TryGetValue(repo, out var held) && now() < held.Until) return held.Token;

        var id = repositories.FirstOrDefault(r => string.Equals(r.Repo, repo, StringComparison.Ordinal))?.RepositoryId
            ?? throw new IntakeFault($"{repo} is not a repository this intake takes; no token is asked for it");
        var token = await mint(await installationOf(repo), new[] { id });
        _byRepo[repo] = (token, now() + Kept);
        return token;
    }
}
