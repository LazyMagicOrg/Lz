using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Orchestration;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Transition checker for AppRunner topology.
/// Checks Secrets Manager entries and custom gates.
/// No Lambda gate-checker needed (no EFS/RDS to verify from within VPC).
/// </summary>
public class AwsAppRunnerTransitionChecker : ITransitionChecker
{
    private readonly SystemConfig _config;

    public AwsAppRunnerTransitionChecker(SystemConfig config)
    {
        _config = config;
    }

    public async Task<bool> CheckAsync(TransitionRequirement requirement, string systemKey, string? tenantKey = null)
    {
        return requirement.CheckType switch
        {
            TransitionCheckType.SecretEntry => await CheckSecretEntryAsync(requirement),
            TransitionCheckType.Custom => requirement.CustomCheck != null && await requirement.CustomCheck(),
            TransitionCheckType.StackOutput => true, // Not implemented for AppRunner yet
            _ => true, // EFS/Database checks not applicable for AppRunner
        };
    }

    private async Task<bool> CheckSecretEntryAsync(TransitionRequirement requirement)
    {
        try
        {
            var profile = requirement.Profile ?? _config.Profile;
            var region = requirement.Region ?? _config.Region;

            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out var credentials))
                return false;

            using var client = new Amazon.SecretsManager.AmazonSecretsManagerClient(
                credentials,
                Amazon.RegionEndpoint.GetBySystemName(region));

            var response = await client.GetSecretValueAsync(
                new Amazon.SecretsManager.Model.GetSecretValueRequest
                {
                    SecretId = requirement.SecretName,
                });

            if (string.IsNullOrEmpty(response.SecretString))
                return false;

            var doc = System.Text.Json.JsonDocument.Parse(response.SecretString);
            return doc.RootElement.TryGetProperty(requirement.CheckTarget, out var prop)
                   && prop.GetString() is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
