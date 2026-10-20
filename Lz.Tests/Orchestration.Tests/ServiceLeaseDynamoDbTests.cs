using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Lz.Aws.Ops;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// The lease's DynamoDB conditions against a real DynamoDB: DynamoDB Local at <c>LZ_DYNAMODB_LOCAL</c>
/// (e.g. <c>docker run -p 8000:8000 amazon/dynamodb-local</c>, then <c>LZ_DYNAMODB_LOCAL=http://localhost:8000</c>). Category
/// Integration, so CI's filter leaves it out; without the variable it passes having checked nothing, and says so.
/// </summary>
[Trait("Category", "Integration")]
public class ServiceLeaseDynamoDbTests
{
    [Fact]
    public async Task TheConditionsHoldAgainstDynamoDb()
    {
        var endpoint = Environment.GetEnvironmentVariable("LZ_DYNAMODB_LOCAL");
        if (string.IsNullOrEmpty(endpoint))
        {
            Console.WriteLine("LZ_DYNAMODB_LOCAL is not set: nothing was checked");
            return;
        }

        var db = new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = endpoint, AuthenticationRegion = "us-west-2" });
        var table = "lease-test-" + Guid.NewGuid().ToString("N")[..8];
        await db.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            // AWS SDK v4 leaves request collections null, so they are created here, not filled.
            AttributeDefinitions = new List<AttributeDefinition> { new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S) },
            KeySchema = new List<KeySchemaElement> { new("PK", KeyType.HASH), new("SK", KeyType.RANGE) },
        });
        try
        {
            var store = new DynamoDbServiceLeaseStore(db, table);
            var t0 = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

            var first = Assert.IsType<ServiceLeaseClaim.Claimed>(await ServiceLease.ClaimAsync(store, "c", "s", "deployer:req-1", "roll", t0)).Lease;
            var held = Assert.IsType<ServiceLeaseClaim.Held>(await ServiceLease.ClaimAsync(store, "c", "s", "updatecontainer:tmay", "roll", t0.AddMinutes(1)));
            Assert.Equal("deployer:req-1", held.By.Holder);
            Assert.Equal(first.Token, held.By.Token);
            Assert.Equal(first.ExpiresAt, held.By.ExpiresAt);

            Assert.False(await ServiceLease.ReleaseAsync(store, "c", "s", first with { Token = "not-it" }));
            Assert.True(await ServiceLease.ReleaseAsync(store, "c", "s", first));
            Assert.Equal(DateTimeOffset.UnixEpoch, (await store.GetAsync(ServiceLease.KeyFor("c", "s")))!.ExpiresAt);   // released, by expiring
            Assert.True(await ServiceLease.ReleaseAsync(store, "c", "s", first));   // a repeated release by its holder

            var crashed = Assert.IsType<ServiceLeaseClaim.Claimed>(await ServiceLease.ClaimAsync(store, "c", "s", "deployer:crashed", "roll", t0)).Lease;
            Assert.IsType<ServiceLeaseClaim.Held>(await ServiceLease.ClaimAsync(store, "c", "s", "x", "roll", t0 + ServiceLease.Duration - TimeSpan.FromSeconds(1)));
            var taker = Assert.IsType<ServiceLeaseClaim.Claimed>(await ServiceLease.ClaimAsync(store, "c", "s", "deploytenant:tmay", "roll", t0 + ServiceLease.Duration)).Lease;
            Assert.False(await ServiceLease.ReleaseAsync(store, "c", "s", crashed));
            Assert.True(await ServiceLease.ReleaseAsync(store, "c", "s", taker));
        }
        finally
        {
            await db.DeleteTableAsync(table);
        }
    }
}
