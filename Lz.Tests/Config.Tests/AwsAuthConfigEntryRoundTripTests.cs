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
        // M0-2 backward-compat: MachineAuth absent -> null -> no resource server / M2M client.
        Assert.Null(aws.MachineAuth);
    }

    [Fact]
    public void MachineAuthPopulatesFromYaml()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              tenantauth:
                MachineAuth:
                  Identifier: https://api.example.com/scutara
                  AccessTokenMinutes: 30
                  Scopes:
                    - Name: seller
                      Description: Act as a seller agent
                    - Name: buyer
                  Clients:
                    - Name: seller-agent
                      Scopes: [seller]
                    - Name: dual-agent
                      Scopes: [seller, buyer]
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        var aws = Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["tenantauth"]);

        Assert.NotNull(aws.MachineAuth);
        var m = aws.MachineAuth!;
        Assert.Equal("https://api.example.com/scutara", m.Identifier);
        Assert.Equal(30, m.AccessTokenMinutes);
        Assert.Equal(2, m.Scopes.Count);
        Assert.Equal("seller", m.Scopes[0].Name);
        Assert.Equal("Act as a seller agent", m.Scopes[0].Description);
        Assert.Null(m.Scopes[1].Description);
        Assert.Equal(2, m.Clients.Count);
        Assert.Equal("seller-agent", m.Clients[0].Name);
        Assert.Equal(new[] { "seller" }, m.Clients[0].Scopes);
        Assert.Equal(new[] { "seller", "buyer" }, m.Clients[1].Scopes);
    }

    [Fact]
    public void MachineAuthDefaultsAccessTokenMinutesTo60()
    {
        var path = WriteSystemConfig("""
            AuthConfigs:
              tenantauth:
                MachineAuth:
                  Identifier: https://api.example.com/scutara
                  Clients:
                    - Name: agent
                      Scopes: [seller]
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        var aws = Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["tenantauth"]);
        Assert.Equal(60, aws.MachineAuth!.AccessTokenMinutes);
    }

    [Fact]
    public void UserPoolTierPopulatesFromYaml_AndDefaultsToNull()
    {
        // Cost-optimization: ESSENTIALS unpins a pool that AdvancedSecurityMode
        // once landed on PLUS. Absent must stay NULL — "tier not managed", the
        // byte-identical baseline for sibling systems.
        var path = WriteSystemConfig("""
            AuthConfigs:
              consumerauth:
                UserPoolTier: ESSENTIALS
              tenantauth: {}
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        Assert.Equal("ESSENTIALS",
            Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["consumerauth"]).UserPoolTier);
        Assert.Null(
            Assert.IsType<AwsAuthConfigEntry>(config.AuthConfigs!["tenantauth"]).UserPoolTier);
    }

    [Fact]
    public void HygieneBlockPopulatesFromYaml_AndDefaultsToNull()
    {
        // The unbounded-growth caps (ECR untagged images, S3 noncurrent versions,
        // Lambda log retention). Omitted block -> null -> nothing applied.
        var path = WriteSystemConfig("""
            AuthConfigs:
              tenantauth: {}
            Hygiene:
              EcrUntaggedImageRetentionDays: 7
              S3NoncurrentVersionExpirationDays: 30
              LambdaLogRetentionDays: 14
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        Assert.NotNull(config.Hygiene);
        Assert.Equal(7, config.Hygiene!.EcrUntaggedImageRetentionDays);
        Assert.Equal(30, config.Hygiene.S3NoncurrentVersionExpirationDays);
        Assert.Equal(14, config.Hygiene.LambdaLogRetentionDays);

        var without = ConfigLoader.LoadSystemConfig(WriteSystemConfig("""
            AuthConfigs:
              tenantauth: {}
            """, "systemconfig.u.dev.yaml"));
        Assert.Null(without.Hygiene);
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
