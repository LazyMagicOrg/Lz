using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // Reuse DynamoDB outputs
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.LB;
using Pulumi.Aws.LB.Inputs;

namespace Lz.Aws.EcsExpress;

/// <summary>
/// Per-tenant ECS Fargate service in public subnets with AssignPublicIp = true.
/// Creates task definition, ALB target group + listener rule, and ECS service.
/// No NAT gateway — tasks access internet directly via public IP.
/// </summary>
public class AwsEcsExpressTenantServiceComponent : ComponentResource, ITenantServiceComponent
{
    public AwsEcsExpressTenantServiceComponent()
        : base("lz:aws:EcsExpressTenantService", "tenant-service", ResourceArgs.Empty, null)
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
        var prefix = $"{sk}-{tk}-{serviceName}";
        var networkOutputs = (AwsEcsExpressNetworkOutputs)network;
        var computeOutputs = (AwsEcsExpressComputeOutputs)compute;
        var dbOutputs = (AwsAppRunnerDatabaseOutputs)database;
        var container = definition.Container ?? new ContainerOptions();
        var appRunner = tenantConfig.AppRunner ?? new AppRunnerConfig();

        var cpu = container.Cpu > 0 ? container.Cpu : appRunner.Cpu;
        var memory = container.Memory > 0 ? container.Memory : appRunner.Memory;
        var port = container.Port > 0 ? container.Port : appRunner.Port;

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}",
            RetentionInDays = appRunner.LogRetentionDays,
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — EXECUTION ROLE (ECR pull + CloudWatch logs)
        // =====================================================================

        var executionRole = new Role($"{prefix}-exec", new RoleArgs
        {
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        new RolePolicyAttachment($"{prefix}-exec-ecr", new RolePolicyAttachmentArgs
        {
            Role = executionRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — TASK ROLE (runtime permissions)
        // =====================================================================

        var taskRole = new Role($"{prefix}-task", new RoleArgs
        {
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // DynamoDB
        new RolePolicy($"{prefix}-dynamodb", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = dbOutputs.TableArnPrefix.Apply(arnPrefix => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [
                        ""dynamodb:GetItem"", ""dynamodb:PutItem"", ""dynamodb:UpdateItem"",
                        ""dynamodb:DeleteItem"", ""dynamodb:Query"", ""dynamodb:Scan"",
                        ""dynamodb:BatchGetItem"", ""dynamodb:BatchWriteItem""
                    ],
                    ""Resource"": [""{arnPrefix}"", ""{arnPrefix}/index/*""]
                }}]
            }}"),
        }, new CustomResourceOptions { Parent = this });

        // S3 (scoped), CloudWatch, Bedrock, Cognito, CloudFront
        new RolePolicyAttachment($"{prefix}-logs-policy", new RolePolicyAttachmentArgs
        {
            Role = taskRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/CloudWatchLogsFullAccess",
        }, new CustomResourceOptions { Parent = this });

        new RolePolicy($"{prefix}-s3", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""s3:GetObject"", ""s3:PutObject"", ""s3:DeleteObject"", ""s3:ListBucket""],
                    ""Resource"": [""arn:aws:s3:::{sk}-*"", ""arn:aws:s3:::{sk}-*/*""]
                }}]
            }}",
        }, new CustomResourceOptions { Parent = this });

        new RolePolicy($"{prefix}-extra", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    { ""Effect"": ""Allow"", ""Action"": [""bedrock:InvokeModel"", ""bedrock:InvokeModelWithResponseStream""], ""Resource"": ""*"" },
                    { ""Effect"": ""Allow"", ""Action"": [""cognito-idp:AdminCreateUser"", ""cognito-idp:AdminDeleteUser"", ""cognito-idp:AdminGetUser"", ""cognito-idp:AdminUpdateUserAttributes"", ""cognito-idp:ListUsers"", ""cognito-identity:*""], ""Resource"": ""*"" },
                    { ""Effect"": ""Allow"", ""Action"": [""cloudfront:CreateInvalidation"", ""cloudfront:GetDistribution""], ""Resource"": ""*"" }
                ]
            }",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // TASK DEFINITION
        // =====================================================================

        var taskDef = new TaskDefinition($"{prefix}-task-def", new TaskDefinitionArgs
        {
            Family = prefix,
            Cpu = cpu.ToString(),
            Memory = memory.ToString(),
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            ExecutionRoleArn = executionRole.Arn,
            TaskRoleArn = taskRole.Arn,
            ContainerDefinitions = computeOutputs.EcrRepositoryUrl.Apply(ecrUrl =>
                System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = serviceName,
                        image = $"{ecrUrl}:latest",
                        portMappings = new[] { new { containerPort = port, protocol = "tcp" } },
                        environment = new[]
                        {
                            new { name = "ASPNETCORE_ENVIRONMENT", value = env == "prod" ? "Production" : "Development" },
                            new { name = "SYSTEM_KEY", value = sk },
                            new { name = "TENANT_KEY", value = tk },
                            new { name = "ENVIRONMENT", value = env },
                        },
                        logConfiguration = new
                        {
                            logDriver = "awslogs",
                            options = new Dictionary<string, string>
                            {
                                ["awslogs-group"] = $"/ecs/{prefix}",
                                ["awslogs-region"] = tenantConfig.Region ?? "us-west-2",
                                ["awslogs-stream-prefix"] = "ecs",
                            },
                        },
                        healthCheck = new
                        {
                            command = new[] { "CMD-SHELL", $"curl -f http://localhost:{port}{container.HealthCheckPath} || exit 1" },
                            interval = 30,
                            timeout = 5,
                            retries = 3,
                            startPeriod = 60,
                        },
                    },
                })),
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ALB TARGET GROUP + LISTENER RULE
        // =====================================================================

        var targetGroup = new TargetGroup($"{prefix}-tg", new TargetGroupArgs
        {
            Port = port,
            Protocol = "HTTP",
            TargetType = "ip",
            VpcId = networkOutputs.NetworkId,
            HealthCheck = new TargetGroupHealthCheckArgs
            {
                Path = container.HealthCheckPath,
                Protocol = "HTTP",
                Interval = 30,
                Timeout = 5,
                HealthyThreshold = 2,
                UnhealthyThreshold = 3,
            },
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // Route all traffic to this target group (default for now — single service)
        new ListenerRule($"{prefix}-rule", new ListenerRuleArgs
        {
            ListenerArn = networkOutputs.HttpsListenerArn,
            Priority = 10,
            Actions =
            {
                new ListenerRuleActionArgs { Type = "forward", TargetGroupArn = targetGroup.Arn },
            },
            Conditions =
            {
                new ListenerRuleConditionArgs
                {
                    PathPattern = new ListenerRuleConditionPathPatternArgs { Values = { "/*" } },
                },
            },
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // ECS SERVICE — public subnets, public IP, no NAT
        // =====================================================================

        var ecsService = new Service($"{prefix}-service", new ServiceArgs
        {
            Name = prefix,
            Cluster = computeOutputs.ClusterArn,
            TaskDefinition = taskDef.Arn,
            DesiredCount = 1,
            LaunchType = "FARGATE",
            NetworkConfiguration = new ServiceNetworkConfigurationArgs
            {
                Subnets = networkOutputs.PublicSubnetIds,
                SecurityGroups = { networkOutputs.EcsTaskSecurityGroupId },
                AssignPublicIp = true,  // KEY DIFFERENCE — no NAT needed
            },
            LoadBalancers =
            {
                new ServiceLoadBalancerArgs
                {
                    TargetGroupArn = targetGroup.Arn,
                    ContainerName = serviceName,
                    ContainerPort = port,
                },
            },
            DeploymentCircuitBreaker = new ServiceDeploymentCircuitBreakerArgs
            {
                Enable = true,
                Rollback = true,
            },
            Tags = { { "System", sk }, { "Tenant", tk }, { "ManagedBy", "lz-pulumi" } },
        }, new CustomResourceOptions { Parent = this });

        return new AwsEcsExpressServiceOutputs
        {
            ServiceId = ecsService.Id,
            Endpoint = networkOutputs.AlbDns.Apply(dns => $"https://{dns}"),
        };
    }
}

internal class AwsEcsExpressServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }
}
