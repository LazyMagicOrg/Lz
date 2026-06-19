using System.Text;
using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Orchestration;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS-specific transition checker. Validates gates by inspecting
/// Secrets Manager entries, and invoking the gate-checker Lambda
/// for EFS data and database checks.
/// </summary>
public class AwsTransitionChecker : ITransitionChecker
{
    private readonly SystemConfig _config;

    public AwsTransitionChecker(SystemConfig config)
    {
        _config = config;
    }

    public async Task<bool> CheckAsync(
        TransitionRequirement requirement,
        string systemKey,
        string? tenantKey = null)
    {
        return requirement.CheckType switch
        {
            TransitionCheckType.SecretEntry => await CheckSecretEntryAsync(requirement, systemKey, tenantKey),
            TransitionCheckType.EfsData => await CheckEfsDataAsync(requirement, systemKey, tenantKey),
            TransitionCheckType.DatabaseData => await CheckDatabaseDataAsync(requirement, systemKey, tenantKey),
            TransitionCheckType.Custom => requirement.CustomCheck != null && await requirement.CustomCheck(),
            TransitionCheckType.StackOutput => false, // Not yet used — implement when a gate requires Pulumi stack output checks
            _ => false,
        };
    }

    /// <summary>
    /// Check that a specific JSON key exists and is non-empty in a Secrets Manager secret.
    /// </summary>
    private async Task<bool> CheckSecretEntryAsync(
        TransitionRequirement requirement,
        string systemKey,
        string? tenantKey)
    {
        var secretName = requirement.SecretName ?? "";
        secretName = secretName.Replace("{SK}", systemKey);
        secretName = secretName.Replace("{env}", _config.Environment);
        if (tenantKey != null)
            secretName = secretName.Replace("{TK}", tenantKey);

        var keyName = requirement.CheckTarget;

        try
        {
            var client = CreateSecretsManagerClient(requirement.Profile, requirement.Region);
            var response = await client.GetSecretValueAsync(
                new Amazon.SecretsManager.Model.GetSecretValueRequest { SecretId = secretName });

            using var doc = JsonDocument.Parse(response.SecretString);
            if (doc.RootElement.TryGetProperty(keyName, out var value))
            {
                var str = value.GetString();
                return !string.IsNullOrEmpty(str);
            }

            return false;
        }
        catch (Amazon.SecretsManager.Model.ResourceNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Could not check secret '{secretName}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    /// <summary>
    /// Check for data on EFS by invoking the gate-checker Lambda.
    /// </summary>
    private async Task<bool> CheckEfsDataAsync(
        TransitionRequirement requirement,
        string systemKey,
        string? tenantKey)
    {
        var path = requirement.CheckTarget;
        path = path.Replace("{SK}", systemKey);
        path = path.Replace("{env}", _config.Environment);
        if (tenantKey != null)
            path = path.Replace("{TK}", tenantKey);

        var payload = new { check_type = "efs", path };
        return await InvokeLambdaCheckAsync(systemKey, payload);
    }

    /// <summary>
    /// Check for database tables by invoking the gate-checker Lambda.
    /// </summary>
    private async Task<bool> CheckDatabaseDataAsync(
        TransitionRequirement requirement,
        string systemKey,
        string? tenantKey)
    {
        var dbName = requirement.CheckTarget;
        dbName = dbName.Replace("{SK}", systemKey);
        dbName = dbName.Replace("{env}", _config.Environment);
        if (tenantKey != null)
            dbName = dbName.Replace("{TK}", tenantKey);

        var payload = new { check_type = "database", db_name = dbName };
        return await InvokeLambdaCheckAsync(systemKey, payload);
    }

    /// <summary>
    /// Invoke the gate-checker Lambda and parse the response.
    /// </summary>
    private async Task<bool> InvokeLambdaCheckAsync(string systemKey, object payload)
    {
        var functionName = $"{systemKey}-gate-checker";

        try
        {
            var client = CreateLambdaClient();
            var payloadJson = JsonSerializer.Serialize(payload);

            var response = await client.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.RequestResponse,
                Payload = payloadJson,
            });

            if (response.FunctionError != null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: Gate-checker Lambda error: {response.FunctionError}");
                Console.ResetColor();
                return false;
            }

            using var reader = new StreamReader(response.Payload);
            var responseJson = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("passed", out var passedProp))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: Gate-checker Lambda returned unexpected response: {responseJson}");
                Console.ResetColor();
                return false;
            }

            var passed = passedProp.GetBoolean();

            if (!passed && doc.RootElement.TryGetProperty("reason", out var reasonProp))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {reasonProp.GetString()}");
                Console.ResetColor();
            }

            return passed;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Gate-checker Lambda '{functionName}' not found. Run `lz deploysystem` first.");
            Console.ResetColor();
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Could not invoke gate-checker Lambda '{functionName}': {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    private AmazonLambdaClient CreateLambdaClient()
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_config.Region);
        var lambdaConfig = new AmazonLambdaConfig
        {
            RegionEndpoint = regionEndpoint,
            // Gate checks may take minutes (DB connectivity, EFS checks).
            // Default SDK timeout (~100s) is too short for synchronous invocations.
            Timeout = TimeSpan.FromMinutes(15),
        };

        if (!string.IsNullOrEmpty(_config.Profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(_config.Profile, out var credentials))
                return new AmazonLambdaClient(credentials, lambdaConfig);
        }

        return new AmazonLambdaClient(lambdaConfig);
    }

    private AmazonSecretsManagerClient CreateSecretsManagerClient(string? profileOverride = null, string? regionOverride = null)
    {
        var profile = profileOverride ?? _config.Profile;
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(regionOverride ?? _config.Region);

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonSecretsManagerClient(credentials, regionEndpoint);
        }

        return new AmazonSecretsManagerClient(regionEndpoint);
    }
}
