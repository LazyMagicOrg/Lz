using Lz.Aws.DynamoDB;
using Lz.Core.Config;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Shared;

/// <summary>
/// Creates and deletes per-subtenant infrastructure imperatively (outside
/// Pulumi). Per-subtenant resources — the S3 assets bucket and the two
/// DynamoDB tables, {sk}_{tk}_{stk} and its GSI twin {sk}_{tk}_{stk}_gsi —
/// are decoupled from the tenant Pulumi stack so that subtenants can be added
/// or removed without re-running <c>deploytenant</c>. The two tables live and
/// die together: every path that ensures or destroys one does both.
/// <para>
/// Called by three places:
/// <list type="bullet">
///   <item><c>lz deploytenant</c> post-deploy — first-time convenience so the
///     operator doesn't have to remember a separate step.</item>
///   <item><c>lz deploysubtenants</c> — the fast path for adding subtenants
///     to an already-deployed tenant.</item>
///   <item><c>lz destroysubtenant</c> — paired destroy.</item>
/// </list>
/// </para>
/// </summary>
public static class SubtenantProvisioner
{
    /// <summary>
    /// Ensure the S3 bucket and DynamoDB tables for every subtenant listed
    /// on <paramref name="tenant"/> exist. Idempotent — existing resources
    /// have their policy/tags re-applied so console-side drift is corrected.
    /// </summary>
    /// <param name="accountId">AWS account ID — used in the subtenant bucket
    /// policy's CloudFront OAC trust condition. Without it, OAC-signed
    /// CloudFront reads return 403.</param>
    public static async Task EnsureAllAsync(
        SystemConfig system, TenantConfig tenant,
        string profile, string region, string accountId)
    {
        if (tenant.Subtenants == null || tenant.Subtenants.Count == 0) return;

        foreach (var (subtenantKey, _) in tenant.Subtenants)
            await EnsureOneAsync(system, tenant, subtenantKey, profile, region, accountId);
    }

    /// <summary>
    /// Ensure the S3 bucket and both DynamoDB tables for a single subtenant exist.
    /// </summary>
    public static async Task EnsureOneAsync(
        SystemConfig system, TenantConfig tenant, string subtenantKey,
        string profile, string region, string accountId)
    {
        var sk = system.SystemKey;
        var tk = tenant.TenantKey;
        var tags = new Dictionary<string, string>
        {
            { "System", sk },
            { "Tenant", tk },
            { "Subtenant", subtenantKey },
        };

        // Build CORS allowed-origins list from tenantconfig.CDN.Cors. Same
        // semantics as the Pulumi-managed tenant bucket
        // (AwsCloudFrontKvsComponent.cs): AllowLocalhostDev=true
        // injects http(s)://localhost:* and AllowedOrigins entries are
        // passed through verbatim. When neither is set, the list is empty
        // and SubtenantBucketManager skips PutCORSConfiguration so an
        // existing manual config isn't dropped silently.
        var corsCfg = (tenant.CDN ?? new CdnConfig()).Cors ?? new CorsConfig();
        var corsOrigins = new List<string>();
        if (corsCfg.AllowLocalhostDev)
        {
            corsOrigins.Add("http://localhost:*");
            corsOrigins.Add("https://localhost:*");
        }
        if (corsCfg.AllowedOrigins != null)
            corsOrigins.AddRange(corsCfg.AllowedOrigins);

        // S3 assets bucket — {sk}-{tk}-{stk}-assets-{systemSuffix}
        var bucketName = SubtenantBucketManager.BucketName(
            sk, tk, subtenantKey, system.SystemSuffix);
        Console.WriteLine($"  subtenant '{subtenantKey}': ensuring bucket {bucketName}");
        var created = await SubtenantBucketManager.EnsureBucketAsync(
            profile, region, bucketName, accountId,
            new Dictionary<string, string>(tags) { { "Purpose", $"{subtenantKey}-assets" } },
            corsOrigins,
            system.Hygiene?.S3NoncurrentVersionExpirationDays);
        Console.WriteLine(created
            ? $"    {bucketName} — created"
            : $"    {bucketName} — exists (policy re-applied)");

        // DynamoDB tables — {sk}_{tk}_{stk} and its GSI twin. These are the subtenant
        // VAULT/PII tables; system.Durability (when set) gates deletion protection +
        // PITR on both, the same decision for each.
        var durability = TableDurabilityPolicy.ForVaultTable(system.Durability);
        using var ddb = CreateDynamoClient(profile, region);
        await EnsureTablesAsync(
            ddb, sk, tk, subtenantKey,
            new Dictionary<string, string>(tags) { { "Level", "subtenant" } },
            durability, DynamoDbTableCreator.DefaultActivePollDelay);
    }

