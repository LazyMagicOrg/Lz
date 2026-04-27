using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.ServiceDiscovery;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// ECSExpress compute — ECS cluster and Cloud Map namespace. ECR repos are
/// per-tenant and are created on first <c>lz deploycontainer</c>, not by
/// Pulumi, matching the ecs-fargate-keycloak convention.
/// </summary>
public class AwsEcsExpressComputeComponent : ComponentResource, IComputeEnvironmentComponent
{
    public AwsEcsExpressComputeComponent()
        : base("lz:aws:EcsExpressCompute", "compute", ResourceArgs.Empty, null)
    {
    }

    public IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var prefix = $"{sk}-{env}";

        // =====================================================================
        // ECS CLUSTER
        // =====================================================================

        var cluster = new Cluster($"{prefix}-cluster", new ClusterArgs
        {
            Name = $"{prefix}-cluster",
            Settings =
            {
                new ClusterSettingArgs { Name = "containerInsights", Value = "enabled" },
            },
            Tags =
            {
                { "System", sk },
                { "Environment", env },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // CLOUD MAP NAMESPACE (service discovery)
        // =====================================================================

        var cloudMapNamespace = new PrivateDnsNamespace($"{prefix}-namespace",
            new PrivateDnsNamespaceArgs
            {
                Name = $"{sk}.internal",
                Vpc = network.NetworkId,
                Description = $"Service discovery for {prefix}",
                Tags =
                {
                    { "System", sk },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, new CustomResourceOptions { Parent = this });

        var networkOutputs = (AwsEcsExpressNetworkOutputs)network;

        return new AwsEcsExpressComputeOutputs
        {
            ClusterId = cluster.Id,
            PublicIngressEndpoint = networkOutputs.AlbDns,
            InternalIngressEndpoint = Output.Create(""), // No internal ALB
            ClusterArn = cluster.Arn,
            CloudMapNamespaceId = cloudMapNamespace.Id,
        };
    }
}
