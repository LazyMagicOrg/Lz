using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;
using Pulumi.Aws.Route53.Inputs;
using Route53Record = Pulumi.Aws.Route53.Record;
using Route53RecordArgs = Pulumi.Aws.Route53.RecordArgs;
using Pulumi.Aws.ServiceDiscovery;
using Pulumi.Aws.ServiceDiscovery.Inputs;
using EcsService = Pulumi.Aws.Ecs.Service;
using EcsServiceArgs = Pulumi.Aws.Ecs.ServiceArgs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS ECS tenant service component — deploys a per-tenant ECS Fargate service
/// with ECR repo, log group, IAM roles, ALB listener rule, Cloud Map registration,
/// ECS task definition (with tenant-specific EFS mounts and secrets), and ECS service.
/// Follows the same patterns as AwsEcsServiceComponent but scoped to a single tenant.
/// </summary>
public class AwsEcsTenantServiceComponent : ComponentResource, ITenantServiceComponent
{
    public AwsEcsTenantServiceComponent()
        : base("lz:aws:EcsTenantService", "tenantservice", ResourceArgs.Empty, null)
    {
    }

    public IServiceOutputs Deploy(
        string serviceName,
        ServiceDefinition definition,
        TenantConfig tenantConfig,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        ITenantDataOutputs tenantData)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var prefix = $"{sk}-{tk}";
        var suffix = tenantConfig.TenantSuffix;
        var awsNetwork = (AwsNetworkOutputs)network;
        var awsCompute = (AwsComputeOutputs)compute;
        var awsDatabase = (Shared.AwsDatabaseOutputs)database;
        var ecs = tenantConfig.ECS ?? new EcsConfig();
        var (cpu, memory) = GetServiceResources(serviceName, ecs);
        // Start at 0 — post-deploy builds/pushes images, then scales up.
        var desiredCount = 0;

        // Resolve host pattern: {domain} → tenant RootDomain
        var host = definition.HostPattern.Replace("{domain}", tenantConfig.RootDomain);

        // ALB selection: internal for service-layer, public for host-layer
        var isInternal = definition.IngressType == IngressType.Internal;
        var listenerArn = isInternal ? awsNetwork.InternalHttpsListenerArn : awsNetwork.HttpsListenerArn;
        var securityGroupId = isInternal ? awsNetwork.EcsPrivateSecurityGroupId : awsNetwork.EcsPublicSecurityGroupId;

        // Container settings
        var containerPort = definition.Container?.Port ?? 80;
        var containerProtocol = definition.Container?.Protocol ?? "HTTP";
        var healthCheckPath = definition.Container?.HealthCheckPath ?? "/health";

