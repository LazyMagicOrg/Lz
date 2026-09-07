using System.Diagnostics;
using System.Text.Json;

namespace Lz.Core.PackageLane;

/// <summary>
/// Which versions of a package id a registry actually serves.
///
/// <para>Separate from <see cref="IPublishedPackages"/>, which answers a richer question (what commit
/// was this version built from?) over the V3 flat container — and which <b>does not work against
/// GitHub Packages</b>: measured 2026-09-07, that registry answers <c>403</c> for the flat
/// container's version list with a token that restores perfectly well. Existence alone is a smaller
/// question with an implementation that does work, so it gets its own seam rather than waiting for
/// the other one.</para>
/// </summary>
public interface IPublishedVersions
{
    /// <summary>
    /// Every version the registry serves for this id, or <c>null</c> if the id is unknown to it.
    ///
    /// <para><b>Throw when the answer is unknown</b> — unreachable, unauthorised, malformed. "No such
    /// package" and "could not ask" lead to opposite decisions, and merging them here would make the
    /// caller pass in exactly the case it exists for.</para>
    /// </summary>
    Task<IReadOnlySet<string>?> VersionsAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// Asks NuGet, by shelling out to <c>dotnet package search</c>.
///
/// <para><b>Why a subprocess rather than HTTP.</b> The same argument as <see cref="LocalFeeds"/>
/// reading the feed list out of <c>NuGet.Config</c>: the tool and restore must not disagree. This
/// resolves the source, its credentials and the whole config chain the way restore does, so it needs
/// no token plumbing and cannot drift from what a build would see. It is also the only method
/// measured to work against GitHub Packages here — the hand-rolled V3 probe is refused with 403 by
/// the same credential this succeeds with.</para>
/// </summary>
public sealed class DotnetPackageSearchVersions : IPublishedVersions
{
    private readonly string _source;
    private readonly string _workingDirectory;

    /// <param name="source">
    /// A source NAME from the NuGet config chain (e.g. <c>LazyMagicRegistry</c>) or a service index
    /// URL. A name is preferable: credentials are keyed by source name, so a name picks them up and
    /// a bare URL may not.
    /// </param>
    /// <param name="workingDirectory">
    /// Where to run, which decides which <c>NuGet.Config</c> chain applies — and therefore which
    /// sources and credentials exist at all.
    /// </param>
    public DotnetPackageSearchVersions(string source, string workingDirectory)
    {
        _source = source;
        _workingDirectory = workingDirectory;
    }

    public async Task<IReadOnlySet<string>?> VersionsAsync(string id, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "package", "search", id, "--exact-match", "--prerelease",
                                  "--source", _source, "--format", "json" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start `dotnet package search`.");

        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"`dotnet package search {id} --source {_source}` exited {p.ExitCode}. That is no " +
                $"answer, not an empty one. {stderr.Trim()}");

        return ParseVersions(stdout, id, _source);
    }

    /// <summary>
    /// The versions for one id out of <c>dotnet package search --format json</c>. Split out from the
    /// subprocess so the shape can be tested without one.
    ///
    /// <para>Returns null when the source reports the id at all but with no versions, or does not
    /// report it — both mean "the registry does not serve this", which is a real answer. A
    /// <c>problems</c> entry is NOT a real answer and throws.</para>
    /// </summary>
    public static IReadOnlySet<string>? ParseVersions(string json, string id, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("problems", out var problems) &&
            problems.ValueKind == JsonValueKind.Array && problems.GetArrayLength() > 0)
        {
            var first = problems[0].TryGetProperty("text", out var t) ? t.GetString() : problems[0].ToString();
            throw new InvalidOperationException($"{source} reported a problem for {id}: {first}");
        }

        if (!root.TryGetProperty("searchResult", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{source}: unrecognised `dotnet package search` output for {id}.");

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array) continue;

            foreach (var package in packages.EnumerateArray())
            {
                var pid = package.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                // --exact-match is on, but compare anyway: the flag is the server's promise, and this
                // decides whether a version gets written into a tracked file.
                if (!string.Equals(pid, id, StringComparison.OrdinalIgnoreCase)) continue;

                var version = package.TryGetProperty("version", out var vProp) ? vProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(version)) found.Add(version!);
            }
        }

        return found.Count == 0 ? null : found;
    }
}
