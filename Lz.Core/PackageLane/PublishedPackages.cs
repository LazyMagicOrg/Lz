using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lz.Core.PackageLane;

/// <summary>What the registry already holds. Separated so guard one is testable without a network.</summary>
public interface IPublishedPackages
{
    /// <summary>
    /// The published package at this id@version, or <c>null</c> if the registry does not have it.
    ///
    /// <para><b>Throw rather than return null when the answer is unknown</b> - unreachable, refused,
    /// no V3 base address. "Not published" and "could not ask" lead to opposite decisions, and merging
    /// them here would make the guard pass in exactly the case it exists for.</para>
    /// </summary>
    Task<PublishedPackage?> LookupAsync(string id, string version, CancellationToken ct = default);
}

/// <summary>
/// The NuGet V3 protocol: read the service index, find its <c>PackageBaseAddress</c>, fetch the
/// nuspec.
///
/// <para><b>Unverified against GitHub Packages as of 2026-09-07</b>, and deliberately so: no
/// credential in this workspace can read that registry, and the empirical publish test is its own
/// owner-gated row (MigrationPlan M4). What that means in practice is that guard one either answers or
/// refuses - it never quietly passes. Both failure paths throw, the caller maps them to
/// <see cref="PublishVerdict.RegistryUndeterminable"/>, and that verdict refuses.</para>
/// </summary>
public sealed class NuGetV3PublishedPackages : IPublishedPackages, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serviceIndexUrl;
    private readonly bool _ownsClient;
    private string? _baseAddress;

    /// <param name="token">
    /// A registry read token, or null for an anonymous read. Read from an environment variable by the
    /// caller and never logged - the report prints ids, versions and verdicts only.
    /// </param>
    public NuGetV3PublishedPackages(string serviceIndexUrl, string? token = null, HttpClient? http = null)
    {
        _serviceIndexUrl = serviceIndexUrl;
        _ownsClient = http is null;
        _http = http ?? new HttpClient();
        if (!string.IsNullOrWhiteSpace(token))
        {
            // GitHub Packages authenticates a NuGet read as HTTP Basic with the token as the password;
            // the username is ignored. nuget.org needs no credential to read at all.
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"lz:{token}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }

    public async Task<PublishedPackage?> LookupAsync(string id, string version, CancellationToken ct = default)
    {
        var baseAddress = _baseAddress ??= await ResolveBaseAddressAsync(ct);

        // The flat container lowercases both segments and the file name.
        var lid = id.ToLowerInvariant();
        var lv = version.ToLowerInvariant();
        var url = $"{baseAddress}{lid}/{lv}/{lid}.nuspec";

        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}. " +
                "That is not 'no such version' - it is no answer, so guard one cannot clear this push.");

        var xml = await response.Content.ReadAsStringAsync(ct);
        return new PublishedPackage(PublishGuards.ParseNuspec(xml, url).RepositoryCommit);
    }

    private async Task<string> ResolveBaseAddressAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(_serviceIndexUrl, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{_serviceIndexUrl} answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        return FindPackageBaseAddress(await response.Content.ReadAsStringAsync(ct), _serviceIndexUrl);
    }

    /// <summary>
    /// The <c>PackageBaseAddress/3.0.0</c> resource from a service index, with a trailing slash.
    /// Split out from the fetch so the shape can be tested without a network.
    /// </summary>
    public static string FindPackageBaseAddress(string serviceIndexJson, string source)
    {
        using var doc = JsonDocument.Parse(serviceIndexJson);
        if (!doc.RootElement.TryGetProperty("resources", out var resources))
            throw new InvalidDataException($"{source}: not a NuGet V3 service index (no 'resources').");

        foreach (var resource in resources.EnumerateArray())
        {
            var type = resource.TryGetProperty("@type", out var t) ? t.GetString() : null;
            if (type is null || !type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal)) continue;

            var address = resource.TryGetProperty("@id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(address)) continue;
            return address!.EndsWith('/') ? address! : address + "/";
        }

        throw new InvalidDataException(
            $"{source}: the service index publishes no PackageBaseAddress/3.0.0 resource, so a version " +
            "cannot be looked up over the V3 protocol.");
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
