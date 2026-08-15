using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.ServiceDiscovery;
using Lz.Aws.Auth;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.Fargate;

/// <summary>
/// ECSExpress compute — ECS cluster and Cloud Map namespace. ECR repos are
/// per-tenant and are created on first <c>lz deploycontainer</c>, not by
/// Pulumi, matching the ecs-fargate-keycloak convention.
/// </summary>
public class AwsFargateComputeComponent : ComponentResource, IComputeEnvironmentComponent
{
    public AwsFargateComputeComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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

        var networkOutputs = (AwsFargateNetworkOutputs)network;

        return new AwsFargateComputeOutputs
        {
            ClusterId = cluster.Id,
            PublicIngressEndpoint = networkOutputs.AlbDns,
            InternalIngressEndpoint = Output.Create(""), // No internal ALB
            ClusterArn = cluster.Arn,
            CloudMapNamespaceId = cloudMapNamespace.Id,
            // Private-network (opt-in) — carried through so the CDN (which only
            // receives compute outputs) can build a VPC origin to the internal ALB.
            AlbArn = networkOutputs.AlbArn,
            PrivateNetworking = networkOutputs.PrivateNetworking,
        };
    }
}
