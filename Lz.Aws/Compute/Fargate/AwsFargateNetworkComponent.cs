using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Acm;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Ec2.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;
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
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.Fargate;

/// <summary>
/// Minimal network for ECSExpress topology.
/// Creates a VPC with public subnets only (no private subnets, no NAT gateway),
/// a public ALB, security groups, ACM certificate, and Route 53 zone lookup.
/// Fargate tasks run in public subnets with AssignPublicIp = true.
/// </summary>
public class AwsFargateNetworkComponent : ComponentResource, ISystemNetworkComponent
{
    public AwsFargateNetworkComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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

        // Opt-in private networking (Phase 1 Fargate hardening). Default OFF so
        // the ~10 sibling Fargate systems emit a byte-identical plan. Every
        // new resource below is gated on this flag.
        var privateNetwork = config.Aws().PrivateNetwork is { Enabled: true };

        // Phase 2: Tailscale subnet-router opt-in (requires PrivateNetwork on).
        // Off => no Tailscale SG, byte-identical. Gate: Enabled AND Tailscale.
        var tailscale = config.Aws().PrivateNetwork is { Enabled: true, Tailscale: true };

        // Populated only when privateNetwork is on; referenced later by the ALB
        // placement and the network outputs.
        Subnet? privateSubnet1 = null;
        Subnet? privateSubnet2 = null;
        NatGateway? natGw = null;
        SecurityGroup? tailscaleSg = null;

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
        // PRIVATE NETWORKING (opt-in) — private subnets, single NAT, FREE gateway
        // endpoints. Ported from Ecs/AwsFargateAlbNetworkComponent.cs, adapted to
        // Fargate naming (priv-1/priv-2) with a SINGLE NAT in publicSubnet1.
        // No interface endpoints in Phase 1.
        // =====================================================================
        if (privateNetwork)
        {
            privateSubnet1 = new Subnet($"{prefix}-priv-1", new SubnetArgs
            {
                VpcId = vpc.Id,
                CidrBlock = $"{cidrBase}.10.0/24",
                AvailabilityZone = azs.Apply(a => a.Names[0]),
                Tags = Tags(sk, "private-subnet-1"),
            }, new CustomResourceOptions { Parent = this });

            privateSubnet2 = new Subnet($"{prefix}-priv-2", new SubnetArgs
            {
                VpcId = vpc.Id,
                CidrBlock = $"{cidrBase}.11.0/24",
                AvailabilityZone = azs.Apply(a => a.Names[1]),
                Tags = Tags(sk, "private-subnet-2"),
            }, new CustomResourceOptions { Parent = this });

            // Single EIP + NAT gateway in publicSubnet1 (one NAT, not per-AZ).
            var eip = new Eip($"{prefix}-nat-eip", new EipArgs
            {
                Domain = "vpc",
                Tags = Tags(sk, "nat-eip"),
            }, new CustomResourceOptions { Parent = this });

            natGw = new NatGateway($"{prefix}-nat", new NatGatewayArgs
            {
                SubnetId = publicSubnet1.Id,
                AllocationId = eip.Id,
                Tags = Tags(sk, "nat-gateway"),
            }, new CustomResourceOptions { Parent = this, DependsOn = { igw } });

            // Private route table: 0.0.0.0/0 -> NAT. Separate Route resource with
            // IgnoreChanges("region") mirrors the Ecs workaround for the
            // pulumi-aws 7.x metadata-only-update behavior on route resources.
            var routeOpts = CustomResourceOptions.Merge(
                new CustomResourceOptions { Parent = this },
                new CustomResourceOptions { IgnoreChanges = { "region" } });

            var privateRt = new RouteTable($"{prefix}-priv-rt", new RouteTableArgs
            {
                VpcId = vpc.Id,
                Tags = Tags(sk, "private-rt"),
            }, new CustomResourceOptions { Parent = this });

            new Route($"{prefix}-priv-route", new RouteArgs
            {
                RouteTableId = privateRt.Id,
                DestinationCidrBlock = "0.0.0.0/0",
                NatGatewayId = natGw.Id,
            }, routeOpts);

            new RouteTableAssociation($"{prefix}-priv-rta-1", new RouteTableAssociationArgs
            {
                SubnetId = privateSubnet1.Id, RouteTableId = privateRt.Id,
            }, new CustomResourceOptions { Parent = this });

            new RouteTableAssociation($"{prefix}-priv-rta-2", new RouteTableAssociationArgs
            {
                SubnetId = privateSubnet2.Id, RouteTableId = privateRt.Id,
            }, new CustomResourceOptions { Parent = this });

            // FREE gateway VPC endpoints for S3 + DynamoDB, associated with the
            // private route table so private-subnet tasks reach those services
            // without NAT data-processing charges.
            new VpcEndpoint($"{prefix}-s3-endpoint", new VpcEndpointArgs
            {
                VpcId = vpc.Id,
                ServiceName = $"com.amazonaws.{config.Region}.s3",
                VpcEndpointType = "Gateway",
                RouteTableIds = { privateRt.Id },
                Tags = Tags(sk, "s3-endpoint"),
            }, new CustomResourceOptions { Parent = this });

            new VpcEndpoint($"{prefix}-dynamodb-endpoint", new VpcEndpointArgs
            {
                VpcId = vpc.Id,
                ServiceName = $"com.amazonaws.{config.Region}.dynamodb",
                VpcEndpointType = "Gateway",
                RouteTableIds = { privateRt.Id },
                Tags = Tags(sk, "dynamodb-endpoint"),
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // SECURITY GROUPS
        // =====================================================================

        // ALB security group ingress. OFF: public 80/443 (byte-identical to today).
        // ON: the ALB is internal and reached only via the CloudFront VPC origin.
        //
        // CRITICAL: a CloudFront VPC origin's AWS-managed ENIs are admitted by
        // SECURITY-GROUP REFERENCE, not by source IP — a VPC-CIDR rule alone does NOT
        // let CloudFront reach the ALB (verified live: CIDR-only => origin timeouts).
        // The managing SG is the account/region singleton "CloudFront-VPCOrigins-Service-SG",
        // created by AWS the first time a VPC origin is created in the region (our
        // CloudFront component's VpcOrigin, deploytenant phase). We reference it here.
        // The tolerant plural lookup returns empty on a brand-new account (no VPC origin
        // yet) — in which case only the VPC-CIDR rule is emitted and a SECOND deploysystem,
        // run after the first deploytenant creates the VPC origin, adds the reference.
        // (Inline ingress is built conditionally rather than via separate SecurityGroupRule
        // resources: the AWS provider forbids mixing in-line rules with rule resources on
        // the same SG.)
        InputList<SecurityGroupIngressArgs> albIngress;
        if (privateNetwork)
        {
            var cfVpcOriginSg = GetSecurityGroups.Invoke(new GetSecurityGroupsInvokeArgs
            {
                Filters =
                {
                    new GetSecurityGroupsFilterInputArgs
                    {
                        Name = "group-name",
                        Values = { "CloudFront-VPCOrigins-Service-SG" },
                    },
                },
            });

            albIngress = cfVpcOriginSg.Apply(sgs =>
            {
                // In-VPC clients on 443 (e.g. ops reaching the internal ALB over Tailscale).
                var cidrRule = new SecurityGroupIngressArgs
                {
                    FromPort = 443, ToPort = 443, Protocol = "tcp",
                    CidrBlocks = { cidr }, Description = "HTTPS from within the VPC",
                };
                if (sgs.Ids.Length == 0)
                    return ImmutableArray.Create(cidrRule);

                var vpcOriginRule = new SecurityGroupIngressArgs
                {
                    FromPort = 443, ToPort = 443, Protocol = "tcp",
                    SecurityGroups = { sgs.Ids[0] },
                    Description = "HTTPS from the CloudFront VPC-origin managed SG",
                };
                return ImmutableArray.Create(cidrRule, vpcOriginRule);
            });
        }
        else
        {
            var ing = new InputList<SecurityGroupIngressArgs>();
            ing.Add(new SecurityGroupIngressArgs { FromPort = 80, ToPort = 80, Protocol = "tcp", CidrBlocks = { "0.0.0.0/0" } });
            ing.Add(new SecurityGroupIngressArgs { FromPort = 443, ToPort = 443, Protocol = "tcp", CidrBlocks = { "0.0.0.0/0" } });
            albIngress = ing;
        }

        // ALB security group — public HTTP/HTTPS (OFF) or VPC-only HTTPS (ON)
        var albSg = new SecurityGroup($"{prefix}-alb-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Description = "ALB - public HTTP/HTTPS",
            Ingress = albIngress,
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

        // Tailscale subnet-router security group (Phase 2 opt-in). Ported from
        // Ecs/AwsFargateAlbNetworkComponent.cs. Ingress from the VPC CIDR (tailnet peers
        // reach VPC resources through the router) AND from the Tailscale CGNAT
        // range 100.64.0.0/10 (router/tailnet traffic); egress all. Created only
        // when PrivateNetwork.Tailscale is on — no SG otherwise (byte-identical).
        if (tailscale)
        {
            tailscaleSg = new SecurityGroup($"{prefix}-tailscale-sg", new SecurityGroupArgs
            {
                VpcId = vpc.Id,
                Description = "Tailscale subnet router",
                Ingress =
                {
                    new SecurityGroupIngressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { cidr }, Description = "All traffic from VPC" },
                    new SecurityGroupIngressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "100.64.0.0/10" }, Description = "All traffic from Tailscale CGNAT range" },
                },
                Egress =
                {
                    new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
                },
                Tags = Tags(sk, "tailscale-sg"),
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // APPLICATION LOAD BALANCER
        // =====================================================================

        // ALB placement: private subnets + internal when the flag is on; public
        // subnets + internet-facing (Internal=false) when off — byte-identical to
        // today in the OFF case (privateNetwork == false).
        var albSubnets = new InputList<string>();
        if (privateNetwork)
        {
            albSubnets.Add(privateSubnet1!.Id);
            albSubnets.Add(privateSubnet2!.Id);
        }
        else
        {
            albSubnets.Add(publicSubnet1.Id);
            albSubnets.Add(publicSubnet2.Id);
        }

        var alb = new LoadBalancer($"{prefix}-alb", new LoadBalancerArgs
        {
            Internal = privateNetwork,
            LoadBalancerType = "application",
            SecurityGroups = { albSg.Id },
            Subnets = albSubnets,
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
        // OFF: public origin.{domain} A-alias → the internet-facing ALB (today's
        // behavior). ON: the ALB is internal and CloudFront reaches it via a VPC
        // ORIGIN targeting the ALB ARN directly, so this public alias is dropped
        // (and would otherwise leak the internal ALB's private IPs into public DNS).
        if (!privateNetwork)
        {
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
        }

        var emptySubnets = Output.Create(ImmutableArray<string>.Empty);

        return new AwsFargateNetworkOutputs
        {
            NetworkId = vpc.Id,
            // Real private-subnet ids when on; empty (today's value) when off.
            PrivateSubnetIds = privateNetwork
                ? Output.All(privateSubnet1!.Id, privateSubnet2!.Id).Apply(ids => ids.ToImmutableArray())
                : emptySubnets,
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
            // Private-network (opt-in) outputs — neutral values when off.
            PrivateNetworking = privateNetwork,
            NatGatewayId = privateNetwork ? natGw!.Id : Output.Create(""),
            TailscaleSecurityGroupId = tailscale ? tailscaleSg!.Id : Output.Create(""),
        };
    }

    private static InputMap<string> Tags(string systemKey, string name) => new()
    {
        { "Name", $"{systemKey}-{name}" },
        { "System", systemKey },
        { "ManagedBy", "lz-pulumi" },
    };
}
