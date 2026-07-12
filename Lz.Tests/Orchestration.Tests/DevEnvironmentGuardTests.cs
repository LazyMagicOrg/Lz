namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// Unit tests for the lifecycle test's dev-only safety gate. These run in
/// the default fast suite (no Integration trait) — the guard must be proven
/// correct BEFORE any destructive drill relies on it.
/// </summary>
public class DevEnvironmentGuardTests
{
    private const string DevPath = @"C:\repos\_MagicPets\_Dev_MagicPets";

    [Fact]
    public void AllSignalsDev_NoViolations()
    {
        var v = DevEnvironmentGuard.Violations(
            "dev", "systemconfig.lzm.dev.yaml", DevPath);
        Assert.Empty(v);
    }

    [Fact]
    public void FullPathToSystemConfig_IsAccepted()
    {
        var v = DevEnvironmentGuard.Violations(
            "dev", Path.Combine(DevPath, "systemconfig.lzm.dev.yaml"), DevPath);
        Assert.Empty(v);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("prod")]
    [InlineData("staging")]
    [InlineData("")]
    [InlineData(null)]
    public void NonDevResolvedEnvironment_IsViolation(string? env)
    {
        var v = DevEnvironmentGuard.Violations(
            env, "systemconfig.lzm.dev.yaml", DevPath);
        Assert.Contains(v, m => m.StartsWith("signal 1"));
    }

    [Theory]
    [InlineData("systemconfig.lzm.test.yaml")]
    [InlineData("systemconfig.lzm.prod.yaml")]
    [InlineData("systemconfig.yaml")]              // legacy shape — no env token
    [InlineData("tenantconfig.lzm.mp.dev.yaml")]   // wrong kind of config
    [InlineData(null)]
    public void NonDevSystemConfigFileName_IsViolation(string? fileName)
    {
        var v = DevEnvironmentGuard.Violations("dev", fileName, DevPath);
        Assert.Contains(v, m => m.StartsWith("signal 2"));
    }

    [Theory]
    [InlineData(@"C:\repos\_MagicPets\_Test_MagicPets")]
    [InlineData(@"C:\repos\_MagicPets\_Prod_MagicPets")]
    [InlineData(@"C:\repos\_MagicPets\_prod_MagicPets")] // case-insensitive
    [InlineData(@"C:\repos\_Test_Anything\nested\dir")]
    public void TestOrProdWorkingCopy_IsViolation(string path)
    {
        var v = DevEnvironmentGuard.Violations(
            "dev", "systemconfig.lzm.dev.yaml", path);
        Assert.Contains(v, m => m.StartsWith("signal 3"));
    }

    [Fact]
    public void EveryViolatedSignal_IsNamed()
    {
        // prod everywhere: all three signals must independently fire.
        var v = DevEnvironmentGuard.Violations(
            "prod", "systemconfig.lzm.prod.yaml",
            @"C:\repos\_MagicPets\_Prod_MagicPets");
        Assert.Contains(v, m => m.StartsWith("signal 1"));
        Assert.Contains(v, m => m.StartsWith("signal 2"));
        Assert.Contains(v, m => m.StartsWith("signal 3"));
    }

    [Fact]
    public void SignalsAreIndependent_DevEnvDoesNotExcuseProdPath()
    {
        // --env dev typed inside a prod checkout must still be blocked.
        var v = DevEnvironmentGuard.Violations(
            "dev", "systemconfig.lzm.dev.yaml",
            @"C:\repos\_MagicPets\_Prod_MagicPets");
        Assert.Single(v);
        Assert.StartsWith("signal 3", v[0]);
    }

    [Theory]
    [InlineData("systemconfig.lzm.dev.yaml", "dev")]
    [InlineData("systemconfig.lzm.prod.yaml", "prod")]
    [InlineData(@"C:\some\dir\systemconfig.med.test.yaml", "test")]
    [InlineData("systemconfig.yaml", null)]
    [InlineData("notaconfig.lzm.dev.yaml", null)]
    [InlineData("systemconfig.lzm.dev.yml", null)]
    [InlineData(null, null)]
    public void EnvironmentTokenParsing(string? fileName, string? expected)
    {
        Assert.Equal(expected, DevEnvironmentGuard.EnvironmentTokenFromFileName(fileName));
    }
}
