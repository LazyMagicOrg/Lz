using Lz.Aws.Secrets;
using Lz.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Tests.Secrets.Tests;

/// <summary>
/// Pure decisions behind the required-secrets pre-flight (--secret parsing, name
/// expansion, missing-key detection, JSON merge) and the YAML opt-in binding. No
/// AWS — <c>AwsSecretsEnsurer</c> only translates these into SDK calls.
/// </summary>
public class SecretsPlannerTests
{
    // ---- ExpandName ----

    [Fact]
    public void ExpandName_ReplacesSecretPrefixToken()
    {
        var config = new SystemConfig { SecretsManager = new SecretsManagerConfig { SecretPrefix = "scu" } };
        Assert.Equal("scu/icecat", SecretsPlanner.ExpandName("{SecretPrefix}/icecat", config));
    }

    [Fact]
    public void ExpandName_LiteralName_PassesThrough_EvenWithoutPrefix()
    {
        Assert.Equal("my/literal", SecretsPlanner.ExpandName("my/literal", new SystemConfig()));
    }

    [Fact]
    public void ExpandName_TokenWithoutPrefixConfigured_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => SecretsPlanner.ExpandName("{SecretPrefix}/icecat", new SystemConfig()));
    }

    // ---- ParseSecretArgs ----

    [Fact]
    public void ParseSecretArgs_ParsesNameKeyValue_WithSlashesAndSpecialsInValue()
    {
        var parsed = SecretsPlanner.ParseSecretArgs(new[]
        {
            "scu/icecat:ApiToken=abc123",
            "scu/icecat:ContentToken=has=equals:and:colons",
        });

        Assert.Equal("abc123", parsed[("scu/icecat", "ApiToken")]);
        Assert.Equal("has=equals:and:colons", parsed[("scu/icecat", "ContentToken")]);
    }

    [Fact]
    public void ParseSecretArgs_NullOrEmpty_YieldsEmpty()
    {
        Assert.Empty(SecretsPlanner.ParseSecretArgs(null));
        Assert.Empty(SecretsPlanner.ParseSecretArgs(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("no-colon-or-equals")]
    [InlineData("name-only:")]
    [InlineData(":Key=value")]
    [InlineData("name:=value")]
    [InlineData("name:Key=")]
    public void ParseSecretArgs_Malformed_Throws(string arg)
    {
        Assert.Throws<ArgumentException>(() => SecretsPlanner.ParseSecretArgs(new[] { arg }));
    }

    // ---- MissingKeys ----

    private static readonly string[] BothKeys = { "ApiToken", "ContentToken" };

    [Fact]
    public void MissingKeys_NoSecret_AllMissing()
    {
        Assert.Equal(BothKeys, SecretsPlanner.MissingKeys(null, BothKeys));
    }

    [Fact]
    public void MissingKeys_AllPresent_NoneMissing()
    {
        const string json = """{"ApiToken":"a","ContentToken":"c"}""";
        Assert.Empty(SecretsPlanner.MissingKeys(json, BothKeys));
    }

    [Fact]
    public void MissingKeys_EmptyStringValue_CountsAsMissing()
    {
        const string json = """{"ApiToken":"a","ContentToken":""}""";
        Assert.Equal(new[] { "ContentToken" }, SecretsPlanner.MissingKeys(json, BothKeys));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void MissingKeys_ExistingNonObjectSecret_Throws_NeverOverwrites(string existing)
    {
        // A secret that exists but is not a JSON object must be an ERROR — the
        // ensurer must never silently clobber a hand-created value.
        Assert.Throws<InvalidOperationException>(
            () => SecretsPlanner.MissingKeys(existing, BothKeys));
    }

    // ---- MergeSecretJson ----

    [Fact]
    public void MergeSecretJson_IntoNothing_CreatesObject()
    {
        var json = SecretsPlanner.MergeSecretJson(null, new Dictionary<string, string> { ["ApiToken"] = "a" });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("a", doc.RootElement.GetProperty("ApiToken").GetString());
    }

    [Fact]
    public void MergeSecretJson_PreservesExistingKeys()
    {
        const string existing = """{"ApiToken":"keep-me","Extra":"also-kept"}""";
        var json = SecretsPlanner.MergeSecretJson(existing,
            new Dictionary<string, string> { ["ContentToken"] = "new" });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("keep-me", doc.RootElement.GetProperty("ApiToken").GetString());
        Assert.Equal("also-kept", doc.RootElement.GetProperty("Extra").GetString());
        Assert.Equal("new", doc.RootElement.GetProperty("ContentToken").GetString());
    }

    // ---- YAML bind: guards against the "green tests, dead config" no-op ----

    private static IDeserializer LoaderDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    [Fact]
    public void SystemConfig_Yaml_BindsRequiredSecrets()
    {
        // The exact shape written into systemconfig.scu.dev.yaml.
        const string yaml = """
            SecretsManager:
              SecretPrefix: scu
            RequiredSecrets:
              - Name: "{SecretPrefix}/icecat"
                Keys: [ApiToken, ContentToken]
                Description: Icecat API tokens
            """;

        var config = LoaderDeserializer().Deserialize<SystemConfig>(yaml);

        var entry = Assert.Single(config.RequiredSecrets!);
        Assert.Equal("{SecretPrefix}/icecat", entry.Name);
        Assert.Equal(new[] { "ApiToken", "ContentToken" }, entry.Keys);
        Assert.Equal("scu/icecat", SecretsPlanner.ExpandName(entry.Name, config));
    }

    [Fact]
    public void SystemConfig_Yaml_NoRequiredSecrets_LeavesItNull_TheBaseline()
    {
        var config = LoaderDeserializer().Deserialize<SystemConfig>("Region: us-west-2");
        Assert.Null(config.RequiredSecrets);
    }
}
