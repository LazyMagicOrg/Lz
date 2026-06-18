using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Ec2.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;
using Pulumi.Aws.Route53;
using Pulumi.Aws.Route53.Inputs;
using AcmCertificate = Pulumi.Aws.Acm.Certificate;
using AcmCertificateArgs = Pulumi.Aws.Acm.CertificateArgs;
using Route53Record = Pulumi.Aws.Route53.Record;
using Route53RecordArgs = Pulumi.Aws.Route53.RecordArgs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS ECS network infrastructure component — full foundation matching SAM template.
/// Creates VPC, subnets, IGW, NAT, security groups, ALBs, ACM cert, Route 53 zones,
/// VPC Flow Logs, and DNS records.
/// </summary>
public class AwsEcsNetworkComponent : ComponentResource, ISystemNetworkComponent
{
    public AwsEcsNetworkComponent()
        : base("lz:aws:EcsNetwork", "network", ResourceArgs.Empty, null)
    {
    }

    public INetworkOutputs Deploy(SystemConfig config)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };

        // =====================================================================
        // VPC
        // =====================================================================

        var vpcCidr = config.VpcCidr;

        // Validate CIDR format — must be x.x.0.0/16
        var cidrParts = vpcCidr.Split('.');
        if (cidrParts.Length < 4 || !vpcCidr.EndsWith(".0.0/16"))
            throw new ArgumentException(
                $"VpcCidr must be a /16 CIDR block (e.g., 10.20.0.0/16), got: {vpcCidr}");

        // Derive subnet CIDRs from VPC CIDR base (e.g., "10.20" from "10.20.0.0/16")
        var cidrBase = $"{cidrParts[0]}.{cidrParts[1]}";

        var publicSubnet1Cidr = $"{cidrBase}.1.0/24";
        var publicSubnet2Cidr = $"{cidrBase}.2.0/24";
        var privateSubnet1Cidr = $"{cidrBase}.10.0/24";
        var privateSubnet2Cidr = $"{cidrBase}.11.0/24";

        var vpc = new Vpc($"{prefix}-vpc", new VpcArgs
        {
            CidrBlock = vpcCidr,
            EnableDnsSupport = true,
            EnableDnsHostnames = true,
            Tags = Tags(config, "vpc"),
        }, opts);

        // =====================================================================
        // VPC FLOW LOGS
        // =====================================================================

        var logRetention = config.Aws().ECS?.LogRetentionDays ?? 3;

        var flowLogsLogGroup = new LogGroup($"{prefix}-vpc-flow-logs", new LogGroupArgs
        {
            Name = $"/vpc/{prefix}/flow-logs",
            RetentionInDays = logRetention,
            Tags = Tags(config, "vpc-flow-logs"),
        }, opts);

        var flowLogsRole = new Role($"{prefix}-vpc-flow-logs-role", new RoleArgs
        {
            Name = $"{prefix}-vpc-flow-logs-role",
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""vpc-flow-logs.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            InlinePolicies =
            {
                new RoleInlinePolicyArgs
                {
                    Name = "FlowLogsPolicy",
                    Policy = flowLogsLogGroup.Arn.Apply(arn => $@"{{
                        ""Version"": ""2012-10-17"",
                        ""Statement"": [{{
                            ""Effect"": ""Allow"",
                            ""Action"": [
                                ""logs:CreateLogStream"",
                                ""logs:PutLogEvents"",
                                ""logs:DescribeLogGroups"",
                                ""logs:DescribeLogStreams""
                            ],
                            ""Resource"": ""{arn}""
                        }}]
                    }}"),
                },
            },
            Tags = Tags(config, "vpc-flow-logs-role"),
        }, opts);

        new FlowLog($"{prefix}-vpc-flow-log", new FlowLogArgs
        {
            IamRoleArn = flowLogsRole.Arn,
            LogDestination = flowLogsLogGroup.Arn,
            VpcId = vpc.Id,
            TrafficType = "ALL",
            Tags = Tags(config, "vpc-flow-logs"),
        }, opts);

        // =====================================================================
        // SUBNETS (matching SAM /24 CIDRs)
        // =====================================================================

        var azs = Pulumi.Aws.GetAvailabilityZones.Invoke(new Pulumi.Aws.GetAvailabilityZonesInvokeArgs
        {
            State = "available",
        });

        // Validate that at least 2 AZs are available (required for multi-AZ ALB + RDS)
        azs.Apply(a =>
        {
            if (a.Names.Length < 2)
                throw new InvalidOperationException(
                    $"Region {config.Region} has {a.Names.Length} availability zone(s), but at least 2 are required.");
            return a;
        });

        var publicSubnet1 = new Subnet($"{prefix}-public-1", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = publicSubnet1Cidr,
            AvailabilityZone = azs.Apply(a => a.Names[0]),
            MapPublicIpOnLaunch = true,
            Tags = Tags(config, "public-subnet-1"),
        }, opts);

        var publicSubnet2 = new Subnet($"{prefix}-public-2", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = publicSubnet2Cidr,
            AvailabilityZone = azs.Apply(a => a.Names[1]),
            MapPublicIpOnLaunch = true,
            Tags = Tags(config, "public-subnet-2"),
        }, opts);

        var privateSubnet1 = new Subnet($"{prefix}-private-1", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = privateSubnet1Cidr,
            AvailabilityZone = azs.Apply(a => a.Names[0]),
            Tags = Tags(config, "private-subnet-1"),
        }, opts);

        var privateSubnet2 = new Subnet($"{prefix}-private-2", new SubnetArgs
        {
            VpcId = vpc.Id,
            CidrBlock = privateSubnet2Cidr,
            AvailabilityZone = azs.Apply(a => a.Names[1]),
            Tags = Tags(config, "private-subnet-2"),
        }, opts);

        // =====================================================================
        // INTERNET GATEWAY + NAT GATEWAY
        // =====================================================================

        var igw = new InternetGateway($"{prefix}-igw", new InternetGatewayArgs
        {
            VpcId = vpc.Id,
            Tags = Tags(config, "igw"),
        }, opts);

        var eip = new Eip($"{prefix}-nat-eip", new EipArgs
        {
            Domain = "vpc",
            Tags = Tags(config, "nat-eip"),
        }, opts);

        var natGw = new NatGateway($"{prefix}-nat", new NatGatewayArgs
        {
            SubnetId = publicSubnet1.Id,
            AllocationId = eip.Id,
            Tags = Tags(config, "nat-gateway"),
        }, new CustomResourceOptions { Parent = this, DependsOn = { igw } });

        // =====================================================================
        // ROUTE TABLES
        // =====================================================================

        var publicRt = new RouteTable($"{prefix}-public-rt", new RouteTableArgs
        {
            VpcId = vpc.Id,
            Tags = Tags(config, "public-rt"),
        }, opts);

        new Route($"{prefix}-public-route", new RouteArgs
        {
            RouteTableId = publicRt.Id,
            DestinationCidrBlock = "0.0.0.0/0",
            GatewayId = igw.Id,
        }, opts);

        new RouteTableAssociation($"{prefix}-public-rta-1", new RouteTableAssociationArgs
        {
            SubnetId = publicSubnet1.Id,
            RouteTableId = publicRt.Id,
        }, opts);

        new RouteTableAssociation($"{prefix}-public-rta-2", new RouteTableAssociationArgs
        {
            SubnetId = publicSubnet2.Id,
            RouteTableId = publicRt.Id,
        }, opts);

        var privateRt = new RouteTable($"{prefix}-private-rt", new RouteTableArgs
        {
            VpcId = vpc.Id,
            Tags = Tags(config, "private-rt"),
        }, opts);

        new Route($"{prefix}-private-route", new RouteArgs
        {
            RouteTableId = privateRt.Id,
            DestinationCidrBlock = "0.0.0.0/0",
            NatGatewayId = natGw.Id,
        }, opts);

        new RouteTableAssociation($"{prefix}-private-rta-1", new RouteTableAssociationArgs
        {
            SubnetId = privateSubnet1.Id,
            RouteTableId = privateRt.Id,
        }, opts);

        new RouteTableAssociation($"{prefix}-private-rta-2", new RouteTableAssociationArgs
        {
            SubnetId = privateSubnet2.Id,
            RouteTableId = privateRt.Id,
        }, opts);

        // =====================================================================
        // SECURITY GROUPS
        // =====================================================================

        var albSg = new SecurityGroup($"{prefix}-alb-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-alb-sg",
            Description = "Security group for public ALB",
            Ingress =
            {
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 443, ToPort = 443, CidrBlocks = { "0.0.0.0/0" }, Description = "HTTPS from internet" },
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 80, ToPort = 80, CidrBlocks = { "0.0.0.0/0" }, Description = "HTTP from internet (redirects to HTTPS)" },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "alb-sg"),
        }, opts);

        var internalAlbSg = new SecurityGroup($"{prefix}-internal-alb-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-internal-alb-sg",
            Description = "Security group for internal ALB (VPN-only access)",
            Ingress =
            {
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 443, ToPort = 443, CidrBlocks = { vpcCidr }, Description = "HTTPS from VPC" },
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 443, ToPort = 443, CidrBlocks = { "100.64.0.0/10" }, Description = "HTTPS from Tailscale network" },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "internal-alb-sg"),
        }, opts);

        // ECS Public — no inline ingress (added via SecurityGroupRule to avoid circular refs)
        var ecsPublicSg = new SecurityGroup($"{prefix}-ecs-public-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-ecs-public-sg",
            Description = "Security group for public ECS tasks",
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "ecs-public-sg"),
        }, opts);

        // ECS Public ingress from public ALB
        new SecurityGroupRule($"{prefix}-ecs-pub-alb-8080", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = albSg.Id, Description = "HTTP from public ALB",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-pub-alb-80", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 80, ToPort = 80,
            SourceSecurityGroupId = albSg.Id, Description = "HTTP port 80 from public ALB",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-pub-alb-9000", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 9000, ToPort = 9000,
            SourceSecurityGroupId = albSg.Id, Description = "Keycloak health port from ALB",
        }, opts);

        // ECS Public ingress from internal ALB
        new SecurityGroupRule($"{prefix}-ecs-pub-ialb-8080", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = internalAlbSg.Id, Description = "Keycloak traffic from internal ALB",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-pub-ialb-9000", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 9000, ToPort = 9000,
            SourceSecurityGroupId = internalAlbSg.Id, Description = "Keycloak health port from internal ALB",
        }, opts);

        // ECS Private
        var ecsPrivateSg = new SecurityGroup($"{prefix}-ecs-private-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-ecs-private-sg",
            Description = "Security group for private ECS tasks (VPN-only)",
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "ecs-private-sg"),
        }, opts);

        // ECS Private ingress from internal ALB
        foreach (var port in new[] { 80, 443, 8080, 9000 })
        {
            new SecurityGroupRule($"{prefix}-ecs-priv-ialb-{port}", new SecurityGroupRuleArgs
            {
                Type = "ingress", SecurityGroupId = ecsPrivateSg.Id,
                Protocol = "tcp", FromPort = port, ToPort = port,
                SourceSecurityGroupId = internalAlbSg.Id,
                Description = $"Port {port} from internal ALB",
            }, opts);
        }

        // Inter-service communication rules
        new SecurityGroupRule($"{prefix}-ecs-pub-self", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = ecsPublicSg.Id, Description = "ECS public tasks self-communication",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-priv-self", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPrivateSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = ecsPrivateSg.Id, Description = "ECS private tasks self-communication",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-pub-to-priv", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPrivateSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = ecsPublicSg.Id, Description = "Public ECS to private ECS",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-priv-to-pub", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 8080, ToPort = 8080,
            SourceSecurityGroupId = ecsPrivateSg.Id, Description = "Private ECS to public ECS",
        }, opts);

        // Tailscale Security Group
        var tailscaleSg = new SecurityGroup($"{prefix}-tailscale-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-tailscale-sg",
            Description = "Security group for Tailscale subnet router",
            Ingress =
            {
                new SecurityGroupIngressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { vpcCidr }, Description = "All traffic from VPC" },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound traffic" },
            },
            Tags = Tags(config, "tailscale-sg"),
        }, opts);

        // RDS Security Group (VPC CIDR-based, matching SAM)
        var rdsSg = new SecurityGroup($"{prefix}-rds-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-rds-sg",
            Description = "Security group for RDS PostgreSQL",
            Ingress =
            {
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 5432, ToPort = 5432, CidrBlocks = { vpcCidr }, Description = "PostgreSQL from VPC (ECS tasks, Tailscale)" },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "rds-sg"),
        }, opts);

        // EFS Security Group (VPC CIDR-based, matching SAM)
        var efsSg = new SecurityGroup($"{prefix}-efs-sg", new SecurityGroupArgs
        {
            VpcId = vpc.Id,
            Name = $"{prefix}-efs-sg",
            Description = "Security group for EFS",
            Ingress =
            {
                new SecurityGroupIngressArgs { Protocol = "tcp", FromPort = 2049, ToPort = 2049, CidrBlocks = { vpcCidr }, Description = "NFS from VPC (ECS tasks, Tailscale)" },
            },
            Egress =
            {
                new SecurityGroupEgressArgs { Protocol = "-1", FromPort = 0, ToPort = 0, CidrBlocks = { "0.0.0.0/0" }, Description = "All outbound" },
            },
            Tags = Tags(config, "efs-sg"),
        }, opts);

        // =====================================================================
        // ROUTE 53 — Look up CentralAuthDomain public zone, create private zone
        // =====================================================================

        // CentralAuthDomain zone may be in a different account (shared-services).
        // Use a cross-account provider when SharedProfile is set.
        var isCrossAccount = !string.IsNullOrEmpty(config.Aws().SharedProfile);
        Provider? sharedProvider = null;
        if (isCrossAccount)
        {
            sharedProvider = new Provider($"{prefix}-shared-provider", new ProviderArgs
            {
                Region = config.Aws().SharedRegion ?? config.Region,
                Profile = config.Aws().SharedProfile,
            }, opts);
        }
        var sharedOpts = sharedProvider != null
            ? new CustomResourceOptions { Parent = this, Provider = sharedProvider }
            : opts;

        var centralAuthZone = GetZone.Invoke(new GetZoneInvokeArgs
        {
            Name = config.CentralAuthDomain,
            PrivateZone = false,
        }, new InvokeOptions { Provider = sharedProvider });

        var privateZone = new Zone($"{prefix}-private-zone", new ZoneArgs
        {
            Name = $"{config.SystemKey}.private",
            Vpcs =
            {
                new ZoneVpcArgs { VpcId = vpc.Id },
            },
            Comment = $"Private zone for {prefix} - VPN-only records",
            Tags = Tags(config, "private-zone"),
        }, opts);

        // =====================================================================
        // ACM CERTIFICATE for CentralAuthDomain (ALB default cert)
        // =====================================================================
        // The cert is created in THIS account (where the ALB lives).
        // DNS validation records go to the CentralAuthDomain zone (possibly cross-account).

        var cert = new AcmCertificate($"{prefix}-auth-cert", new AcmCertificateArgs
        {
            DomainName = config.CentralAuthDomain,
            SubjectAlternativeNames =
            {
                $"*.{config.CentralAuthDomain}",
            },
            ValidationMethod = "DNS",
            Tags = Tags(config, "auth-cert"),
        }, opts);

        // DNS validation record — written to the CentralAuthDomain zone (may be cross-account)
        var validationRecord = new Route53Record($"{prefix}-cert-validation", new Route53RecordArgs
        {
            ZoneId = centralAuthZone.Apply(z => z.ZoneId),
            Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
            Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
            Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
            Ttl = 300,
            AllowOverwrite = true,
        }, sharedOpts);

        var certValidation = new CertificateValidation($"{prefix}-cert-validated", new CertificateValidationArgs
        {
            CertificateArn = cert.Arn,
            ValidationRecordFqdns =
            {
                validationRecord.Fqdn,
            },
        }, opts);

        // =====================================================================
        // APPLICATION LOAD BALANCERS
        // =====================================================================

        var publicAlb = new LoadBalancer($"{prefix}-alb", new LoadBalancerArgs
        {
            Name = $"{prefix}-alb",
            LoadBalancerType = "application",
            Internal = false,
            IpAddressType = "ipv4",
            Subnets = { publicSubnet1.Id, publicSubnet2.Id },
            SecurityGroups = { albSg.Id },
            // Default 60s 504s the Hugo-build upload (554+ MB → Smartstore
            // unzips into ECS task memory while the ALB waits for any byte
            // from the target). 10 min covers the realistic worst case for
            // legitimate uploads at typical office bandwidth, and the
            // increase only matters for connections that genuinely sit
            // idle — normal request traffic still completes well under 60s.
            // Will become moot once the Hugo-builder ECS task replaces
            // workstation uploads (planned arch change).
            IdleTimeout = 600,
            Tags = Tags(config, "alb"),
        }, opts);

        var httpsListener = new Listener($"{prefix}-https-listener", new ListenerArgs
        {
            LoadBalancerArn = publicAlb.Arn,
            Port = 443,
            Protocol = "HTTPS",
            SslPolicy = "ELBSecurityPolicy-TLS13-1-2-2021-06",
            CertificateArn = certValidation.CertificateArn,
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "fixed-response",
                    FixedResponse = new ListenerDefaultActionFixedResponseArgs
                    {
                        StatusCode = "404",
                        ContentType = "text/plain",
                        MessageBody = "Not Found",
                    },
                },
            },
        }, opts);

        // WebFinger endpoint — only on the shared-services ALB where
        // CentralAuthDomain DNS actually points. Per-system ALBs don't receive
        // WebFinger traffic (CentralAuthDomain resolves to the shared ALB).
        if (!isCrossAccount)
        {
            var webFingerJson = $@"{{""subject"":""acct:tailscale@{config.CentralAuthDomain}"",""links"":[{{""rel"":""http://openid.net/specs/connect/1.0/issuer"",""href"":""https://{config.CentralAuthDomain}/realms/adminsauth""}}]}}";

            new ListenerRule($"{prefix}-webfinger", new ListenerRuleArgs
            {
                ListenerArn = httpsListener.Arn,
                Priority = 2,
                Conditions =
                {
                    new ListenerRuleConditionArgs
                    {
                        HostHeader = new ListenerRuleConditionHostHeaderArgs
                        {
                            Values = { config.CentralAuthDomain },
                        },
                    },
                    new ListenerRuleConditionArgs
                    {
                        PathPattern = new ListenerRuleConditionPathPatternArgs
                        {
                            Values = { "/.well-known/webfinger" },
                        },
                    },
                },
                Actions =
                {
                    new ListenerRuleActionArgs
                    {
                        Type = "fixed-response",
                        FixedResponse = new ListenerRuleActionFixedResponseArgs
                        {
                            StatusCode = "200",
                            ContentType = "application/json",
                            MessageBody = webFingerJson,
                        },
                    },
                },
                Tags = Tags(config, "webfinger-rule"),
            }, opts);
        }

        // HTTP -> HTTPS redirect
        new Listener($"{prefix}-http-listener", new ListenerArgs
        {
            LoadBalancerArn = publicAlb.Arn,
            Port = 80,
            Protocol = "HTTP",
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "redirect",
                    Redirect = new ListenerDefaultActionRedirectArgs
                    {
                        Protocol = "HTTPS",
                        Port = "443",
                        StatusCode = "HTTP_301",
                    },
                },
            },
        }, opts);

        // Internal ALB
        var internalAlb = new LoadBalancer($"{prefix}-internal-alb", new LoadBalancerArgs
        {
            Name = $"{prefix}-internal-alb",
            LoadBalancerType = "application",
            Internal = true,
            IpAddressType = "ipv4",
            Subnets = { privateSubnet1.Id, privateSubnet2.Id },
            SecurityGroups = { internalAlbSg.Id },
            Tags = Tags(config, "internal-alb"),
        }, opts);

        var internalHttpsListener = new Listener($"{prefix}-internal-https-listener", new ListenerArgs
        {
            LoadBalancerArn = internalAlb.Arn,
            Port = 443,
            Protocol = "HTTPS",
            SslPolicy = "ELBSecurityPolicy-TLS13-1-2-2021-06",
            CertificateArn = certValidation.CertificateArn,
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "fixed-response",
                    FixedResponse = new ListenerDefaultActionFixedResponseArgs
                    {
                        StatusCode = "404",
                        ContentType = "text/plain",
                        MessageBody = "Not Found - VPN Access Required",
                    },
                },
            },
        }, opts);

        // =====================================================================
        // NETWORK LOAD BALANCER (for UDP media traffic — e.g., LiveKit SFU)
        // =====================================================================
        // NLB handles UDP (WebRTC media) and TCP (WebSocket signaling) for services
        // that need direct transport-layer access. Unlike ALBs, NLBs support UDP.
        // NLB does not use security groups — traffic is controlled by target SGs.

        var nlb = new LoadBalancer($"{prefix}-nlb", new LoadBalancerArgs
        {
            Name = $"{prefix}-nlb",
            LoadBalancerType = "network",
            Internal = false,
            IpAddressType = "ipv4",
            Subnets = { publicSubnet1.Id, publicSubnet2.Id },
            Tags = Tags(config, "nlb"),
        }, opts);

        // TCP target group for WebSocket signaling (port 7880)
        var nlbTcpTargetGroup = new TargetGroup($"{prefix}-nlb-tcp-tg", new TargetGroupArgs
        {
            NamePrefix = "nlbtcp",
            Port = 7880,
            Protocol = "TCP",
            VpcId = vpc.Id,
            TargetType = "ip",
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Enabled = true,
                Protocol = "TCP",
                Port = "7880",
                Interval = 30,
                HealthyThreshold = 2,
                UnhealthyThreshold = 3,
            },
            Tags = Tags(config, "nlb-tcp-tg"),
        }, opts);

        // UDP target group for WebRTC media (port 7882)
        var nlbUdpTargetGroup = new TargetGroup($"{prefix}-nlb-udp-tg", new TargetGroupArgs
        {
            NamePrefix = "nlbudp",
            Port = 7882,
            Protocol = "UDP",
            VpcId = vpc.Id,
            TargetType = "ip",
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Enabled = true,
                Protocol = "TCP",  // Health check uses TCP fallback — UDP can't be probed
                Port = "7880",
                Interval = 30,
                HealthyThreshold = 2,
                UnhealthyThreshold = 3,
            },
            Tags = Tags(config, "nlb-udp-tg"),
        }, opts);

        // TCP listener for WebSocket signaling
        new Listener($"{prefix}-nlb-tcp-listener", new ListenerArgs
        {
            LoadBalancerArn = nlb.Arn,
            Port = 7880,
            Protocol = "TCP",
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = nlbTcpTargetGroup.Arn,
                },
            },
        }, opts);

        // UDP listener for WebRTC media
        new Listener($"{prefix}-nlb-udp-listener", new ListenerArgs
        {
            LoadBalancerArn = nlb.Arn,
            Port = 7882,
            Protocol = "UDP",
            DefaultActions =
            {
                new ListenerDefaultActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = nlbUdpTargetGroup.Arn,
                },
            },
        }, opts);

        // Allow NLB traffic to ECS tasks — NLBs don't have security groups,
        // so we allow the NLB CIDR (VPC CIDR) to reach ECS on the required ports.
        new SecurityGroupRule($"{prefix}-ecs-pub-nlb-tcp-7880", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "tcp", FromPort = 7880, ToPort = 7880,
            CidrBlocks = { vpcCidr },
            Description = "WebSocket signaling from NLB",
        }, opts);
        new SecurityGroupRule($"{prefix}-ecs-pub-nlb-udp-7882", new SecurityGroupRuleArgs
        {
            Type = "ingress", SecurityGroupId = ecsPublicSg.Id,
            Protocol = "udp", FromPort = 7882, ToPort = 7900,
            CidrBlocks = { "0.0.0.0/0" },
            Description = "WebRTC media UDP from NLB (NLB preserves client IP)",
        }, opts);

        // =====================================================================
        // PUBLIC DNS RECORDS
        // =====================================================================
        // Per-tenant public DNS (origin.{RootDomain}, etc.) is created by
        // AwsTenantDnsAndCertComponent. Foundation only creates auth DNS
        // if CentralAuthDomain's zone is in this account.

        // Auth DNS record — only for the shared-services deployment where Keycloak
        // is behind this ALB. Per-system deployments must NOT create this record
        // (the shared deployment already points CentralAuthDomain → the shared ALB).
        if (!isCrossAccount)
        {
            new Route53Record($"{prefix}-dns-auth", new Route53RecordArgs
            {
                ZoneId = centralAuthZone.Apply(z => z.ZoneId),
                Name = config.CentralAuthDomain,
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = publicAlb.DnsName,
                        ZoneId = publicAlb.ZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
                AllowOverwrite = true,
            }, opts);
        }

        // =====================================================================
        // PRIVATE DNS RECORDS (split-horizon for VPC/VPN)
        // =====================================================================

        new Route53Record($"{prefix}-private-dns-root", new Route53RecordArgs
        {
            ZoneId = privateZone.ZoneId,
            Name = $"{config.SystemKey}.private",
            Type = "A",
            Aliases =
            {
                new RecordAliasArgs
                {
                    Name = publicAlb.DnsName,
                    ZoneId = publicAlb.ZoneId,
                    EvaluateTargetHealth = true,
                },
            },
        }, opts);

        // auth.{systemKey}.internal in private zone — resolves to internal ALB via VPC DNS.
        // When on VPN, auth traffic resolves here instead of the public ALB,
        // so the admin console OIDC login flow stays same-origin.
        new Route53Record($"{prefix}-private-dns-auth", new Route53RecordArgs
        {
            ZoneId = privateZone.ZoneId,
            Name = $"auth.{config.SystemKey}.private",
            Type = "A",
            Aliases =
            {
                new RecordAliasArgs
                {
                    Name = internalAlb.DnsName,
                    ZoneId = internalAlb.ZoneId,
                    EvaluateTargetHealth = true,
                },
            },
        }, opts);

        // Auth paths (/realms/*, /resources/*, /js/*) are routed by CloudFront
        // directly to the shared Keycloak public ALB via the "shared-auth" origin.
        // No PrivateLink, VPC endpoints, or auth-forwarding ALB rules needed.

        // =====================================================================
        // OUTPUTS
        // =====================================================================

        return new AwsNetworkOutputs
        {
            NetworkId = vpc.Id,
            PrivateSubnetIds = Output.All(privateSubnet1.Id, privateSubnet2.Id)
                .Apply(ids => ids.ToImmutableArray()),
            PublicSubnetIds = Output.All(publicSubnet1.Id, publicSubnet2.Id)
                .Apply(ids => ids.ToImmutableArray()),
            PublicDnsZoneId = centralAuthZone.Apply(z => z.ZoneId),
            PrivateDnsZoneId = privateZone.ZoneId,
            PublicAlbArn = publicAlb.Arn,
            InternalAlbArn = internalAlb.Arn,
            PublicAlbDns = publicAlb.DnsName,
            PublicAlbZoneId = publicAlb.ZoneId,
            InternalAlbDns = internalAlb.DnsName,
            InternalAlbZoneId = internalAlb.ZoneId,
            HttpsListenerArn = httpsListener.Arn,
            InternalHttpsListenerArn = internalHttpsListener.Arn,
            EcsPublicSecurityGroupId = ecsPublicSg.Id,
            EcsPrivateSecurityGroupId = ecsPrivateSg.Id,
            AlbSecurityGroupId = albSg.Id,
            InternalAlbSecurityGroupId = internalAlbSg.Id,
            RdsSecurityGroupId = rdsSg.Id,
            EfsSecurityGroupId = efsSg.Id,
            TailscaleSecurityGroupId = tailscaleSg.Id,
            CertificateArn = certValidation.CertificateArn,
            NatGatewayId = natGw.Id,
            NlbArn = nlb.Arn,
            NlbDns = nlb.DnsName,
            NlbZoneId = nlb.ZoneId,
            NlbTcpTargetGroupArn = nlbTcpTargetGroup.Arn,
            NlbUdpTargetGroupArn = nlbUdpTargetGroup.Arn,
        };
    }

    private static InputMap<string> Tags(SystemConfig config, string resourceName) => new()
    {
        { "Name", $"{config.SystemKey}-{resourceName}" },
        { "System", config.SystemKey },
        { "Environment", config.Environment },
        { "ManagedBy", "lz-pulumi" },
    };
}
