using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Aws.DynamoDB;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Foundation post-deploy:
/// 1. Creates the system-level DynamoDB table (table name = SystemKey).
/// 2. Ensures a Managed Login branding exists for every Cognito pool
///    client created by the foundation stack. The pool domains are
///    pinned to ManagedLoginVersion=2 (the legacy v1 has a broken
///    post-signup-confirm OAuth continuation — see
///    AwsAppRunnerCognitoComponent for context). v2 requires a
///    branding to be configured per-client, otherwise its pages won't
///    render. We create the branding with UseCognitoProvidedValues
///    (default look) since custom branding assets aren't needed for
///    the system to work — operators can swap in real branding later
///    via the Cognito console or by extending this step.
/// </summary>
public class AwsEcsExpressFoundationPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;

    public AwsEcsExpressFoundationPostDeployAction(SystemConfig config)
    {
        _config = config;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        // Step 1 — system DynamoDB table.
        var tableName = _config.SystemKey;
        Console.WriteLine($"  Ensuring system DynamoDB table '{tableName}'...");

        var created = await DynamoDbTableCreator.EnsureTableAsync(
            _config.Profile, _config.Region, tableName,
            new Dictionary<string, string>
            {
                { "System", _config.SystemKey },
                { "Level", "system" },
            });

        if (created)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  System table '{tableName}' created.");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine($"  System table '{tableName}' already exists.");
        }

        // Step 2 — Managed Login branding per Cognito client.
        await EnsureManagedLoginBrandingsAsync(outputs);
    }

    /// <summary>
    /// For each Cognito pool the foundation stack exported, create a
    /// Managed Login branding (using Cognito-provided defaults) if one
    /// doesn't already exist. ResourceAlreadyExistsException is the
    /// expected response on a re-run — swallowed for idempotency.
    /// </summary>
    private async Task EnsureManagedLoginBrandingsAsync(IDictionary<string, object> outputs)
    {
        if (_config.AuthConfigs is null || _config.AuthConfigs.Count == 0) return;

        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(_config.Profile, out var credentials))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"  Skipping Managed Login branding ensure — could not resolve credentials " +
                $"for profile '{_config.Profile}'.");
            Console.ResetColor();
            return;
        }

        using var idp = new Amazon.CognitoIdentityProvider.AmazonCognitoIdentityProviderClient(
            credentials,
            Amazon.RegionEndpoint.GetBySystemName(_config.Region));

        foreach (var authType in _config.AuthConfigs.Keys)
        {
            // Pulumi outputs from AwsAppRunnerCognitoComponent are keyed
            // as `auth_{authType}_{field}` and arrive here as raw
            // OutputValue objects. The post-deploy framework hands us the
            // .Value already unboxed via the IDictionary<string, object>
            // shape, so a plain ToString() is enough.
            if (!outputs.TryGetValue($"auth_{authType}_userPoolId", out var poolIdObj) ||
                !outputs.TryGetValue($"auth_{authType}_clientId", out var clientIdObj))
            {
                continue;
            }
            var poolId = poolIdObj?.ToString();
            var clientId = clientIdObj?.ToString();
            if (string.IsNullOrEmpty(poolId) || string.IsNullOrEmpty(clientId)) continue;

            Console.WriteLine($"  Ensuring Managed Login branding for {authType} (client {clientId})...");
            try
            {
                await idp.CreateManagedLoginBrandingAsync(
                    new Amazon.CognitoIdentityProvider.Model.CreateManagedLoginBrandingRequest
                    {
                        UserPoolId = poolId,
                        ClientId = clientId,
                        UseCognitoProvidedValues = true,
                    });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    {authType} — branding created.");
                Console.ResetColor();
            }
            catch (Amazon.CognitoIdentityProvider.Model.ManagedLoginBrandingExistsException)
            {
                Console.WriteLine($"    {authType} — branding already exists.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"    {authType} — branding ensure failed (non-fatal): {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}
