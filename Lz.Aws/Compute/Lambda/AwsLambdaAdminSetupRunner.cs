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
/// Invokes the gate-checker Lambda with check_type=setup_admin to create
/// the InternalAdmin customer and WebApi API credentials in the SmartStore
/// database, then stores all credentials in the tenant secret.
/// Follows the same Lambda invocation pattern as AwsLambdaConfigInitRunner.
/// </summary>
public class AwsLambdaAdminSetupRunner : IAdminSetupRunner
{
    private readonly SystemConfig _config;

    public AwsLambdaAdminSetupRunner(SystemConfig config)
    {
        _config = config;
    }

    public async Task<bool> RunSetupAdminAsync(string tenantKey, string dbName)
    {
        var functionName = $"{_config.SystemKey}-gate-checker";

        var payload = new
        {
            check_type = "setup_admin",
            system_key = _config.SystemKey,
            tenant_key = tenantKey,
            environment = _config.Environment,
            db_name = dbName,
        };

        try
        {
            var client = CreateLambdaClient();
            var payloadJson = JsonSerializer.Serialize(payload);

            Console.WriteLine($"  Invoking {functionName} (setup_admin)...");
            Console.WriteLine($"    Database: {dbName}");

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
                Console.WriteLine($"  Admin setup succeeded: {reason}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Admin setup failed: {reason}");
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
            Console.Error.WriteLine($"  Admin setup error: {ex.Message}");
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
            // Admin setup may take minutes (DB operations via Lambda).
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
