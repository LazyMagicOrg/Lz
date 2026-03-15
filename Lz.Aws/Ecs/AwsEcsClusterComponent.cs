using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.ServiceDiscovery;
using Pulumi.Aws.ServiceDiscovery.Inputs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS ECS cluster and Cloud Map namespace component.
/// </summary>
public class AwsEcsClusterComponent : ComponentResource, IComputeEnvironmentComponent
{
    public AwsEcsClusterComponent()
        : base("lz:aws:EcsCluster", "cluster", ResourceArgs.Empty, null)
    {
    }

    public IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsNetworkOutputs)network;

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

        return new AwsComputeOutputs
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
