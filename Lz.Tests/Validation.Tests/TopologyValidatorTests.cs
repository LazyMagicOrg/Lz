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

/// <summary>
/// The platform-neutral TopologyValidator in Lz.Core carries no topology-
/// specific rules — those live on the platform library's topology descriptor
/// (e.g. Lz.Aws.Topologies.AwsTopology.ValidateConfig). These tests cover the
/// cross-topology contract: the validator always returns a result and never
/// throws on unknown topology strings.
/// </summary>
public class TopologyValidatorTests
{
    [Fact]
    public void Validate_ReturnsValid_ForServicesUnderKnownTopologies()
    {
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            Container = new ContainerOptions { Port = 80 },
            Volumes = { new VolumeMount("data", "/data", "/data") }
        });

        var result = TopologyValidator.Validate(system, "ecs-fargate-keycloak");
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReturnsValid_ForUnknownTopologyString()
    {
        // Platform libraries own topology-specific validation; the core
        // validator does not reject unknown topology names.
        var system = new TestSystem();
        system.AddTestService("svc", new ServiceDefinition
        {
            Container = new ContainerOptions { Port = 80 }
        });

        var result = TopologyValidator.Validate(system, "some-future-topology");
        Assert.True(result.IsValid);
    }
}
