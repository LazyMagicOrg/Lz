using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Aws.DynamoDB;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Foundation post-deploy: creates the system-level DynamoDB table.
/// Table name = {SystemKey} (e.g., "bcs").
/// Idempotent — skips if table already exists.
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
