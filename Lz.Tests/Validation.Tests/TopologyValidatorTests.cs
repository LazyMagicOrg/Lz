using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Validation;

namespace Lz.Tests.Validation.Tests;

/// <summary>
/// Minimal system definition for testing purposes.
/// </summary>
public class TestSystem : SystemDefinition
{
    public override void Define(SystemConfig config)
    {
        // Empty — tests add services directly
    }

    public void AddTestService(string name, ServiceDefinition def)
        => AddService(name, def);
}

public class TopologyValidatorTests
{
    [Fact]
    public void Validate_EcsTopology_PassesForContainerServices()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            Container = new ContainerOptions { Port = 80 },
            Volumes = { new VolumeMount("data", "/data", "/data") }
        });

        var result = TopologyValidator.Validate(system, "ecs");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_LambdaTopology_RejectsVolumes()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            Lambda = new LambdaOptions(),
            Volumes = { new VolumeMount("data", "/data", "/data") }
        });

        var result = TopologyValidator.Validate(system, "lambda");
        Assert.False(result.IsValid);
        Assert.Contains("volumes", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LambdaTopology_RejectsContainerOnlyService()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            Container = new ContainerOptions { Port = 80 }
        });

        var result = TopologyValidator.Validate(system, "lambda");
        Assert.False(result.IsValid);
        Assert.Contains("LambdaOptions", result.Errors[0]);
    }

    [Fact]
    public void Validate_LambdaTopology_RejectsInternalIngress()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            IngressType = IngressType.Internal,
            Lambda = new LambdaOptions()
        });

        var result = TopologyValidator.Validate(system, "lambda");
        Assert.False(result.IsValid);
        Assert.Contains("Internal", result.Errors[0]);
    }

    [Fact]
    public void Validate_LambdaTopology_PassesForValidLambdaService()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            IngressType = IngressType.Public,
            Lambda = new LambdaOptions { MemorySize = 512 }
        });

        var result = TopologyValidator.Validate(system, "lambda");
        Assert.True(result.IsValid);
    }
}
