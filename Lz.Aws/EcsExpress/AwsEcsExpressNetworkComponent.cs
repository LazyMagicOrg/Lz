using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Acm;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Ec2.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Minimal network for ECSExpress topology.
/// Creates a VPC with public subnets only (no private subnets, no NAT gateway),
/// a public ALB, security groups, ACM certificate, and Route 53 zone lookup.
/// Fargate tasks run in public subnets with AssignPublicIp = true.
/// </summary>
public class AwsEcsExpressNetworkComponent : ComponentResource, ISystemNetworkComponent
{
    public AwsEcsExpressNetworkComponent()
        : base("lz:aws:EcsExpressNetwork", "network", ResourceArgs.Empty, null)
    {
    }

    public INetworkOutputs Deploy(SystemConfig config)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var prefix = $"{sk}-{env}";
        var cidr = config.VpcCidr;
        var domain = config.SystemDomain;

        // =====================================================================
        // VPC
        // =====================================================================

        var vpc = new Vpc($"{prefix}-vpc", new VpcArgs
        {
            CidrBlock = cidr,
            EnableDnsHostnames = true,
            EnableDnsSupport = true,
            Tags = Tags(sk, "vpc"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // PUBLIC SUBNETS ONLY — 2 AZs
        // =====================================================================

        var azs = Pulumi.Aws.GetAvailabilityZones.Invoke(
            new Pulumi.Aws.GetAvailabilityZonesInvokeArgs { State = "available" });

        var cidrBase = cidr.Split('.')[0] + "." + cidr.Split('.')[1];

        var publicSubnet1 = new Subnet($"{prefix}-pub-1", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = $"{cidrBase}.1.0/24",
            AvailabilityZone = azs.Apply(a => a.Names[0]),
            MapPublicIpOnLaunch = true,
            Tags = Tags(sk, "public-subnet-1"),
        }, new CustomResourceOptions { Parent = this });

        var publicSubnet2 = new Subnet($"{prefix}-pub-2", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = $"{cidrBase}.2.0/24",
            AvailabilityZone = azs.Apply(a => a.Names[1]),
            MapPublicIpOnLaunch = true,
            Tags = Tags(sk, "public-subnet-2"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // INTERNET GATEWAY + ROUTE TABLE
        // =====================================================================

        var igw = new InternetGateway($"{prefix}-igw", new InternetGatewayArgs
        {
            VpcId = vpc.Id,
            Tags = Tags(sk, "igw"),
        }, new CustomResourceOptions { Parent = this });

        var publicRt = new RouteTable($"{prefix}-pub-rt", new RouteTableArgs
        {
            VpcId = vpc.Id,
            Routes =
            {
                new RouteTableRouteArgs { CidrBlock = "0.0.0.0/0", GatewayId = igw.Id },
            },
            Tags = Tags(sk, "public-rt"),
        }, new CustomResourceOptions { Parent = this });

        new RouteTableAssociation($"{prefix}-pub-rta-1", new RouteTableAssociationArgs
        {
            SubnetId = publicSubnet1.Id, RouteTableId = publicRt.Id,
        }, new CustomResourceOptions { Parent = this });

        new RouteTableAssociation($"{prefix}-pub-rta-2", new RouteTableAssociationArgs
        {
            SubnetId = publicSubnet2.Id, RouteTableId = publicRt.Id,
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // SECURITY GROUPS
        // =====================================================================

        // ALB security group — public internet access on 80/443
        var albSg = new SecurityGroup($"{prefix}-alb-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Description = "ALB - public HTTP/HTTPS",
            Ingress =
            {
                new SecurityGroupIngressArgs { FromPort = 80, ToPort = 80, Protocol = "tcp", CidrBlocks = { "0.0.0.0/0" } },
                new SecurityGroupIngressArgs { FromPort = 443, ToPort = 443, Protocol = "tcp", CidrBlocks = { "0.0.0.0/0" } },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { FromPort = 0, ToPort = 0, Protocol = "-1", CidrBlocks = { "0.0.0.0/0" } },
            },
            Tags = Tags(sk, "alb-sg"),
        }, new CustomResourceOptions { Parent = this });

        // ECS task security group — only ALB can reach tasks on port 8080
        var ecsSg = new SecurityGroup($"{prefix}-ecs-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Description = "ECS tasks - ingress from ALB only",
            Ingress =
            {
                new SecurityGroupIngressArgs { FromPort = 8080, ToPort = 8080, Protocol = "tcp", SecurityGroups = { albSg.Id } },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { FromPort = 0, ToPort = 0, Protocol = "-1", CidrBlocks = { "0.0.0.0/0" } },
            },
            Tags = Tags(sk, "ecs-sg"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // APPLICATION LOAD BALANCER
        // =====================================================================

        var alb = new LoadBalancer($"{prefix}-alb", new LoadBalancerArgs
        {
            Internal = false,
            LoadBalancerType = "application",
            SecurityGroups = { albSg.Id },
            Subnets = { publicSubnet1.Id, publicSubnet2.Id },
            Tags = Tags(sk, "alb"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ACM CERTIFICATE
        // =====================================================================

        var cert = new Certificate($"{prefix}-cert", new CertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = { $"*.{domain}" },
            ValidationMethod = "DNS",
            Tags = Tags(sk, "cert"),
        }, new CustomResourceOptions { Parent = this });

        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = domain });

        var validationRecord = new Pulumi.Aws.Route53.Record($"{prefix}-cert-validation",
            new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = publicZone.Apply(z => z.ZoneId),
                Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
                Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
                Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
                Ttl = 300,
                AllowOverwrite = true,
            }, new CustomResourceOptions { Parent = this });

        var certValidation = new CertificateValidation($"{prefix}-cert-validated",
            new CertificateValidationArgs
            {
                CertificateArn = cert.Arn,
                ValidationRecordFqdns = { validationRecord.Fqdn },
            }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // HTTPS LISTENER
        // =====================================================================

        var httpsListener = new Listener($"{prefix}-https", new ListenerArgs
        {
            LoadBalancerArn = alb.Arn,
            Port = 443,
            Protocol = "HTTPS",
            CertificateArn = certValidation.CertificateArn,
            SslPolicy = "ELBSecurityPolicy-TLS13-1-2-2021-06",
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "fixed-response",
                    FixedResponse = new ListenerDefaultActionFixedResponseArgs
                    {
                        ContentType = "text/plain",
                        MessageBody = "Not Found",
                        StatusCode = "404",
                    },
                },
            },
            Tags = Tags(sk, "https-listener"),
        }, new CustomResourceOptions { Parent = this });

        // HTTP → HTTPS redirect
        new Listener($"{prefix}-http-redirect", new ListenerArgs
        {
            LoadBalancerArn = alb.Arn,
            Port = 80,
            Protocol = "HTTP",
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "redirect",
                    Redirect = new ListenerDefaultActionRedirectArgs
                    {
                        Port = "443",
                        Protocol = "HTTPS",
                        StatusCode = "HTTP_301",
                    },
                },
            },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ALB DNS → Route 53 (origin record for CloudFront)
        // =====================================================================

        new Pulumi.Aws.Route53.Record($"{prefix}-alb-origin", new Pulumi.Aws.Route53.RecordArgs
        {
            ZoneId = publicZone.Apply(z => z.ZoneId),
            Name = $"origin.{domain}",
            Type = "A",
            AllowOverwrite = true,
            Aliases =
            {
                new Pulumi.Aws.Route53.Inputs.RecordAliasArgs
                {
                    Name = alb.DnsName,
                    ZoneId = alb.ZoneId,
                    EvaluateTargetHealth = false,
                },
            },
        }, new CustomResourceOptions { Parent = this });

        var emptySubnets = Output.Create(ImmutableArray<string>.Empty);

        return new AwsEcsExpressNetworkOutputs
        {
            NetworkId = vpc.Id,
            PrivateSubnetIds = emptySubnets, // No private subnets
            PublicSubnetIds = Output.All(publicSubnet1.Id, publicSubnet2.Id)
                .Apply(ids => ids.ToImmutableArray()),
            PrivateDnsZoneId = Output.Create(""),
            PublicDnsZoneId = publicZone.Apply(z => z.ZoneId),
            AlbArn = alb.Arn,
            AlbDns = alb.DnsName,
            AlbZoneId = alb.ZoneId,
            HttpsListenerArn = httpsListener.Arn,
            AlbSecurityGroupId = albSg.Id,
            EcsTaskSecurityGroupId = ecsSg.Id,
            CertificateArn = certValidation.CertificateArn,
        };
    }

    private static InputMap<string> Tags(string systemKey, string name) => new()
    {
        { "Name", $"{systemKey}-{name}" },
        { "System", systemKey },
        { "ManagedBy", "lz-pulumi" },
    };
}
