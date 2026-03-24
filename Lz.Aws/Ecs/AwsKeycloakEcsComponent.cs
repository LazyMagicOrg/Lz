using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
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
/// AWS Keycloak ECS component — task definition, service, target groups,
/// listener rules, service discovery, IAM roles, and log groups.
/// </summary>
public class AwsKeycloakEcsComponent : ComponentResource, IAuthServiceComponent
{
    public AwsKeycloakEcsComponent()
        : base("lz:aws:KeycloakEcs", "keycloak", ResourceArgs.Empty, null)
    {
    }

    public IServiceOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage,
        bool enableAdminBlocking)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsNetworkOutputs)network;
        var awsCompute = (AwsComputeOutputs)compute;
        var awsDatabase = (Lz.Aws.Shared.AwsDatabaseOutputs)database;
        var awsFileStorage = (Lz.Aws.Shared.AwsFileStorageOutputs)fileStorage;
        var ecs = config.ECS ?? new EcsConfig();
        var logRetention = ecs.LogRetentionDays;
        var themeName = Path.GetFileName(ecs.KeycloakThemePath.TrimEnd('/'));

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-keycloak-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}/keycloak",
            RetentionInDays = logRetention,
            Tags =
            {
                { "System", prefix },
                { "Service", "keycloak" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // =====================================================================
        // IAM ROLES
        // =====================================================================

        // Resolve KMS key for the system secret (same approach as Tailscale ASG).
        // Shared deployments use a custom KMS key (alias/shared-secrets-key) for
        // cross-account access; the execution role needs kms:Decrypt to pull
        // secret values at task startup.
        Input<string> secretsPolicy;
        if (config.TrustedAccountIds.Count > 0)
        {
            secretsPolicy = Output.Tuple(
                awsDatabase.MasterSecretArn,
                awsDatabase.SystemSecretArn,
                Pulumi.Aws.Kms.GetAlias.Invoke(new Pulumi.Aws.Kms.GetAliasInvokeArgs
                {
                    Name = "alias/shared-secrets-key",
                }).Apply(a => a.TargetKeyArn)
            ).Apply(t => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    {{
                        ""Effect"": ""Allow"",
                        ""Action"": [""secretsmanager:GetSecretValue""],
                        ""Resource"": [""{t.Item1}"", ""{t.Item2}"", ""arn:aws:secretsmanager:*:*:secret:rds!*""]
                    }},
                    {{
                        ""Effect"": ""Allow"",
                        ""Action"": [""kms:Decrypt"", ""kms:DescribeKey""],
                        ""Resource"": ""{t.Item3}""
                    }}
                ]
            }}");
        }
        else
        {
            secretsPolicy = Output.Tuple(awsDatabase.MasterSecretArn, awsDatabase.SystemSecretArn)
                .Apply(t => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Effect"": ""Allow"",
                        ""Action"": [""secretsmanager:GetSecretValue""],
                        ""Resource"": [""{t.Item1}"", ""{t.Item2}"", ""arn:aws:secretsmanager:*:*:secret:rds!*""]
                    }}]
                }}");
        }

        var executionRole = new Role($"{prefix}-keycloak-execution-role", new RoleArgs
        {
            Name = $"{prefix}-keycloak-execution-role",
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            ManagedPolicyArns =
            {
                "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy",
            },
            InlinePolicies =
            {
                new RoleInlinePolicyArgs
                {
                    Name = "SecretsAccess",
                    Policy = secretsPolicy,
                },
            },
            Tags =
            {
                { "System", prefix },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        var taskRole = new Role($"{prefix}-keycloak-task-role", new RoleArgs
        {
            Name = $"{prefix}-keycloak-task-role",
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            InlinePolicies =
            {
                new RoleInlinePolicyArgs
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
                },
            },
            Tags =
            {
                { "System", prefix },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // =====================================================================
        // TARGET GROUPS
        // =====================================================================

        var publicTg = new TargetGroup($"{prefix}-keycloak-tg", new TargetGroupArgs
        {
            Name = $"{prefix}-keycloak-tg",
            Port = 8080,
            Protocol = "HTTP",
            VpcId = network.NetworkId,
            TargetType = "ip",
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Enabled = true,
                Path = "/health/ready",
                Protocol = "HTTP",
                Port = "9000",
                Interval = 30,
                Timeout = 10,
                HealthyThreshold = 2,
                UnhealthyThreshold = 5,
            },
            DeregistrationDelay = 30,
            Tags =
            {
                { "System", prefix },
                { "Service", "keycloak" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        var internalTg = new TargetGroup($"{prefix}-keycloak-int-tg", new TargetGroupArgs
        {
            Name = $"{prefix}-keycloak-int-tg",
            Port = 8080,
            Protocol = "HTTP",
            VpcId = network.NetworkId,
            TargetType = "ip",
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Enabled = true,
                Path = "/health/ready",
                Protocol = "HTTP",
                Port = "9000",
                Interval = 30,
                Timeout = 10,
                HealthyThreshold = 2,
                UnhealthyThreshold = 5,
            },
            DeregistrationDelay = 30,
            Tags =
            {
                { "System", prefix },
                { "Service", "keycloak-internal" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // =====================================================================
        // LISTENER RULES
        // =====================================================================

        // Admin REST API pass-through (priority 4) — allows /admin/realms/* before
        // the block rule catches it. Cross-region consumer accounts (no PrivateLink)
        // need this path for service-account user management. The API is still
        // protected by Keycloak client credentials (client_id + client_secret).
        // The admin console UI (/admin/master/*) stays blocked at priority 5.
        if (enableAdminBlocking)
        {
            new ListenerRule($"{prefix}-kc-admin-api-allow", new ListenerRuleArgs
            {
                ListenerArn = awsNetwork.HttpsListenerArn,
                Priority = 4,
                Conditions =
                {
                    new ListenerRuleConditionArgs
                    {
                        HostHeader = new ListenerRuleConditionHostHeaderArgs
                        {
                            Values = { $"auth.{config.SystemDomain}" },
                        },
                    },
                    new ListenerRuleConditionArgs
                    {
                        PathPattern = new ListenerRuleConditionPathPatternArgs
                        {
                            Values = { "/admin/realms/*" },
                        },
                    },
                },
                Actions =
                {
                    new ListenerRuleActionArgs
                    {
                        Type = "forward",
                        TargetGroupArn = publicTg.Arn,
                    },
                },
            }, opts);
        }

        // Admin block rule (priority 5) — only when EnableAdminBlocking is true
        if (enableAdminBlocking)
        {
            new ListenerRule($"{prefix}-kc-admin-block", new ListenerRuleArgs
            {
                ListenerArn = awsNetwork.HttpsListenerArn,
                Priority = 5,
                Conditions =
                {
                    new ListenerRuleConditionArgs
                    {
                        HostHeader = new ListenerRuleConditionHostHeaderArgs
                        {
                            Values = { $"auth.{config.SystemDomain}" },
                        },
                    },
                    new ListenerRuleConditionArgs
                    {
                        PathPattern = new ListenerRuleConditionPathPatternArgs
                        {
                            Values = { "/admin/*", "/master/*" },
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
                            StatusCode = "403",
                            ContentType = "text/plain",
                            MessageBody = "Access denied. Use VPN for admin access.",
                        },
                    },
                },
            }, opts);
        }

        // auth.{domain} host rule (priority 10) — public ALB
        var authRule = new ListenerRule($"{prefix}-kc-auth-host", new ListenerRuleArgs
        {
            ListenerArn = awsNetwork.HttpsListenerArn,
            Priority = 10,
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    HostHeader = new ListenerRuleConditionHostHeaderArgs
                    {
                        Values = { $"auth.{config.SystemDomain}" },
                    },
                },
            },
            Actions =
            {
                new ListenerRuleActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = publicTg.Arn,
                },
            },
        }, opts);

        // /realms/* path rule (priority 12) — public ALB (same-origin OIDC)
        new ListenerRule($"{prefix}-kc-realms-path", new ListenerRuleArgs
        {
            ListenerArn = awsNetwork.HttpsListenerArn,
            Priority = 12,
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    PathPattern = new ListenerRuleConditionPathPatternArgs
                    {
                        Values = { "/realms/*", "/resources/*", "/js/*" },
                    },
                },
            },
            Actions =
            {
                new ListenerRuleActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = publicTg.Arn,
                },
            },
        }, opts);

        // Internal ALB rule (priority 10) — auth.{domain} for VPN admin access.
        // When on VPN, auth.{domain} resolves to the internal ALB via private DNS,
        // keeping the entire admin OIDC flow same-origin (no 3rd-party cookie issues).
        var internalRule = new ListenerRule($"{prefix}-kc-internal", new ListenerRuleArgs
        {
            ListenerArn = awsNetwork.InternalHttpsListenerArn,
            Priority = 10,
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    HostHeader = new ListenerRuleConditionHostHeaderArgs
                    {
                        Values = { $"auth.{config.SystemDomain}" },
                    },
                },
            },
            Actions =
            {
                new ListenerRuleActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = internalTg.Arn,
                },
            },
        }, opts);

        // Path-based rule on internal ALB (priority 12) for PrivateLink traffic
        // from tenant accounts. Tenant requests arrive with tenant Host headers
        // (not auth.{domain}), so we match by path to route auth traffic
        // to Keycloak.
        new ListenerRule($"{prefix}-kc-internal-paths", new ListenerRuleArgs
        {
            ListenerArn = awsNetwork.InternalHttpsListenerArn,
            Priority = 12,
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    PathPattern = new ListenerRuleConditionPathPatternArgs
                    {
                        Values = { "/realms/*", "/resources/*", "/js/*" },
                    },
                },
            },
            Actions =
            {
                new ListenerRuleActionArgs
                {
                    Type = "forward",
                    TargetGroupArn = internalTg.Arn,
                },
            },
        }, opts);

        // =====================================================================
        // SERVICE DISCOVERY
        // =====================================================================

        var serviceDiscovery = new Pulumi.Aws.ServiceDiscovery.Service($"{prefix}-kc-discovery", new Pulumi.Aws.ServiceDiscovery.ServiceArgs
        {
            Name = "keycloak",
            Description = "Keycloak authentication service",
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
        }, opts);

        // =====================================================================
        // TASK DEFINITION
        // =====================================================================

        // Dynamic hostname mode: KC_HOSTNAME is NOT set, so Keycloak uses the
        // incoming request's Host header as its frontend URL / issuer.
        // - Browser requests via CloudFront (monrodev.click/realms/*) → issuer = monrodev.click
        // - Admin/VPN requests via auth.monroadmin.click → issuer = auth.monroadmin.click
        // This avoids cross-domain issues and keeps browser auth on the tenant domain.

        var taskDef = new TaskDefinition($"{prefix}-keycloak-task", new TaskDefinitionArgs
        {
            Family = $"{prefix}-keycloak",
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            Cpu = ecs.KeycloakCpu.ToString(),
            Memory = ecs.KeycloakMemory.ToString(),
            ExecutionRoleArn = executionRole.Arn,
            TaskRoleArn = taskRole.Arn,
            Volumes =
            {
                new TaskDefinitionVolumeArgs
                {
                    Name = $"keycloak-theme-{themeName}",
                    EfsVolumeConfiguration = new TaskDefinitionVolumeEfsVolumeConfigurationArgs
                    {
                        FileSystemId = awsFileStorage.FileSystemId,
                        TransitEncryption = "ENABLED",
                        AuthorizationConfig = new TaskDefinitionVolumeEfsVolumeConfigurationAuthorizationConfigArgs
                        {
                            AccessPointId = awsFileStorage.KeycloakThemeAccessPointId,
                            Iam = "ENABLED",
                        },
                    },
                },
            },
            ContainerDefinitions = Output.Tuple(
                database.Endpoint, awsDatabase.MasterSecretArn, awsDatabase.SystemSecretArn
            ).Apply(t =>
            {
                var (dbHost, masterSecretArn, systemSecretArn) = t;
                return System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = "keycloak",
                        image = $"quay.io/keycloak/keycloak:{ecs.KeycloakImageTag ?? "26.5.0"}",
                        essential = true,
                        command = new[] { "start" },
                        portMappings = new[]
                        {
                            new { containerPort = 8080, protocol = "tcp" },
                            new { containerPort = 9000, protocol = "tcp" },
                        },
                        mountPoints = new[]
                        {
                            new { sourceVolume = $"keycloak-theme-{themeName}", containerPath = $"/opt/keycloak/themes/{themeName}" },
                        },
                        environment = new[]
                        {
                            new { name = "KC_DB", value = "postgres" },
                            new { name = "KC_DB_URL", value = $"jdbc:postgresql://{dbHost}:5432/keycloak" },
                            new { name = "KC_PROXY_HEADERS", value = "xforwarded" },
                            new { name = "KC_HEALTH_ENABLED", value = "true" },
                            new { name = "KC_METRICS_ENABLED", value = "true" },
                            new { name = "KC_HTTP_ENABLED", value = "true" },
                            new { name = "KC_HTTP_PORT", value = "8080" },
                            new { name = "KC_CACHE", value = "local" },
                            new { name = "KC_HOSTNAME_STRICT", value = "false" },
                        },
                        secrets = new[]
                        {
                            new { name = "KC_DB_USERNAME", valueFrom = $"{masterSecretArn}:username::" },
                            new { name = "KC_DB_PASSWORD", valueFrom = $"{masterSecretArn}:password::" },
                            new { name = "KC_BOOTSTRAP_ADMIN_USERNAME", valueFrom = $"{systemSecretArn}:keycloak-admin-username::" },
                            new { name = "KC_BOOTSTRAP_ADMIN_PASSWORD", valueFrom = $"{systemSecretArn}:keycloak-admin-password::" },
                        },
                        logConfiguration = new
                        {
                            logDriver = "awslogs",
                            options = new Dictionary<string, string>
                            {
                                ["awslogs-group"] = $"/ecs/{prefix}/keycloak",
                                ["awslogs-region"] = config.Region,
                                ["awslogs-stream-prefix"] = "keycloak",
                            },
                        },
                        healthCheck = new
                        {
                            command = new[] { "CMD-SHELL", "bash -c '(echo > /dev/tcp/localhost/9000) 2>/dev/null' || exit 1" },
                            interval = 30,
                            timeout = 10,
                            retries = 3,
                            startPeriod = 120,
                        },
                    },
                });
            }),
            Tags =
            {
                { "System", prefix },
                { "Service", "keycloak" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // =====================================================================
        // ECS SERVICE
        // =====================================================================

        var service = new EcsService($"{prefix}-keycloak-service", new EcsServiceArgs
        {
            Name = $"{prefix}-keycloak",
            Cluster = awsCompute.ClusterArn,
            TaskDefinition = taskDef.Arn,
            DesiredCount = 1, // Post-deploy Step 2 scales to 1; keep at 1 so Step 3 doesn't reset
            LaunchType = "FARGATE",
            PlatformVersion = "LATEST",
            HealthCheckGracePeriodSeconds = 180,
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
                SecurityGroups = { awsNetwork.EcsPublicSecurityGroupId },
            },
            LoadBalancers =
            {
                new ServiceLoadBalancerArgs
                {
                    ContainerName = "keycloak",
                    ContainerPort = 8080,
                    TargetGroupArn = publicTg.Arn,
                },
                new ServiceLoadBalancerArgs
                {
                    ContainerName = "keycloak",
                    ContainerPort = 8080,
                    TargetGroupArn = internalTg.Arn,
                },
            },
            ServiceRegistries = new ServiceServiceRegistriesArgs
            {
                RegistryArn = serviceDiscovery.Arn,
            },
            Tags =
            {
                { "System", prefix },
                { "Service", "keycloak" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions
        {
            Parent = this,
            DependsOn = { authRule, internalRule },
        });

        return new AwsServiceOutputs
        {
            ServiceId = service.Id,
            Endpoint = Output.Create($"https://auth.{config.SystemDomain}"),
        };
    }
}

/// <summary>
/// AWS-specific service outputs.
/// </summary>
public class AwsServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }
}
