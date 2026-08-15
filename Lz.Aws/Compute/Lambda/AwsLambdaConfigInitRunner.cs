using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.Lambda;

/// <summary>
/// Invokes the gate-checker Lambda with check_type=init_config to create
/// the tenant database, app user, and write EFS config files.
/// Follows the same Lambda invocation pattern as AwsFargateAlbTransitionChecker.
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
        string appVersion = "6.3.0.0", Dictionary<string, object>? userSettings = null,
        string? platformDatabaseName = null,
        string? mediaBucket = null, string? mediaStorage = null)
    {
        var functionName = $"{_config.SystemKey}-gate-checker";

        var platformAppUser = platformDatabaseName != null
            ? $"{_config.SystemKey}_{tenantKey}_platform_app"
            : null;

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
            platform_db_name = platformDatabaseName,
            platform_app_user = platformAppUser,
            media_bucket = mediaBucket,
            media_storage = mediaStorage ?? "filesystem",
        };

        try
        {
            var client = CreateLambdaClient();
            var payloadJson = JsonSerializer.Serialize(payload);

            Console.WriteLine($"  Invoking {functionName} (init_config)...");
            Console.WriteLine($"    Database: {dbName}");
            Console.WriteLine($"    App user: {appUser}");
            if (platformDatabaseName != null)
                Console.WriteLine($"    Platform DB: {platformDatabaseName}");

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
            Console.WriteLine($"  Warning: Lambda '{functionName}' not found. Run `lz deploysystem` first.");
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
        var lambdaConfig = new AmazonLambdaConfig
        {
            RegionEndpoint = regionEndpoint,
            // init_config Lambda may take several minutes (DB creation, EFS extraction).
            // Default SDK timeout is ~100s which is too short.
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
