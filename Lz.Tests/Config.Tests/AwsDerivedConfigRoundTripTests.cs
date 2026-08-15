using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Phase 3 — verifies ConfigLoader materialises SystemConfig, TenantConfig,
/// and SharedConfig as their AWS-derived types when AwsConfigExtensions is
/// registered, and that AWS-specific fields populate from the same YAML the
/// user already writes (no schema break).
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class AwsDerivedConfigRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public AwsDerivedConfigRoundTripTests()
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
    public void SystemConfigMaterialisesAsAwsSystemConfigUnderAws()
    {
        var path = WriteFile("systemconfig.t.dev.yaml", """
            Platform: aws
            Topology: ecs-fargate-cognito-dynamodb
            SystemSuffix: test
            Profile: dummy
            Region: us-west-2
            SharedProfile: shared-prof
            SharedRegion: us-east-1
            SharedSecretArn: arn:aws:secretsmanager:us-east-1:123:secret:shared/system
            SharedKmsKeyArn: arn:aws:kms:us-east-1:123:key/abc
            TrustedAccountIds:
              - "111122223333"
              - "444455556666"
            Fargate:
              Cpu: 2048
              Memory: 4096
              Port: 9090
            """);

        var config = ConfigLoader.LoadSystemConfig(path);
        var aws = Assert.IsType<AwsSystemConfig>(config);

        Assert.Equal("shared-prof", aws.SharedProfile);
        Assert.Equal("us-east-1", aws.SharedRegion);
        Assert.Equal("arn:aws:secretsmanager:us-east-1:123:secret:shared/system", aws.SharedSecretArn);
        Assert.Equal("arn:aws:kms:us-east-1:123:key/abc", aws.SharedKmsKeyArn);
        Assert.Equal(new[] { "111122223333", "444455556666" }, aws.TrustedAccountIds);
        Assert.NotNull(aws.Fargate);
        Assert.Equal(2048, aws.Fargate!.Cpu);
        Assert.Equal(4096, aws.Fargate.Memory);
        Assert.Equal(9090, aws.Fargate.Port);
    }

    [Fact]
    public void TenantConfigMaterialisesAsAwsTenantConfigUnderAws()
    {
        var path = WriteFile("tenantconfig.t.tk.dev.yaml", """
            RootDomain: tenant.example.com
            TenantSuffix: tk-suf
            AcmCertificateArn: arn:aws:acm:us-west-2:123:certificate/abc
            HostedZoneId: Z1234567890
            SharedSecretArn: arn:aws:secretsmanager:us-east-1:123:secret:shared/system
            SharedKmsKeyArn: arn:aws:kms:us-east-1:123:key/abc
            Fargate:
              Cpu: 1024
              Memory: 2048
            """);

        var config = ConfigLoader.LoadTenantConfig(path);
        var aws = Assert.IsType<AwsTenantConfig>(config);

        Assert.Equal("arn:aws:acm:us-west-2:123:certificate/abc", aws.AcmCertificateArn);
        Assert.Equal("Z1234567890", aws.HostedZoneId);
        Assert.Equal("arn:aws:secretsmanager:us-east-1:123:secret:shared/system", aws.SharedSecretArn);
        Assert.NotNull(aws.Fargate);
        Assert.Equal(1024, aws.Fargate!.Cpu);
    }

    [Fact]
    public void SharedConfigMaterialisesAsAwsSharedConfigUnderAws()
    {
        var path = WriteFile("sharedconfig.yaml", """
            Profile: shared
            Region: us-west-2
            Domain: shared.example.com
            VpcCidr: 10.0.0.0/16
            SharedSuffix: 1234-5678
            Keycloak:
              ImageTag: 26.5.0
              Cpu: 1024
              Memory: 2048
              DesiredCount: 3
              ThemePath: /keycloak-themes/my-theme
            TailscaleInstanceType: t4g.small
            TailscaleDesiredCapacity: 4
            TrustedAccountIds:
              - "111122223333"
            """);

        var config = ConfigLoader.LoadSharedConfig(path);
        var aws = Assert.IsType<AwsSharedConfig>(config);

        Assert.Equal("26.5.0", aws.Keycloak.ImageTag);
        Assert.Equal(1024, aws.Keycloak.Cpu);
        Assert.Equal(2048, aws.Keycloak.Memory);
        Assert.Equal(3, aws.Keycloak.DesiredCount);
        Assert.Equal("/keycloak-themes/my-theme", aws.Keycloak.ThemePath);
        Assert.Equal("t4g.small", aws.TailscaleInstanceType);
        Assert.Equal(4, aws.TailscaleDesiredCapacity);
        Assert.Equal(new[] { "111122223333" }, aws.TrustedAccountIds);
    }

    [Fact]
    public void ExistingYamlWithoutAwsFieldsStillLoadsWithDefaults()
    {
        var path = WriteFile("sharedconfig.yaml", """
            Profile: shared
            Region: us-west-2
            Domain: shared.example.com
            VpcCidr: 10.0.0.0/16
            SharedSuffix: 1234-5678
            """);

        var config = ConfigLoader.LoadSharedConfig(path);
        var aws = Assert.IsType<AwsSharedConfig>(config);

        // Defaults preserve current behaviour
        Assert.Equal("26.5.0", aws.Keycloak.ImageTag);
        Assert.Equal("t4g.nano", aws.TailscaleInstanceType);
        Assert.Equal(2, aws.TailscaleDesiredCapacity);
        Assert.Empty(aws.TrustedAccountIds);
    }

    [Fact]
    public void AwsExtensionHelpersRoundTrip()
    {
        var sysPath = WriteFile("systemconfig.t.dev.yaml", """
            Platform: aws
            Topology: ecs-fargate-cognito-dynamodb
            SystemSuffix: test
            Profile: dummy
            Region: us-west-2
            Fargate:
              Port: 7777
            """);
        var sys = ConfigLoader.LoadSystemConfig(sysPath);

        // The .Aws() extension resolves cleanly for AWS-loaded configs.
        Assert.Equal(7777, sys.Aws().Fargate!.Port);
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
