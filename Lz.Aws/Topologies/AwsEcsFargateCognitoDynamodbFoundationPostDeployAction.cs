using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Aws.DynamoDB;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Topologies;

/// <summary>
/// Foundation post-deploy: creates the system-level DynamoDB table.
/// Table name = {SystemKey} (e.g., "bcs").
/// Idempotent — skips if table already exists.
/// <para>
/// Per-client Cognito Managed Login branding (required by
/// ManagedLoginVersion=2) is now created declaratively as a
/// ManagedLoginBranding resource in AwsCognitoComponent —
/// available since Pulumi.Aws 7.x. Earlier 6.x revisions of this
/// post-deploy action created the branding imperatively via the AWS
/// SDK; that fallback is no longer needed.
/// </para>
/// </summary>
public class AwsEcsFargateCognitoDynamodbFoundationPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;

    public AwsEcsFargateCognitoDynamodbFoundationPostDeployAction(SystemConfig config)
    {
        _config = config;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
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
    }
}
