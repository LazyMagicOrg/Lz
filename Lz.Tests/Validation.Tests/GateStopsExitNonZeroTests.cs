using Lz.Core.Interfaces;
using Lz.Core.Orchestration;
using Lz.Tests.Build.Tests;

namespace Lz.Tests.Validation.Tests;

/// <summary>
/// DecoupledCd.md P3 stage A: "the paths that stop and exit 0 exit non-zero" - every <see cref="TransitionGate"/> stop,
/// and <c>deploytenant</c>'s ECR gate. A gate is a deploy that did not happen; run by a person it was a yellow message to
/// act on, run by the deployer's tooling task it must be a failed step, or the infrastructure branch records a deploy that
/// stopped at its first gate as done.
/// </summary>
[Collection("ProcessExitCode")]
public class GateStopsExitNonZeroTests
{
    private sealed class Checker(bool met) : ITransitionChecker
    {
        public Task<bool> CheckAsync(TransitionRequirement requirement, string systemKey, string? tenantKey = null)
            => Task.FromResult(met);
    }

    private static readonly TransitionRequirement[] Gate = { new() { Name = "a-gate", Description = "do the thing" } };

    [Fact]
    public async Task AnUnmetGate_SetsTheExitCode()
    {
        var before = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            Assert.False(await TransitionGate.CheckAndReportAsync(new Checker(met: false), Gate, "sk"));
            Assert.Equal(1, Environment.ExitCode);
        }
        finally { Environment.ExitCode = before; }   // the test host must not exit 1 because a gate was tested
    }

    [Fact]
    public async Task AMetGate_LeavesItAlone()
    {
        var before = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            Assert.True(await TransitionGate.CheckAndReportAsync(new Checker(met: true), Gate, "sk"));
            Assert.Equal(0, Environment.ExitCode);
        }
        finally { Environment.ExitCode = before; }
    }

    [Fact]
    public void DeployTenantsEcrGate_FailsTheCommand()
    {
        // Lz.Tests does not reference Lz.Cli, so this reads the source, as WorkstationImageRefusalCallSiteTests does.
        var src = File.ReadAllText(Path.Combine(PackageHandlingScratchBuild.FindLzRepoRoot(), "Lz.Cli", "Program.cs"));
        var gate = src.IndexOf("Console.WriteLine(\"  GATE: ecr-images\");", StringComparison.Ordinal);
        Assert.True(gate >= 0, "deploytenant's ECR gate message has moved or been reworded");
        var block = src[gate..src.IndexOf("continue;", gate, StringComparison.Ordinal)];
        Assert.Contains("Environment.ExitCode = 1;", block);
    }
}
