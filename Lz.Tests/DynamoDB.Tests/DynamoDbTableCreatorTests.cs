using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Lz.Aws.DynamoDB;
using Moq;

namespace Lz.Tests.DynamoDB.Tests;

/// <summary>
/// The two table shapes <see cref="DynamoDbTableCreator"/> sends, pinned against a mocked client.
///
/// The GSI table is what LazyMagic's Gsi kind reads: its five global secondary indexes must carry the LSIs' names and
/// keys (PK, SKn), because DYDBRepository's query builders name the index PK-{keyField}-Index and ask for
/// PK = :PKval and {keyField} ...; they must be KEYS_ONLY, because the reader fetches the item and a projection of
/// Data would cost a write per save; and the table must have NO local secondary index, because an LSI is what caps
/// a partition key value's items at 10 GB.
/// </summary>
public class DynamoDbTableCreatorTests
{
    private static readonly Dictionary<string, string> Tags = new() { ["System"] = "scu", ["Level"] = "subtenant" };
    private static readonly string[] IndexNames = { "PK-SK1-Index", "PK-SK2-Index", "PK-SK3-Index", "PK-SK4-Index", "PK-SK5-Index" };

    private static void AssertEnvelopeKeysAndAttributes(CreateTableRequest request)
    {
        Assert.Equal(
            new[] { ("PK", KeyType.HASH), ("SK", KeyType.RANGE) },
            request.KeySchema.Select(k => (k.AttributeName, k.KeyType)));
        Assert.Equal(new[] { "PK", "SK", "SK1", "SK2", "SK3", "SK4", "SK5" }, request.AttributeDefinitions.Select(a => a.AttributeName));
        Assert.All(request.AttributeDefinitions, a => Assert.Equal(ScalarAttributeType.S, a.AttributeType));
        Assert.Equal(BillingMode.PAY_PER_REQUEST, request.BillingMode);
        Assert.Contains(request.Tags, t => t.Key == "ManagedBy" && t.Value == "lz-pulumi");
        Assert.Contains(request.Tags, t => t.Key == "System" && t.Value == "scu");
        Assert.Contains(request.Tags, t => t.Key == "Level" && t.Value == "subtenant");
    }

    [Fact]
    public async Task TheGsiTable_HasFiveKeysOnlyGsisKeyedLikeTheLsis_AndNoLsi()
    {
        var aws = new FakeDynamoTables();

        var created = await DynamoDbTableCreator.EnsureGsiTableAsync(aws.Client.Object, "scu_mp_match_gsi", Tags, null, TimeSpan.Zero);

        Assert.True(created);
        var request = Assert.Single(aws.Creates);
        Assert.Equal("scu_mp_match_gsi", request.TableName);
        AssertEnvelopeKeysAndAttributes(request);
        Assert.True(request.LocalSecondaryIndexes is null || request.LocalSecondaryIndexes.Count == 0,
            "an LSI would bring back the 10 GB limit the GSI table exists to avoid");
        Assert.Equal(IndexNames, request.GlobalSecondaryIndexes.Select(g => g.IndexName));
        for (var i = 1; i <= 5; i++)
        {
            var gsi = request.GlobalSecondaryIndexes[i - 1];
            Assert.Equal(new[] { ("PK", KeyType.HASH), ($"SK{i}", KeyType.RANGE) }, gsi.KeySchema.Select(k => (k.AttributeName, k.KeyType)));
            Assert.Equal(ProjectionType.KEYS_ONLY, gsi.Projection.ProjectionType);
            Assert.Null(gsi.ProvisionedThroughput); // on-demand: an index takes the table's billing
        }
        Assert.Null(request.DeletionProtectionEnabled);
    }

    [Fact]
    public async Task TheLsiTable_KeepsItsFiveAllProjectionLsis_AndHasNoGsi()
    {
        var aws = new FakeDynamoTables();

        await DynamoDbTableCreator.EnsureTableAsync(aws.Client.Object, "scu_mp_match", Tags, null, TimeSpan.Zero);

        var request = Assert.Single(aws.Creates);
        AssertEnvelopeKeysAndAttributes(request);
        Assert.Equal(IndexNames, request.LocalSecondaryIndexes.Select(l => l.IndexName));
        Assert.All(request.LocalSecondaryIndexes, l => Assert.Equal(ProjectionType.ALL, l.Projection.ProjectionType));
        Assert.True(request.GlobalSecondaryIndexes is null || request.GlobalSecondaryIndexes.Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewTable_WaitsForActive_ThenEnablesTtl(bool gsi)
    {
        var aws = new FakeDynamoTables { CreatingPolls = 2 };

        _ = gsi
            ? await DynamoDbTableCreator.EnsureGsiTableAsync(aws.Client.Object, "t", Tags, null, TimeSpan.Zero)
            : await DynamoDbTableCreator.EnsureTableAsync(aws.Client.Object, "t", Tags, null, TimeSpan.Zero);

        // One describe to find it missing, two that say CREATING, one that says ACTIVE.
        aws.Client.Verify(c => c.DescribeTableAsync("t", It.IsAny<CancellationToken>()), Times.Exactly(4));
        var ttl = Assert.Single(aws.TtlUpdates);
        Assert.Equal("TTL", ttl.TimeToLiveSpecification.AttributeName);
        Assert.True(ttl.TimeToLiveSpecification.Enabled);
        Assert.Equal(new[] { "create:t", "ttl:t" }, aws.Events);
    }

    [Fact]
    public async Task AnExistingGsiTable_IsNotRecreated_ButGetsTheProtectionAskedFor()
    {
        var aws = new FakeDynamoTables(("scu_mp_match_gsi", false));

        var created = await DynamoDbTableCreator.EnsureGsiTableAsync(
            aws.Client.Object, "scu_mp_match_gsi", Tags, new TableDurabilityDecision(true, false), TimeSpan.Zero);

        Assert.False(created);
        Assert.Empty(aws.Creates);
        Assert.True(Assert.Single(aws.Updates).DeletionProtectionEnabled);
        Assert.Empty(aws.PitrUpdates);
    }

    [Fact]
    public async Task ANewGsiTable_TakesTheDurabilityDecisionAtCreate()
    {
        var aws = new FakeDynamoTables();

        await DynamoDbTableCreator.EnsureGsiTableAsync(
            aws.Client.Object, "scu_mp_match_gsi", Tags, new TableDurabilityDecision(true, true), TimeSpan.Zero);

        Assert.True(Assert.Single(aws.Creates).DeletionProtectionEnabled);
        Assert.Empty(aws.Updates); // set on the create, not again after it
        Assert.Equal("scu_mp_match_gsi", Assert.Single(aws.PitrUpdates).TableName);
    }
}
