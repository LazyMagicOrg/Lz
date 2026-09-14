using Lz.Aws.Ops;
using Lz.Aws.Topologies;
using Lz.Core.Config;
using Lz.Core.Definitions;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Where <c>lz updateconfig</c> may run. It writes a tenant's tenantconfig YAML to
/// <c>/{sk}/{tk}/{env}/tenantconfig</c>, the parameter the ecs-fargate-keycloak deploy writes. No other topology's deploy
/// writes it and no other topology's service reads it — Scutara's AipHost loads <c>/scu/dev/</c>, and its task role may
/// not read the tenant path (measured with the IAM simulator, 2026-09-14) — so there the command wrote a parameter nothing
/// would ever see, and said a running service would pick it up.
/// </summary>
public class TenantConfigPublisherRefusalTests
{
    private static SystemConfig Config(string topology) => new()
    {
        SystemKey = "scu",
        Environment = "dev",
        Topology = topology,
    };

    private static string? RefusalOn(string topology)
    {
        var config = Config(topology);
        return AwsTenantConfigPublisher.RefusalFor(config, AwsTopologies.Get(topology).CreateFactory(config));
    }

    [Theory]
    [InlineData("ecs-fargate-cognito-dynamodb")]
    [InlineData("lambda-cognito-dynamodb")]
    public void WhereNoDeployWritesTheParameter_UpdateConfigIsRefused(string topology)
    {
        var refusal = RefusalOn(topology);

        Assert.NotNull(refusal);
        Assert.Contains(topology, refusal);
        Assert.Contains("/scu/{tenant}/dev/tenantconfig", refusal);
        // It says where this topology's service does load configuration from, not only what not to do.
        Assert.Contains("/scu/dev/", refusal);
    }

    [Fact]
    public void OnTheKeycloakTopology_UpdateConfigRuns()
    {
        Assert.Null(RefusalOn("ecs-fargate-keycloak"));
    }

    [Fact]
    public void EachTopologysFlag_AgreesWithWhatItsDeployWrites()
    {
        // ONE FACT, TWO PLACES: the flag the refusal reads, and the post-deploy action that performs the upload. A
        // topology whose deploy starts writing the parameter without the flag would have updateconfig refused where it
        // works; one that stops would have it write a parameter nothing reads, which is the bug this replaces.
        foreach (var topology in AwsTopologies.ByName.Values)
        {
            var config = Config(topology.Name);
            var factory = topology.CreateFactory(config);
            var action = factory.GetServiceDeployAction(new NoServices(), Array.Empty<ServiceDefinition>(), "mp", new TenantConfig());

            Assert.True(action is AwsServicesPostDeployAction == factory.PublishesTenantConfigParameter,
                $"{topology.Name}: PublishesTenantConfigParameter is {factory.PublishesTenantConfigParameter}, " +
                $"but its tenant deploy action is {action?.GetType().Name ?? "none"}");
        }
    }

    private sealed class NoServices : SystemDefinition
    {
        public override void Define(SystemConfig config) { }
    }
}
