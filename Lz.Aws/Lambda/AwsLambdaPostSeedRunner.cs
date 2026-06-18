using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Core.Interfaces;

namespace Lz.Aws.Lambda;

/// <summary>
/// Invokes the gate-checker Lambda with check_type=post_seed_config to re-write
/// Settings.txt and usersettings.json after the seed process. The seed may have
/// overwritten these files with source-environment values.
/// </summary>
public class AwsLambdaPostSeedRunner : IPostSeedRunner
{
    private readonly SystemConfig _config;

    public AwsLambdaPostSeedRunner(SystemConfig config)
    {
        _config = config;
    }

    public async Task<bool> RunPostSeedAsync(
        string tenantKey, string dbName, string appUser,
        string appVersion = "6.3.0.0", Dictionary<string, object>? userSettings = null)
    {
        var functionName = $"{_config.SystemKey}-gate-checker";

        var payload = new
        {
            check_type = "post_seed_config",
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

            Console.WriteLine($"  Invoking {functionName} (post_seed_config)...");

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
                Console.WriteLine($"  Post-seed config succeeded: {reason}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Post-seed config failed: {reason}");
                Console.ResetColor();
            }

            return passed;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Lambda '{functionName}' not found. Run `lz deploysystem` first.");
            Console.ResetColor();
            return false;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  Post-seed config error: {ex.Message}");
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
            // Post-seed config may take minutes (EFS writes via Lambda).
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
}
