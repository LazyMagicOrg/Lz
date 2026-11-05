using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// DecoupledCd P-10 (P3 stage A): <c>lz</c> on a role's credentials while the config names a profile. The override is
/// <c>LZ_CREDENTIALS=ambient</c>; under it every loaded config's <c>Profile</c> is empty - which every site already reads as
/// "the ambient chain" - and a system config must declare the <c>AccountId</c> those credentials belong to, since nothing
/// else ties ambient credentials to one account.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class CredentialOverrideTests : IDisposable
{
    private readonly string _dir;

    public CredentialOverrideTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _dir = Path.Combine(Path.GetTempPath(), "lz-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("ambient", true)]
    [InlineData(" Ambient ", true)]
    public void Parse_ReadsAmbientOrNothing(string? value, bool ambient)
        => Assert.Equal(ambient, CredentialOverride.Parse(value));

    [Theory]
    [InlineData("profile")]
    [InlineData("scu-dev")]
    [InlineData("true")]
    public void Parse_RefusesAnythingElse(string value)
    {
        // A misspelt override must not quietly run on the configured profile, the opposite of what was asked.
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialOverride.Parse(value));
        Assert.Contains(CredentialOverride.Variable, ex.Message);
    }

    [Fact]
    public void WithoutTheOverride_TheProfileIsKept()
    {
        var config = ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));
        Assert.Equal("scu-dev", config.Profile);
    }

    [Fact]
    public void UnderTheOverride_TheSystemProfileIsEmpty_WhenTheAccountMatches()
    {
        Ambient("111111111111");
        var config = ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));
        Assert.Equal(string.Empty, config.Profile);
    }

    [Fact]
    public void UnderTheOverride_TheTenantProfileIsEmpty_SoTheEffectiveProfileIsAmbient()
    {
        Ambient("111111111111");
        var system = ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));
        var tenant = ConfigLoader.LoadTenantConfig(WriteTenant());
        Assert.Equal(string.Empty, tenant.Profile);
        Assert.Equal(string.Empty, ConfigMerger.GetEffectiveProfile(system, tenant));
    }

    [Fact]
    public void UnderTheOverride_ADifferentAccountIsRefused()
    {
        Ambient("222222222222");
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111")));
        Assert.Contains("111111111111", ex.Message);
        Assert.Contains("222222222222", ex.Message);
    }

    [Fact]
    public void UnderTheOverride_AConfigWithNoAccountIdIsRefused()
    {
        Ambient("111111111111");
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(WriteSystem(accountId: null)));
        Assert.Contains("AccountId", ex.Message);
    }

    [Fact]
    public void UnderTheOverride_AnUnresolvedAmbientAccountIsRefused()
    {
        // The CLI resolves it at startup; a host that switched the override on without resolving it has no account to compare.
        CredentialOverride.ReadForTests = () => "ambient";
        CredentialOverride.AmbientAccountId = null;
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111")));
    }

    [Fact]
    public void UnderTheOverride_TheAccountIsResolvedOnFirstUse_AndOnce()
    {
        CredentialOverride.ReadForTests = () => "ambient";
        var calls = 0;
        CredentialOverride.ResolveAmbientAccount = () => { calls++; return "111111111111"; };

        ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));
        ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void WithoutTheOverride_NothingIsResolved()
    {
        // --help, and every workstation run, must make no STS call.
        var calls = 0;
        CredentialOverride.ResolveAmbientAccount = () => { calls++; return "111111111111"; };
        ConfigLoader.LoadSystemConfig(WriteSystem(accountId: "111111111111"));
        Assert.Equal(0, calls);
    }

    private static void Ambient(string account)
    {
        CredentialOverride.ReadForTests = () => "ambient";
        CredentialOverride.AmbientAccountId = account;
    }

    private string WriteSystem(string? accountId)
    {
        var path = Path.Combine(_dir, "systemconfig.tst.dev.yaml");
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "Profile: scu-dev",
            accountId is null ? "" : $"AccountId: \"{accountId}\"",
            "Region: us-west-2",
            "Platform: aws",
            "Topology: lambda-cognito-dynamodb",
            "SystemDomain: example.test",
            "SystemSuffix: ab12-cd34",
            "AdminEmail: admin@example.test",
            "VpcCidr: 10.0.0.0/16",
        }));
        return path;
    }

    private string WriteTenant()
    {
        var path = Path.Combine(_dir, "tenantconfig.tst.mp.dev.yaml");
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "RootDomain: example.test",
            "TenantSuffix: ab12-cd34",
            "Profile: scu-dev",
        }));
        return path;
    }
}
