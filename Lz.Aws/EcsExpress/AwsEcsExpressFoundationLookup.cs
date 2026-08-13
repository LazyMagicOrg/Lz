using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Aws.Config;
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

        // Opt-in private networking (Phase 1). When on, the deploysystem phase
        // created private subnets + an internal ALB; reflect that here so the
        // tenant phase places tasks privately and CloudFront builds a VPC origin.
        var privateNet = config.Aws().PrivateNetwork is { Enabled: true };
        var tailscaleNet = config.Aws().PrivateNetwork is { Enabled: true, Tailscale: true };

        // Tailscale SG — looked up by tag:Name={sk}-tailscale-sg when the flag is
        // on (created by deploysystem), empty otherwise (byte-identical lookup).
        var tailscaleSgId = tailscaleNet
            ? GetSecurityGroup.Invoke(new GetSecurityGroupInvokeArgs
              {
                  Filters = new[] { new GetSecurityGroupFilterInputArgs { Name = "tag:Name", Values = new[] { $"{sk}-tailscale-sg" } } },
              }).Apply(s => s.Id)
            : Output.Create("");

        // Public subnets
        var publicSubnets = GetSubnets.Invoke(new GetSubnetsInvokeArgs
        {
            Filters = new[]
            {
                new GetSubnetsFilterInputArgs { Name = "tag:System", Values = new[] { sk } },
                new GetSubnetsFilterInputArgs { Name = "map-public-ip-on-launch", Values = new[] { "true" } },
            },
        });

        // Private subnets — only when the opt-in flag is on. Distinguished from
        // the public subnets by map-public-ip-on-launch=false. Empty otherwise
        // (byte-identical to the pre-hardening lookup).
        var privateSubnetIds = privateNet
            ? GetSubnets.Invoke(new GetSubnetsInvokeArgs
              {
                  Filters = new[]
                  {
                      new GetSubnetsFilterInputArgs { Name = "tag:System", Values = new[] { sk } },
                      new GetSubnetsFilterInputArgs { Name = "map-public-ip-on-launch", Values = new[] { "false" } },
                  },
              }).Apply(s => s.Ids.ToImmutableArray())
            : Output.Create(ImmutableArray<string>.Empty);

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
            PrivateSubnetIds = privateSubnetIds,
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
            PrivateNetworking = privateNet,
            TailscaleSecurityGroupId = tailscaleSgId,
        };

        var compute = new AwsEcsExpressComputeOutputs
        {
            ClusterId = cluster.Apply(c => c.Id),
            PublicIngressEndpoint = alb.Apply(a => a.DnsName),
            InternalIngressEndpoint = Output.Create(""),
            ClusterArn = cluster.Apply(c => c.Arn),
            CloudMapNamespaceId = Output.Create(""),
            AlbArn = alb.Apply(a => a.Arn),
            PrivateNetworking = privateNet,
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
