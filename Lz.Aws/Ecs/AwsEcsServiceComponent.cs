using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ecr;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;
using Pulumi.Aws.ServiceDiscovery;
using Pulumi.Aws.ServiceDiscovery.Inputs;
using EcsService = Pulumi.Aws.Ecs.Service;
using EcsServiceArgs = Pulumi.Aws.Ecs.ServiceArgs;

namespace Lz.Aws.Ecs;

/// <summary>
/// Generic AWS ECS Fargate service component. Deploys a single service with:
/// ECR repo, log group, IAM roles, EFS access points, ALB target group + listener rule,
/// Cloud Map service discovery, task definition, and ECS service.
/// Reused for SmartStore, AppHost, and any future services.
/// </summary>
public class AwsEcsServiceComponent : IServiceComponent
{
    private readonly SystemConfig _config;

    public AwsEcsServiceComponent(SystemConfig config)
    {
        _config = config;
    }

    public IServiceOutputs Deploy(
        string serviceName,
        ServiceDefinition definition,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        IFileStorageOutputs? fileStorage)
    {
        var prefix = _config.SystemKey;
        var awsNetwork = (AwsNetworkOutputs)network;
        var awsCompute = (AwsComputeOutputs)compute;
        var awsDatabase = (Shared.AwsDatabaseOutputs)database;
        var awsFileStorage = fileStorage != null ? (Shared.AwsFileStorageOutputs)fileStorage : null;
        var ecs = _config.ECS ?? new EcsConfig();
        var (cpu, memory) = GetServiceResources(serviceName, ecs);
        // Start at 0 — services post-deploy action builds/pushes images
        // then scales to the configured count.
        var desiredCount = 0;

        // Resolve host pattern: {domain} → config.SystemDomain
        var host = definition.HostPattern.Replace("{domain}", _config.SystemDomain);

        // Which ALB to use: Internal for shop services, Public for public-facing
        var isInternal = definition.IngressType == IngressType.Internal;
        var listenerArn = isInternal ? awsNetwork.InternalHttpsListenerArn : awsNetwork.HttpsListenerArn;
        var securityGroupId = isInternal ? awsNetwork.EcsPrivateSecurityGroupId : awsNetwork.EcsPublicSecurityGroupId;

        // Container port and protocol from definition
        var containerPort = definition.Container?.Port ?? 80;
        var containerProtocol = definition.Container?.Protocol ?? "HTTP";
        var healthCheckPath = definition.Container?.HealthCheckPath ?? "/health";

        // ECR image URI
        var identity = GetCallerIdentity.Invoke();
        var imageUri = identity.Apply(id =>
            $"{id.AccountId}.dkr.ecr.{_config.Region}.amazonaws.com/{prefix}-{_config.SystemSuffix}-{_config.Environment}-{serviceName}:latest");

        // =====================================================================
        // ECR REPOSITORY
        // =====================================================================

        var ecrRepo = new Repository($"{prefix}-{serviceName}-ecr", new RepositoryArgs
        {
            Name = $"{prefix}-{_config.SystemSuffix}-{_config.Environment}-{serviceName}",
            ImageTagMutability = "MUTABLE",
            ForceDelete = _config.Environment == "dev",
            Tags = Tags(serviceName),
        });

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-{serviceName}-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}/{serviceName}",
            RetentionInDays = ecs.LogRetentionDays,
            Tags = Tags(serviceName),
        });

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
                    Policy = Output.Tuple(awsDatabase.MasterSecretArn, awsDatabase.SystemSecretArn, ecrRepo.Arn)
                        .Apply(t => $@"{{
                            ""Version"": ""2012-10-17"",
                            ""Statement"": [
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": [""secretsmanager:GetSecretValue""],
                                    ""Resource"": [""{t.Item1}"", ""{t.Item2}"", ""arn:aws:secretsmanager:*:*:secret:rds!*""]
                                }},
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": [
                                        ""ecr:GetDownloadUrlForLayer"",
                                        ""ecr:BatchGetImage"",
                                        ""ecr:BatchCheckLayerAvailability""
                                    ],
                                    ""Resource"": ""{t.Item3}""
                                }},
                                {{
                                    ""Effect"": ""Allow"",
                                    ""Action"": ""ecr:GetAuthorizationToken"",
                                    ""Resource"": ""*""
                                }}
                            ]
                        }}"),
                },
            },
            Tags = Tags(serviceName),
        });

        var taskRoleInlinePolicies = new InputList<RoleInlinePolicyArgs>();

        // EFS access policy (if volumes are defined)
        if (awsFileStorage != null && definition.Volumes.Count > 0)
        {
            taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
            {
                Name = "EfsAccess",
                Policy = awsFileStorage.FileSystemArn.Apply(arn => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Effect"": ""Allow"",
                        ""Action"": [
                            ""elasticfilesystem:ClientMount"",
                            ""elasticfilesystem:ClientWrite"",
                            ""elasticfilesystem:ClientRootAccess""
                        ],
                        ""Resource"": ""{arn}""
                    }}]
                }}"),
            });
        }

        // ECS Exec policy (for debugging)
        taskRoleInlinePolicies.Add(new RoleInlinePolicyArgs
        {
            Name = "EcsExec",
            Policy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Action"": [
                        ""ssmmessages:CreateControlChannel"",
                        ""ssmmessages:CreateDataChannel"",
                        ""ssmmessages:OpenControlChannel"",
                        ""ssmmessages:OpenDataChannel""
                    ],
                    ""Resource"": ""*""
                }]
            }",
        });

        var taskRole = new Role($"{prefix}-{serviceName}-task-role", new RoleArgs
        {
            Name = $"{prefix}-{serviceName}-task-role",
            AssumeRolePolicy = EcsAssumeRolePolicy,
            InlinePolicies = taskRoleInlinePolicies,
            Tags = Tags(serviceName),
        });

        // =====================================================================
        // EFS ACCESS POINTS (one per VolumeMount)
        // =====================================================================

        var accessPoints = new Dictionary<string, AccessPoint>();
        if (awsFileStorage != null)
        {
            foreach (var vol in definition.Volumes)
            {
                var apName = $"{prefix}-{serviceName}-{vol.Name}-ap";
                var ap = new AccessPoint(apName, new AccessPointArgs
                {
                    FileSystemId = awsFileStorage.FileSystemId,
                    PosixUser = new AccessPointPosixUserArgs
                    {
                        Uid = 1000,
                        Gid = 1000,
                    },
                    RootDirectory = new AccessPointRootDirectoryArgs
                    {
                        Path = $"/{prefix}{vol.EfsPath}",
                        CreationInfo = new AccessPointRootDirectoryCreationInfoArgs
                        {
                            OwnerUid = 1000,
                            OwnerGid = 1000,
                            Permissions = "755",
                        },
                    },
                    Tags = Tags(serviceName, vol.Name),
                });
                accessPoints[vol.Name] = ap;
            }
        }

        // =====================================================================
        // TARGET GROUP
        // =====================================================================

        var targetGroup = new TargetGroup($"{prefix}-{serviceName}-tg", new TargetGroupArgs
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
            Tags = Tags(serviceName),
        });

        // =====================================================================
        // LISTENER RULE (priority 20 — after Keycloak rules at 2/5/10/12)
        // =====================================================================

        var listenerRule = new ListenerRule($"{prefix}-{serviceName}-rule", new ListenerRuleArgs
        {
            ListenerArn = listenerArn,
            Priority = GetListenerPriority(serviceName, ecs),
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
                    TargetGroupArn = targetGroup.Arn,
                },
            },
        });

        // =====================================================================
        // SERVICE DISCOVERY
        // =====================================================================

        var serviceDiscovery = new Pulumi.Aws.ServiceDiscovery.Service(
            $"{prefix}-{serviceName}-discovery",
            new Pulumi.Aws.ServiceDiscovery.ServiceArgs
            {
                Name = serviceName,
                Description = $"{serviceName} service",
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
            });

        // =====================================================================
        // TASK DEFINITION
        // =====================================================================

        // Build EFS volumes for the task definition
        var taskVolumes = new InputList<TaskDefinitionVolumeArgs>();
        foreach (var vol in definition.Volumes)
        {
            if (accessPoints.TryGetValue(vol.Name, out var ap))
            {
                taskVolumes.Add(new TaskDefinitionVolumeArgs
                {
                    Name = vol.Name,
                    EfsVolumeConfiguration = new TaskDefinitionVolumeEfsVolumeConfigurationArgs
                    {
                        FileSystemId = awsFileStorage!.FileSystemId,
                        TransitEncryption = "ENABLED",
                        AuthorizationConfig = new TaskDefinitionVolumeEfsVolumeConfigurationAuthorizationConfigArgs
                        {
                            AccessPointId = ap.Id,
                            Iam = "ENABLED",
                        },
                    },
                });
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
                imageUri, database.Endpoint, awsDatabase.MasterSecretArn, awsDatabase.SystemSecretArn
            ).Apply(t =>
            {
                var (image, dbHost, masterSecretArn, systemSecretArn) = t;

                var mountPoints = definition.Volumes.Select(v => new
                {
                    sourceVolume = v.Name,
                    containerPath = v.ContainerPath,
                }).ToArray();

                var envVars = new List<object>
                {
                    new { name = "ASPNETCORE_ENVIRONMENT", value = _config.Environment == "dev" ? "Development" : "Production" },
                    new { name = "LZ_SYSTEM_KEY", value = prefix },
                    new { name = "LZ_SYSTEM_DOMAIN", value = _config.SystemDomain },
                    new { name = "LZ_SERVICE_NAME", value = serviceName },
                    new { name = "AWS_REGION", value = _config.Region },
                };

                // Add DB host for services that require it
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

                return System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = serviceName,
                        image,
                        essential = true,
                        portMappings = new[]
                        {
                            new { containerPort, protocol = "tcp" },
                        },
                        mountPoints,
                        environment = envVars.ToArray(),
                        secrets = secrets.ToArray(),
                        logConfiguration = new
                        {
                            logDriver = "awslogs",
                            options = new Dictionary<string, string>
                            {
                                ["awslogs-group"] = $"/ecs/{prefix}/{serviceName}",
                                ["awslogs-region"] = _config.Region,
                                ["awslogs-stream-prefix"] = serviceName,
                            },
                        },
                        healthCheck = new
                        {
                            command = new[] { "CMD-SHELL", $"curl -sf http://localhost:{containerPort}{healthCheckPath} || exit 1" },
                            interval = 30,
                            timeout = 10,
                            retries = 3,
                            startPeriod = 120,
                        },
                    },
                });
            }),
            Tags = Tags(serviceName),
        });

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
            LoadBalancers =
            {
                new ServiceLoadBalancerArgs
                {
                    ContainerName = serviceName,
                    ContainerPort = containerPort,
                    TargetGroupArn = targetGroup.Arn,
                },
            },
            ServiceRegistries = new ServiceServiceRegistriesArgs
            {
                RegistryArn = serviceDiscovery.Arn,
            },
            Tags = Tags(serviceName),
        }, new CustomResourceOptions
        {
            DependsOn = { listenerRule },
        });

        var endpoint = isInternal
            ? $"https://{host}"
            : $"https://{host}";

        return new AwsServiceOutputs
        {
            ServiceId = service.Id,
            Endpoint = Output.Create(endpoint),
        };
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    private static (int cpu, int memory) GetServiceResources(string serviceName, EcsConfig ecs)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "smartstore" => (ecs.SmartStoreCpu, ecs.SmartStoreMemory),
            "apphost" => (ecs.AppHostCpu, ecs.AppHostMemory),
            _ => (256, 512),
        };
    }

    /// <summary>
    /// Listener rule priorities from config. Keycloak uses 2/5/10/12.
    /// System services default: SmartStore=20, AppHost=30.
    /// Configurable via ECS.ListenerPriorities in systemconfig/tenantconfig.
    /// </summary>
    private static int GetListenerPriority(string serviceName, EcsConfig ecs)
    {
        var priorities = ecs.ListenerPriorities ?? new ListenerPrioritiesConfig();
        return serviceName.ToLowerInvariant() switch
        {
            "smartstore" => priorities.SmartStore,
            "apphost" => priorities.AppHost,
            _ => 30,
        };
    }

    private InputMap<string> Tags(string serviceName, string? extra = null)
    {
        var tags = new InputMap<string>
        {
            { "System", _config.SystemKey },
            { "Service", serviceName },
            { "ManagedBy", "lz-pulumi" },
        };
        if (extra != null)
            tags.Add("Volume", extra);
        return tags;
    }

    private static string TruncateName(string name, int maxLen)
        => name.Length <= maxLen ? name : name[..maxLen];

    private const string EcsAssumeRolePolicy = @"{
        ""Version"": ""2012-10-17"",
        ""Statement"": [{
            ""Effect"": ""Allow"",
            ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
            ""Action"": ""sts:AssumeRole""
        }]
    }";
}
