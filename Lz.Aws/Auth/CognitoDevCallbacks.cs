namespace Lz.Aws.Auth;

/// <summary>
/// The localhost origins and base paths registered when <c>IncludeDevCallbackUrls</c> is on, on a pool's public client
/// and the clients that reuse its callback list. A local Blazor WASM app builds its redirect URI from its own origin and
/// base path (<c>https://localhost:{port}/{basePath}authentication/login-callback</c>), and Cognito accepts only a
/// registered URI, exactly, so every port and path a local app serves at has to be listed. Pure, so the list is
/// testable without AWS.
/// </summary>
public static class CognitoDevCallbacks
{
    /// <summary>
    /// Registered for every system that turns dev callbacks on: the bare root and MagicPets' three apps. A pool with
    /// no <c>DevCallbackBasePaths</c> gets exactly these, in this order, so its plan doesn't change.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInBasePaths = new[] { "", "store/", "admin/", "app/" };

    /// <summary>
    /// The Blazor WASM dev-server port every local app launched on until a pool could name its own (MagicPets' three
    /// apps, one at a time, per each <c>WASMApp/Properties/launchSettings.json</c>). A pool with no
    /// <c>DevCallbackPorts</c> gets this one alone, so its plan doesn't change.
    /// </summary>
    public const int DefaultPort = 7218;

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

    /// <summary>
    /// The pool's ports, each once and in order, INSTEAD OF <see cref="DefaultPort"/>: a pool whose local apps moved
    /// to another port lists that port alone, and the old one stops being accepted. None named gives the default.
    /// </summary>
    public static IReadOnlyList<int> Ports(IEnumerable<int>? poolPorts)
    {
        var ports = (poolPorts ?? Enumerable.Empty<int>()).Distinct().ToList();
        return ports.Count == 0 ? new[] { DefaultPort } : ports;
    }

    /// <summary>
    /// Every localhost base URL a pool's local apps redirect back to, ending in <c>/</c>: each port, then each base
    /// path under it. The component appends <c>authentication/login-callback</c> and <c>authentication/logout-callback</c>.
    /// </summary>
    public static IReadOnlyList<string> BaseUrls(IEnumerable<int>? poolPorts, IEnumerable<string>? poolBasePaths)
    {
        var paths = BasePaths(poolBasePaths);
        return Ports(poolPorts).SelectMany(port => paths.Select(path => $"https://localhost:{port}/{path}")).ToList();
    }
}
