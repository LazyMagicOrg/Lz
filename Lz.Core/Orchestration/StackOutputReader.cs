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
}