        // ECR image URI — repo is created by `lz deploycontainer`, not by Pulumi
        var ecrName = $"{sk}-{suffix}-{env}-{tk}-{serviceName}";
        var region = tenantConfig.Region ?? "us-west-2";
        var identity = GetCallerIdentity.Invoke();
        var imageUri = identity.Apply(id =>
            $"{id.AccountId}.dkr.ecr.{region}.amazonaws.com/{ecrName}:latest");
        var ecrRepoArn = identity.Apply(id =>
            $"arn:aws:ecr:{region}:{id.AccountId}:repository/{ecrName}");

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-{serviceName}-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}/{serviceName}",
            RetentionInDays = ecs.LogRetentionDays,
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM ROLES
        // =====================================================================

        var executionRole = new Role($"{prefix}-{serviceName}-exec-role", new RoleArgs
        {
            Name = $"{prefix}-{serviceName}-exec-role",
            AssumeRolePolicy = EcsAssumeRolePolicy,
            ManagedPolicyArns =
            {
                "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy",
            },
            InlinePolicies =
            {
                new RoleInlinePolicyArgs
                {
                    Name = "SecretsAndEcrAccess",
                    Policy = Output.Tuple(
                        awsDatabase.MasterSecretArn,
                        awsDatabase.SystemSecretArn,
                        tenantData.TenantSecretId,
                        ecrRepoArn
                    ).Apply(t =>
                    {
                        var secretResources = $@"""{t.Item1}"", ""{t.Item2}"", ""arn:aws:secretsmanager:*:*:secret:{sk}/{tk}*"", ""arn:aws:secretsmanager:*:*:secret:rds!*""";
                        if (!string.IsNullOrEmpty(tenantConfig.SharedSecretArn))
                            secretResources += $@", ""{tenantConfig.SharedSecretArn}*""";

                        var kmsStatement = "";
                        if (!string.IsNullOrEmpty(tenantConfig.SharedKmsKeyArn))
                        {
                            kmsStatement = $@",
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": [""kms:Decrypt"", ""kms:DescribeKey""],
                                    ""Resource"": ""{tenantConfig.SharedKmsKeyArn}""
                                }}";
                        }

                        return $@"{{
                            ""Version"": ""2012-10-17"",
                            ""Statement"": [
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": [""secretsmanager:GetSecretValue""],
                                    ""Resource"": [{secretResources}]
                                }},
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": [
                                        ""ecr:GetDownloadUrlForLayer"",
                                        ""ecr:BatchGetImage"",
                                        ""ecr:BatchCheckLayerAvailability""
                                    ],
                                    ""Resource"": ""{t.Item4}""
                                }},
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": ""ecr:GetAuthorizationToken"",
                                    ""Resource"": ""*""
                                }}{kmsStatement}
                            ]
                        }}";
                    }),
                },
            },
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        var taskRoleInlinePolicies = new InputList<RoleInlinePolicyArgs>();

        // EFS access policy for tenant volumes
        taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
        {
            Name = "EfsAccess",
            Policy = tenantData.FileSystemId.Apply(fsId =>
            {
                // Use wildcard for the EFS ARN since we have the file system ID
                return $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Effect"": ""Allow"",
                        ""Action"": [
                            ""elasticfilesystem:ClientMount"",
                            ""elasticfilesystem:ClientWrite"",
                            ""elasticfilesystem:ClientRootAccess""
                        ],
                        ""Resource"": ""*"",
                        ""Condition"": {{
                            ""StringEquals"": {{
                                ""elasticfilesystem:AccessPointArn"": ""arn:aws:elasticfilesystem:*:*:access-point/*""
                            }}
                        }}
                    }}]
                }}";
            }),
        });

        // ECS Exec policy (for debugging) + SSM Parameter Store read (for tenant config)
        taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
        {
            Name = "EcsExecAndSsm",
            Policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    {{
                        ""Effect"": ""Allow"",
                        ""Action"": [
                            ""ssmmessages:CreateControlChannel"",
                            ""ssmmessages:CreateDataChannel"",
                            ""ssmmessages:OpenControlChannel"",
                            ""ssmmessages:OpenDataChannel""
                        ],
                        ""Resource"": ""*""
                    }},
                    {{
                        ""Effect"": ""Allow"",
                        ""Action"": ""ssm:GetParameter"",
                        ""Resource"": ""arn:aws:ssm:*:*:parameter/{sk}/{tk}/*""
                    }}
                ]
            }}",
        });

        // Secrets Manager runtime access (for application-level secret reads)
        taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
        {
            Name = "SecretsManagerRead",
            Policy = Output.Tuple(
                awsDatabase.MasterSecretArn,
                awsDatabase.SystemSecretArn
            ).Apply(t =>
            {
                var secretResources = $@"""{t.Item1}"", ""{t.Item2}"", ""arn:aws:secretsmanager:*:*:secret:{sk}/{tk}*"", ""arn:aws:secretsmanager:*:*:secret:rds!*""";
                if (!string.IsNullOrEmpty(tenantConfig.SharedSecretArn))
                    secretResources += $@", ""{tenantConfig.SharedSecretArn}*""";

                var kmsStatement = "";
                if (!string.IsNullOrEmpty(tenantConfig.SharedKmsKeyArn))
                {
                    kmsStatement = $@",
                        {{
                            ""Effect"": ""Allow"",
                            ""Action"": [""kms:Decrypt"", ""kms:DescribeKey""],
                            ""Resource"": ""{tenantConfig.SharedKmsKeyArn}""
                        }}";
                }

                return $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [
                        {{
                            ""Effect"": ""Allow"",
                            ""Action"": [""secretsmanager:GetSecretValue""],
                            ""Resource"": [{secretResources}]
                        }}{kmsStatement}
                    ]
                }}";
            }),
        });

        // S3 write access for Explore Pages publish (SmartStore → S3 assets bucket)
        var assetsBucketName = $"{sk}-{tk}-{suffix}-{env}-assets";
        taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
        {
            Name = "S3ExplorePublish",
            Policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""s3:PutObject"", ""s3:GetObject"", ""s3:ListBucket""],
                    ""Resource"": [
                        ""arn:aws:s3:::{assetsBucketName}"",
                        ""arn:aws:s3:::{assetsBucketName}/wwwroot/explore/*""
                    ]
                }}]
            }}",
        });

        var taskRole = new Role($"{prefix}-{serviceName}-task-role", new RoleArgs
        {
            Name = $"{prefix}-{serviceName}-task-role",
            AssumeRolePolicy = EcsAssumeRolePolicy,
            InlinePolicies = taskRoleInlinePolicies,
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // TARGET GROUP
        // =====================================================================

        var targetGroup = new TargetGroup($"{prefix}-{serviceName}-tg", new TargetGroupArgs
        {
            // Use NamePrefix so AWS appends a unique suffix.
            // This allows create-before-delete replacements: Pulumi creates a new TG,
            // updates the listener rule to point to it, then deletes the old TG.
            // Using a fixed Name causes delete failures when the old TG is still
            // referenced by a listener rule during replacement.
            NamePrefix = TruncateName($"{prefix}-{serviceName}-", 6),
            Port = containerPort,
            Protocol = containerProtocol,
            VpcId = network.NetworkId,
            TargetType = "ip",
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Enabled = true,
                Path = healthCheckPath,
                Protocol = containerProtocol,
                Port = "traffic-port",
                Interval = 30,
                Timeout = 10,
                HealthyThreshold = 2,
                UnhealthyThreshold = 5,
            },
            DeregistrationDelay = 30,
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // LISTENER RULE (tenant-specific priority offset from system priorities)
        // =====================================================================

        var priorities = ecs.ListenerPriorities ?? new ListenerPrioritiesConfig();
        var basePriority = GetServiceBasePriority(serviceName, priorities);

        var listenerRule = new ListenerRule($"{prefix}-{serviceName}-rule", new ListenerRuleArgs
        {
            ListenerArn = listenerArn,
            Priority = basePriority,
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    HostHeader = new ListenerRuleConditionHostHeaderArgs
                    {
                        // Internal services also match wildcard subdomains (e.g. *.shop.{domain})
                        Values = isInternal ? new[] { host, $"*.{host}" } : new[] { host },
                    },
                },
            },
            Actions =
            {
                new ListenerRuleActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = targetGroup.Arn,
                },
            },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // PRIVATE DNS (internal services need Route53 records for VPN access)
        // =====================================================================

        if (isInternal)
        {
            // Private Route53 ALIAS record: {host} → internal ALB
            var privateDnsRecord = new Route53Record($"{prefix}-{serviceName}-private-dns", new Route53RecordArgs
            {
                ZoneId = awsNetwork.PrivateDnsZoneId,
                Name = host,
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, new CustomResourceOptions { Parent = this });

            // Private Route53 wildcard ALIAS record: *.{host} → internal ALB
            var privateWildcardDnsRecord = new Route53Record($"{prefix}-{serviceName}-private-wildcard-dns", new Route53RecordArgs
            {
                ZoneId = awsNetwork.PrivateDnsZoneId,
                Name = $"*.{host}",
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // VPN ACCESS (public services: internal ALB + private Route53 record)
        // =====================================================================

        TargetGroup? vpnTargetGroup = null;

        if (GetVpnAccess(serviceName, ecs) && !isInternal)
        {
            // Second target group for VPN access via internal ALB
            vpnTargetGroup = new TargetGroup($"{prefix}-{serviceName}-vpn-tg", new TargetGroupArgs
            {
                NamePrefix = TruncateName($"{prefix}-{serviceName}-", 6),
                Port = containerPort,
                Protocol = containerProtocol,
                VpcId = network.NetworkId,
                TargetType = "ip",
                HealthCheck = new TargetGroupHealthCheckArgs
                {
                    Enabled = true,
                    Path = healthCheckPath,
                    Protocol = containerProtocol,
                    Port = "traffic-port",
                    Interval = 30,
                    Timeout = 10,
                    HealthyThreshold = 2,
                    UnhealthyThreshold = 5,
                },
                DeregistrationDelay = 30,
                Tags = Tags(sk, tk, $"{serviceName}-vpn"),
            }, new CustomResourceOptions { Parent = this });

            // Listener rule on internal ALB for VPN access
            var vpnListenerRule = new ListenerRule($"{prefix}-{serviceName}-vpn-rule", new ListenerRuleArgs
            {
                ListenerArn = awsNetwork.InternalHttpsListenerArn,
                Priority = basePriority + 100, // offset to avoid collision with other internal rules
                Conditions =
                {
                    new ListenerRuleConditionArgs
                    {
                        HostHeader = new ListenerRuleConditionHostHeaderArgs
                        {
                            Values = { host },
                        },
                    },
                },
                Actions =
                {
                    new ListenerRuleActionArgs
                    {
                        Type = "forward",
                        TargetGroupArn = vpnTargetGroup.Arn,
                    },
                },
            }, new CustomResourceOptions { Parent = this });

            // Private Route53 ALIAS record: shop.{domain} → internal ALB
            var vpnDnsRecord = new Route53Record($"{prefix}-{serviceName}-vpn-dns", new Route53RecordArgs
            {
                ZoneId = awsNetwork.PrivateDnsZoneId,
                Name = host,
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // SERVICE DISCOVERY
        // =====================================================================

        var discoveryName = serviceName == "smartstore"
            ? (ecs.SmartStoreServiceDiscoveryName ?? $"{tk}-{serviceName}")
            : (ecs.AppHostServiceDiscoveryName ?? $"{tk}-{serviceName}");

        var serviceDiscovery = new Pulumi.Aws.ServiceDiscovery.Service(
            $"{prefix}-{serviceName}-discovery",
            new Pulumi.Aws.ServiceDiscovery.ServiceArgs
            {
                Name = discoveryName,
                Description = $"{tk} {serviceName} service",
                NamespaceId = awsCompute.CloudMapNamespaceId,
                DnsConfig = new ServiceDnsConfigArgs
                {
                    NamespaceId = awsCompute.CloudMapNamespaceId,
                    DnsRecords =
                    {
                        new ServiceDnsConfigDnsRecordArgs { Type = "A", Ttl = 60 },
                    },
                    RoutingPolicy = "MULTIVALUE",
                },
                HealthCheckCustomConfig = new ServiceHealthCheckCustomConfigArgs
                {
                    FailureThreshold = 1,
                },
            }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // TASK DEFINITION
        // =====================================================================

        // Build EFS volumes — map tenant data access points to container mount points
        var taskVolumes = new InputList<TaskDefinitionVolumeArgs>();
        if (definition.Volumes.Count > 0)
        {
            // Map EFS paths to tenant-specific access points
            // EfsPath (e.g., "/smartstore-data") is the unique key per access point
            var apMapping = new Dictionary<string, Output<string>>
            {
                ["/smartstore-data"] = tenantData.SmartStoreDataAccessPointId,
                ["/smartstore-config"] = tenantData.SmartStoreConfigAccessPointId,
                ["/smartstore-dataprotection"] = tenantData.SmartStoreDataProtectionAccessPointId,
                ["/apphost-config"] = tenantData.AppHostConfigAccessPointId,
            };

            foreach (var vol in definition.Volumes)
            {
                if (apMapping.TryGetValue(vol.EfsPath, out var apId))
                {
                    taskVolumes.Add(new TaskDefinitionVolumeArgs
                    {
                        Name = vol.Name,
                        EfsVolumeConfiguration = new TaskDefinitionVolumeEfsVolumeConfigurationArgs
                        {
                            FileSystemId = tenantData.FileSystemId,
                            TransitEncryption = "ENABLED",
                            AuthorizationConfig = new TaskDefinitionVolumeEfsVolumeConfigurationAuthorizationConfigArgs
                            {
                                AccessPointId = apId,
                                Iam = "ENABLED",
                            },
                        },
                    });
                }
            }
        }

        var taskDef = new TaskDefinition($"{prefix}-{serviceName}-task", new TaskDefinitionArgs
        {
            Family = $"{prefix}-{serviceName}",
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            Cpu = cpu.ToString(),
            Memory = memory.ToString(),
            ExecutionRoleArn = executionRole.Arn,
            TaskRoleArn = taskRole.Arn,
            Volumes = taskVolumes,
            ContainerDefinitions = Output.Tuple(
                imageUri, database.Endpoint, awsDatabase.MasterSecretArn, tenantData.TenantSecretId
            ).Apply(t =>
            {
                var (image, dbHost, masterSecretArn, tenantSecretId) = t;

                var mountPoints = definition.Volumes.Select(v => new
                {
                    sourceVolume = v.Name,
                    containerPath = v.ContainerPath,
                }).ToArray();

                // Explicitly set Kestrel's listening URL to match the container port/protocol
                var scheme = containerProtocol.ToLowerInvariant();
                var aspnetUrls = $"{scheme}://+:{containerPort}";

                var envVars = new List<object>
                {
                    new { name = "ASPNETCORE_ENVIRONMENT", value = env == "dev" ? "Development" : "Production" },
                    new { name = "ASPNETCORE_URLS", value = aspnetUrls },
                    new { name = "LZ_SYSTEM_KEY", value = sk },
                    new { name = "LZ_TENANT_KEY", value = tk },
                    new { name = "LZ_ENVIRONMENT", value = env },
                    new { name = "LZ_SERVICE_NAME", value = serviceName },
                    new { name = "AWS_REGION", value = region },
                    new { name = "APPHOST_DATA_PATH", value = "/app" },
                    new { name = "LZ_ASSETS_BUCKET", value = $"{sk}-{tk}-{suffix}-{env}-assets" },
                };

                if (definition.RequiresDatabase)
                {
                    envVars.Add(new { name = "DB_HOST", value = dbHost });
                    envVars.Add(new { name = "DB_PORT", value = "5432" });
                }

                var secrets = new List<object>
                {
                    new { name = "DB_USERNAME", valueFrom = $"{masterSecretArn}:username::" },
                    new { name = "DB_PASSWORD", valueFrom = $"{masterSecretArn}:password::" },
                };

                // Cross-account Keycloak admin credentials from shared secret
                if (!string.IsNullOrEmpty(tenantConfig.SharedSecretArn))
                {
                    secrets.Add(new { name = "KC_ADMIN_USERNAME", valueFrom = $"{tenantConfig.SharedSecretArn}:keycloak-admin-username::" });
                    secrets.Add(new { name = "KC_ADMIN_PASSWORD", valueFrom = $"{tenantConfig.SharedSecretArn}:keycloak-admin-password::" });
                }

                return System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = serviceName,
                        image,
                        essential = true,
                        portMappings = BuildPortMappings(containerPort, definition),
                        mountPoints,
                        environment = envVars.ToArray(),
                        secrets = secrets.ToArray(),
                        logConfiguration = new
                        {
                            logDriver = "awslogs",
                            options = new Dictionary<string, string>
                            {
                                ["awslogs-group"] = $"/ecs/{prefix}/{serviceName}",
                                ["awslogs-region"] = region,
                                ["awslogs-stream-prefix"] = serviceName,
                            },
                        },
                        healthCheck = BuildHealthCheck(containerPort, definition),
                    },
                });
            }),
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ECS SERVICE
        // =====================================================================

        var service = new EcsService($"{prefix}-{serviceName}-service", new EcsServiceArgs
        {
            Name = $"{prefix}-{serviceName}",
            Cluster = awsCompute.ClusterArn,
            TaskDefinition = taskDef.Arn,
            DesiredCount = desiredCount,
            LaunchType = "FARGATE",
            PlatformVersion = "LATEST",
            HealthCheckGracePeriodSeconds = 120,
            EnableExecuteCommand = true,
            DeploymentMaximumPercent = 200,
            DeploymentMinimumHealthyPercent = 100,
            DeploymentCircuitBreaker = new ServiceDeploymentCircuitBreakerArgs
            {
                Enable = true,
                Rollback = true,
            },
            NetworkConfiguration = new ServiceNetworkConfigurationArgs
            {
                AssignPublicIp = false,
                Subnets = network.PrivateSubnetIds.Apply(ids => ids.AsEnumerable().ToList()),
                SecurityGroups = { securityGroupId },
            },
            LoadBalancers = BuildLoadBalancers(serviceName, containerPort, definition, targetGroup, vpnTargetGroup, awsNetwork),
            ServiceRegistries = new ServiceServiceRegistriesArgs
            {
                RegistryArn = serviceDiscovery.Arn,
            },
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions
        {
            Parent = this,
            DependsOn = { listenerRule },
        });

        return new AwsEcsTenantServiceOutputs(
            serviceId: service.Id,
            endpoint: Output.Create($"https://{host}"));
    }

    private static (int cpu, int memory) GetServiceResources(string serviceName, EcsConfig ecs)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "smartstore" => (ecs.SmartStoreCpu, ecs.SmartStoreMemory),
            "apphost" => (ecs.AppHostCpu, ecs.AppHostMemory),
            "livekit" => (ecs.LiveKitCpu, ecs.LiveKitMemory),
            _ => (256, 512),
        };
    }

    private static bool GetVpnAccess(string serviceName, EcsConfig ecs)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "smartstore" => ecs.SmartStoreVpnAccess,
            _ => false,
        };
    }

    private static int GetServiceBasePriority(string serviceName, ListenerPrioritiesConfig priorities)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "smartstore" => priorities.SmartStore,
            "apphost" => priorities.AppHost,
            _ => 50,
        };
    }

    private static InputList<ServiceLoadBalancerArgs> BuildLoadBalancers(
        string serviceName, int containerPort, ServiceDefinition definition,
        TargetGroup primaryTg, TargetGroup? vpnTg, AwsNetworkOutputs awsNetwork)
    {
        var lbs = new InputList<ServiceLoadBalancerArgs>
        {
            new ServiceLoadBalancerArgs
            {
                ContainerName = serviceName,
                ContainerPort = containerPort,
                TargetGroupArn = primaryTg.Arn,
            },
        };

        if (vpnTg != null)
        {
            lbs.Add(new ServiceLoadBalancerArgs
            {
                ContainerName = serviceName,
                ContainerPort = containerPort,
                TargetGroupArn = vpnTg.Arn,
            });
        }

        // Register with NLB target groups for services with UDP ports
        // Guard: only register if NLB target groups are available (created by deployfoundation)
        var additionalPorts = definition.Container?.AdditionalPorts ?? new();
        var nlbTcpArn = awsNetwork.NlbTcpTargetGroupArn;
        var nlbUdpArn = awsNetwork.NlbUdpTargetGroupArn;

        bool hasNlb = nlbTcpArn != null;
        bool hasUdp = additionalPorts.Any(p => p.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase));
        bool hasTcp7880 = additionalPorts.Any(p => p.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase) && p.Port == 7880);

        if (hasTcp7880 && hasNlb)
        {
            lbs.Add(new ServiceLoadBalancerArgs
            {
                ContainerName = serviceName,
                ContainerPort = 7880,
                TargetGroupArn = nlbTcpArn!,
            });
        }
        if (hasUdp && hasNlb)
        {
            var udpPort = additionalPorts.First(p => p.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase));
            lbs.Add(new ServiceLoadBalancerArgs
            {
                ContainerName = serviceName,
                ContainerPort = udpPort.Port,
                TargetGroupArn = nlbUdpArn!,
            });
        }

        return lbs;
    }

    /// <summary>
    /// Build port mappings for the ECS task definition, supporting additional TCP/UDP ports.
    /// </summary>
    private static object[] BuildPortMappings(int primaryPort, ServiceDefinition definition)
    {
        var mappings = new List<object>
        {
            new { containerPort = primaryPort, protocol = "tcp" },
        };

        foreach (var pm in definition.Container?.AdditionalPorts ?? new())
        {
            if (pm.ToPort.HasValue)
            {
                // Port range — add each port individually (ECS requires individual mappings)
                for (int p = pm.Port; p <= pm.ToPort.Value; p++)
                {
                    mappings.Add(new { containerPort = p, hostPort = p, protocol = pm.Protocol.ToLowerInvariant() });
                }
            }
            else
            {
                mappings.Add(new { containerPort = pm.Port, hostPort = pm.Port, protocol = pm.Protocol.ToLowerInvariant() });
            }
        }

        return mappings.ToArray();
    }

    /// <summary>
    /// Build health check for the ECS task definition.
    /// Services with a HealthCheckPath use curl; others use TCP socket test.
    /// </summary>
    private static object BuildHealthCheck(int containerPort, ServiceDefinition definition)
    {
        var hasAdditionalPorts = (definition.Container?.AdditionalPorts?.Count ?? 0) > 0;

        // Services with additional ports (like LiveKit) often use HTTP health endpoints
        // rather than raw TCP socket tests
        if (hasAdditionalPorts && definition.Container?.HealthCheckPath != null)
        {
            return new
            {
                command = new[] { "CMD-SHELL", $"curl -f http://localhost:{containerPort}{definition.Container.HealthCheckPath} || exit 1" },
                interval = 30,
                timeout = 10,
                retries = 3,
                startPeriod = 120,
            };
        }

        return new
        {
            command = new[] { "CMD-SHELL", $"bash -c '(echo > /dev/tcp/localhost/{containerPort}) 2>/dev/null' || exit 1" },
            interval = 30,
            timeout = 10,
            retries = 3,
            startPeriod = 120,
        };
    }

    private static string TruncateName(string name, int maxLen)
        => name.Length <= maxLen ? name : name[..maxLen];

    private static InputMap<string> Tags(string systemKey, string tenantKey, string serviceName) => new()
    {
        { "System", systemKey },
        { "Tenant", tenantKey },
        { "Service", serviceName },
        { "ManagedBy", "lz-pulumi" },
    };

    private const string EcsAssumeRolePolicy = @"{
        ""Version"": ""2012-10-17"",
        ""Statement"": [{
            ""Effect"": ""Allow"",
            ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
            ""Action"": ""sts:AssumeRole""
        }]
    }";
}

internal class AwsEcsTenantServiceOutputs : IServiceOutputs
{
    public Output<string> ServiceId { get; }
    public Output<string> Endpoint { get; }

    public AwsEcsTenantServiceOutputs(Output<string> serviceId, Output<string> endpoint)
    {
        ServiceId = serviceId;
        Endpoint = endpoint;
    }
}
