using System.Net;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// THE INTAKE APP'S TOKENS, ONE PER REPOSITORY (2026-11-05): a single token narrowed to every configured repository was refused
/// whole (422) when the App was not installed on one of them, and the intake stopped for all eight. Each repository now gets
/// its own installation lookup and its own token, so a refusal fails only that repository's calls.
/// </summary>
public class IntakeTokensTests
{
    private static readonly IntakeRepository Service = new("Example/Service", 1, 7, ".github/workflows/a.yml", "live", "image", new[] { "aiphost" });
    private static readonly IntakeRepository RootTooling = new("Example/Root", 2, 7, ".github/workflows/tooling.yml", "dry", "tooling", new[] { "tooling" });
    private static readonly IntakeRepository RootConfig = new("Example/Root", 2, 7, ".github/workflows/config.yml", "dry", "config", new[] { "config" });

    private sealed class GitHub
    {
        public readonly HashSet<string> NotInstalled = new();
        public readonly List<(long Installation, IReadOnlyList<long> Ids)> Minted = new();
        public Task<long> InstallationOf(string repo)
            => NotInstalled.Contains(repo)
                ? throw new GitHubApiException(HttpStatusCode.NotFound, $"the installation for {repo}")
                : Task.FromResult(100L);
        public Task<string> Token(long installation, IReadOnlyList<long> ids)
        {
            Minted.Add((installation, ids));
            return Task.FromResult($"token-{string.Join("-", ids)}-{Minted.Count}");
        }
    }

    private static (IntakeTokens Tokens, GitHub GitHub, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) Make(params IntakeRepository[] repos)
    {
        var gh = new GitHub();
        var now = new DateTimeOffset(2026, 11, 5, 21, 0, 0, TimeSpan.Zero);
        return (new IntakeTokens(gh.InstallationOf, gh.Token, repos, () => now), gh, () => now, d => now += d);
    }

    [Fact]
    public async Task EachRepository_GetsATokenNarrowedToItAlone()
    {
        var (tokens, gh, _, _) = Make(Service, RootTooling);

        await tokens.ForAsync(Service.Repo);
        await tokens.ForAsync(RootTooling.Repo);

        Assert.Equal(new[] { new long[] { 1 }, new long[] { 2 } }, gh.Minted.Select(m => m.Ids.ToArray()));
    }

    [Fact]
    public async Task ARepositoryTheAppCannotSee_FailsAlone()
    {
        var (tokens, gh, _, _) = Make(RootTooling, Service);   // the refused one first, as the root was
        gh.NotInstalled.Add(RootTooling.Repo);

        await Assert.ThrowsAsync<GitHubApiException>(() => tokens.ForAsync(RootTooling.Repo));
        Assert.StartsWith("token-1-", await tokens.ForAsync(Service.Repo));
    }

    [Fact]
    public async Task ATokenIsKeptForFiftyMinutes_ThenMintedAgain()
    {
        var (tokens, gh, _, advance) = Make(Service);

        var first = await tokens.ForAsync(Service.Repo);
        advance(TimeSpan.FromMinutes(49));
        Assert.Equal(first, await tokens.ForAsync(Service.Repo));
        advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(first, await tokens.ForAsync(Service.Repo));
        Assert.Equal(2, gh.Minted.Count);
    }

    [Fact]
    public async Task TwoEntriesOfOneRepository_ShareItsToken()
    {
        var (tokens, gh, _, _) = Make(RootTooling, RootConfig);

        await tokens.ForAsync(RootTooling.Repo);
        await tokens.ForAsync(RootConfig.Repo);

        Assert.Single(gh.Minted);
    }

    [Fact]
    public async Task ARepositoryNotConfigured_IsRefusedWithoutAskingGitHub()
    {
        var (tokens, gh, _, _) = Make(Service);
        await Assert.ThrowsAsync<IntakeFault>(() => tokens.ForAsync("Example/Other"));
        Assert.Empty(gh.Minted);
    }
}
