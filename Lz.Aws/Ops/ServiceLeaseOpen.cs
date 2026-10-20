using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Lz.Aws.Ops;

/// <summary>The CLI's half of the lease store: opening it with a profile. Not compiled into the deployer's functions.</summary>
public sealed partial class DynamoDbServiceLeaseStore
{
    /// <summary>
    /// The store in <paramref name="table"/>, or null when that table DOES NOT EXIST: a topology without a system table
    /// (Keycloak's) has nowhere to keep a lease, and the caller says it runs without one. Every other failure (access
    /// denied, an expired session, throttling) throws, so a lease that cannot be read is never taken for no lease at all.
    /// </summary>
    public static async Task<DynamoDbServiceLeaseStore?> OpenAsync(string profile, string region, string table, CancellationToken ct)
    {
        var credentials = Lz.Aws.AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        IAmazonDynamoDB db = credentials != null ? new AmazonDynamoDBClient(credentials, endpoint) : new AmazonDynamoDBClient(endpoint);
        try
        {
            await db.DescribeTableAsync(table, ct);
        }
        catch (ResourceNotFoundException)
        {
            db.Dispose();
            return null;
        }
        return new DynamoDbServiceLeaseStore(db, table);
    }
}
