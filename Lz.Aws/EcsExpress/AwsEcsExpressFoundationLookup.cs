using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // Reuse DynamoDB/FileStorage outputs
using Pulumi;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Ec2.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Looks up foundation resources for ECSExpress tenant phase.
/// VPC, ALB, ECS cluster — all created by deploysystem. ECR repos are
/// per-tenant and imperatively created by <c>lz deploycontainer</c>, so they
/// aren't looked up here.
/// </summary>
public static class AwsEcsExpressFoundationLookup
{
    public static (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        Lookup(SystemConfig config)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var prefix = $"{sk}-{env}";

        // VPC
        var vpc = GetVpc.Invoke(new GetVpcInvokeArgs
        {
            Filters = new[] { new GetVpcFilterInputArgs { Name = "tag:Name", Values = new[] { $"{sk}-vpc" } } },
        });

        // Public subnets
        var publicSubnets = GetSubnets.Invoke(new GetSubnetsInvokeArgs
        {
            Filters = new[]
            {
                new GetSubnetsFilterInputArgs { Name = "tag:System", Values = new[] { sk } },
                new GetSubnetsFilterInputArgs { Name = "map-public-ip-on-launch", Values = new[] { "true" } },
            },
        });

        // ALB
        var alb = Pulumi.Aws.LB.GetLoadBalancer.Invoke(new GetLoadBalancerInvokeArgs
        {
            Tags = { { "Name", $"{sk}-alb" } },
        });

        // HTTPS listener
        // We need the listener ARN — look it up via ALB ARN
        // For now, pass it through Pulumi stack outputs

        // Security groups
        var albSg = GetSecurityGroup.Invoke(new GetSecurityGroupInvokeArgs
        {
            Filters = new[] { new GetSecurityGroupFilterInputArgs { Name = "tag:Name", Values = new[] { $"{sk}-alb-sg" } } },
        });

        var ecsSg = GetSecurityGroup.Invoke(new GetSecurityGroupInvokeArgs
        {
            Filters = new[] { new GetSecurityGroupFilterInputArgs { Name = "tag:Name", Values = new[] { $"{sk}-ecs-sg" } } },
        });

        // Route 53
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = config.SystemDomain });

        // ECS cluster
        var cluster = Pulumi.Aws.Ecs.GetCluster.Invoke(new Pulumi.Aws.Ecs.GetClusterInvokeArgs
        {
            ClusterName = $"{prefix}-cluster",
        });

        // Look up HTTPS listener (port 443) on the ALB
        var httpsListener = alb.Apply(a => GetListener.InvokeAsync(new GetListenerArgs
        {
            LoadBalancerArn = a.Arn,
            Port = 443,
        }));

        var network = new AwsEcsExpressNetworkOutputs
        {
            NetworkId = vpc.Apply(v => v.Id),
            PrivateSubnetIds = Output.Create(ImmutableArray<string>.Empty),
            PublicSubnetIds = publicSubnets.Apply(s => s.Ids.ToImmutableArray()),
            PrivateDnsZoneId = Output.Create(""),
            PublicDnsZoneId = publicZone.Apply(z => z.ZoneId),
            AlbArn = alb.Apply(a => a.Arn),
            AlbDns = alb.Apply(a => a.DnsName),
            AlbZoneId = alb.Apply(a => a.ZoneId),
            HttpsListenerArn = httpsListener.Apply(l => l.Arn),
            AlbSecurityGroupId = albSg.Apply(s => s.Id),
            EcsTaskSecurityGroupId = ecsSg.Apply(s => s.Id),
            CertificateArn = Output.Create(""),
        };

        var compute = new AwsEcsExpressComputeOutputs
        {
            ClusterId = cluster.Apply(c => c.Id),
            PublicIngressEndpoint = alb.Apply(a => a.DnsName),
            InternalIngressEndpoint = Output.Create(""),
            ClusterArn = cluster.Apply(c => c.Arn),
            CloudMapNamespaceId = Output.Create(""),
        };

        var tableArnPrefix = Pulumi.Aws.GetCallerIdentity.Invoke(new Pulumi.Aws.GetCallerIdentityInvokeArgs())
            .Apply(id => $"arn:aws:dynamodb:{config.Region}:{id.AccountId}:table/{sk}-{suffix}-{env}-*");

        var database = new AwsAppRunnerDatabaseOutputs
        {
            Endpoint = Output.Create($"dynamodb.{config.Region}.amazonaws.com"),
            Port = Output.Create(443),
            AdminSecretId = Output.Create(""),
            TableArnPrefix = tableArnPrefix,
        };

        var fileStorage = new AwsAppRunnerFileStorageOutputs { FileSystemId = Output.Create("") };

        return (network, compute, database, fileStorage);
    }
}
