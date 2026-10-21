using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// GitHub refused or failed a request the intake made. Carries the status, so a caller can tell "not there" (404) from a
/// fault, and never the response body, which can echo what was sent.
/// </summary>
public sealed class GitHubApiException(HttpStatusCode status, string what)
    : Exception($"GitHub answered {(int)status} {status} to {what}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// THE INTAKE APP'S IDENTITY (DecoupledCd.md V-17): the RS256 JWT an App signs with its private key to ask GitHub for an
/// installation token. BCL only, so the intake's functions compile it.
///
/// <para>GitHub's rules, as documented for "Generating a JSON Web Token (JWT) for a GitHub App": RS256; <c>iat</c> 60 s in the
/// past against clock drift; <c>exp</c> at most ten minutes after; <c>iss</c> the App's client ID (or its App ID). Nine
/// minutes are used, inside the limit.</para>
/// </summary>
public static class GitHubAppJwt
{
    public static string Create(string clientId, string privateKeyPem, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("the App's client ID is required", nameof(clientId));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = clientId,
        }));
        var signingInput = $"{header}.{payload}";
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64(signature)}";
    }

    internal static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The intake's reads of GitHub's REST API (DecoupledCd.md §15.4, V-B): the installation token, a workflow's finished runs on
/// <c>main</c>, an attempt's jobs, a run's artifacts and an artifact's bytes. It maps only the fields V-A's decisions read
/// (<see cref="GitHubRun"/>, <see cref="GitHubJob"/>, <see cref="GitHubArtifact"/>), and decides nothing: refusing a run is
/// <see cref="RunCheck"/>'s, an artifact <see cref="ArtifactCheck"/>'s.
///
/// <para><b>A LIST IT CANNOT READ WHOLE IS AN ERROR</b>, not a shorter list: a page short of GitHub's <c>total_count</c> after
/// the last allowed page throws, so a run past the cut is never taken for one that does not exist.</para>
///
/// <para><b>AN ARTIFACT DOWNLOAD IS A REDIRECT</b> to short-lived blob storage. .NET's handler follows it and does not send
/// the <c>Authorization</c> header to the other host, which is what GitHub's documentation expects.</para>
/// </summary>
public sealed class IntakeGitHub
{
    public const string ApiVersion = "2022-11-28";
    public const int PageSize = 100;
    public const int MaxPages = 5;

    private readonly HttpClient _http;

    public IntakeGitHub(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.github.com/");
    }

