using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

public class AwsNamingValidatorTests
{
    [Theory]
    [InlineData("bcs")]
    [InlineData("a")]
    [InlineData("system-01")]
    [InlineData("abcdefghijklmnopqrst")] // 20 chars max
    public void ValidateKey_AcceptsValidKeys(string key)
    {
        var errs = new List<string>();
        AwsNamingValidator.ValidateKey(key, "TestKey", errs);
        Assert.Empty(errs);
    }

    [Theory]
    [InlineData("BCS")]
    [InlineData("1abc")]
    [InlineData("a_b")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("abcdefghijklmnopqrstu")] // 21 chars, over 20 max
    [InlineData("")]
    public void ValidateKey_RejectsInvalidKeys(string key)
    {
        var errs = new List<string>();
        AwsNamingValidator.ValidateKey(key, "TestKey", errs);
        Assert.NotEmpty(errs);
    }

    [Fact]
    public void ValidateSystemKeys_FlagsBothSystemAndEnvironment()
    {
        var cfg = new SystemConfig { SystemKey = "BAD", Environment = "ALSO_BAD" };
        var errs = new List<string>();
        AwsNamingValidator.ValidateSystemKeys(cfg, errs);
        Assert.Contains(errs, e => e.Contains("SystemKey"));
        Assert.Contains(errs, e => e.Contains("Environment"));
    }

    [Fact]
    public void ValidateTenantKeys_FlagsOversizeBucketName()
    {
        var sys = new SystemConfig
        {
            SystemKey = "abcdefghij",
            Environment = "dev",
            SystemSuffix = "0123456789abcd-0123456789",
        };
        var tenant = new TenantConfig
        {
            SystemKey = sys.SystemKey,
            TenantKey = "abcdefghij",
            Environment = sys.Environment,
            RootDomain = "example.com",
            Subtenants = new Dictionary<string, SubtenantEntry>
            {
                ["abcdefghij"] = new() { SubDomain = "x.example.com" },
            },
        };
        var errs = new List<string>();
        AwsNamingValidator.ValidateTenantKeys(sys, "abcdefghij", tenant, errs);
        Assert.Contains(errs, e => e.Contains("S3 limit is 63"));
    }

    [Fact]
    public void ValidateTenantKeys_FlagsBucketNameEndingInHyphen()
    {
        // Empty SystemSuffix produces '...assets-' which S3 rejects.
        var sys = new SystemConfig
        {
            SystemKey = "bcs",
            Environment = "dev",
            SystemSuffix = "",
        };
        var tenant = new TenantConfig
        {
            SystemKey = sys.SystemKey,
            TenantKey = "bcs",
            Environment = sys.Environment,
            RootDomain = "example.com",
            Subtenants = new Dictionary<string, SubtenantEntry>
            {
                ["cerulean"] = new() { SubDomain = "cerulean.example.com" },
            },
        };
        var errs = new List<string>();
        AwsNamingValidator.ValidateTenantKeys(sys, "bcs", tenant, errs);
        Assert.Contains(errs, e => e.Contains("must end with a lowercase letter or"));
    }

    [Fact]
    public void ValidateTenantKeys_AcceptsValidConfiguration()
    {
        var sys = new SystemConfig
        {
            SystemKey = "bcs",
            Environment = "dev",
            SystemSuffix = "4543-a317",
        };
        var tenant = new TenantConfig
        {
            SystemKey = sys.SystemKey,
            TenantKey = "bcs",
            Environment = sys.Environment,
            RootDomain = "example.com",
            Subtenants = new Dictionary<string, SubtenantEntry>
            {
                ["cerulean"] = new() { SubDomain = "cerulean.example.com" },
            },
        };
        var errs = new List<string>();
        AwsNamingValidator.ValidateTenantKeys(sys, "bcs", tenant, errs);
        Assert.Empty(errs);
    }
}
