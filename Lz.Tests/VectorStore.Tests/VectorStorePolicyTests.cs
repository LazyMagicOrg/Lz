using System.Text.Json;
using Lz.Aws.VectorStore;
using Lz.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Tests.VectorStore.Tests;

/// <summary>
/// Pure decisions behind the aoss vector-store provisioning (names + policy
/// documents) and the YAML opt-in binding. No AWS/Pulumi — the components only
/// translate these decisions into resources, so pinning them here pins the
/// provisioning behaviour.
/// </summary>
public class VectorStorePolicyTests
{
    private static SystemConfig Config(VectorStoreConfig? vs) => new()
    {
        SystemKey = "scu",
        Environment = "dev",
        VectorStore = vs,
    };

    // ---- names ----

    [Fact]
    public void CollectionName_Defaults_To_SkEnvMatch()
    {
        Assert.Equal("scu-dev-match", VectorStorePolicy.CollectionName(Config(new VectorStoreConfig())));
    }

    [Fact]
    public void CollectionName_ExplicitOverride_Wins()
    {
        var cfg = Config(new VectorStoreConfig { CollectionName = "custom-vectors" });
        Assert.Equal("custom-vectors", VectorStorePolicy.CollectionName(cfg));
    }

    [Fact]
    public void CollectionName_InvalidOverride_Throws()
    {
        var cfg = Config(new VectorStoreConfig { CollectionName = "Has_Bad-Chars" });
        Assert.Throws<InvalidOperationException>(() => VectorStorePolicy.CollectionName(cfg));
    }

    [Theory]
    [InlineData("scu-dev-match")]
    [InlineData("a1-b")]
    [InlineData("abc")]
    public void ValidateName_Accepts_ValidAossNames(string name)
        => Assert.Equal(name, VectorStorePolicy.ValidateName(name));

    [Theory]
    [InlineData("ab")]                                    // too short (min 3)
    [InlineData("a-name-that-is-far-too-long-for-aoss")]  // > 32 chars
    [InlineData("1starts-with-digit")]
    [InlineData("has_underscore")]
    [InlineData("Has-Upper")]
    public void ValidateName_Rejects_InvalidAossNames(string name)
        => Assert.Throws<InvalidOperationException>(() => VectorStorePolicy.ValidateName(name));

    [Fact]
    public void TenantAccessPolicyName_Appends_TenantKey()
        => Assert.Equal("scu-dev-match-mp", VectorStorePolicy.TenantAccessPolicyName("scu-dev-match", "mp"));

    [Fact]
    public void TenantAccessPolicyName_OverLengthCombination_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => VectorStorePolicy.TenantAccessPolicyName("a-twentynine-char-collection1", "longtenant"));

    // ---- policy documents ----

    [Fact]
    public void EncryptionPolicy_IsAwsOwnedKey_ForTheCollection()
    {
        using var doc = JsonDocument.Parse(VectorStorePolicy.EncryptionPolicyJson("scu-dev-match"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("AWSOwnedKey").GetBoolean());
        var rule = root.GetProperty("Rules")[0];
        Assert.Equal("collection", rule.GetProperty("ResourceType").GetString());
        Assert.Equal("collection/scu-dev-match", rule.GetProperty("Resource")[0].GetString());
    }

    [Fact]
    public void NetworkPolicy_IsPublic_ForCollectionAndDashboard()
    {
        using var doc = JsonDocument.Parse(VectorStorePolicy.NetworkPolicyJson("scu-dev-match"));
        var block = doc.RootElement[0]; // network policies are a JSON array

        Assert.True(block.GetProperty("AllowFromPublic").GetBoolean());
        var types = block.GetProperty("Rules").EnumerateArray()
            .Select(r => r.GetProperty("ResourceType").GetString())
            .ToArray();
        Assert.Contains("collection", types);
        Assert.Contains("dashboard", types);
    }

    [Fact]
    public void DataAccessPolicy_Grants_CollectionAndIndex_ToPrincipals()
    {
        const string arn = "arn:aws:iam::123456789012:role/some-role";
        using var doc = JsonDocument.Parse(
            VectorStorePolicy.DataAccessPolicyJson("scu-dev-match", new[] { arn, arn, " " }));
        var block = doc.RootElement[0]; // data policies are a JSON array

        // duplicates and blanks dropped
        var principals = block.GetProperty("Principal").EnumerateArray()
            .Select(p => p.GetString()).ToArray();
        Assert.Equal(new[] { arn }, principals);

        var rules = block.GetProperty("Rules").EnumerateArray().ToArray();
        Assert.Equal("collection/scu-dev-match", rules[0].GetProperty("Resource")[0].GetString());
        Assert.Equal("index/scu-dev-match/*", rules[1].GetProperty("Resource")[0].GetString());
        Assert.All(rules, r => Assert.Equal("aoss:*", r.GetProperty("Permission")[0].GetString()));
    }

    [Fact]
    public void DataAccessPolicy_NoPrincipals_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => VectorStorePolicy.DataAccessPolicyJson("scu-dev-match", new[] { " ", "" }));

    // ---- config defaults ----

    [Fact]
    public void VectorStoreConfig_Defaults_AreScaleToZeroVectorsearch_MaxTwoTwo()
    {
        var cfg = new VectorStoreConfig();

        Assert.Equal(string.Empty, cfg.CollectionName);
        Assert.Equal("VECTORSEARCH", cfg.Type);
        Assert.True(cfg.ScaleToZero);
        Assert.Equal(2, cfg.MaxIndexingOcu);
        Assert.Equal(2, cfg.MaxSearchOcu);
        Assert.Empty(cfg.DataAccessPrincipals);
    }

    // ---- YAML bind: guards against the "green tests, dead config" no-op ----
    // Same deserializer contract as ConfigLoader.BuildDeserializer (PascalCase +
    // IgnoreUnmatchedProperties), mirroring TableDurabilityPolicyTests.

    private static IDeserializer LoaderDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    [Fact]
    public void SystemConfig_Yaml_BindsVectorStoreSection()
    {
        // The exact shape written into systemconfig.scu.dev.yaml.
        const string yaml = """
            Region: us-west-2
            VectorStore:
              ScaleToZero: true
              MaxIndexingOcu: 2
              MaxSearchOcu: 2
              DataAccessPrincipals:
                - arn:aws:iam::123456789012:role/dev-sso
            """;

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.NotNull(config.VectorStore);
        Assert.True(config.VectorStore!.ScaleToZero);
        Assert.Equal(2, config.VectorStore.MaxIndexingOcu);
        Assert.Equal(2, config.VectorStore.MaxSearchOcu);
        Assert.Equal("arn:aws:iam::123456789012:role/dev-sso",
            Assert.Single(config.VectorStore.DataAccessPrincipals));
        Assert.Equal("VECTORSEARCH", config.VectorStore.Type); // unset -> default
    }

    [Fact]
    public void SystemConfig_Yaml_NoVectorStoreSection_LeavesItNull_TheBaseline()
    {
        // Omitting the section is the no-opt-in baseline: nothing is provisioned.
        const string yaml = "Region: us-west-2";

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.Null(config.VectorStore);
    }

    [Fact]
    public void SystemConfig_Yaml_ScaleToZeroFalse_BindsThrough()
    {
        // ScaleToZero: false must survive the bind (classic collection path).
        const string yaml = """
            VectorStore:
              ScaleToZero: false
              CollectionName: my-vectors
            """;

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        Assert.NotNull(config.VectorStore);
        Assert.False(config.VectorStore!.ScaleToZero);
        Assert.Equal("my-vectors", config.VectorStore.CollectionName);
    }
}
