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
/// Pulumi). Per-subtenant resources — the S3 assets bucket and the DynamoDB
/// table — are decoupled from the tenant Pulumi stack so that subtenants
/// can be added or removed without re-running <c>deploytenant</c>.
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
    /// Ensure the S3 bucket and DynamoDB table for every subtenant listed
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
    /// Ensure the S3 bucket and DynamoDB table for a single subtenant exist.
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

        // DynamoDB table — {sk}_{tk}_{stk}. This is the subtenant VAULT/PII table;
        // system.Durability (when set) gates deletion protection + PITR on it.
        var tableName = $"{sk}_{tk}_{subtenantKey}";
        Console.WriteLine($"  subtenant '{subtenantKey}': ensuring table {tableName}");
        var durability = TableDurabilityPolicy.ForVaultTable(system.Durability);
        var tableCreated = await DynamoDbTableCreator.EnsureTableAsync(
            profile, region, tableName,
            new Dictionary<string, string>(tags) { { "Level", "subtenant" } },
            durability);
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

    /// <summary>
    /// Destroy the S3 bucket and DynamoDB table for a single subtenant.
    /// When <paramref name="forceEmptyBucket"/> is true the bucket is emptied
    /// before deletion (data loss — callers should confirm with the user).
    /// <para>
    /// If the subtenant table has DynamoDB deletion protection enabled, it is
    /// NOT deleted unless <paramref name="forceDeleteProtected"/> is also set —
    /// in which case protection is disabled first, then the table is deleted.
    /// Without the flag, a protected table causes this to throw (the destroy
    /// fails loudly rather than silently leaving PII behind or silently
    /// stripping the protection).
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
        var tableName = $"{sk}_{tk}_{subtenantKey}";

        using var ddb = CreateDynamoClient(profile, region);

        // Resolve the table teardown decision BEFORE any destructive step. A
        // protected-table refusal must abort the WHOLE destroy — never leave a
        // deleted bucket beside a surviving table (a half-destroyed subtenant).
        var (tableExists, isProtected) = await DescribeTableProtectionAsync(ddb, tableName);
        var action = TableDurabilityPolicy.DecideTeardown(isProtected, forceDeleteProtected);
        if (action == TableTeardownAction.Refuse)
            throw new InvalidOperationException(
                $"DynamoDB table '{tableName}' has deletion protection enabled; refusing " +
                "to delete it (nothing was destroyed — the S3 bucket is untouched). This is " +
                "the subtenant vault/PII table; its rows are destroyed by deletion. Re-run " +
                "with --force-delete-protected to disable protection and delete it (DATA LOSS; " +
                "ensure the PITR/backup window is an acceptable recovery point first).");

        // Past the gate — both the bucket and the table WILL be destroyed.
        Console.WriteLine($"  deleting bucket {bucketName}");
        await SubtenantBucketManager.DeleteBucketAsync(profile, region, bucketName, forceEmptyBucket);

        Console.WriteLine($"  deleting table {tableName}");
        if (!tableExists)
            return; // Table already gone; bucket handled above.
        await ExecuteTableTeardownAsync(ddb, tableName, action);
    }

    private static Amazon.DynamoDBv2.AmazonDynamoDBClient CreateDynamoClient(
        string profile, string region)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException(
                $"Cannot resolve AWS credentials for profile '{profile}'.");

        return new Amazon.DynamoDBv2.AmazonDynamoDBClient(
            credentials, Amazon.RegionEndpoint.GetBySystemName(region));
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
    /// <see cref="DeleteOneAsync"/> before any destructive step.
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