    /// <summary>The App's installation on <paramref name="repo"/>'s owner: its id, read with the App's JWT.</summary>
    public async Task<long> InstallationIdAsync(string appJwt, string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"repos/{repo}/installation", appJwt, $"the App's installation on {repo}", ct);
        return json["id"]!.GetValue<long>();
    }

    /// <summary>
    /// An installation token for exactly <paramref name="repositoryIds"/>, with only Actions and Contents read, whatever the
    /// App was granted: GitHub narrows a token to what the request names.
    /// </summary>
    public async Task<string> InstallationTokenAsync(string appJwt, long installationId, IReadOnlyList<long> repositoryIds, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", appJwt);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            repository_ids = repositoryIds,
            permissions = new { actions = "read", contents = "read" },
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new GitHubApiException(response.StatusCode, $"an installation token for installation {installationId}");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return json["token"]!.GetValue<string>();
    }

    /// <summary>
    /// The workflow's COMPLETED runs on <c>main</c> created at or after <paramref name="since"/>, newest first, every page.
    /// <paramref name="workflowFile"/> is the file name under <c>.github/workflows/</c>.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRun>> CompletedRunsAsync(string token, string repo, string workflowFile, DateTimeOffset since, CancellationToken ct)
    {
        var created = Uri.EscapeDataString($">={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        var path = $"repos/{repo}/actions/workflows/{Uri.EscapeDataString(workflowFile)}/runs?branch=main&status=completed&created={created}";
        var items = await PagedAsync(path, "workflow_runs", token, $"the runs of {repo}'s {workflowFile}", ct);
        return items.Select(MapRun).ToList();
    }

    /// <summary>The jobs of one attempt of a run, every page.</summary>
    public async Task<IReadOnlyList<GitHubJob>> JobsAsync(string token, string repo, long runId, int attempt, CancellationToken ct)
    {
        var items = await PagedAsync($"repos/{repo}/actions/runs/{runId}/attempts/{attempt}/jobs", "jobs", token,
            $"the jobs of {repo} run {runId} attempt {attempt}", ct);
        return items.Select(j => new GitHubJob(
            j["id"]!.GetValue<long>(),
            j["runner_group_name"]?.GetValueKind() == JsonValueKind.String ? j["runner_group_name"]!.GetValue<string>() : null,
            (j["labels"] as JsonArray ?? new JsonArray()).Select(l => l!.GetValue<string>()).ToList())).ToList();
    }

    /// <summary>A run's artifacts, every page.</summary>
    public async Task<IReadOnlyList<GitHubArtifact>> ArtifactsAsync(string token, string repo, long runId, CancellationToken ct)
    {
        var items = await PagedAsync($"repos/{repo}/actions/runs/{runId}/artifacts", "artifacts", token,
            $"the artifacts of {repo} run {runId}", ct);
        return items.Select(a => new GitHubArtifact(
            a["id"]!.GetValue<long>(),
            a["name"]!.GetValue<string>(),
            a["workflow_run"]?["id"]?.GetValue<long>() ?? 0,
            a["expired"]?.GetValue<bool>() ?? true,
            a["digest"]?.GetValueKind() == JsonValueKind.String ? a["digest"]!.GetValue<string>() : null,
            a["size_in_bytes"]?.GetValue<long>() ?? 0)).ToList();
    }

    /// <summary>An artifact's zip, as a stream the caller disposes. GitHub redirects to blob storage, which the handler follows.</summary>
    public async Task<Stream> DownloadArtifactAsync(string token, string repo, long artifactId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"repos/{repo}/actions/artifacts/{artifactId}/zip", token);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new GitHubApiException(response.StatusCode, $"the download of {repo} artifact {artifactId}");
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public static GitHubRun MapRun(JsonNode r) => new(
        r["id"]!.GetValue<long>(),
        r["run_attempt"]!.GetValue<int>(),
        r["repository"]!["id"]!.GetValue<long>(),
        r["repository"]!["owner"]!["id"]!.GetValue<long>(),
        // A run whose head repository is gone reports none: 0 is no repository, which RunCheck refuses as a fork.
        r["head_repository"]?["id"]?.GetValue<long>() ?? 0,
        r["path"]!.GetValue<string>(),
        r["head_branch"]?.GetValue<string>() ?? "",
        r["event"]!.GetValue<string>(),
        r["status"]!.GetValue<string>(),
        r["conclusion"]?.GetValueKind() == JsonValueKind.String ? r["conclusion"]!.GetValue<string>() : null,
        DateTimeOffset.Parse(r["created_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(r["updated_at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

    private async Task<List<JsonNode>> PagedAsync(string path, string listName, string token, string what, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            var json = await GetJsonAsync($"{path}{separator}per_page={PageSize}&page={page}", token, what, ct);
            var items = json[listName] as JsonArray ?? throw new InvalidOperationException($"GitHub's answer for {what} has no '{listName}'");
            var total = json["total_count"]?.GetValue<int>() ?? items.Count;
            all.AddRange(items.Where(i => i is not null)!);
            if (all.Count >= total || items.Count == 0) return all;
        }
        throw new InvalidOperationException(
            $"{what}: more than {MaxPages * PageSize} items, and a list read in part would be taken for the whole.");
    }

    private async Task<JsonNode> GetJsonAsync(string path, string bearer, string what, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, bearer);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new GitHubApiException(response.StatusCode, what);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException($"GitHub's answer for {what} is empty");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("lz-intake", "1"));
        return request;
    }
}
