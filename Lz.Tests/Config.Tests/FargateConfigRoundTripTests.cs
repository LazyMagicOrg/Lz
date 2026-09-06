using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// That a <c>Fargate:</c> block in YAML actually reaches <see cref="FargateConfig"/>.
///
/// <para>Worth its own test because the failure is silent in both directions. A key the
/// deserialiser does not recognise is simply absent — no warning, no error — and the knob it was
/// meant to set then reads as null, which for
/// <see cref="FargateConfig.RollbackOnTarget5xxPerMinute"/> means "no alarm" and for the sizing
/// fields means "class default". That is the same shape as the plumbing bug fixed on 2026-09-06,
/// where the block was read by nothing and nobody noticed for as long as its values happened to
/// equal the defaults.</para>
///
/// <para>So the assertions below deliberately use values that DIFFER from every class default.
/// Asserting <c>Cpu == 1024</c> would pass whether or not the YAML was read at all.</para>
/// </summary>
[Collection("ConfigLoaderStaticState")]
public class FargateConfigRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public FargateConfigRoundTripTests()
    {
        ConfigLoader.ResetForTests();
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
        _tempDir = Path.Combine(Path.GetTempPath(), "lz-fargate-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ConfigLoader.ResetForTests();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSystemConfig(string fargateSection)
    {
        var yaml =
            "Platform: aws\n" +
            "Topology: lambda-cognito-dynamodb\n" +
            "SystemSuffix: test\n" +
            "Profile: dummy\n" +
            "Region: us-west-2\n" +
            fargateSection + "\n";
        var path = Path.Combine(_tempDir, "systemconfig.t.dev.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void TheFargateBlockDeserialisesWithEveryFieldDistinctFromItsDefault()
    {
        var path = WriteSystemConfig("""
            Fargate:
              Cpu: 2048
              Memory: 4096
              Port: 9090
              HealthCheckPath: /healthz
              LogRetentionDays: 7
              DesiredCount: 3
              RollbackOnTarget5xxPerMinute: 5
            """);

        var fargate = ((AwsSystemConfig)ConfigLoader.LoadSystemConfig(path)).Fargate;

        Assert.NotNull(fargate);
        Assert.Equal(2048, fargate!.Cpu);
        Assert.Equal(4096, fargate.Memory);
        Assert.Equal(9090, fargate.Port);
        Assert.Equal("/healthz", fargate.HealthCheckPath);
        Assert.Equal(7, fargate.LogRetentionDays);
        Assert.Equal(3, fargate.DesiredCount);
        Assert.Equal(5, fargate.RollbackOnTarget5xxPerMinute);
    }

    [Fact]
    public void AFargateBlockWithoutTheAlarmKnobLeavesItNull_NotZero()
    {
        // Null and 0 are different answers: 0 is a real threshold meaning "any 5xx", null is off.
        // A deserialiser that defaulted the int? to 0 would arm the strictest possible rollback on
        // every system that never asked for one.
        var path = WriteSystemConfig("""
            Fargate:
              Cpu: 1024
            """);

        var fargate = ((AwsSystemConfig)ConfigLoader.LoadSystemConfig(path)).Fargate;

        Assert.NotNull(fargate);
        Assert.Null(fargate!.RollbackOnTarget5xxPerMinute);
    }

    [Fact]
    public void NoFargateBlockAtAllLeavesTheSectionNull()
    {
        var path = WriteSystemConfig("SystemKey: t");

        Assert.Null(((AwsSystemConfig)ConfigLoader.LoadSystemConfig(path)).Fargate);
    }
}
