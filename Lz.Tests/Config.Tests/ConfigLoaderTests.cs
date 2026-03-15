using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

public class ConfigLoaderTests
{
    private static string TestDataPath(string filename)
        => Path.Combine("Config.Tests", "testdata", filename);

    [Fact]
    public void ParseSystemConfigFilename_ExtractsSystemKeyAndEnvironment()
    {
        var (systemKey, env) = ConfigLoader.ParseSystemConfigFilename("systemconfig.med.dev.yaml");
        Assert.Equal("med", systemKey);
        Assert.Equal("dev", env);
    }

    [Fact]
    public void ParseSystemConfigFilename_WorksWithFullPath()
    {
        var (systemKey, env) = ConfigLoader.ParseSystemConfigFilename(
            "/some/path/systemconfig.acme.prod.yaml");
        Assert.Equal("acme", systemKey);
        Assert.Equal("prod", env);
    }

    [Fact]
    public void ParseSystemConfigFilename_ThrowsForInvalidFormat()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigLoader.ParseSystemConfigFilename("invalid.yaml"));
    }

    [Fact]
    public void ParseTenantConfigFilename_ExtractsAllFields()
    {
        var (systemKey, tenantKey, env) = ConfigLoader.ParseTenantConfigFilename(
            "tenantconfig.med.meadows.dev.yaml");
        Assert.Equal("med", systemKey);
        Assert.Equal("meadows", tenantKey);
        Assert.Equal("dev", env);
    }

    [Fact]
    public void ParseTenantConfigFilename_ThrowsForInvalidFormat()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigLoader.ParseTenantConfigFilename("tenantconfig.med.yaml"));
    }

    [Fact]
    public void LoadSystemConfig_ParsesAllFields()
    {
        var config = ConfigLoader.LoadSystemConfig(TestDataPath("systemconfig.testapp.dev.yaml"));

        // Derived from filename
        Assert.Equal("testapp", config.SystemKey);
        Assert.Equal("dev", config.Environment);

        // Deployment settings
        Assert.Equal("496a-ffff", config.SystemSuffix);
        Assert.Equal("testapp-dev", config.Profile);
        Assert.Equal("us-west-2", config.Region);
        Assert.Equal("testdev.click", config.SystemDomain);
        Assert.Equal("testdev.click", config.DefaultTenant);
        Assert.Equal("aws", config.Platform);
        Assert.Equal("ecs", config.Topology);
        Assert.Equal("10.20.0.0/16", config.VpcCidr);
        Assert.Equal("auth.meadowsservices.com", config.CentralAuthDomain);

        // State (auto-generated from SystemSuffix)
        Assert.NotNull(config.State);
        Assert.Equal("s3://testapp-dev-pulumi-state-496a-ffff?region=us-west-2", config.State!.Backend);
        Assert.Equal("awskms://alias/testapp-dev-pulumi-key-496a-ffff", config.State.SecretsProvider);

        // ECS
        Assert.NotNull(config.ECS);
        Assert.Equal(512, config.ECS!.SmartStoreCpu);
        Assert.Equal(1024, config.ECS.SmartStoreMemory);
        Assert.Equal(256, config.ECS.AppHostCpu);
        Assert.Equal(512, config.ECS.AppHostMemory);
        Assert.Equal("db.t4g.micro", config.ECS.DbInstanceClass);
        Assert.Equal("26.5.0", config.ECS.KeycloakImageTag);
        Assert.Equal(2, config.ECS.TailscaleDesiredCapacity);

        // CDN
        Assert.NotNull(config.CDN);
        Assert.Equal("PriceClass_100", config.CDN!.PriceClass);

        // Runtime settings
        Assert.Equal("adminsauth", config.AdminAuth);
        Assert.Equal("test@example.com", config.AdminEmail);
        Assert.NotNull(config.SecretsManager);
        Assert.Equal("testapp", config.SecretsManager!.SecretPrefix);

        // Integrations
        Assert.NotNull(config.Integrations);
        Assert.True(config.Integrations!.Services.ContainsKey("store"));
        Assert.Equal("shop.testdev.click", config.Integrations.Services["store"].Host);
        Assert.Equal(2, config.Integrations.Services["store"].Modules.Count);

        // AuthConfigs
        Assert.NotNull(config.AuthConfigs);
        Assert.True(config.AuthConfigs!.ContainsKey("usersauth"));
        Assert.True(config.AuthConfigs.ContainsKey("adminsauth"));

        // RequestRewriter
        Assert.NotNull(config.RequestRewriter);
        Assert.Single(config.RequestRewriter!.Rules);
        Assert.Equal("/AppApi", config.RequestRewriter.Rules[0].MatchPrefix);
    }

    [Fact]
    public void LoadTenantConfig_ParsesAllFields()
    {
        var config = ConfigLoader.LoadTenantConfig(
            TestDataPath("tenantconfig.testapp.meadows.dev.yaml"));

        // Derived from filename
        Assert.Equal("testapp", config.SystemKey);
        Assert.Equal("meadows", config.TenantKey);
        Assert.Equal("dev", config.Environment);

        // Deployment settings
        Assert.Equal("testdev.click", config.RootDomain);
        Assert.Equal("496a-aaaa", config.TenantSuffix);
        Assert.Equal("testapp-dev", config.Profile);
        Assert.Equal("us-west-2", config.Region);

        // Secrets Manager with tenant-scoped prefix
        Assert.NotNull(config.SecretsManager);
        Assert.Equal("testapp/meadows", config.SecretsManager!.SecretPrefix);

        // ECS overrides
        Assert.NotNull(config.ECS);
        Assert.Equal(512, config.ECS!.SmartStoreCpu);
        Assert.Equal(256, config.ECS.AppHostCpu);

        // CDN
        Assert.NotNull(config.CDN);
        Assert.Equal("PriceClass_100", config.CDN!.PriceClass);

        // Runtime settings present
        Assert.NotNull(config.Integrations);
        Assert.NotNull(config.AuthConfigs);
    }

    [Fact]
    public void LoadSharedConfig_ParsesAllFields()
    {
        var config = ConfigLoader.LoadSharedConfig(TestDataPath("sharedconfig.yaml"));

        // Deployment settings
        Assert.Equal("monro-shared", config.Profile);
        Assert.Equal("us-west-2", config.Region);
        Assert.Equal("monroadmin.click", config.Domain);
        Assert.Equal("10.10.0.0/16", config.VpcCidr);
        Assert.Equal("49d2-8357", config.SharedSuffix);

        // State (auto-generated from SharedSuffix)
        Assert.NotNull(config.State);
        Assert.Equal("s3://shared-pulumi-state-49d2-8357?region=us-west-2", config.State!.Backend);
        Assert.Equal("awskms://alias/shared-pulumi-key-49d2-8357", config.State.SecretsProvider);

        // Keycloak
        Assert.Equal("26.5.0", config.Keycloak.ImageTag);
        Assert.Equal(512, config.Keycloak.Cpu);
        Assert.Equal(1024, config.Keycloak.Memory);
        Assert.Equal(2, config.Keycloak.DesiredCount);

        // Infrastructure
        Assert.Equal("db.t4g.micro", config.DbInstanceClass);
        Assert.Equal(20, config.DbAllocatedStorage);
        Assert.Equal("t4g.nano", config.TailscaleInstanceType);
        Assert.Equal(2, config.TailscaleDesiredCapacity);
        Assert.Equal(3, config.LogRetentionDays);
    }

    [Fact]
    public void LoadSystemConfig_HandlesMonroNewConfigFile()
    {
        // Test against the actual Monro-New systemconfig file
        var monroConfigPath = Path.Combine(
            "..", "..", "..", "..", "..", "Monro-New", "systemconfig.med.dev.yaml");

        if (!File.Exists(monroConfigPath))
            return; // Skip if Monro-New repo not at expected relative path

        var config = ConfigLoader.LoadSystemConfig(monroConfigPath);
        Assert.Equal("med", config.SystemKey);
        Assert.Equal("dev", config.Environment);
        Assert.Equal("monro-devnew", config.Profile);
        Assert.Equal("monrodev.click", config.SystemDomain);
    }
}
