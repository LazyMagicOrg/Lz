using Lz.Core.Interfaces;

namespace Lz.Core.Orchestration;

/// <summary>
/// Evaluates a list of transition requirements against the current deployment state.
/// Returns the first unmet requirement so the orchestrator can stop and inform the user.
/// </summary>
public static class TransitionGate
{
    /// <summary>
    /// Check all requirements in order. Returns the first unmet requirement,
    /// or null if all requirements are satisfied.
    /// </summary>
    public static async Task<TransitionRequirement?> CheckAsync(
        ITransitionChecker checker,
        IEnumerable<TransitionRequirement> requirements,
        string systemKey,
        string? tenantKey = null)
    {
        foreach (var req in requirements)
        {
            var met = await checker.CheckAsync(req, systemKey, tenantKey);
            if (!met)
                return req;
        }

        return null;
    }

    /// <summary>
    /// Check all requirements. Prints a confirmation for each passing gate.
    /// If any are unmet, print the gate failure message and return false.
    /// If all pass, return true.
    /// </summary>
    public static async Task<bool> CheckAndReportAsync(
        ITransitionChecker checker,
        IEnumerable<TransitionRequirement> requirements,
        string systemKey,
        string? tenantKey = null)
    {
        foreach (var req in requirements)
        {
            var met = await checker.CheckAsync(req, systemKey, tenantKey);
            if (met)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ {req.Name}");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  GATE: {req.Name}");
                Console.WriteLine();
                Console.WriteLine($"  {req.Description}");
                Console.WriteLine();
                Console.WriteLine("  Re-run the same deploy command after completing this step.");
                Console.ResetColor();
                Console.WriteLine();
                return false;
            }
        }

        return true;
    }
}
