using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;

namespace Lz.Tests.DynamoDB.Tests;

/// <summary>
/// A strict mocked <see cref="IAmazonDynamoDB"/> over a set of live tables: each table's deletion protection is
/// tracked, and every request that creates, changes or deletes one is recorded, with <see cref="Events"/> keeping
/// their order. Any call the tests did not expect fails the test. A table that is not live answers DescribeTable
/// with <see cref="ResourceNotFoundException"/>, as DynamoDB does.
/// </summary>
internal sealed class FakeDynamoTables
{
    private readonly Dictionary<string, int> _pollsUntilActive = new();

    public Mock<IAmazonDynamoDB> Client { get; } = new(MockBehavior.Strict);
    /// <summary>The live tables, and whether each has deletion protection.</summary>
    public Dictionary<string, bool> Live { get; } = new();
    public List<CreateTableRequest> Creates { get; } = new();
    public List<UpdateTableRequest> Updates { get; } = new();
    public List<UpdateTimeToLiveRequest> TtlUpdates { get; } = new();
    public List<UpdateContinuousBackupsRequest> PitrUpdates { get; } = new();
    public List<string> Events { get; } = new();
    /// <summary>How many DescribeTable calls answer CREATING after a CreateTable, before ACTIVE.</summary>
    public int CreatingPolls { get; set; }

    public FakeDynamoTables(params (string Name, bool Protected)[] live)
    {
        foreach (var (name, isProtected) in live)
            Live[name] = isProtected;

        Client
            .Setup(c => c.DescribeTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => Describe(name));
        Client
            .Setup(c => c.CreateTableAsync(It.IsAny<CreateTableRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTableRequest r, CancellationToken _) =>
            {
                Creates.Add(r);
                Events.Add($"create:{r.TableName}");
                Live[r.TableName] = r.DeletionProtectionEnabled == true;
                _pollsUntilActive[r.TableName] = CreatingPolls;
                return new CreateTableResponse();
            });
        Client
            .Setup(c => c.UpdateTableAsync(It.IsAny<UpdateTableRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateTableRequest r, CancellationToken _) =>
            {
                Updates.Add(r);
                Events.Add($"update:{r.TableName}:{r.DeletionProtectionEnabled}");
                if (r.DeletionProtectionEnabled is bool isProtected)
                    Live[r.TableName] = isProtected;
                return new UpdateTableResponse();
            });
        Client
            .Setup(c => c.DeleteTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) =>
            {
                if (!Live.TryGetValue(name, out var isProtected))
                    throw new ResourceNotFoundException($"Requested resource not found: Table: {name} not found");
                if (isProtected)
                    throw new AmazonDynamoDBException($"(fake) {name} has deletion protection enabled");
                Live.Remove(name);
                Events.Add($"delete:{name}");
                return new DeleteTableResponse();
            });
        Client
            .Setup(c => c.UpdateTimeToLiveAsync(It.IsAny<UpdateTimeToLiveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateTimeToLiveRequest r, CancellationToken _) =>
            {
                TtlUpdates.Add(r);
                Events.Add($"ttl:{r.TableName}");
                return new UpdateTimeToLiveResponse();
            });
        Client
            .Setup(c => c.UpdateContinuousBackupsAsync(It.IsAny<UpdateContinuousBackupsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateContinuousBackupsRequest r, CancellationToken _) =>
            {
                PitrUpdates.Add(r);
                Events.Add($"pitr:{r.TableName}");
                return new UpdateContinuousBackupsResponse();
            });
    }

    private DescribeTableResponse Describe(string name)
    {
        if (!Live.TryGetValue(name, out var isProtected))
            throw new ResourceNotFoundException($"Requested resource not found: Table: {name} not found");
        var creating = _pollsUntilActive.TryGetValue(name, out var remaining) && remaining > 0;
        if (creating)
            _pollsUntilActive[name] = remaining - 1;
        return new DescribeTableResponse
        {
            Table = new TableDescription
            {
                TableName = name,
                TableStatus = creating ? TableStatus.CREATING : TableStatus.ACTIVE,
                DeletionProtectionEnabled = isProtected,
            },
        };
    }
}
