using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.ServiceDiscovery;
using Pulumi.Aws.ServiceDiscovery.Inputs;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
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

namespace Lz.Aws.Compute.FargateAlb;

/// <summary>
/// AWS ECS cluster and Cloud Map namespace component.
/// </summary>
public class AwsFargateAlbClusterComponent : ComponentResource, IComputeEnvironmentComponent
{
    public AwsFargateAlbClusterComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
        : base("lz:aws:EcsCluster", "cluster", ResourceArgs.Empty, null)
    {
    }

    public IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsFargateAlbNetworkOutputs)network;

        // ECS Cluster
        var cluster = new Cluster($"{prefix}-cluster", new ClusterArgs
        {
            Name = $"{prefix}-cluster",
            Settings =
            {
                new ClusterSettingArgs { Name = "containerInsights", Value = "enabled" },
            },
            Configuration = new ClusterConfigurationArgs
            {
                ExecuteCommandConfiguration = new ClusterConfigurationExecuteCommandConfigurationArgs
                {
                    Logging = "DEFAULT",
                },
            },
            Tags =
            {
                { "Name", $"{prefix}-cluster" },
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // Cloud Map Private DNS Namespace
        var cloudMap = new PrivateDnsNamespace($"{prefix}-cloudmap", new PrivateDnsNamespaceArgs
        {
            Name = $"{prefix}.internal",
            Description = "Private DNS namespace for ECS service discovery",
            Vpc = network.NetworkId,
            Tags =
            {
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        return new AwsFargateAlbComputeOutputs
        {
            ClusterId = cluster.Id,
            ClusterArn = cluster.Arn,
            PublicIngressEndpoint = awsNetwork.PublicAlbDns,
            InternalIngressEndpoint = awsNetwork.InternalAlbDns,
            CloudMapNamespaceId = cloudMap.Id,
            CloudMapNamespaceArn = cloudMap.Arn,
        };
    }
}
