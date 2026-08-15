using Lz.Core.Config;
using YamlDotNet.Serialization;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Phase 0 — verifies ConfigLoader gates IConfigExtensions by ActivePlatform.
/// Two fake extensions (aws-fake, azure-fake) both map AuthConfigEntry to
/// different derived types; only the one matching the active platform
/// contributes, so loading the same YAML under different platforms produces
/// different runtime types.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class ConfigLoaderExtensionsTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLoaderExtensionsTests()
    {
        ConfigLoader.ResetForTests();
        _tempDir = Path.Combine(Path.GetTempPath(), "lz-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AwsExtensionMatchesWhenPlatformIsAws()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        ConfigLoader.RegisterExtensions(new FakeAzureExtensions());

        var path = WriteSystemConfig(platform: "aws");
        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.Equal("aws", ConfigLoader.ActivePlatform);
        Assert.NotNull(config.AuthConfigs);
        var entry = config.AuthConfigs!["primary"];
        Assert.IsType<FakeAwsAuthConfigEntry>(entry);
    }

    [Fact]
    public void AzureExtensionMatchesWhenPlatformIsAzure()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        ConfigLoader.RegisterExtensions(new FakeAzureExtensions());

        var path = WriteSystemConfig(platform: "azure");
        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.Equal("azure", ConfigLoader.ActivePlatform);
        Assert.NotNull(config.AuthConfigs);
        var entry = config.AuthConfigs!["primary"];
        Assert.IsType<FakeAzureAuthConfigEntry>(entry);
    }

    [Fact]
    public void NoMatchingExtensionYieldsBaseType()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        // Azure not registered.

        var path = WriteSystemConfig(platform: "azure");
        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.Equal("azure", ConfigLoader.ActivePlatform);
        Assert.NotNull(config.AuthConfigs);
        var entry = config.AuthConfigs!["primary"];
        Assert.Equal(typeof(AuthConfigEntry), entry.GetType());
    }

    [Fact]
    public void PlatformDefaultsToAwsWhenKeyAbsent()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        ConfigLoader.RegisterExtensions(new FakeAzureExtensions());

        var path = WriteSystemConfig(platform: null);
        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.Equal("aws", ConfigLoader.ActivePlatform);
        var entry = config.AuthConfigs!["primary"];
        Assert.IsType<FakeAwsAuthConfigEntry>(entry);
    }

    [Fact]
    public void SwitchingPlatformRebuildsDeserializer()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        ConfigLoader.RegisterExtensions(new FakeAzureExtensions());

        var awsPath = WriteSystemConfig(platform: "aws", filename: "systemconfig.a.dev.yaml");
        var azurePath = WriteSystemConfig(platform: "azure", filename: "systemconfig.b.dev.yaml");

        var awsCfg = ConfigLoader.LoadSystemConfig(awsPath);
        Assert.IsType<FakeAwsAuthConfigEntry>(awsCfg.AuthConfigs!["primary"]);

        var azureCfg = ConfigLoader.LoadSystemConfig(azurePath);
        Assert.IsType<FakeAzureAuthConfigEntry>(azureCfg.AuthConfigs!["primary"]);
    }

    [Fact]
    public void IndentedPlatformKeyIsIgnored()
    {
        ConfigLoader.RegisterExtensions(new FakeAwsExtensions());
        ConfigLoader.RegisterExtensions(new FakeAzureExtensions());

        // `platform:` is nested under another key — must NOT switch active platform
        var yaml = """
                   Topology: lambda-cognito-dynamodb
                   SystemSuffix: test
                   Profile: dummy
                   Region: us-west-2
                   Nested:
                     Platform: azure
                   AuthConfigs:
                     primary: {}
                   """;
        var path = Path.Combine(_tempDir, "systemconfig.nested.dev.yaml");
        File.WriteAllText(path, yaml);

        ConfigLoader.LoadSystemConfig(path);

        Assert.Equal("aws", ConfigLoader.ActivePlatform);
    }

    private string WriteSystemConfig(string? platform, string filename = "systemconfig.t.dev.yaml")
    {
        var lines = new List<string>();
        if (platform != null) lines.Add($"Platform: {platform}");
        lines.Add("Topology: lambda-cognito-dynamodb");
        lines.Add("SystemSuffix: test");
        lines.Add("Profile: dummy");
        lines.Add("Region: us-west-2");
        lines.Add("AuthConfigs:");
        lines.Add("  primary: {}");
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllLines(path, lines);
        return path;
    }

    // --- Fakes ---

    private sealed class FakeAwsAuthConfigEntry : AuthConfigEntry { }
    private sealed class FakeAzureAuthConfigEntry : AuthConfigEntry { }

    private sealed class FakeAwsExtensions : IConfigExtensions
    {
        public string Platform => "aws";
        public void Configure(DeserializerBuilder builder)
            => builder.WithTypeMapping<AuthConfigEntry, FakeAwsAuthConfigEntry>();
    }

    private sealed class FakeAzureExtensions : IConfigExtensions
    {
        public string Platform => "azure";
        public void Configure(DeserializerBuilder builder)
            => builder.WithTypeMapping<AuthConfigEntry, FakeAzureAuthConfigEntry>();
    }
}
