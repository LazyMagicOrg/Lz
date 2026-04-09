using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Lz.Aws.DynamoDB;

/// <summary>
/// Creates DynamoDB tables with the LazyMagic envelope schema:
///   PK (id, HASH) + SK (sk, RANGE)
///   5 Local Secondary Indexes: PK-SK1-Index through PK-SK5-Index
///   TTL enabled on "TTL" attribute
///   PAY_PER_REQUEST billing
///
/// Tables are persistent — not deleted on destroy.
/// Creation is idempotent — skips if table already exists.
/// </summary>
public static class DynamoDbTableCreator
{
    /// <summary>
    /// Ensures a DynamoDB table exists with the standard LazyMagic schema.
    /// Returns true if created, false if already existed.
    /// </summary>
    public static async Task<bool> EnsureTableAsync(
        string profile, string region, string tableName,
        Dictionary<string, string>? tags = null)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"Cannot resolve credentials for profile '{profile}'");

        using var client = new AmazonDynamoDBClient(
            credentials,
            Amazon.RegionEndpoint.GetBySystemName(region));

        return await EnsureTableAsync(client, tableName, tags);
    }

    /// <summary>
    /// Ensures a DynamoDB table exists using an existing client.
    /// </summary>
    public static async Task<bool> EnsureTableAsync(
        IAmazonDynamoDB client, string tableName,
        Dictionary<string, string>? tags = null)
    {
        // Check if table already exists
        try
        {
            await client.DescribeTableAsync(tableName);
            return false; // Already exists
        }
        catch (ResourceNotFoundException)
        {
            // Table doesn't exist — create it
        }

        // Attribute definitions — PK, SK, and 5 LSI sort keys
        var attributeDefinitions = new List<AttributeDefinition>
        {
            new() { AttributeName = "id", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk1", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk2", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk3", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk4", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "sk5", AttributeType = ScalarAttributeType.S },
        };

        // Key schema — composite key (PK + SK)
        var keySchema = new List<KeySchemaElement>
        {
            new() { AttributeName = "id", KeyType = KeyType.HASH },
            new() { AttributeName = "sk", KeyType = KeyType.RANGE },
        };

        // 5 Local Secondary Indexes
        var localSecondaryIndexes = new List<LocalSecondaryIndex>();
        for (int i = 1; i <= 5; i++)
        {
            localSecondaryIndexes.Add(new LocalSecondaryIndex
            {
                IndexName = $"PK-SK{i}-Index",
                KeySchema = new List<KeySchemaElement>
                {
                    new() { AttributeName = "id", KeyType = KeyType.HASH },
                    new() { AttributeName = $"sk{i}", KeyType = KeyType.RANGE },
                },
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
            });
        }

        // Tags
        var tableTags = new List<Tag>
        {
            new() { Key = "ManagedBy", Value = "lz-pulumi" },
        };
        if (tags != null)
        {
            foreach (var (key, value) in tags)
                tableTags.Add(new Tag { Key = key, Value = value });
        }

        // Create table
        var createRequest = new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = attributeDefinitions,
            KeySchema = keySchema,
            LocalSecondaryIndexes = localSecondaryIndexes,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = tableTags,
        };

        await client.CreateTableAsync(createRequest);

        // Wait for table to become ACTIVE
        Console.Write($"    Waiting for {tableName}...");
        while (true)
        {
            await Task.Delay(3000);
            var desc = await client.DescribeTableAsync(tableName);
            if (desc.Table.TableStatus == TableStatus.ACTIVE)
            {
                Console.WriteLine(" ACTIVE");
                break;
            }
            Console.Write(".");
        }

        // Enable TTL
        try
        {
            await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = tableName,
                TimeToLiveSpecification = new TimeToLiveSpecification
                {
                    Enabled = true,
                    AttributeName = "TTL",
                },
            });
        }
        catch (Exception ex)
        {
            // TTL update can fail if already enabled — not critical
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    TTL warning for {tableName}: {ex.Message}");
            Console.ResetColor();
        }

        return true;
    }
}
