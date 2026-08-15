using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Shared;
using Pulumi;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Efs;
using Pulumi.Aws.LB;
using Pulumi.Aws.SecretsManager;
using Pulumi.Aws.ServiceDiscovery;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Topologies;

/// <summary>
/// Looks up existing foundation resources (created by deploysystem) using
/// Pulumi AWS data-source invocations. Returns the same typed output interfaces
/// so tenant components can use them transparently.
///
/// This avoids re-declaring foundation resources in the tenant Pulumi stack,
/// which would conflict with the foundation stack that already owns them.
/// </summary>
public static class AwsEcsFargateKeycloakFoundationLookup
{
    public static (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        Lookup(SystemConfig config)
    {
        var prefix = config.SystemKey;
        var region = config.Region;

        // CentralAuthDomain zone may be in a different account (shared-services)
        Pulumi.Aws.Provider? sharedProvider = null;
        if (!string.IsNullOrEmpty(config.Aws().SharedProfile))
        {
            sharedProvider = new Pulumi.Aws.Provider($"lookup-shared-provider", new Pulumi.Aws.ProviderArgs
            {
                Region = config.Aws().SharedRegion ?? config.Region,
                Profile = config.Aws().SharedProfile,
            });
        }

        var network = LookupNetwork(prefix, config.CentralAuthDomain, config.SystemKey, sharedProvider);
        var compute = LookupCompute(prefix);
        var database = LookupDatabase(prefix);
        var fileStorage = LookupFileStorage(prefix, config.Environment);

        return (network, compute, database, fileStorage);
    }

    private static AwsFargateAlbNetworkOutputs LookupNetwork(
        string prefix, string centralAuthDomain, string systemKey,
        Pulumi.Aws.Provider? sharedProvider)
    {
        // VPC
        var vpc = GetVpc.Invoke(new GetVpcInvokeArgs
        {
            Filters = new[]
            {
                new Pulumi.Aws.Ec2.Inputs.GetVpcFilterInputArgs
                {
                    Name = "tag:Name",
                    Values = new[] { $"{prefix}-vpc" },
                },
            },
        });

        var vpcId = vpc.Apply(v => v.Id);

        // Subnets — use vpcId (Output<string>) in InputArgs which accept Input<string>
        var publicSubnets = GetSubnets.Invoke(new GetSubnetsInvokeArgs
        {
            Filters = new[]
            {
                new Pulumi.Aws.Ec2.Inputs.GetSubnetsFilterInputArgs
                {
                    Name = "tag:Name",
                    Values = new[] { $"{prefix}-public-*" },
                },
                new Pulumi.Aws.Ec2.Inputs.GetSubnetsFilterInputArgs
                {
                    Name = "vpc-id",
                    Values = new InputList<string> { vpcId },
                },
            },
        });

        var privateSubnets = GetSubnets.Invoke(new GetSubnetsInvokeArgs
        {
            Filters = new[]
            {
                new Pulumi.Aws.Ec2.Inputs.GetSubnetsFilterInputArgs
                {
                    Name = "tag:Name",
                    Values = new[] { $"{prefix}-private-*" },
                },
                new Pulumi.Aws.Ec2.Inputs.GetSubnetsFilterInputArgs
                {
                    Name = "vpc-id",
                    Values = new InputList<string> { vpcId },
                },
            },
        });

        // ALBs
        var publicAlb = GetLoadBalancer.Invoke(new GetLoadBalancerInvokeArgs
        {
            Name = $"{prefix}-alb",
        });

        var internalAlb = GetLoadBalancer.Invoke(new GetLoadBalancerInvokeArgs
        {
            Name = $"{prefix}-internal-alb",
        });

        // HTTPS Listeners (port 443 on each ALB)
        // Use Invoke (not InvokeAsync) so we can pass Input<string> for LoadBalancerArn
        var httpsListener = GetListener.Invoke(new GetListenerInvokeArgs
        {
            LoadBalancerArn = publicAlb.Apply(a => a.Arn),
            Port = 443,
        });

        var internalHttpsListener = GetListener.Invoke(new GetListenerInvokeArgs
        {
            LoadBalancerArn = internalAlb.Apply(a => a.Arn),
            Port = 443,
        });

        // Security Groups
        var ecsPublicSg = LookupSecurityGroup(prefix, $"{prefix}-ecs-public-sg", vpcId);
        var ecsPrivateSg = LookupSecurityGroup(prefix, $"{prefix}-ecs-private-sg", vpcId);
        var albSg = LookupSecurityGroup(prefix, $"{prefix}-alb-sg", vpcId);
        var internalAlbSg = LookupSecurityGroup(prefix, $"{prefix}-internal-alb-sg", vpcId);
        var rdsSg = LookupSecurityGroup(prefix, $"{prefix}-rds-sg", vpcId);
        var efsSg = LookupSecurityGroup(prefix, $"{prefix}-efs-sg", vpcId);
        var tailscaleSg = LookupSecurityGroup(prefix, $"{prefix}-tailscale-sg", vpcId);

        // ACM certificate — now issued for CentralAuthDomain
        var cert = Pulumi.Aws.Acm.GetCertificate.Invoke(new Pulumi.Aws.Acm.GetCertificateInvokeArgs
        {
            Domain = centralAuthDomain,
            MostRecent = true,
            Statuses = new[] { "ISSUED" },
        });

        // DNS zones — CentralAuthDomain public zone (possibly cross-account), {systemKey}.internal private zone
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = centralAuthDomain },
            new InvokeOptions { Provider = sharedProvider });
        var privateZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = $"{systemKey}.private", PrivateZone = true });

        return new AwsFargateAlbNetworkOutputs
        {
            NetworkId = vpcId,
            PublicSubnetIds = publicSubnets.Apply(s => s.Ids.ToImmutableArray()),
            PrivateSubnetIds = privateSubnets.Apply(s => s.Ids.ToImmutableArray()),
            PrivateDnsZoneId = privateZone.Apply(z => z.ZoneId),
            PublicDnsZoneId = publicZone.Apply(z => z.ZoneId),
            PublicAlbArn = publicAlb.Apply(a => a.Arn),
            InternalAlbArn = internalAlb.Apply(a => a.Arn),
            PublicAlbDns = publicAlb.Apply(a => a.DnsName),
            PublicAlbZoneId = publicAlb.Apply(a => a.ZoneId),
            InternalAlbDns = internalAlb.Apply(a => a.DnsName),
            InternalAlbZoneId = internalAlb.Apply(a => a.ZoneId),
            HttpsListenerArn = httpsListener.Apply(l => l.Arn),
            InternalHttpsListenerArn = internalHttpsListener.Apply(l => l.Arn),
            EcsPublicSecurityGroupId = ecsPublicSg.Apply(s => s.Id),
            EcsPrivateSecurityGroupId = ecsPrivateSg.Apply(s => s.Id),
            AlbSecurityGroupId = albSg.Apply(s => s.Id),
            InternalAlbSecurityGroupId = internalAlbSg.Apply(s => s.Id),
            RdsSecurityGroupId = rdsSg.Apply(s => s.Id),
            EfsSecurityGroupId = efsSg.Apply(s => s.Id),
            TailscaleSecurityGroupId = tailscaleSg.Apply(s => s.Id),
            CertificateArn = cert.Apply(c => c.Arn),
            NatGatewayId = Output.Create(""), // Not used in tenant lookup path
        };
    }

    private static Output<GetSecurityGroupResult> LookupSecurityGroup(
        string prefix, string name, Output<string> vpcId)
    {
        return vpcId.Apply(vid =>
            GetSecurityGroup.InvokeAsync(new GetSecurityGroupArgs
            {
                Name = name,
                VpcId = vid,
            }));
    }

    private static AwsFargateAlbComputeOutputs LookupCompute(string prefix)
    {
        var cluster = Pulumi.Aws.Ecs.GetCluster.Invoke(
            new Pulumi.Aws.Ecs.GetClusterInvokeArgs
            {
                ClusterName = $"{prefix}-cluster",
            });

        // Cloud Map private DNS namespace
        var dnsNamespace = Pulumi.Aws.ServiceDiscovery.GetDnsNamespace.Invoke(
            new GetDnsNamespaceInvokeArgs
            {
                Name = $"{prefix}.internal",
                Type = "DNS_PRIVATE",
            });

        return new AwsFargateAlbComputeOutputs
        {
            ClusterId = cluster.Apply(c => c.Id),
            ClusterArn = cluster.Apply(c => c.Arn),
            PublicIngressEndpoint = Output.Create(""),
            InternalIngressEndpoint = Output.Create(""),
            CloudMapNamespaceId = dnsNamespace.Apply(n => n.Id),
            CloudMapNamespaceArn = dnsNamespace.Apply(n => n.Arn),
        };
    }

    private static AwsDatabaseOutputs LookupDatabase(string prefix)
    {
        var db = Pulumi.Aws.Rds.GetInstance.Invoke(
            new Pulumi.Aws.Rds.GetInstanceInvokeArgs
            {
                DbInstanceIdentifier = $"{prefix}-db",
            });

        var systemSecret = Pulumi.Aws.SecretsManager.GetSecret.Invoke(
            new GetSecretInvokeArgs { Name = $"{prefix}/system" });

        // Master secret ARN — RDS manages this; we derive it from the instance
        var masterSecretArn = db.Apply(d => d.MasterUserSecrets.FirstOrDefault()?.SecretArn ?? "");

        return new AwsDatabaseOutputs
        {
            Endpoint = db.Apply(d => d.Endpoint.Split(':')[0]),
            Port = db.Apply(d => d.Port),
            AdminSecretId = systemSecret.Apply(s => s.Id),
            DbInstanceIdentifier = db.Apply(d => d.DbInstanceIdentifier),
            MasterSecretArn = masterSecretArn,
            SystemSecretArn = systemSecret.Apply(s => s.Arn),
            DbSubnetGroupName = db.Apply(d => d.DbSubnetGroup),
            InitTaskFamily = Output.Create($"{prefix}-system-init"),
        };
    }

    private static AwsFileStorageOutputs LookupFileStorage(string prefix, string environment)
    {
        var efs = Pulumi.Aws.Efs.GetFileSystem.Invoke(new GetFileSystemInvokeArgs
        {
            Tags = new InputMap<string>
            {
                { "Name", $"{prefix}-efs" },
                { "Environment", environment },
                { "ManagedBy", "lz-pulumi" },
            },
        });

        return new AwsFileStorageOutputs
        {
            FileSystemId = efs.Apply(f => f.Id),
            FileSystemArn = efs.Apply(f => f.Arn),
            // Keycloak theme access point is a foundation-level concern, not needed by tenant
            KeycloakThemeAccessPointId = Output.Create(""),
        };
    }
}
