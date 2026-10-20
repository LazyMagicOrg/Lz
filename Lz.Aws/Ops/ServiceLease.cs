using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Lz.Aws.Ops;

/// <summary>One claim on a service: who holds it, why, since and until when, and the token only its holder knows.</summary>
public sealed record ServiceLeaseRecord(string Holder, string Purpose, DateTimeOffset ClaimedAt, DateTimeOffset ExpiresAt, string Token);

/// <summary>The answer to a claim: the lease was taken (with its token), or another holds it.</summary>
public abstract record ServiceLeaseClaim
{
    public sealed record Claimed(ServiceLeaseRecord Lease) : ServiceLeaseClaim;
    public sealed record Held(ServiceLeaseRecord By) : ServiceLeaseClaim;
}

/// <summary>A deploy refused because another writer holds a service's lease. The refusal has been printed; the CLI exits 1
/// without a stack trace, since nothing went wrong but the timing.</summary>
public sealed class ServiceLeaseHeldException(string message) : InvalidOperationException(message);

/// <summary>The conditional reads and writes a lease needs; one implementation per store, and a fake for the tests.</summary>
public interface IServiceLeaseStore
{
    /// <summary>Writes the record only if no lease is at the key, or the one there expired at or before <paramref name="now"/>.</summary>
    Task<bool> PutIfFreeAsync(string key, ServiceLeaseRecord record, DateTimeOffset now);

    Task<ServiceLeaseRecord?> GetAsync(string key);

    /// <summary>
    /// Releases the lease only if it still carries <paramref name="token"/>, by EXPIRING it in place (ExpiresAt set to
    /// the epoch) rather than deleting it: an expired lease is free to every claimer already, and the deployer's roles
    /// then hold no delete on anything, as DeployBundle's lease releases by a conditional overwrite. True again for a
    /// release repeated by the same holder.
    /// </summary>
    Task<bool> ReleaseIfTokenAsync(string key, string token);
}

/// <summary>
/// ONE WRITER AT A TIME ON A SERVICE (DecoupledCd.md V-22, §15.7 V-J), until P3 J moves the image into Pulumi's state.
/// The deployer, <c>lz updatecontainer</c> and <c>lz deploytenant</c> each roll a service's image by their own path; two
/// at once can leave it on either image, or roll it back to an older one. Each claims this lease before the span that
/// moves the image and releases it after: the deployer waits for a held lease, the CLIs refuse naming its holder.
///
/// <para>IN THE ENVIRONMENT'S SYSTEM TABLE (owner, 2026-10-20): <c>{SystemKey}</c>, which every environment has, with one
/// item per service at <c>PK = lease#ecs-service#{cluster}#{service}</c>, <c>SK = lease</c>, so a role can be granted
/// these keys and no others (<c>dynamodb:LeadingKeys</c>). A claim is a conditional put (no lease there, or the one
/// there has expired); a release is a conditional delete on the claim's token, so only its holder can release it.</para>
///
/// <para>A LEASE EXPIRES after <see cref="Duration"/>, longer than any writer's span (a rollout wait is ten minutes), so a
/// live writer is never taken over and a crashed one blocks the service for at most that long. A writer that outlives
/// its lease finds, at its release, that another took it (<see cref="ReleaseAsync"/> returns false), and says so: the
/// same honest limit DeployBundle's lease has.</para>
/// </summary>
public static class ServiceLease
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
    public const string SortKey = "lease";
    public const string KeyPrefix = "lease#ecs-service#";

    public static string KeyFor(string cluster, string service)
    {
        if (string.IsNullOrWhiteSpace(cluster)) throw new ArgumentException("a lease names its cluster", nameof(cluster));
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("a lease names its service", nameof(service));
        return $"{KeyPrefix}{cluster}#{service}";
    }

    public static async Task<ServiceLeaseClaim> ClaimAsync(
        IServiceLeaseStore store, string cluster, string service, string holder, string purpose, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(holder)) throw new ArgumentException("a claim names its holder", nameof(holder));
        var key = KeyFor(cluster, service);
        var record = new ServiceLeaseRecord(holder, purpose, now, now + Duration, Guid.NewGuid().ToString("N"));

        // Two tries: a lease released between our refused put and our read is free, and the second put takes it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await store.PutIfFreeAsync(key, record, now)) return new ServiceLeaseClaim.Claimed(record);
            // A lease read back expired was released (or ran out) after our put was refused: free, so put again.
            if (await store.GetAsync(key) is { } held && held.ExpiresAt > now) return new ServiceLeaseClaim.Held(held);
        }
        throw new InvalidOperationException($"the lease on {cluster}/{service} could be neither claimed nor read; it is changing under us");
    }

    /// <summary>
    /// A claim that a holder's OWN lease satisfies: the lease it already holds comes back as claimed, token and expiry
    /// unchanged. ONLY FOR A HOLDER NAME NO OTHER WRITER CAN BEAR, a deployer execution's (<c>deployer:{execution}</c>):
    /// Step Functions retries a state, and a retried Prepare must find its own claim, not wait it out. The CLIs' holder
    /// names a user and a machine, which two runs can share, so they never claim this way.
    /// </summary>
    public static async Task<ServiceLeaseClaim> ClaimReentrantAsync(
        IServiceLeaseStore store, string cluster, string service, string holder, string purpose, DateTimeOffset now)
    {
        var claim = await ClaimAsync(store, cluster, service, holder, purpose, now);
        return claim is ServiceLeaseClaim.Held held && held.By.Holder == holder && held.By.ExpiresAt > now
            ? new ServiceLeaseClaim.Claimed(held.By)
            : claim;
    }

    /// <summary>
    /// Releases the lease if <paramref name="holder"/> holds it, whatever its token: for a step that knows who it is but
    /// not the claim's token (the deployer's Record and RecordFailure, after a Prepare whose result may never have reached
    /// the execution's state). Null when there was no lease; false when another holds it.
    /// </summary>
    public static async Task<bool?> ReleaseHeldByAsync(IServiceLeaseStore store, string cluster, string service, string holder)
    {
        var key = KeyFor(cluster, service);
        if (await store.GetAsync(key) is not { } lease) return null;
        if (lease.ExpiresAt <= DateTimeOffset.UnixEpoch) return null;
        return lease.Holder == holder && await store.ReleaseIfTokenAsync(key, lease.Token);
    }

    /// <summary>
    /// Claims every service's lease, ALL OR NONE: <c>deploytenant</c> moves every service of a tenant in one Pulumi update,
    /// so it needs them all. On the first held one, those already claimed are released and the holder is returned.
    /// </summary>
    public static async Task<(IReadOnlyList<(string Service, ServiceLeaseRecord Lease)> Claimed, (string Service, ServiceLeaseRecord By)? Held)>
        ClaimAllAsync(IServiceLeaseStore store, string cluster, IEnumerable<string> services, string holder, string purpose, DateTimeOffset now)
    {
        var claimed = new List<(string, ServiceLeaseRecord)>();
        foreach (var service in services)
        {
            switch (await ClaimAsync(store, cluster, service, holder, purpose, now))
            {
                case ServiceLeaseClaim.Claimed c:
                    claimed.Add((service, c.Lease));
                    break;
                case ServiceLeaseClaim.Held h:
                    foreach (var (s, l) in claimed) await ReleaseAsync(store, cluster, s, l);
                    return (Array.Empty<(string, ServiceLeaseRecord)>(), (service, h.By));
            }
        }
        return (claimed, null);
    }

    /// <summary>Releases the lease if <paramref name="lease"/>'s token still holds it; false if another took it since.</summary>
    public static Task<bool> ReleaseAsync(IServiceLeaseStore store, string cluster, string service, ServiceLeaseRecord lease)
        => store.ReleaseIfTokenAsync(KeyFor(cluster, service), lease.Token);

    /// <summary>The line a refusal prints: who holds the service, why, and until when.</summary>
    public static string Describe(ServiceLeaseRecord lease)
        => $"{lease.Holder} holds it for {lease.Purpose}, claimed {lease.ClaimedAt:yyyy-MM-dd HH:mm:ss}Z, expiring {lease.ExpiresAt:HH:mm:ss}Z";
}

