using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Core.Interfaces;

namespace Lz.Aws.Lambda;

/// <summary>
/// Invokes the gate-checker Lambda with check_type=init_config to create
/// the tenant database, app user, and write EFS config files.
/// Follows the same Lambda invocation pattern as AwsTransitionChecker.
/// </summary>
public class AwsLambdaConfigInitRunner : IConfigInitRunner
{
    private readonly SystemConfig _config;

    public AwsLambdaConfigInitRunner(SystemConfig config)
    {
        _config = config;
    }

    public async Task<bool> RunInitConfigAsync(
        string tenantKey, string dbName, string appUser,
        string appVersion = "6.3.0.0", Dictionary<string, object>? userSettings = null)
    {
        var functionName = $"{_config.SystemKey}-gate-checker";

        var payload = new
        {
            check_type = "init_config",
            system_key = _config.SystemKey,
            tenant_key = tenantKey,
            environment = _config.Environment,
            db_name = dbName,
            app_user = appUser,
            app_version = appVersion,
            user_settings = userSettings,
        };

        try
        {
            var client = CreateLambdaClient();
            var payloadJson = JsonSerializer.Serialize(payload);

            Console.WriteLine($"  Invoking {functionName} (init_config)...");
            Console.WriteLine($"    Database: {dbName}");
            Console.WriteLine($"    App user: {appUser}");

            var response = await client.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.RequestResponse,
                Payload = payloadJson,
            });

            if (response.FunctionError != null)
            {
                using var errReader = new StreamReader(response.Payload);
                var errBody = await errReader.ReadToEndAsync();

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Lambda error: {response.FunctionError}");
                Console.Error.WriteLine($"    {errBody}");
                Console.ResetColor();
                return false;
            }

            using var reader = new StreamReader(response.Payload);
            var responseJson = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (!doc.RootElement.TryGetProperty("passed", out var passedProp))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Warning: Unexpected Lambda response: {responseJson}");
                Console.ResetColor();
                return false;
            }

            var passed = passedProp.GetBoolean();
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp)
                ? reasonProp.GetString() ?? ""
                : "";

            if (passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Config init succeeded: {reason}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Config init failed: {reason}");
                Console.ResetColor();
            }

            return passed;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Lambda '{functionName}' not found. Deploy foundation first.");
            Console.ResetColor();
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  Config init error: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    private AmazonLambdaClient CreateLambdaClient()
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_config.Region);

        if (!string.IsNullOrEmpty(_config.Profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(_config.Profile, out var credentials))
                return new AmazonLambdaClient(credentials, regionEndpoint);
        }

        return new AmazonLambdaClient(regionEndpoint);
    }
}
