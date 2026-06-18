using Lz.Aws.Config;

namespace Lz.Tests.Config.Tests;

public class FargateValidatorTests
{
    private static FargateConfig Valid() => new()
    {
        Cpu = 512,
        Memory = 2048,
        Port = 8080,
        HealthCheckPath = "/health",
        LogRetentionDays = 7,
        DesiredCount = 1,
    };

    [Fact]
    public void Validate_AcceptsDefaultValidConfig()
    {
        var errs = new List<string>();
        FargateValidator.Validate(Valid(), errs, "t1");
        Assert.Empty(errs);
    }

    [Fact]
    public void Validate_RejectsInvalidCpuUnit()
    {
        var cfg = Valid(); cfg.Cpu = 999;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("Cpu=999") && e.Contains("Allowed:"));
    }

    [Fact]
    public void Validate_RejectsMemoryOutOfRangeForCpu()
    {
        var cfg = Valid(); cfg.Cpu = 512; cfg.Memory = 999;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("Memory=999") && e.Contains("Cpu=512"));
    }

    [Fact]
    public void Validate_RejectsMemoryOffStepForCpu()
    {
        // Cpu=1024 allows 2048..8192 in 1024 steps. 3000 is in range but off step.
        var cfg = Valid(); cfg.Cpu = 1024; cfg.Memory = 3000;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("Memory=3000"));
    }

    [Fact]
    public void Validate_AcceptsCpu256With512Mem()
    {
        var cfg = Valid(); cfg.Cpu = 256; cfg.Memory = 512;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Empty(errs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Validate_RejectsPortOutOfRange(int port)
    {
        var cfg = Valid(); cfg.Port = port;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("Port=") && e.Contains("out of range"));
    }

    [Fact]
    public void Validate_RejectsHealthCheckPathWithoutLeadingSlash()
    {
        var cfg = Valid(); cfg.HealthCheckPath = "health";
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("HealthCheckPath"));
    }

    [Fact]
    public void Validate_RejectsNegativeDesiredCount()
    {
        var cfg = Valid(); cfg.DesiredCount = -1;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("DesiredCount") && e.Contains(">= 0"));
    }

    [Fact]
    public void Validate_RejectsDesiredCountAboveSanityCap()
    {
        var cfg = Valid(); cfg.DesiredCount = 10_000;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("DesiredCount=10000") && e.Contains("sanity cap"));
    }

    [Theory]
    [InlineData(2)]   // not in the whitelist
    [InlineData(10)]  // not in the whitelist
    [InlineData(0)]
    public void Validate_RejectsInvalidCloudWatchRetentionValue(int days)
    {
        var cfg = Valid(); cfg.LogRetentionDays = days;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.Contains(errs, e => e.Contains("LogRetentionDays=") && e.Contains("CloudWatch"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(365)]
    [InlineData(3653)]
    public void Validate_AcceptsWhitelistedCloudWatchRetentionValues(int days)
    {
        var cfg = Valid(); cfg.LogRetentionDays = days;
        var errs = new List<string>();
        FargateValidator.Validate(cfg, errs, "t1");
        Assert.DoesNotContain(errs, e => e.Contains("LogRetentionDays"));
    }
}
