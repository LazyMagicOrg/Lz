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
        Dictionary<string, string>? tags = null,
        TableDurabilityDecision? durability = null)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"Cannot resolve credentials for profile '{profile}'");

        using var client = new AmazonDynamoDBClient(
            credentials,
            Amazon.RegionEndpoint.GetBySystemName(region));

        return await EnsureTableAsync(client, tableName, tags, durability);
    }

    /// <summary>
    /// Ensures a DynamoDB table exists using an existing client.
    /// <paramref name="durability"/> gates OPTIONAL durability protections
    /// (deletion protection + PITR). Null or <see cref="TableDurabilityDecision.None"/>
    /// applies nothing, so the emitted request is byte-identical to a
    /// pre-durability deploy. The protections are re-asserted idempotently when
    /// the table already exists (never DISABLED here — disabling is reserved for
    /// the deliberate --force-delete-protected teardown path).
    /// </summary>
    public static async Task<bool> EnsureTableAsync(
        IAmazonDynamoDB client, string tableName,
        Dictionary<string, string>? tags = null,
        TableDurabilityDecision? durability = null)
    {
        var decision = durability ?? TableDurabilityDecision.None;

        // Check if table already exists
        try
        {
            var existing = await client.DescribeTableAsync(tableName);
            // Idempotent ensure: re-apply requested protections to an existing
            // table so opting a deployed system in (or re-running deploy) actually
            // takes effect. Guarded on the flags, so None is a pure no-op here.
            await ApplyDurabilityAsync(
                client, tableName, decision,
                existing.Table.DeletionProtectionEnabled ?? false);
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
        // Deletion protection is a create-time field (Nullable<bool>): set it ONLY
        // when requested, so an unset (null) leaves the request byte-identical to
        // the pre-durability baseline. PITR is applied separately below — it is
        // not a CreateTable field and requires the table to be ACTIVE first.
        if (decision.DeletionProtection)
            createRequest.DeletionProtectionEnabled = true;

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

        // Apply PITR now that the table is ACTIVE. Deletion protection was already
        // set in the CreateTable request above, so pass currentDeletionProtection:
        // decision.DeletionProtection to skip a redundant UpdateTable.
        await ApplyDurabilityAsync(client, tableName, decision, decision.DeletionProtection);

        return true;
    }

    /// <summary>
    /// Applies the requested durability protections to an ACTIVE table. Gated on
    /// the decision flags, so <see cref="TableDurabilityDecision.None"/> is a pure
    /// no-op (the byte-identical baseline). Deletion protection is only ever
    /// ENABLED here — never disabled; disabling is reserved for the deliberate
    /// --force-delete-protected teardown path, so a manual protection is never
    /// silently stripped by a routine deploy.
    /// </summary>
    private static async Task ApplyDurabilityAsync(
        IAmazonDynamoDB client, string tableName,
        TableDurabilityDecision decision, bool currentDeletionProtection)
    {
        if (!decision.Any) return;

        if (decision.DeletionProtection && !currentDeletionProtection)
        {
            await client.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = tableName,
                DeletionProtectionEnabled = true,
            });
        }

        if (decision.PointInTimeRecovery)
            await EnablePitrWithRetryAsync(client, tableName);
    }

    /// <summary>
    /// Enables point-in-time recovery, tolerating the transient
    /// <see cref="ContinuousBackupsUnavailableException"/> ("Backups are being
    /// enabled for the table … Please retry later") that DynamoDB returns for the
    /// first several seconds after a table becomes ACTIVE — a fresh table is not
    /// immediately ready for the continuous-backups subsystem. Retries with backoff
    /// until it takes or a ~3-minute ceiling is hit (past which the exception
    /// propagates and fails the deploy loudly — a genuinely unavailable PITR is a
    /// real problem, not something to swallow). Enabling is idempotent server-side,
    /// so a re-ensure on an already-PITR table just returns OK.
    /// </summary>
    private static async Task EnablePitrWithRetryAsync(IAmazonDynamoDB client, string tableName)
    {
        var request = new UpdateContinuousBackupsRequest
        {
            TableName = tableName,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification
            {
                PointInTimeRecoveryEnabled = true,
            },
        };

        var deadline = DateTime.UtcNow.AddMinutes(3);
        var delayMs = 3000;
        var waited = false;
        while (true)
        {
            try
            {
                await client.UpdateContinuousBackupsAsync(request);
                if (waited) Console.WriteLine(" enabled");
                return;
            }
            catch (ContinuousBackupsUnavailableException) when (DateTime.UtcNow < deadline)
            {
                if (!waited) { Console.Write($"    Waiting for PITR on {tableName}..."); waited = true; }
                Console.Write(".");
                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs + 2000, 10000);
            }
        }
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
        Dictionary<string, string>? tags = null,
        TableDurabilityDecision? durability = null)
    {
        var decision = durability ?? TableDurabilityDecision.None;

        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException($"Cannot resolve credentials for profile '{profile}'");

        using var client = new AmazonDynamoDBClient(
            credentials, Amazon.RegionEndpoint.GetBySystemName(region));

        // Already exists?
        try
        {
            var existing = await client.DescribeTableAsync(tableName);
            // Idempotent ensure, mirroring EnsureTableAsync: re-apply requested
            // protections to an ALREADY-EXISTING table. Without this the create path
            // below is dead code for every session table that predates the opt-in —
            // the method would print "exists", return false, and protect nothing,
            // which is a change that deploys green and does nothing at all.
            await ApplyDurabilityAsync(
                client, tableName, decision,
                existing.Table.DeletionProtectionEnabled ?? false);
            return false;
        }
        catch (ResourceNotFoundException) { /* create below */ }

        var tableTags = new List<Tag> { new() { Key = "ManagedBy", Value = "lz-pulumi" } };
        if (tags != null)
            foreach (var (key, value) in tags)
                tableTags.Add(new Tag { Key = key, Value = value });

        var createRequest = new CreateTableRequest
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
        };
        // Same shape as EnsureTableAsync: set ONLY when requested, so an unset
        // decision leaves the request byte-identical to the pre-durability baseline.
        if (decision.DeletionProtection)
            createRequest.DeletionProtectionEnabled = true;

        await client.CreateTableAsync(createRequest);

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

        // Anything that is not a CreateTable field (today: PITR) is applied here, once
        // the table is ACTIVE. Deletion protection was already set on the create request,
        // so it is passed as the current state and ApplyDurabilityAsync skips it.
        await ApplyDurabilityAsync(client, tableName, decision, decision.DeletionProtection);

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
