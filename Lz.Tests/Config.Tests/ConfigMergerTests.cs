using Lz.Core.Config;
using Lz.Aws.Config;

namespace Lz.Tests.Config.Tests;

[Collection("ConfigLoaderStaticState")]
public class ConfigMergerTests : IDisposable
{
    public ConfigMergerTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
    }

    public void Dispose() => ConfigLoader.ResetForTests();

    private static string TestDataPath(string filename)
        => Path.Combine("Config.Tests", "testdata", filename);

    private static (SystemConfig System, TenantConfig Tenant) LoadTestConfigs()
    {
        var system = ConfigLoader.LoadSystemConfig(TestDataPath("systemconfig.testapp.dev.yaml"));
        var tenant = ConfigLoader.LoadTenantConfig(TestDataPath("tenantconfig.testapp.meadows.dev.yaml"));
        return (system, tenant);
    }

    [Fact]
    public void GetEffectiveProfile_ReturnsTenantProfile_WhenPresent()
    {
        var (system, tenant) = LoadTestConfigs();
        tenant.Profile = "tenant-override";
        var result = ConfigMerger.GetEffectiveProfile(system, tenant);
        Assert.Equal("tenant-override", result);
    }

    [Fact]
    public void GetEffectiveProfile_ReturnsSystemProfile_WhenTenantNull()
    {
        var (system, tenant) = LoadTestConfigs();
        tenant.Profile = null;
        var result = ConfigMerger.GetEffectiveProfile(system, tenant);
        Assert.Equal("testapp-dev", result);
    }

    [Fact]
    public void GetEffectiveSecretsManager_ReturnsTenantSecrets_WhenPresent()
    {
        var (system, tenant) = LoadTestConfigs();
        var result = ConfigMerger.GetEffectiveSecretsManager(system, tenant);
        Assert.Equal("testapp/meadows", result.SecretPrefix);
    }

    [Fact]
    public void GetEffectiveEcsConfig_MergesCorrectly()
    {
        var (system, tenant) = LoadTestConfigs();
        var result = AwsConfigMerger.GetEffectiveEcsConfig(system, tenant);
        Assert.Equal(512, result.SmartStoreCpu);
        Assert.Equal(1024, result.SmartStoreMemory);
        Assert.Equal(256, result.AppHostCpu);
        Assert.Equal(512, result.AppHostMemory);
    }

    [Fact]
    public void GetEffectiveCdnConfig_ReturnsTenantCdn_WhenPresent()
    {
        var (system, tenant) = LoadTestConfigs();
        var result = ConfigMerger.GetEffectiveCdnConfig(system, tenant);
        Assert.Equal("PriceClass_100", result.PriceClass);
    }

    [Fact]
    public void GetEffectiveCdnConfig_FallsBackToSystem_WhenTenantNull()
    {
        var (system, tenant) = LoadTestConfigs();
        tenant.CDN = null;
        var result = ConfigMerger.GetEffectiveCdnConfig(system, tenant);
        Assert.Equal("PriceClass_100", result.PriceClass);
        Assert.Equal("app/index.html", result.DefaultRootObject);
    }

    [Fact]
    public void GetEffectiveIntegrations_ReturnsTenant_WhenPresent()
    {
        var (system, tenant) = LoadTestConfigs();
        var result = ConfigMerger.GetEffectiveIntegrations(system, tenant);
        Assert.NotNull(result);
        Assert.True(result!.Services.ContainsKey("store"));
    }

}
