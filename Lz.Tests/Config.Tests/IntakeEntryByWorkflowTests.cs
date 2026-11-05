using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// ONE REPOSITORY, TWO CLASSES (DecoupledCd.md P3 B, owner 2026-11-05): the root builds the tooling image and, in P3 C, the config
/// bundle, each with a workflow of its own. The intake used to find a run's entry by repository alone, so a second entry for
/// the same repository was never reached. The poller now says which workflow it listed the run under, and the steps match on
/// repository, id and workflow; an input written before that carries no workflow and still resolves where the repository has
/// one entry.
/// </summary>
public class IntakeEntryByWorkflowTests
{
    private const long Id = 42;
    private static readonly IntakeRepository Tooling = new("Example/Root", Id, 7, ".github/workflows/build-tooling.yml", "dry", "tooling", new[] { "tooling" });
    private static readonly IntakeRepository Config = new("Example/Root", Id, 7, ".github/workflows/build-config.yml", "dry", "config", new[] { "config" });

    private static IntakeInput Input(string? workflow) => IntakeInput.From(new JsonObject
    {
        ["repo"] = "Example/Root", ["repositoryId"] = Id, ["runId"] = 1L, ["runAttempt"] = 1, ["retry"] = 0,
        ["workflowPath"] = workflow,
    });

    [Fact]
    public void TheWorkflowPicksTheEntry()
    {
        var both = new[] { Tooling, Config };
        Assert.Same(Config, Input(".github/workflows/build-config.yml").In(both));
        Assert.Same(Tooling, Input(".github/workflows/build-tooling.yml").In(both));
    }

    [Fact]
    public void AWorkflowNoEntryNames_IsNotTaken()
        => Assert.Null(Input(".github/workflows/other.yml").In(new[] { Tooling, Config }));

    [Fact]
    public void WithoutAWorkflow_OneEntryStillResolves()
        => Assert.Same(Tooling, Input(null).In(new[] { Tooling }));

    [Fact]
    public void WithoutAWorkflow_TwoEntriesAreAmbiguous_AndNeitherIsGuessed()
        => Assert.Null(Input(null).In(new[] { Tooling, Config }));

    [Fact]
    public void TheValidatorRefusesTwoEntriesOfOneRepositoryWithTheSameWorkflow()
    {
        var config = new Lz.Core.Config.BuildSideConfig
        {
            BuildAccountId = "111111111111", BuildSuffix = "ab12-cd34",
            Repositories = new()
            {
                new() { Repo = "Example/Root", Class = "tooling", RepositoryId = Id, OwnerId = 7, WorkflowPath = ".github/workflows/x.yml", Intake = "dry", Artifacts = new() { "tooling" } },
                new() { Repo = "Example/Root", Class = "config", RepositoryId = Id, OwnerId = 7, WorkflowPath = ".github/workflows/x.yml", Intake = "dry", Artifacts = new() { "config" } },
            },
        };
        Assert.Contains(config.Problems(), p => p.Contains("Example/Root") && p.Contains("workflow"));
    }
}
