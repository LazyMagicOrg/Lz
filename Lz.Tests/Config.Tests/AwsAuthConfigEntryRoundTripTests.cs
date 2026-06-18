using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Phase 2 — verifies that YAML with Cognito-specific AuthConfig fields
/// deserialises through ConfigLoader into AwsAuthConfigEntry when the
/// platform is AWS, and that all fields populate from their YAML keys.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class AwsAuthConfigEntryRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public AwsAuthConfigEntryRoundTripTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _tempDir = Path.Combine(Path.GetTempPath(), "lz-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AuthEntryMaterialisesAsAwsAuthConfigEntryUnderAws()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              systemauth: {}
            """);

        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.NotNull(config.AuthConfigs);
        var entry = config.AuthConfigs!["systemauth"];
        Assert.IsType<AwsAuthConfigEntry>(entry);
    }

    [Fact]
    public void CognitoHardeningFieldsPopulateFromYaml()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              systemauth:
                Authority: https://example.com/sysauth
                MfaConfiguration: ON
                SoftwareTokenMfa: true
                SmsMfa: true
                PasswordMinLength: 12
                PasswordRequireSymbols: true
                AdvancedSecurityMode: ENFORCED
                IncludeDevCallbackUrls: true
                Groups:
                  - Name: super-admin
                    Description: Destructive privileges
                    Precedence: 1
                  - Name: operator
                    Description: Day-to-day admin
                    Precedence: 5
                    RoleArn: arn:aws:iam::123:role/OperatorRole
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        var aws = Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["systemauth"]);

        Assert.Equal("https://example.com/sysauth", aws.Authority);
        Assert.Equal("ON", aws.MfaConfiguration);
        Assert.True(aws.SoftwareTokenMfa);
        Assert.True(aws.SmsMfa);
        Assert.Equal(12, aws.PasswordMinLength);
        Assert.True(aws.PasswordRequireSymbols);
        Assert.Equal("ENFORCED", aws.AdvancedSecurityMode);
        Assert.True(aws.IncludeDevCallbackUrls);

        Assert.NotNull(aws.Groups);
        Assert.Equal(2, aws.Groups!.Count);

        var superAdmin = aws.Groups[0];
        Assert.Equal("super-admin", superAdmin.Name);
        Assert.Equal("Destructive privileges", superAdmin.Description);
        Assert.Equal(1, superAdmin.Precedence);
        Assert.Null(superAdmin.RoleArn);

        var op = aws.Groups[1];
        Assert.Equal("operator", op.Name);
        Assert.Equal("arn:aws:iam::123:role/OperatorRole", op.RoleArn);
    }

    [Fact]
    public void DefaultsPreserveCurrentBehaviourWhenYamlOmitsFields()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              plannerauth: {}
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        var aws = Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["plannerauth"]);

        Assert.Equal("OFF", aws.MfaConfiguration);
        Assert.False(aws.SoftwareTokenMfa);
        Assert.False(aws.SmsMfa);
        Assert.Equal(8, aws.PasswordMinLength);
        Assert.False(aws.PasswordRequireSymbols);
        Assert.Equal("OFF", aws.AdvancedSecurityMode);
        Assert.False(aws.IncludeDevCallbackUrls);
        Assert.Null(aws.Groups);
    }

    [Fact]
    public void MultipleAuthEntriesAllMaterialiseAsAws()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              systemauth:
                MfaConfiguration: ON
              tenantauth:
                MfaConfiguration: OPTIONAL
              plannerauth: {}
            """);

        var config = ConfigLoader.LoadSystemConfig(path);

        Assert.Equal(3, config.AuthConfigs!.Count);
        foreach (var (_, entry) in config.AuthConfigs)
            Assert.IsType<AwsAuthConfigEntry>(entry);
    }

    private string WriteSystemConfig(string authSection, string filename = "systemconfig.t.dev.yaml")
    {
        // apprunner topology keeps validator requirements minimal.
        var yaml =
            "Platform: aws\n" +
            "Topology: apprunner\n" +
            "SystemSuffix: test\n" +
            "Profile: dummy\n" +
            "Region: us-west-2\n" +
            authSection + "\n";
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, yaml);
        return path;
    }
}
