using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

public class ConfigValidatorTests
{
    [Fact]
    public void Validate_SystemConfig_ThrowsOnMissingProfile()
    {
        var config = new SystemConfig
        {
            SystemKey = "med",
            Environment = "dev",
            // Profile intentionally empty
            Region = "us-west-2",
            VpcCidr = "10.20.0.0/16",
            SystemSuffix = "496a-ffff",
            CentralAuthDomain = "auth.test.click",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(config, "test.yaml"));
        Assert.Contains("Profile", ex.Message);
    }

    [Fact]
    public void Validate_SystemConfig_ThrowsOnMultipleMissingFields()
    {
        var config = new SystemConfig
        {
            SystemKey = "med",
            Environment = "dev",
            // Profile, Region all empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(config, "test.yaml"));
        Assert.Contains("Profile", ex.Message);
        Assert.Contains("Region", ex.Message);
    }

    [Fact]
    public void Validate_SystemConfig_PassesWithAllFields()
    {
        var config = new SystemConfig
        {
            SystemKey = "med",
            Environment = "dev",
            Profile = "monro-dev2",
            Region = "us-west-2",
            VpcCidr = "10.20.0.0/16",
            SystemSuffix = "496a-ffff",
            CentralAuthDomain = "auth.test.click",
        };

        // Should not throw
        ConfigValidator.Validate(config, "test.yaml");
    }

    [Fact]
    public void Validate_SharedConfig_ThrowsOnMissingDomain()
    {
        var config = new SharedConfig
        {
            Profile = "shared-profile",
            Region = "us-west-2",
            VpcCidr = "10.10.0.0/16",
            SharedSuffix = "49d2-8357",
            // Domain intentionally empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(config, "test.yaml"));
        Assert.Contains("Domain", ex.Message);
    }

    [Fact]
    public void Validate_TenantConfig_ThrowsOnMissingRootDomain()
    {
        var config = new TenantConfig
        {
            SystemKey = "med",
            TenantKey = "meadows",
            Environment = "dev",
            // RootDomain intentionally empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(config, "test.yaml"));
        Assert.Contains("RootDomain", ex.Message);
    }

    [Fact]
    public void Validate_TenantConfig_PassesWithAllFields()
    {
        var config = new TenantConfig
        {
            SystemKey = "med",
            TenantKey = "meadows",
            Environment = "dev",
            RootDomain = "test.click",
        };

        // Should not throw
        ConfigValidator.Validate(config, "test.yaml");
    }
}