    /// <summary>
    /// The subtenant's two tables: the LSI table and its GSI twin, which holds the
    /// entities LazyMagic's Gsi table kind puts there.
    /// </summary>
    internal static (string Lsi, string Gsi) TableNames(string sk, string tk, string subtenantKey)
        => ($"{sk}_{tk}_{subtenantKey}", $"{sk}_{tk}_{subtenantKey}_gsi");

    /// <summary>
    /// Ensures both of a subtenant's tables, each in its own shape and under the
    /// same durability decision.
    /// </summary>
    internal static async Task EnsureTablesAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB ddb, string sk, string tk, string subtenantKey,
        Dictionary<string, string> tags, TableDurabilityDecision durability, TimeSpan activePollDelay)
    {
        var (lsi, gsi) = TableNames(sk, tk, subtenantKey);
        foreach (var (tableName, isGsi) in new[] { (lsi, false), (gsi, true) })
        {
            Console.WriteLine($"  subtenant '{subtenantKey}': ensuring table {tableName}");
            var tableCreated = isGsi
                ? await DynamoDbTableCreator.EnsureGsiTableAsync(ddb, tableName, tags, durability, activePollDelay)
                : await DynamoDbTableCreator.EnsureTableAsync(ddb, tableName, tags, durability, activePollDelay);
            Console.WriteLine(tableCreated
                ? $"    {tableName} — created"
                : $"    {tableName} — exists");
            // Surface the durability protections applied — the requested decision IS
            // the applied state (ApplyDurabilityAsync applies exactly it, and any
            // failure throws before 'created' prints). Reported HERE, uniformly for
            // the create and exists paths, rather than inside DynamoDbTableCreator
            // where the create-path deletion-protection set is deliberately skipped by
            // ApplyDurabilityAsync and would go unlogged. Printed only when something
            // is requested, so a no-opt-in system's output is unchanged.
            if (durability.Any)
                Console.WriteLine(
                    $"    {tableName} — durability: deletion protection " +
                    $"{(durability.DeletionProtection ? "ENABLED" : "off")}, " +
                    $"point-in-time recovery {(durability.PointInTimeRecovery ? "ENABLED" : "off")}");
        }
    }

    /// <summary>
    /// Destroy the S3 bucket and both DynamoDB tables for a single subtenant.
    /// When <paramref name="forceEmptyBucket"/> is true the bucket is emptied
    /// before deletion (data loss — callers should confirm with the user).
    /// <para>
    /// If either subtenant table has DynamoDB deletion protection enabled, nothing
    /// is deleted unless <paramref name="forceDeleteProtected"/> is also set — in
    /// which case protection is disabled first, then the table is deleted. Without
    /// the flag, a protected table causes this to throw before anything is
    /// destroyed (the destroy fails loudly rather than silently leaving PII behind,
    /// silently stripping the protection, or leaving half a subtenant).
    /// </para>
    /// </summary>
    public static async Task DeleteOneAsync(
        SystemConfig system, TenantConfig tenant, string subtenantKey,
        string profile, string region, bool forceEmptyBucket,
        bool forceDeleteProtected = false)
    {
        var sk = system.SystemKey;
        var tk = tenant.TenantKey;

        var bucketName = SubtenantBucketManager.BucketName(
            sk, tk, subtenantKey, system.SystemSuffix);
        var (lsi, gsi) = TableNames(sk, tk, subtenantKey);

        using var ddb = CreateDynamoClient(profile, region);
        await DeleteOneAsync(
            ddb, new[] { lsi, gsi }, bucketName,
            () => SubtenantBucketManager.DeleteBucketAsync(profile, region, bucketName, forceEmptyBucket),
            forceDeleteProtected);
    }

    /// <summary>
    /// The teardown itself, over any client and bucket deletion. Every table's
    /// decision is resolved BEFORE any destructive step: a protected-table refusal
    /// must abort the WHOLE destroy — never leave a deleted bucket, or one deleted
    /// table, beside a surviving table (a half-destroyed subtenant). A table that is
    /// already gone is skipped, and never stops the other from being deleted.
    /// </summary>
    internal static async Task DeleteOneAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB ddb, IReadOnlyList<string> tableNames, string bucketName,
        Func<Task> deleteBucket, bool forceDeleteProtected)
    {
        var teardowns = new List<(string Table, bool Exists, TableTeardownAction Action)>();
        foreach (var tableName in tableNames)
        {
            var (exists, isProtected) = await DescribeTableProtectionAsync(ddb, tableName);
            teardowns.Add((tableName, exists, TableDurabilityPolicy.DecideTeardown(isProtected, forceDeleteProtected)));
        }
        var refused = teardowns
            .Where(t => t.Action == TableTeardownAction.Refuse)
            .Select(t => $"'{t.Table}'")
            .ToList();
        if (refused.Count > 0)
            throw new InvalidOperationException(
                $"DynamoDB table {string.Join(" and ", refused)} has deletion protection enabled; refusing " +
                "to delete (nothing was destroyed — the S3 bucket and both tables are untouched). These " +
                "are the subtenant vault/PII tables; their rows are destroyed by deletion. Re-run " +
                "with --force-delete-protected to disable protection and delete them (DATA LOSS; " +
                "ensure the PITR/backup window is an acceptable recovery point first).");

        // Past the gate — the bucket and every table that exists WILL be destroyed.
        Console.WriteLine($"  deleting bucket {bucketName}");
        await deleteBucket();

        foreach (var (tableName, exists, action) in teardowns)
        {
            Console.WriteLine($"  deleting table {tableName}");
            if (!exists)
                continue; // Table already gone.
            await ExecuteTableTeardownAsync(ddb, tableName, action);
        }
    }

    private static Amazon.DynamoDBv2.AmazonDynamoDBClient CreateDynamoClient(
        string profile, string region)
    {
        var credentials = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        return credentials != null
            ? new Amazon.DynamoDBv2.AmazonDynamoDBClient(credentials, endpoint)
            : new Amazon.DynamoDBv2.AmazonDynamoDBClient(endpoint);
    }

    /// <summary>
    /// (exists, isProtected) for the live table. A missing table is (false,
    /// false) — nothing to delete and nothing to refuse.
    /// </summary>
    private static async Task<(bool exists, bool isProtected)> DescribeTableProtectionAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB client, string tableName)
    {
        try
        {
            var desc = await client.DescribeTableAsync(tableName);
            return (true, desc.Table.DeletionProtectionEnabled ?? false);
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
        {
            return (false, false);
        }
    }

    /// <summary>
    /// Executes the resolved teardown for an existing table. <see
    /// cref="TableTeardownAction.Refuse"/> is impossible here — it is gated in
    /// DeleteOneAsync before any destructive step.
    /// </summary>
    private static async Task ExecuteTableTeardownAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB client, string tableName, TableTeardownAction action)
    {
        if (action == TableTeardownAction.DisableProtectionThenDelete)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"    {tableName} is deletion-protected — disabling protection " +
                "(--force-delete-protected) before delete.");
            Console.ResetColor();
            await client.UpdateTableAsync(new Amazon.DynamoDBv2.Model.UpdateTableRequest
            {
                TableName = tableName,
                DeletionProtectionEnabled = false,
            });
            // UpdateTable returns before the change applies; DeleteTable on a
            // still-UPDATING or still-protected table fails. Wait for it to clear.
            await WaitForDeletionProtectionClearedAsync(client, tableName);
        }

        try
        {
            await client.DeleteTableAsync(tableName);
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
        {
            // Already gone — no-op
        }
    }

    /// <summary>
    /// Polls until the table is ACTIVE with deletion protection cleared, so a
    /// following DeleteTable does not race a still-protected or still-UPDATING
    /// view. Two-minute ceiling — disabling protection is a fast metadata update.
    /// </summary>
    private static async Task WaitForDeletionProtectionClearedAsync(
        Amazon.DynamoDBv2.IAmazonDynamoDB client, string tableName)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var desc = await client.DescribeTableAsync(tableName);
            if (desc.Table.TableStatus == Amazon.DynamoDBv2.TableStatus.ACTIVE &&
                desc.Table.DeletionProtectionEnabled != true)
                return;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Deletion protection on '{tableName}' did not clear within 2 minutes.");

            await Task.Delay(2000);
        }
    }
}
