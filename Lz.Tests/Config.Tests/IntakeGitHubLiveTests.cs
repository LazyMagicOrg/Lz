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

        // Read, not judged: the workflow uploads no artifacts until V-B changes it.
        var artifacts = await gh.ArtifactsAsync(token, repo, run.Id, CancellationToken.None);
        Assert.All(artifacts, a => Assert.Equal(run.Id, a.WorkflowRunId));
    }
}
