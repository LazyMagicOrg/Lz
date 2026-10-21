using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The intake's GitHub reads against GitHub itself (DecoupledCd.md V-B): the newest completed run of a workflow on main, its
/// jobs and its artifacts, mapped and then judged by <see cref="RunCheck"/>, so the mapping is held to the real API and not
/// only to fixtures written from it. Category Integration; it needs LZ_INTAKE_REPO (owner/name), LZ_INTAKE_WORKFLOW (the
/// file name) and LZ_GITHUB_TOKEN (any token that can read the repository's Actions), and without them passes having checked
/// nothing, and says so. Nothing it reads is printed.
/// </summary>
[Trait("Category", "Integration")]
public class IntakeGitHubLiveTests
{
    [Fact]
    public async Task TheNewestRunOnMain_MapsAndPassesTheRunCheck()
    {
        var repo = Environment.GetEnvironmentVariable("LZ_INTAKE_REPO");
        var workflow = Environment.GetEnvironmentVariable("LZ_INTAKE_WORKFLOW");
        var token = Environment.GetEnvironmentVariable("LZ_GITHUB_TOKEN");
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(workflow) || string.IsNullOrEmpty(token))
        {
            Console.WriteLine("LZ_INTAKE_REPO, LZ_INTAKE_WORKFLOW or LZ_GITHUB_TOKEN is not set: nothing was checked");
            return;
        }

        var gh = new IntakeGitHub(new HttpClient());
        var runs = await gh.CompletedRunsAsync(token, repo, workflow, DateTimeOffset.UtcNow.AddDays(-14), CancellationToken.None);
        var run = runs.Where(r => r.Conclusion == "success" && r.Event == "push").OrderByDescending(r => r.CreatedAt).First();

        Assert.Equal($".github/workflows/{workflow}", run.Path);
        Assert.Equal("main", run.HeadBranch);
        Assert.True(run.RepositoryId > 0 && run.OwnerId > 0);
        Assert.Equal(run.RepositoryId, run.HeadRepositoryId);

        var jobs = await gh.JobsAsync(token, repo, run.Id, run.RunAttempt, CancellationToken.None);
        Assert.NotEmpty(jobs);

        var source = new IntakeSource(repo, run.RepositoryId, run.OwnerId, run.Path);
        var check = RunCheck.Check(source, run, jobs);
        Assert.True(check.Kind == RunCheckKind.Passed, string.Join("; ", check.Reasons));

        // Read, not judged: a run from before the workflow uploaded its image has none.
        var artifacts = await gh.ArtifactsAsync(token, repo, run.Id, CancellationToken.None);
        Assert.All(artifacts, a => Assert.Equal(run.Id, a.WorkflowRunId));
    }

    /// <summary>
    /// The newest run's image artifact, downloaded as the fetch function downloads it, held to the fetch's own checks: V-A's
    /// ArtifactCheck for the attempt, the bytes against GitHub's digest, and the zip's one OCI archive read by OciArchive. Needs
    /// LZ_INTAKE_ARTIFACT (the configured artifact name, e.g. the image's) besides the variables above; downloads it to a
    /// temporary file and deletes it.
    /// </summary>
    [Fact]
    public async Task TheNewestImageArtifact_PassesTheFetchsChecks()
    {
        var repo = Environment.GetEnvironmentVariable("LZ_INTAKE_REPO");
        var workflow = Environment.GetEnvironmentVariable("LZ_INTAKE_WORKFLOW");
        var token = Environment.GetEnvironmentVariable("LZ_GITHUB_TOKEN");
        var artifact = Environment.GetEnvironmentVariable("LZ_INTAKE_ARTIFACT");
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(workflow) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(artifact))
        {
            Console.WriteLine("LZ_INTAKE_REPO, LZ_INTAKE_WORKFLOW, LZ_GITHUB_TOKEN or LZ_INTAKE_ARTIFACT is not set: nothing was checked");
            return;
        }

        var gh = new IntakeGitHub(new HttpClient());
        var runs = await gh.CompletedRunsAsync(token, repo, workflow, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        var run = runs.Where(r => r.Conclusion == "success").OrderByDescending(r => r.CreatedAt).First();
        var listed = await gh.ArtifactsAsync(token, repo, run.Id, CancellationToken.None);

        var check = ArtifactCheck.Check(run.Id, run.RunAttempt, new[] { artifact }, listed);
        Assert.True(check.Passed, string.Join("; ", check.Reasons));
        var found = check.Found[artifact];

        var path = Path.Combine(Path.GetTempPath(), $"lz-intake-live-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var source = await gh.DownloadArtifactAsync(token, repo, found.Id, CancellationToken.None))
            await using (var file = File.Create(path))
                await source.CopyToAsync(file);

            await using (var read = File.OpenRead(path))
            {
                var sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(read)).ToLowerInvariant();
                Assert.True(ArtifactCheck.DigestMatches(found.Digest, sha256), $"downloaded sha256:{sha256}, GitHub says {found.Digest}");
            }

            var image = IntakeFetchStep.ReadImage(path);
            Assert.StartsWith("sha256:", image.ManifestDigest);
            Assert.NotEmpty(image.Layers);
            Console.WriteLine($"one image: manifest {image.ManifestDigest[..19]}, {image.Layers.Count} layers");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
