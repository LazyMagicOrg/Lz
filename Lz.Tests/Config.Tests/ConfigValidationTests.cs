using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// <c>lz validateconfig</c> (DecoupledCd.md P3 C, owner 2026-11-06): every system, tenant and subtenant config of one environment
/// loaded and validated the way a deploy loads them, with no AWS call - Gate 1's check of the config bundle, run in the tooling
/// image at the Lz version the bundle pins.
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class ConfigValidationTests : IDisposable
{
    private readonly string _dir;

    public ConfigValidationTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _dir = Path.Combine(Path.GetTempPath(), "lz-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string name, params string[] lines) => File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    private void ASoundEnvironment(string env = "dev")
    {
        Write($"systemconfig.tst.{env}.yaml", "Profile: scu-dev", "Region: us-west-2", "Platform: aws", "Topology: lambda-cognito-dynamodb",
            "SystemDomain: example.test", "SystemSuffix: ab12-cd34", "AdminEmail: admin@example.test", "VpcCidr: 10.0.0.0/16");
        Write($"tenantconfig.tst.mp.{env}.yaml", "RootDomain: example.test", "TenantSuffix: ab12-cd34");
        Write($"subtenantconfig.tst.mp.{env}.yaml", "Subtenants:", "  match:", "    SubDomain: match");
    }

    [Fact]
    public void ASoundEnvironment_LoadsEveryFile_AndSaysWhich()
    {
        ASoundEnvironment();

        var loaded = ConfigValidation.ValidateEnvironment(_dir, "dev");

        Assert.Equal(new[] { "systemconfig.tst.dev.yaml", "tenantconfig.tst.mp.dev.yaml (1 subtenant)" }, loaded);
    }

    [Fact]
    public void OnlyTheNamedEnvironment_IsLoaded()
    {
        ASoundEnvironment("dev");
        ASoundEnvironment("test");

        Assert.All(ConfigValidation.ValidateEnvironment(_dir, "test"), line => Assert.Contains(".test.yaml", line));
    }

    [Fact]
    public void AnUnsoundTenant_FailsTheEnvironment()
    {
        // Inline Subtenants beside a sibling subtenantconfig file: the loader refuses it, as a deploy would.
        ASoundEnvironment();
        Write("tenantconfig.tst.mp.dev.yaml", "RootDomain: example.test", "TenantSuffix: ab12-cd34", "Subtenants:", "  other:", "    SubDomain: other");

        Assert.Throws<InvalidOperationException>(() => ConfigValidation.ValidateEnvironment(_dir, "dev"));
    }

    [Fact]
    public void AnEnvironmentWithNoSystemConfig_IsAnError_NotAnEmptyPass()
        => Assert.Throws<FileNotFoundException>(() => ConfigValidation.ValidateEnvironment(_dir, "prod"));

    [Fact]
    public void TheEnvironmentsPresent_AreThoseWithASystemConfig()
    {
        ASoundEnvironment("dev");
        ASoundEnvironment("test");
        Write("pipelineconfig.tst.yaml", "BuildAccountId: \"111111111111\"");

        Assert.Equal(new[] { "dev", "test" }, ConfigValidation.EnvironmentsIn(_dir));
    }
}