/// <summary>The lease in the environment's DynamoDB system table. BCL and the DynamoDB SDK only, since the deployer's
/// functions compile this file too; the CLI's opener, which needs lz's credentials, is in ServiceLeaseOpen.cs.</summary>
public sealed partial class DynamoDbServiceLeaseStore : IServiceLeaseStore
{
    private readonly IAmazonDynamoDB _db;
    private readonly string _table;

    public DynamoDbServiceLeaseStore(IAmazonDynamoDB db, string table)
    {
        _db = db;
        _table = table;
    }

    public async Task<bool> PutIfFreeAsync(string key, ServiceLeaseRecord record, DateTimeOffset now)
    {
        try
        {
            await _db.PutItemAsync(new PutItemRequest
            {
                TableName = _table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new() { S = key },
                    ["SK"] = new() { S = ServiceLease.SortKey },
                    ["Holder"] = new() { S = record.Holder },
                    ["Purpose"] = new() { S = record.Purpose },
                    ["ClaimedAt"] = new() { N = record.ClaimedAt.ToUnixTimeSeconds().ToString() },
                    ["ExpiresAt"] = new() { N = record.ExpiresAt.ToUnixTimeSeconds().ToString() },
                    ["Token"] = new() { S = record.Token },
                },
                ConditionExpression = "attribute_not_exists(PK) OR ExpiresAt <= :now",
                ExpressionAttributeValues = new() { [":now"] = new() { N = now.ToUnixTimeSeconds().ToString() } },
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<ServiceLeaseRecord?> GetAsync(string key)
    {
        var r = await _db.GetItemAsync(new GetItemRequest
        {
            TableName = _table,
            ConsistentRead = true,
            Key = new() { ["PK"] = new() { S = key }, ["SK"] = new() { S = ServiceLease.SortKey } },
        });
        if (r.Item is not { Count: > 0 } i) return null;
        return new ServiceLeaseRecord(
            i["Holder"].S, i.TryGetValue("Purpose", out var p) ? p.S : "",
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(i["ClaimedAt"].N)),
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(i["ExpiresAt"].N)), i["Token"].S);
    }

    public async Task<bool> ReleaseIfTokenAsync(string key, string token)
    {
        try
        {
            await _db.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _table,
                Key = new() { ["PK"] = new() { S = key }, ["SK"] = new() { S = ServiceLease.SortKey } },
                UpdateExpression = "SET ExpiresAt = :released",
                ConditionExpression = "#t = :token",
                ExpressionAttributeNames = new() { ["#t"] = "Token" },
                ExpressionAttributeValues = new()
                {
                    [":token"] = new() { S = token },
                    [":released"] = new() { N = "0" },
                },
            });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }
}
