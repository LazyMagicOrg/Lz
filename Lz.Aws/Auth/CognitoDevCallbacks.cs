namespace Lz.Aws.Auth;

/// <summary>
/// The localhost base paths registered when <c>IncludeDevCallbackUrls</c> is on, on a pool's public client and the
/// clients that reuse its callback list. A local Blazor WASM app builds its redirect URI from its own base path
/// (<c>https://localhost:7218/{basePath}authentication/login-callback</c>), and Cognito accepts only a registered
/// URI, exactly, so every path a local app mounts at has to be listed. Pure, so the list is testable without AWS.
/// </summary>
public static class CognitoDevCallbacks
{
    /// <summary>
    /// Registered for every system that turns dev callbacks on: the bare root and MagicPets' three apps. A pool with
    /// no <c>DevCallbackBasePaths</c> gets exactly these, in this order, so its plan doesn't change.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInBasePaths = new[] { "", "store/", "admin/", "app/" };

    /// <summary>The built-in paths, then the pool's own, each once and in that order.</summary>
    public static IReadOnlyList<string> BasePaths(IEnumerable<string>? poolBasePaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var path in BuiltInBasePaths.Concat(poolBasePaths ?? Enumerable.Empty<string>()))
        {
            if (seen.Add(path)) paths.Add(path);
        }
        return paths;
    }
}
