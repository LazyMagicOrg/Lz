using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

public class ConfigValidatorTests
{
    private static SystemConfig ValidBase() => new()
    {
        SystemKey = "med", Environment = "dev", Profile = "p", Region = "us-west-2", SystemSuffix = "abcd-1234",
    };

    [Fact]
    public void BucketVersioning_WithoutANoncurrentWindow_IsRefused()
    {
        // Versioned content buckets are republished in full on every deploy; without an expiry
        // window each deploy leaves a whole superseded bundle behind, forever.
        var config = ValidBase();
        config.Durability = new DurabilityConfig { BucketVersioning = true };
        config.Hygiene = new HygieneConfig { EcrUntaggedImageRetentionDays = 7 }; // window NOT set

        var ex = Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(config, "test.yaml"));

        Assert.Contains("S3NoncurrentVersionExpirationDays", ex.Message);
    }

    [Fact]
    public void BucketVersioning_WithAWindow_IsAccepted()
    {
        var config = ValidBase();
        config.Durability = new DurabilityConfig { BucketVersioning = true };
        config.Hygiene = new HygieneConfig { S3NoncurrentVersionExpirationDays = 30 };

        ConfigValidator.Validate(config, "test.yaml"); // no throw
    }

    [Fact]
    public void BucketVersioningOff_NeedsNoWindow()
    {
        // The refusal is about the combination, not about Hygiene being absent: a system that
        // has not opted in must validate exactly as it did before the flag existed.
        var config = ValidBase();
        config.Durability = new DurabilityConfig { DeletionProtection = true };

        ConfigValidator.Validate(config, "test.yaml"); // no throw
    }

    /// <summary>
    /// INVERTED 2026-09-11. This test used to assert that a missing Profile was a validation
    /// error, which was right while lz only ever ran on a workstation with an SSO profile. The
    /// decoupled-CD deployer runs inside the target account on a task role and has no profile at
    /// all (Docs/specs/DecoupledCd.md §8 item 2), so an empty Profile is now the AMBIENT case and
    /// an ordinary supported state. It is kept rather than deleted because the assertion it makes
    /// is still load-bearing — just in the other direction.
    /// </summary>
    [Fact]
    public void Validate_SystemConfig_AllowsAMissingProfile()
    {
        var config = new SystemConfig
        {
            SystemKey = "med",
            Environment = "dev",
            // Profile intentionally empty — ambient credentials
            Region = "us-west-2",
            VpcCidr = "10.20.0.0/16",
            SystemSuffix = "496a-ffff",
            CentralAuthDomain = "auth.test.click",
        };

        ConfigValidator.Validate(config, "test.yaml"); // no throw
    }

    [Fact]
    public void Validate_SystemConfig_ThrowsOnMultipleMissingFields()
    {
        var config = new SystemConfig
        {
            SystemKey = "med",
            Environment = "dev",
            // Region empty; Profile is deliberately NOT required any more (see above)
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigValidator.Validate(config, "test.yaml"));
        Assert.Contains("Region", ex.Message);
        Assert.DoesNotContain("Profile", ex.Message);
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
