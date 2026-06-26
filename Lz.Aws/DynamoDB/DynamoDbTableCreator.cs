using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Lz.Aws.DynamoDB;

/// <summary>
/// Creates DynamoDB tables with the LazyMagic envelope schema:
///   PK (HASH) + SK (RANGE)            -- attribute names MUST be "PK"/"SK"
///   5 Local Secondary Indexes: PK-SK1-Index through PK-SK5-Index (sort keys SK1..SK5)
///   TTL enabled on "TTL" attribute
///   PAY_PER_REQUEST billing
///
/// The key/index ATTRIBUTE NAMES must exactly match what
/// LazyMagic.Service.DynamoDBRepo.DYDBRepository reads and writes:
/// it stores the partition key as a literal "PK" attribute and the sort key as
/// "SK" (see AssignEntityAttributes / QueryEquals), and queries the LSIs by
/// "SK1".."SK5". A prior version created the keys as "id"/"sk"/"sk1".."sk5",
/// which the repo cannot read or write (every /AppApi call 500'd with a
/// swallowed DynamoDB ValidationException).
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

        // Attribute definitions — PK, SK, and 5 LSI sort keys. Names MUST match
        // DYDBRepository (literal "PK"/"SK"/"SK1".."SK5"), not "id"/"sk".
        var attributeDefinitions = new List<AttributeDefinition>
        {
            new() { AttributeName = "PK", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK1", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK2", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK3", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK4", AttributeType = ScalarAttributeType.S },
            new() { AttributeName = "SK5", AttributeType = ScalarAttributeType.S },
        };

        // Key schema — composite key (PK HASH + SK RANGE)
        var keySchema = new List<KeySchemaElement>
        {
            new() { AttributeName = "PK", KeyType = KeyType.HASH },
            new() { AttributeName = "SK", KeyType = KeyType.RANGE },
        };

        // 5 Local Secondary Indexes (PK + SKi), names PK-SK1-Index .. PK-SK5-Index
        var localSecondaryIndexes = new List<LocalSecondaryIndex>();
        for (int i = 1; i <= 5; i++)
        {
            localSecondaryIndexes.Add(new LocalSecondaryIndex
            {
                IndexName = $"PK-SK{i}-Index",
                KeySchema = new List<KeySchemaElement>
                {
                    new() { AttributeName = "PK", KeyType = KeyType.HASH },
                    new() { AttributeName = $"SK{i}", KeyType = KeyType.RANGE },
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

        // Wait for table to become ACTIVE. 5-minute ceiling — pay-per-request
        // tables typically activate in <30s; anything longer means something is
        // wrong (throttling, region issue, AWS incident) and failing loudly is
        // better than hanging the deploy.
        Console.Write($"    Waiting for {tableName}...");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.WriteLine();
                throw new TimeoutException(
                    $"DynamoDB table '{tableName}' did not become ACTIVE within 5 minutes. " +
                    "Check the AWS console for the table status and retry.");
            }
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

    /// <summary>
    /// Ensures a DEDICATED BFF session table exists: partition key "id" (HASH),
    /// sort key "sk" (RANGE), TTL on "TTL", NO secondary indexes. Kept separate from
    /// the PK/SK app tables created by EnsureTableAsync — DynamoBffSessionStore reads/
    /// writes id/sk and performs only point operations (Get/Put/Update/Delete by key),
    /// so it needs neither the PK/SK envelope nor the SK1..SK5 LSIs. Using a dedicated
    /// table (e.g. {sk}_{tk}_bff) also stops the session store from colliding with the
    /// app's data table {sk}_{tk}. Returns true if created, false if it already existed.
    /// </summary>
    public static async Task<bool> EnsureSessionTableAsync(
        string profile, string region, string tableName,
        Dictionary<string, string>? tags = null)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"Cannot resolve credentials for profile '{profile}'");

        using var client = new AmazonDynamoDBClient(
            credentials, Amazon.RegionEndpoint.GetBySystemName(region));

        // Already exists?
        try
        {
            await client.DescribeTableAsync(tableName);
            return false;
        }
        catch (ResourceNotFoundException) { /* create below */ }

        var tableTags = new List<Tag> { new() { Key = "ManagedBy", Value = "lz-pulumi" } };
        if (tags != null)
            foreach (var (key, value) in tags)
                tableTags.Add(new Tag { Key = key, Value = value });

        await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new() { AttributeName = "id", AttributeType = ScalarAttributeType.S },
                new() { AttributeName = "sk", AttributeType = ScalarAttributeType.S },
            },
            KeySchema = new List<KeySchemaElement>
            {
                new() { AttributeName = "id", KeyType = KeyType.HASH },
                new() { AttributeName = "sk", KeyType = KeyType.RANGE },
            },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = tableTags,
        });

        // Wait for ACTIVE (same 5-minute ceiling as EnsureTableAsync).
        Console.Write($"    Waiting for {tableName}...");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.WriteLine();
                throw new TimeoutException(
                    $"DynamoDB table '{tableName}' did not become ACTIVE within 5 minutes.");
            }
            await Task.Delay(3000);
            var desc = await client.DescribeTableAsync(tableName);
            if (desc.Table.TableStatus == TableStatus.ACTIVE) { Console.WriteLine(" ACTIVE"); break; }
            Console.Write(".");
        }

        // Enable TTL on "TTL".
        try
        {
            await client.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
            {
                TableName = tableName,
                TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "TTL" },
            });
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    TTL warning for {tableName}: {ex.Message}");
            Console.ResetColor();
        }

        return true;
    }
}
