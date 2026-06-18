using Lz.Core.Config;
using Pulumi.Automation;

namespace Lz.Core.Orchestration;

/// <summary>
/// Reads Pulumi stack outputs without running an update.
/// Used by CLI commands that need infrastructure outputs (e.g., S3 bucket names, CloudFront IDs).
/// </summary>
public static class StackOutputReader
{
    public static async Task<IDictionary<string, OutputValue>> GetOutputsAsync(
        SystemConfig config, string stackName)
    {
        PulumiPathResolver.EnsurePulumiOnPath();

        var projectName = $"lz-{config.SystemKey}";

        var envVars = new Dictionary<string, string?>();

        if (config.State != null && !string.IsNullOrEmpty(config.State.Backend))
            envVars["PULUMI_BACKEND_URL"] = config.State.Backend;

        if (config.State != null && !string.IsNullOrEmpty(config.State.SecretsProvider))
            envVars["PULUMI_CONFIG_PASSPHRASE"] = "";

        if (!string.IsNullOrEmpty(config.Region))
            envVars["AWS_REGION"] = config.Region;
        if (!string.IsNullOrEmpty(config.Profile))
            envVars["AWS_PROFILE"] = config.Profile;

        // Use a no-op program — we only need to read outputs, not run an update
        var stackArgs = new InlineProgramArgs(projectName, stackName,
            PulumiFn.Create(() => new Dictionary<string, object?>()));
        stackArgs.EnvironmentVariables = envVars;

        if (config.State != null && !string.IsNullOrEmpty(config.State.SecretsProvider))
            stackArgs.SecretsProvider = config.State.SecretsProvider;

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs);
        return await stack.GetOutputsAsync();
    }

    /// <summary>
    /// Preflight check: verify the foundation stack for this system/environment
    /// has been successfully deployed. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message pointing at <c>lz deploysystem</c> if the
    /// stack is absent or its required outputs are missing.
    /// </summary>
    /// <remarks>
    /// Uses the foundation stack naming convention <c>{systemKey}-{environment}</c>
    /// (matches <c>SystemDeployment.DeployFoundationAsync</c>). Uses
    /// <c>SelectStackAsync</c> (read-only) so running this check on a never-
    /// deployed system does not leak workspace metadata into <c>$HOME/.pulumi</c>.
    /// Foundation deployment always exports <c>vpcId</c> — absent or empty-valued,
    /// treat the stack as not-yet-deployed.
    /// </remarks>
    public static async Task EnsureFoundationDeployedAsync(SystemConfig config)
    {
        PulumiPathResolver.EnsurePulumiOnPath();

        var stackName = $"{config.SystemKey}-{config.Environment}";
        var projectName = $"lz-{config.SystemKey}";

        var envVars = new Dictionary<string, string?>();
        if (config.State?.Backend is { Length: > 0 } backend)
            envVars["PULUMI_BACKEND_URL"] = backend;
        if (config.State?.SecretsProvider is { Length: > 0 })
            envVars["PULUMI_CONFIG_PASSPHRASE"] = "";
        if (!string.IsNullOrEmpty(config.Region))
            envVars["AWS_REGION"] = config.Region;
        if (!string.IsNullOrEmpty(config.Profile))
            envVars["AWS_PROFILE"] = config.Profile;

        var stackArgs = new InlineProgramArgs(projectName, stackName,
            PulumiFn.Create(() => new Dictionary<string, object?>()))
        {
            EnvironmentVariables = envVars,
        };
        if (config.State?.SecretsProvider is { Length: > 0 } sp)
            stackArgs.SecretsProvider = sp;

        IDictionary<string, OutputValue> outputs;
        try
        {
            // SelectStackAsync throws if the stack doesn't exist in the backend —
            // no side effects (unlike CreateOrSelectStackAsync).
            var stack = await LocalWorkspace.SelectStackAsync(stackArgs);
            outputs = await stack.GetOutputsAsync();
        }
        catch (Exception ex) when (LooksLikeStackMissing(ex))
        {
            throw new InvalidOperationException(
                $"System stack '{stackName}' does not exist for system " +
                $"'{config.SystemKey}' ({config.Environment}). " +
                "Run `lz deploysystem` first to deploy system-level infrastructure.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read outputs from system stack '{stackName}' for " +
                $"system '{config.SystemKey}' ({config.Environment}). " +
                $"Check the Pulumi backend and AWS credentials, then re-run." +
                Environment.NewLine +
                $"  (underlying error: {ex.Message})", ex);
        }

        // Foundation always exports vpcId; its presence is the signal that
        // deploysystem completed. Checking a specific key avoids the edge case
        // where a caller-authored stack has zero exports by design.
        if (!outputs.ContainsKey("vpcId"))
        {
            throw new InvalidOperationException(
                $"System stack '{stackName}' is missing the 'vpcId' output — " +
                $"`lz deploysystem` hasn't completed for system '{config.SystemKey}' ({config.Environment}). " +
                "Run `lz deploysystem` first, then retry the tenant command.");
        }
    }

    // Pulumi Automation doesn't expose a typed stack-not-found exception; it
    // surfaces as a CommandException whose message includes "no stack named"
    // or "not found". Match on the message rather than add a runtime-version
    // coupling to internal Pulumi types.
    private static bool LooksLikeStackMissing(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("no stack named", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("stack not found", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
