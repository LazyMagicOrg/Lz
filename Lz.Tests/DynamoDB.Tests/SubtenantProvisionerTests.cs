using Lz.Aws.DynamoDB;
using Lz.Aws.Shared;

namespace Lz.Tests.DynamoDB.Tests;

/// <summary>
/// A subtenant has two tables, {sk}_{tk}_{stk} and its GSI twin {sk}_{tk}_{stk}_gsi, and they live and die together.
///
/// Ensure creates each in its own shape under the same durability decision, and creates a missing twin beside a
/// table that already exists (the upgrade of every deployed subtenant). Destroy decides deletion protection for
/// both before it deletes anything, so one protected table leaves the bucket and both tables untouched; and a table
/// that is already gone is skipped without stopping the other from being deleted. Before the twin, destroy
/// returned as soon as it found its one table gone.
/// </summary>
public class SubtenantProvisionerTests
{
    private const string Table = "scu_mp_match";
    private const string Twin = "scu_mp_match_gsi";
    private static readonly Dictionary<string, string> Tags = new() { ["System"] = "scu", ["Level"] = "subtenant" };

    [Fact]
    public void TheTwinIsNamedForTheTable()
    {
        Assert.Equal((Table, Twin), SubtenantProvisioner.TableNames("scu", "mp", "match"));
    }

    [Fact]
    public async Task Ensure_CreatesTheTableAndItsTwin_EachInItsOwnShape()
    {
        var aws = new FakeDynamoTables();

        await SubtenantProvisioner.EnsureTablesAsync(
            aws.Client.Object, "scu", "mp", "match", Tags, TableDurabilityDecision.None, TimeSpan.Zero);

        Assert.Equal(new[] { Table, Twin }, aws.Creates.Select(c => c.TableName));
        Assert.Equal(5, aws.Creates[0].LocalSecondaryIndexes.Count);
        Assert.Equal(5, aws.Creates[1].GlobalSecondaryIndexes.Count);
        Assert.True(aws.Creates[1].LocalSecondaryIndexes is null || aws.Creates[1].LocalSecondaryIndexes.Count == 0);
        Assert.All(aws.Creates, c => Assert.Contains(c.Tags, t => t.Key == "Level" && t.Value == "subtenant"));
    }

    [Fact]
    public async Task Ensure_GivesBothTablesTheVaultTablesProtection()
    {
        var aws = new FakeDynamoTables();

        await SubtenantProvisioner.EnsureTablesAsync(
            aws.Client.Object, "scu", "mp", "match", Tags, new TableDurabilityDecision(true, true), TimeSpan.Zero);

        Assert.All(aws.Creates, c => Assert.True(c.DeletionProtectionEnabled));
        Assert.Equal(new[] { Table, Twin }, aws.PitrUpdates.Select(p => p.TableName));
    }

    [Fact]
    public async Task Ensure_CreatesAMissingTwinBesideATableThatExists()
    {
        // Every subtenant deployed before the twin existed: deploysubtenants must add it and leave the table be.
        var aws = new FakeDynamoTables((Table, false));

        await SubtenantProvisioner.EnsureTablesAsync(
            aws.Client.Object, "scu", "mp", "match", Tags, TableDurabilityDecision.None, TimeSpan.Zero);

        Assert.Equal(Twin, Assert.Single(aws.Creates).TableName);
    }

    private static async Task Destroy(FakeDynamoTables aws, bool forceDeleteProtected)
        => await SubtenantProvisioner.DeleteOneAsync(
            aws.Client.Object, new[] { Table, Twin }, "bucket",
            () => { aws.Events.Add("bucket"); return Task.CompletedTask; },
            forceDeleteProtected);

    [Fact]
    public async Task Destroy_DeletesTheBucketThenBothTables()
    {
        var aws = new FakeDynamoTables((Table, false), (Twin, false));

        await Destroy(aws, forceDeleteProtected: false);

        Assert.Equal(new[] { "bucket", $"delete:{Table}", $"delete:{Twin}" }, aws.Events);
        Assert.Empty(aws.Live);
    }

    [Fact]
    public async Task Destroy_WhenOnlyTheTwinIsProtected_DestroysNothing()
    {
        var aws = new FakeDynamoTables((Table, false), (Twin, true));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Destroy(aws, forceDeleteProtected: false));

        Assert.Contains($"'{Twin}'", refusal.Message);
        Assert.DoesNotContain($"'{Table}'", refusal.Message);
        Assert.Empty(aws.Events); // no bucket, no table
        Assert.Equal(2, aws.Live.Count);
    }

    [Fact]
    public async Task Destroy_WhenBothAreProtected_NamesBoth()
    {
        var aws = new FakeDynamoTables((Table, true), (Twin, true));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Destroy(aws, forceDeleteProtected: false));

        Assert.Contains($"'{Table}' and '{Twin}'", refusal.Message);
        Assert.Empty(aws.Events);
    }

    [Fact]
    public async Task Destroy_WithForce_UnprotectsTheProtectedTable_ThenDeletesBoth()
    {
        var aws = new FakeDynamoTables((Table, false), (Twin, true));

        await Destroy(aws, forceDeleteProtected: true);

        Assert.Equal(
            new[] { "bucket", $"delete:{Table}", $"update:{Twin}:False", $"delete:{Twin}" },
            aws.Events);
        Assert.Empty(aws.Live);
    }

    [Fact]
    public async Task Destroy_WhenTheTwinIsMissing_StillDeletesTheTable()
    {
        var aws = new FakeDynamoTables((Table, false));

        await Destroy(aws, forceDeleteProtected: false);

        Assert.Equal(new[] { "bucket", $"delete:{Table}" }, aws.Events);
    }

    [Fact]
    public async Task Destroy_WhenTheTableIsGone_StillDeletesTheTwin()
    {
        // The case the old early return got wrong: finding the first table gone ended the destroy.
        var aws = new FakeDynamoTables((Twin, false));

        await Destroy(aws, forceDeleteProtected: false);

        Assert.Equal(new[] { "bucket", $"delete:{Twin}" }, aws.Events);
    }
}
