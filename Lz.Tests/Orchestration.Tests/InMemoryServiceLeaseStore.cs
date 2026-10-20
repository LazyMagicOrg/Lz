using Lz.Aws.Ops;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// The per-service lease's store in memory, with the DynamoDB store's conditional semantics: a put succeeds only on a free
/// or expired key, a release only on the holder's token. <see cref="ServiceLeaseDynamoDbTests"/> holds the real one to them.
/// </summary>
public sealed class InMemoryServiceLeaseStore : IServiceLeaseStore
{
    public readonly Dictionary<string, ServiceLeaseRecord> Items = new();
    public Action? BeforeGet;
    public Exception? FailWith;

    public Task<bool> PutIfFreeAsync(string key, ServiceLeaseRecord record, DateTimeOffset now)
    {
        if (FailWith is { } e) throw e;
        if (Items.TryGetValue(key, out var held) && held.ExpiresAt > now) return Task.FromResult(false);
        Items[key] = record;
        return Task.FromResult(true);
    }

    public Task<ServiceLeaseRecord?> GetAsync(string key)
    {
        if (FailWith is { } e) throw e;
        BeforeGet?.Invoke();
        return Task.FromResult(Items.TryGetValue(key, out var r) ? r : null);
    }

    public Task<bool> ReleaseIfTokenAsync(string key, string token)
    {
        if (FailWith is { } e) throw e;
        if (!Items.TryGetValue(key, out var r) || r.Token != token) return Task.FromResult(false);
        Items[key] = r with { ExpiresAt = DateTimeOffset.UnixEpoch };
        return Task.FromResult(true);
    }

    /// <summary>The leases not released: what a claimer would find held, given an unexpired time.</summary>
    public IReadOnlyDictionary<string, ServiceLeaseRecord> Live
        => Items.Where(i => i.Value.ExpiresAt > DateTimeOffset.UnixEpoch).ToDictionary(i => i.Key, i => i.Value);
}
