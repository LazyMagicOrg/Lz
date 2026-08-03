using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Core.Config;

namespace Lz.Aws.Orchestration;

/// <summary>
/// Reads and releases Pulumi state locks for a stack in the DIY S3 state backend.
/// While an update runs, Pulumi writes a lock object at
///   {backend}/.pulumi/locks/organization/{project}/{stack}/{uuid}.json
/// and a hard-killed process (Ctrl+C, crash) leaves it behind, blocking further
/// operations with "the stack is currently locked". <c>lz unlock</c> clears such a
/// lock — the same effect as <c>pulumi cancel</c> on a DIY backend, done straight
/// against S3 so it needs no pulumi subprocess and no live update to signal.
/// </summary>
public static class PulumiStateLock
{
    /// <summary>One lock file's holder metadata (Pulumi records pid/username/hostname/timestamp).</summary>
    public sealed record LockRecord(string Key, int? Pid, string? Username, string? Hostname, string? Timestamp);

    // Pulumi's DIY-backend lock layout. "organization" is the fixed pseudo-org
    // Pulumi uses for self-managed backends; the project is lz-{systemKey}, and the
    // stack is {sk}-{env} (foundation) or {sk}-{tk}-{env} (tenant) — the caller
    // passes the resolved stack name.
    private static string LockPrefix(SystemConfig config, string stackName)
        => $".pulumi/locks/organization/lz-{config.SystemKey}/{stackName}/";

    /// <summary>Lock holders currently recorded for <paramref name="stackName"/> (empty = unlocked).</summary>
    public static async Task<IReadOnlyList<LockRecord>> ListAsync(SystemConfig config, string stackName)
    {
        using var client = CreateClient(config, out var bucket);
        var records = new List<LockRecord>();
        foreach (var key in await ListLockKeysAsync(client, bucket, LockPrefix(config, stackName)))
            records.Add(await ReadLockAsync(client, bucket, key));
        return records;
    }

    /// <summary>Delete every lock object for the stack. Returns the number removed.</summary>
    public static async Task<int> ReleaseAsync(SystemConfig config, string stackName)
    {
        using var client = CreateClient(config, out var bucket);
        var keys = await ListLockKeysAsync(client, bucket, LockPrefix(config, stackName));
        foreach (var key in keys)
            await client.DeleteObjectAsync(bucket, key);
        return keys.Count;
    }

    private static async Task<List<string>> ListLockKeysAsync(IAmazonS3 client, string bucket, string prefix)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request);
            foreach (var o in response.S3Objects ?? new List<S3Object>())
                if (o.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    keys.Add(o.Key);
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        return keys;
    }

    private static async Task<LockRecord> ReadLockAsync(IAmazonS3 client, string bucket, string key)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key);
            using var reader = new StreamReader(response.ResponseStream);
            using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = doc.RootElement;
            int? pid = root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : null;
            string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
            return new LockRecord(key, pid, Get("username"), Get("hostname"), Get("timestamp"));
        }
        catch
        {
            // A lock object we cannot read/parse is still a lock — report it by key alone.
            return new LockRecord(key, null, null, null, null);
        }
    }

    private static IAmazonS3 CreateClient(SystemConfig config, out string bucket)
    {
        if (config.State is null || string.IsNullOrEmpty(config.State.Backend))
            throw new InvalidOperationException(
                "No Pulumi state backend is configured (config.State.Backend is empty).");
        bucket = AwsStateBootstrapper.ParseBucketName(config.State.Backend);
        return AwsStateBootstrapper.CreateS3Client(config.Profile, config.Region);
    }
}
