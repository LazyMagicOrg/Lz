using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The intake's GitHub reads (DecoupledCd.md V-B). The answers below are shaped as GitHub's, with invented values: Lz is a
/// public repository and a real run of a private one is not test data for it. Shapes measured 2026-10-20 with `gh api` against
/// a build-aiphost.yml run: its two jobs report runner_group_name "GitHub Actions" and labels ["ubuntu-latest"].
/// </summary>
public class IntakeGitHubTests
{
    private const string Repo = "Example/ExampleService";

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string PathAndQuery, string? Auth, string? Body, HttpRequestMessage Request)> Seen = new();
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _answer;
        public Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body, request));
            var (status, json) = _answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (IntakeGitHub GitHub, Fake Fake) With(Func<HttpRequestMessage, (HttpStatusCode, string)> answer)
    {
        var fake = new Fake(answer);
        return (new IntakeGitHub(new HttpClient(fake)), fake);
    }

    private static string Run(long id, int attempt = 1, string conclusion = "success", string head = "{\"id\": 1001}") => $$$"""
        {"id": {{{id}}}, "run_attempt": {{{attempt}}}, "status": "completed", "conclusion": "{{{conclusion}}}", "event": "push",
         "head_branch": "main", "path": ".github/workflows/build-aiphost.yml",
         "created_at": "2026-10-20T22:08:49Z", "updated_at": "2026-10-20T22:11:28Z",
         "repository": {"id": 1001, "owner": {"id": 2002}}, "head_repository": {{{head}}}}
        """;

    // ---- the App's JWT ----

    [Fact]
    public void TheJwtIsRs256_SignedWithTheAppsKey_AndInsideGitHubsTenMinutes()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);

        var jwt = GitHubAppJwt.Create("Iv23liExampleClientId", rsa.ExportRSAPrivateKeyPem(), now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] Un(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
        using var header = JsonDocument.Parse(Un(parts[0]));
        using var payload = JsonDocument.Parse(Un(parts[1]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("Iv23liExampleClientId", payload.RootElement.GetProperty("iss").GetString());
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds(), iat);
        Assert.True(exp - now.ToUnixTimeSeconds() <= 600, "GitHub refuses an exp more than ten minutes ahead");
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Un(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    // ---- tokens ----

    [Fact]
    public async Task TheInstallationToken_IsAskedForTheNamedRepositoriesWithReadOnlyActionsAndContents()
    {
        var (gh, fake) = With(_ => (HttpStatusCode.Created, """{"token": "ghs_example", "expires_at": "2026-10-21T02:00:00Z"}"""));

        var token = await gh.InstallationTokenAsync("app.jwt", 77, new long[] { 1001 }, CancellationToken.None);

        Assert.Equal("ghs_example", token);
        var (method, path, auth, body, request) = Assert.Single(fake.Seen);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/app/installations/77/access_tokens", path);
        Assert.Equal("Bearer app.jwt", auth);
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(new long[] { 1001 }, sent.RootElement.GetProperty("repository_ids").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal("read", sent.RootElement.GetProperty("permissions").GetProperty("actions").GetString());
        Assert.Equal("read", sent.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal(2, sent.RootElement.GetProperty("permissions").EnumerateObject().Count());
        Assert.Equal(IntakeGitHub.ApiVersion, request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task ARefusedTokenRequest_ThrowsWithTheStatus()
    {
        var (gh, _) = With(_ => (HttpStatusCode.Unauthorized, """{"message": "A JSON web token could not be decoded"}"""));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => gh.InstallationTokenAsync("bad", 77, new long[] { 1001 }, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.DoesNotContain("decoded", ex.Message);   // GitHub's body is never carried
    }

    [Fact]
    public async Task TheInstallation_IsFoundFromTheRepository()
    {
        var (gh, fake) = With(_ => (HttpStatusCode.OK, """{"id": 77, "app_id": 5}"""));
        Assert.Equal(77, await gh.InstallationIdAsync("app.jwt", Repo, CancellationToken.None));
        Assert.Equal($"/repos/{Repo}/installation", fake.Seen.Single().PathAndQuery);
    }

    // ---- runs ----

    [Fact]
    public async Task Runs_AreTheWorkflowsCompletedRunsOnMain_SinceTheCutoff_Mapped()
    {
        var (gh, fake) = With(_ => (HttpStatusCode.OK, $$"""{"total_count": 2, "workflow_runs": [{{Run(9001)}}, {{Run(9000, 2, "failure")}}]}"""));
        var since = new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);

        var runs = await gh.CompletedRunsAsync("ghs_example", Repo, "build-aiphost.yml", since, CancellationToken.None);

        var path = fake.Seen.Single().PathAndQuery;
        Assert.StartsWith($"/repos/{Repo}/actions/workflows/build-aiphost.yml/runs?", path);
        Assert.Contains("branch=main", path);
        Assert.Contains("status=completed", path);
        Assert.Contains("created=%3E%3D2026-10-20T01%3A02%3A03Z", path);
        Assert.Equal("Bearer ghs_example", fake.Seen.Single().Auth);

        Assert.Equal(new GitHubRun(9001, 1, 1001, 2002, 1001, ".github/workflows/build-aiphost.yml", "main", "push", "completed", "success",
            DateTimeOffset.Parse("2026-10-20T22:08:49Z"), DateTimeOffset.Parse("2026-10-20T22:11:28Z")), runs[0]);
        Assert.Equal((2, "failure"), (runs[1].RunAttempt, runs[1].Conclusion));
    }

    [Fact]
    public void ARunWhoseHeadRepositoryIsGone_MapsToNone_WhichRunCheckRefusesAsAFork()
    {
        var run = IntakeGitHub.MapRun(System.Text.Json.Nodes.JsonNode.Parse(Run(9001, head: "null"))!);
        Assert.Equal(0, run.HeadRepositoryId);
    }

    [Fact]
    public async Task EveryPageIsRead_UntilGitHubsTotal()
    {
        var (gh, fake) = With(r => r.RequestUri!.Query.Contains("&page=1")
            ? (HttpStatusCode.OK, $$"""{"total_count": 2, "workflow_runs": [{{Run(9001)}}]}""")
            : (HttpStatusCode.OK, $$"""{"total_count": 2, "workflow_runs": [{{Run(9000)}}]}"""));

        var runs = await gh.CompletedRunsAsync("t", Repo, "build-aiphost.yml", DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(new long[] { 9001, 9000 }, runs.Select(r => r.Id));
        Assert.Equal(2, fake.Seen.Count);
    }

    [Fact]
    public async Task AListTooLongToReadWhole_IsAnError_NotAShorterList()
    {
        var (gh, fake) = With(_ => (HttpStatusCode.OK, $$"""{"total_count": 100000, "workflow_runs": [{{Run(9001)}}]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gh.CompletedRunsAsync("t", Repo, "build-aiphost.yml", DateTimeOffset.UnixEpoch, CancellationToken.None));
        Assert.Equal(IntakeGitHub.MaxPages, fake.Seen.Count);
    }

    // ---- jobs and artifacts ----

    [Fact]
    public async Task Jobs_AreTheAttemptsJobs_WithTheirRunnerGroupAndLabels()
    {
        var (gh, fake) = With(_ => (HttpStatusCode.OK, """
            {"total_count": 2, "jobs": [
              {"id": 501, "runner_group_name": "GitHub Actions", "labels": ["ubuntu-latest"]},
              {"id": 502, "runner_group_name": null, "labels": ["self-hosted", "linux"]}]}
            """));

        var jobs = await gh.JobsAsync("t", Repo, 9001, 2, CancellationToken.None);

        Assert.StartsWith($"/repos/{Repo}/actions/runs/9001/attempts/2/jobs?", fake.Seen.Single().PathAndQuery);
        Assert.Equal("GitHub Actions", jobs[0].RunnerGroupName);
        Assert.Equal(new[] { "ubuntu-latest" }, jobs[0].Labels);
        Assert.Null(jobs[1].RunnerGroupName);
        Assert.Contains("self-hosted", jobs[1].Labels);
    }

    [Fact]
    public async Task Artifacts_AreMappedWithTheirDigestRunAndExpiry()
    {
        var (gh, _) = With(_ => (HttpStatusCode.OK, """
            {"total_count": 2, "artifacts": [
              {"id": 31, "name": "aiphost-1", "expired": false, "digest": "sha256:abc", "size_in_bytes": 12345, "workflow_run": {"id": 9001}},
              {"id": 32, "name": "tests-1", "expired": true, "digest": null, "size_in_bytes": 9, "workflow_run": {"id": 9001}}]}
            """));

        var artifacts = await gh.ArtifactsAsync("t", Repo, 9001, CancellationToken.None);

        Assert.Equal(new GitHubArtifact(31, "aiphost-1", 9001, false, "sha256:abc", 12345), artifacts[0]);
        Assert.Equal((true, (string?)null), (artifacts[1].Expired, artifacts[1].Digest));
    }

    [Fact]
    public async Task ADownload_StreamsTheBytes_AndAFailureThrowsWithTheStatus()
    {
        var (gh, fake) = With(r => r.RequestUri!.AbsolutePath.EndsWith("/31/zip") ? (HttpStatusCode.OK, "PK-bytes") : (HttpStatusCode.Gone, ""));

        using (var s = await gh.DownloadArtifactAsync("t", Repo, 31, CancellationToken.None))
            Assert.Equal("PK-bytes", await new StreamReader(s).ReadToEndAsync());
        Assert.Equal($"/repos/{Repo}/actions/artifacts/31/zip", fake.Seen[0].PathAndQuery);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => gh.DownloadArtifactAsync("t", Repo, 32, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Gone, ex.Status);
    }
}
