using Lz.Aws.DynamoDB;
using Lz.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Tests.DynamoDB.Tests;

/// <summary>
/// Pure decisions behind the subtenant vault/PII table's durability (O1) and its
/// deletion-protection-aware teardown (O2). No AWS — the imperative
/// <c>DynamoDbTableCreator</c>/<c>SubtenantProvisioner</c> only translate these
/// decisions into SDK calls, so pinning the decisions here pins the behaviour.
/// </summary>
public class TableDurabilityPolicyTests
{
    // ---- ForVaultTable: config -> create/ensure-time decision ----

    [Fact]
    public void ForVaultTable_NullConfig_IsNone_TheByteIdenticalBaseline()
    {
        // The whole MagicPets/no-opt-in guarantee rides on this: no config -> apply nothing.
        var decision = TableDurabilityPolicy.ForVaultTable(null);

        Assert.Equal(TableDurabilityDecision.None, decision);
        Assert.False(decision.DeletionProtection);
        Assert.False(decision.PointInTimeRecovery);
        Assert.False(decision.Any);
    }

    [Fact]
    public void ForVaultTable_AllFlagsFalse_IsNone()
    {
        var decision = TableDurabilityPolicy.ForVaultTable(new DurabilityConfig
        {
            DeletionProtection = false,
            PointInTimeRecovery = false,
        });

        Assert.Equal(TableDurabilityDecision.None, decision);
        Assert.False(decision.Any);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ForVaultTable_FlagsFlowThroughVerbatim(bool deletionProtection, bool pitr)
    {
        var decision = TableDurabilityPolicy.ForVaultTable(new DurabilityConfig
        {
            DeletionProtection = deletionProtection,
            PointInTimeRecovery = pitr,
        });

        Assert.Equal(deletionProtection, decision.DeletionProtection);
        Assert.Equal(pitr, decision.PointInTimeRecovery);
        Assert.True(decision.Any); // at least one flag set
    }

    [Fact]
    public void DurabilityConfig_DefaultsToBothOff()
    {
        // A section present with no flags set must still be the off baseline.
        var cfg = new DurabilityConfig();

        Assert.False(cfg.DeletionProtection);
        Assert.False(cfg.PointInTimeRecovery);
    }

    // ---- DecideTeardown: live protection + operator intent -> action ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecideTeardown_Unprotected_AlwaysDeletes_ForceIrrelevant(bool forceDeleteProtected)
    {
        // An unprotected table deletes exactly as it does today, with or without
        // the force flag — the flag only matters when protection is actually on.
        Assert.Equal(
            TableTeardownAction.Delete,
            TableDurabilityPolicy.DecideTeardown(tableIsProtected: false, forceDeleteProtected));
    }

    [Fact]
    public void DecideTeardown_Protected_NoForce_Refuses()
    {
        // The point of protection: a routine destroy must NOT silently delete it.
        Assert.Equal(
            TableTeardownAction.Refuse,
            TableDurabilityPolicy.DecideTeardown(tableIsProtected: true, forceDeleteProtected: false));
    }

    [Fact]
    public void DecideTeardown_Protected_Forced_DisablesThenDeletes()
    {
        // Deliberate opt-in: disable protection first (DeleteTable fails otherwise), then delete.
        Assert.Equal(
            TableTeardownAction.DisableProtectionThenDelete,
            TableDurabilityPolicy.DecideTeardown(tableIsProtected: true, forceDeleteProtected: true));
    }

    // ---- YAML bind: guards against the "green tests, dead config" no-op ----
    // Uses the SAME deserializer contract ConfigLoader.BuildDeserializer uses
    // (PascalCase + IgnoreUnmatchedProperties), so a future rename of the
    // Durability property or its flags that silently breaks the bind fails here.

    private static IDeserializer LoaderDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    [Fact]
    public void SystemConfig_Yaml_BindsDurabilitySection()
    {
        // The exact shape written into systemconfig.aip.dev.yaml.
        const string yaml = """
            Region: us-west-2
            Durability:
              DeletionProtection: true
              PointInTimeRecovery: true
            """;

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.NotNull(config.Durability);
        Assert.True(config.Durability!.DeletionProtection);
        Assert.True(config.Durability.PointInTimeRecovery);
        // End-to-end: YAML -> config -> the create/ensure decision the provisioner applies.
        var decision = TableDurabilityPolicy.ForVaultTable(config.Durability);
        Assert.True(decision.DeletionProtection);
        Assert.True(decision.PointInTimeRecovery);
    }

    [Fact]
    public void SystemConfig_Yaml_NoDurabilitySection_LeavesItNull_TheBaseline()
    {
        // Omitting the section is the no-opt-in baseline (MagicPets): null -> None.
        const string yaml = "Region: us-west-2";

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.Null(config.Durability);
        Assert.Equal(TableDurabilityDecision.None, TableDurabilityPolicy.ForVaultTable(config.Durability));
    }

    [Fact]
    public void SystemConfig_Yaml_PartialDurabilitySection_UnsetFlagDefaultsOff()
    {
        // PITR omitted must not silently become true.
        const string yaml = """
            Durability:
              DeletionProtection: true
            """;

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.NotNull(config.Durability);
        Assert.True(config.Durability!.DeletionProtection);
        Assert.False(config.Durability.PointInTimeRecovery);
    }
}
