namespace Lz.Core.Interaction;

/// <summary>
/// A "type 'yes' to confirm" question, and what it does with no one to answer (DecoupledCd.md P3 D). The deployer runs
/// <c>lz</c> as an ECS task, whose stdin is empty: a question there reads end-of-input, which the commands took as "no" and
/// then exited 0, so a task that asked and did nothing looked like a task that succeeded. With no terminal the question is
/// not asked at all: the command is refused, says so, and the exit code is <see cref="NoTerminalExitCode"/>. A person who
/// answers anything but "yes" is still a plain "no", exit 0, as before.
/// </summary>
public static class Confirmation
{
    /// <summary>The exit code of a command that needed a confirmation and had no terminal to ask on.</summary>
    public const int NoTerminalExitCode = 3;

    /// <summary>
    /// Ask, and return whether the answer was "yes" (trimmed, case ignored when <paramref name="exact"/> is false). With no
    /// terminal, ask nothing, print why, set <see cref="Environment.ExitCode"/> and return false.
    /// </summary>
    public static bool Ask(string prompt, string? passInstead = "--yes", bool exact = false)
        => Ask(prompt, Console.IsInputRedirected, Console.In, Console.Out, Console.Error, code => Environment.ExitCode = code, passInstead, exact);

    /// <summary>The same, with the terminal's parts passed in, for tests.</summary>
    public static bool Ask(string prompt, bool inputRedirected, TextReader input, TextWriter output, TextWriter error,
        Action<int> setExitCode, string? passInstead = "--yes", bool exact = false)
    {
        if (inputRedirected)
        {
            error.WriteLine($"Refusing: this needs a confirmation ({prompt.Trim().TrimEnd(':')}) and no terminal is attached to ask on." +
                (passInstead is null ? "" : $" Pass {passInstead} to confirm it non-interactively."));
            setExitCode(NoTerminalExitCode);
            return false;
        }
        output.Write(prompt);
        var answer = input.ReadLine();
        return exact
            ? string.Equals(answer, "yes", StringComparison.Ordinal)
            : string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }
}
