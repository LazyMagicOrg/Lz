using System.Diagnostics;

namespace Lz.Core.Repos;

/// <summary>Result of one git invocation. A non-zero exit is DATA, never an exception.</summary>
public readonly record struct GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>StdOut trimmed, or null when the call failed or produced nothing.</summary>
    public string? Value => Ok && !string.IsNullOrWhiteSpace(StdOut) ? StdOut.Trim() : null;

    public IEnumerable<string> Lines =>
        StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
}

/// <summary>
/// Runs git as a child process. Deliberately minimal and deliberately non-throwing: half the value
/// of the repo report is rendering rows for repositories whose remote is unreachable, so a failure
/// must be reportable rather than fatal.
/// </summary>
public sealed class GitRunner
{
    private readonly int _timeoutMs;

    public GitRunner(int timeoutMs = 60_000) => _timeoutMs = timeoutMs;

    public async Task<GitResult> RunAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never let git stop for input: a repo whose credentials are not cached would otherwise
        // hang the whole report behind an invisible prompt.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new GitResult(-1, string.Empty, "failed to start git");

            // Read both streams concurrently BEFORE waiting. Waiting first can deadlock once a
            // pipe buffer fills — `git status` in a large repo is more than enough output to hit it.
            var stdOut = p.StandardOutput.ReadToEndAsync();
            var stdErr = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(_timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new GitResult(-1, string.Empty, $"git timed out after {_timeoutMs} ms");
            }

            return new GitResult(p.ExitCode,
                await stdOut.ConfigureAwait(false),
                await stdErr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new GitResult(-1, string.Empty, ex.Message);
        }
    }

    /// <summary>True when the command exited 0. Used for predicate-style git calls.</summary>
    public async Task<bool> SucceedsAsync(string workingDirectory, params string[] args) =>
        (await RunAsync(workingDirectory, args).ConfigureAwait(false)).Ok;
}
