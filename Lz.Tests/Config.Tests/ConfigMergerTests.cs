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

    // ─── ResolveWebApps cascade ──────────────────────────────────────────────

    private static SystemConfig SystemWithWebApps(params WebAppBehavior[] apps)
        => new AwsSystemConfig
        {
            Platform = "aws",
            Behaviors = new BehaviorsConfig { WebApps = apps.ToList() },
        };

    private static TenantConfig TenantWithWebApps(params WebAppBehavior[] apps)
        => new AwsTenantConfig
        {
            Behaviors = new BehaviorsConfig { WebApps = apps.ToList() },
        };

    private static BehaviorsConfig SubtenantWithWebApps(params WebAppBehavior[] apps)
        => new BehaviorsConfig { WebApps = apps.ToList() };

    [Fact]
    public void ResolveWebApps_SystemOnly_ReturnsSystemEntries()
    {
        var system = SystemWithWebApps(
            new WebAppBehavior { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" });
        var resolved = ConfigMerger.ResolveWebApps(system, tenant: null, subtenantBehaviors: null);

        var app = Assert.Single(resolved);
        Assert.Equal("/", app.Path);
        Assert.Equal("eventit", app.AppName);
        Assert.Equal("plannerauth", app.AuthConfig);
        Assert.Equal(0, app.Level);
    }

    [Fact]
    public void ResolveWebApps_TenantOverridesAuthConfig_PreservesAppNameAndLevel()
    {
        var system = SystemWithWebApps(
            new WebAppBehavior { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" });
        var tenant = TenantWithWebApps(
            new WebAppBehavior { Path = "/", AuthConfig = "tenantauth" });

        var resolved = ConfigMerger.ResolveWebApps(system, tenant, subtenantBehaviors: null);

        var app = Assert.Single(resolved);
        Assert.Equal("eventit", app.AppName);
        Assert.Equal("tenantauth", app.AuthConfig);
        Assert.Equal(0, app.Level); // bucket stays at system level
    }

    [Fact]
    public void ResolveWebApps_SubtenantOverridesTenantOverridesSystem()
    {
        var system = SystemWithWebApps(
            new WebAppBehavior { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" });
        var tenant = TenantWithWebApps(
            new WebAppBehavior { Path = "/", AuthConfig = "tenantauth" });
        var subtenant = SubtenantWithWebApps(
            new WebAppBehavior { Path = "/", AuthConfig = "systemauth" });

        var resolved = ConfigMerger.ResolveWebApps(system, tenant, subtenant);

        var app = Assert.Single(resolved);
        Assert.Equal("systemauth", app.AuthConfig);
        Assert.Equal("eventit", app.AppName);
        Assert.Equal(0, app.Level);
    }

    [Fact]
    public void ResolveWebApps_NullAuthConfigInherits_EmptyStringOverridesToPublic()
    {
        var system = SystemWithWebApps(
            new WebAppBehavior { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" });

        // Tenant declares Path with AuthConfig=null → inherit from system
        var tenantInherit = TenantWithWebApps(
            new WebAppBehavior { Path = "/", AuthConfig = null });
        var inherited = ConfigMerger.ResolveWebApps(system, tenantInherit, null);
        Assert.Equal("plannerauth", Assert.Single(inherited).AuthConfig);

        // Tenant declares Path with AuthConfig="" → explicit public
        var tenantPublic = TenantWithWebApps(
            new WebAppBehavior { Path = "/", AuthConfig = "" });
        var madePublic = ConfigMerger.ResolveWebApps(system, tenantPublic, null);
        Assert.Equal("", Assert.Single(madePublic).AuthConfig);
    }

    [Fact]
    public void ResolveWebApps_SubtenantDeclaresOwnPath_LevelIsSubtenant()
    {
        var system = SystemWithWebApps(
            new WebAppBehavior { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" });
        var subtenant = SubtenantWithWebApps(
            new WebAppBehavior { Path = "/admin/", AppName = "admin", AuthConfig = "systemauth" });

        var resolved = ConfigMerger.ResolveWebApps(system, tenant: null, subtenant);

        Assert.Equal(2, resolved.Count);
        var rootApp = resolved.Single(a => a.Path == "/");
        var adminApp = resolved.Single(a => a.Path == "/admin/");
        Assert.Equal(0, rootApp.Level);
        Assert.Equal(2, adminApp.Level); // bucket lives at subtenant level
        Assert.Equal("admin", adminApp.AppName);
    }
}
