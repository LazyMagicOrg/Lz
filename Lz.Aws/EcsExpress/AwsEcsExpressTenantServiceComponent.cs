using Lz.Core.Config;
using Lz.Aws.Config;
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
        var suffix = tenantConfig.TenantSuffix;
        var prefix = $"{sk}-{tk}-{serviceName}";
        var networkOutputs = (AwsEcsExpressNetworkOutputs)network;
        var computeOutputs = (AwsEcsExpressComputeOutputs)compute;
        var dbOutputs = (AwsAppRunnerDatabaseOutputs)database;
        var container = definition.Container ?? new ContainerOptions();

        // Per-tenant ECR image — repo is created on first `lz deploycontainer`,
        // not by Pulumi. Naming mirrors the ecs-fargate-keycloak topology so
        // tooling can assume a single convention:
        //   {sk}-{suffix}-{env}-{tk}-{serviceName}
        var ecrName = $"{sk}-{suffix}-{env}-{tk}-{serviceName}";
        var ecsRegion = tenantConfig.Region ?? "us-west-2";
        var ecsIdentity = Pulumi.Aws.GetCallerIdentity.Invoke();
        var imageUri = ecsIdentity.Apply(id =>
            $"{id.AccountId}.dkr.ecr.{ecsRegion}.amazonaws.com/{ecrName}:latest");

        // Resolve effective Fargate sizing — prefers Fargate: block on tenant,
        // then system; falls back to the legacy AppRunner: block for configs
        // that predate the Fargate alias.
        // SystemConfig isn't available here, so construct a merger call with
        // an empty system and let the tenant-side override-or-fallback run.
        var fargate = AwsConfigMerger.GetEffectiveFargateConfig(
            new AwsSystemConfig(), tenantConfig);

        var cpu = container.Cpu > 0 ? container.Cpu : fargate.Cpu;
        var memory = container.Memory > 0 ? container.Memory : fargate.Memory;
        var port = container.Port > 0 ? container.Port : fargate.Port;

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}",
            RetentionInDays = fargate.LogRetentionDays,
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
                    ""Resource"": [
                        ""{arnPrefix}"", ""{arnPrefix}/index/*"",
                        ""arn:aws:dynamodb:*:*:table/{sk}_{tk}"", ""arn:aws:dynamodb:*:*:table/{sk}_{tk}/index/*"",
                        ""arn:aws:dynamodb:*:*:table/{sk}_{tk}_bff""
                    ]
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

        // Bedrock is kept at Resource: "*" because cross-region foundation-model
        // ARNs aren't known at policy-construction time. Cognito, CloudFront,
        // and Secrets Manager are scoped to the caller's account/region and
        // to the tenant's prefix for secrets.
        var callerId = Pulumi.Aws.GetCallerIdentity.Invoke();
        var awsRegion = Pulumi.Aws.GetRegion.Invoke();
        new RolePolicy($"{prefix}-extra", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = Output.Tuple(callerId.Apply(c => c.AccountId), awsRegion.Apply(r => r.Name))
                .Apply(ids => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [
                        {{ ""Effect"": ""Allow"", ""Action"": [""bedrock:InvokeModel"", ""bedrock:InvokeModelWithResponseStream""], ""Resource"": ""*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""cognito-idp:AdminCreateUser"", ""cognito-idp:AdminDeleteUser"", ""cognito-idp:AdminGetUser"", ""cognito-idp:AdminUpdateUserAttributes"", ""cognito-idp:ListUsers""],
                           ""Resource"": ""arn:aws:cognito-idp:{ids.Item2}:{ids.Item1}:userpool/*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""cognito-identity:GetId"", ""cognito-identity:GetCredentialsForIdentity"", ""cognito-identity:GetOpenIdTokenForDeveloperIdentity""],
                           ""Resource"": ""arn:aws:cognito-identity:{ids.Item2}:{ids.Item1}:identitypool/*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""cloudfront:CreateInvalidation"", ""cloudfront:GetDistribution""],
                           ""Resource"": ""arn:aws:cloudfront::{ids.Item1}:distribution/*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""secretsmanager:GetSecretValue"", ""secretsmanager:DescribeSecret""],
                           ""Resource"": ""arn:aws:secretsmanager:{ids.Item2}:{ids.Item1}:secret:{sk}/{tk}*"" }}
                    ]
                }}"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // BFF DATA PROTECTION IAM (additive, flag-gated) — §8.4
        // =====================================================================
        // The BFF persists its ASP.NET Data Protection key ring to an SSM
        // Parameter (LZ_BFF_DP_PARAM = /{sk}/{env}/bff/dataprotection) as a
        // SecureString, which uses the AWS-managed alias/aws/ssm KMS key.
        // Grant the task role SSM read/write on that path prefix plus KMS
        // encrypt/decrypt. Created ONLY when the BFF is enabled for this
        // tenant — a non-BFF tenant gets no new policy, so its plan is
        // unchanged.
        if (BffWiring.IsEnabled(tenantConfig))
        {
            var dpParam = BffWiring.DataProtectionParamPath(sk, env);
            new RolePolicy($"{prefix}-bff-dataprotection", new RolePolicyArgs
            {
                Role = taskRole.Id,
                Policy = Output.Tuple(callerId.Apply(c => c.AccountId), awsRegion.Apply(r => r.Name))
                    .Apply(ids => $@"{{
                        ""Version"": ""2012-10-17"",
                        ""Statement"": [
                            {{ ""Effect"": ""Allow"",
                               ""Action"": [""ssm:GetParameter"", ""ssm:GetParameters"", ""ssm:GetParametersByPath"", ""ssm:PutParameter""],
                               ""Resource"": ""arn:aws:ssm:{ids.Item2}:{ids.Item1}:parameter{dpParam}*"" }},
                            {{ ""Effect"": ""Allow"",
                               ""Action"": [""kms:Encrypt"", ""kms:Decrypt"", ""kms:GenerateDataKey""],
                               ""Resource"": ""*"",
                               ""Condition"": {{ ""StringEquals"": {{ ""kms:ViaService"": ""ssm.{ids.Item2}.amazonaws.com"" }} }} }}
                        ]
                    }}"),
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // TASK DEFINITION
        // =====================================================================

        // Base (always-present) container env. BFF env vars are appended ONLY
        // when the BFF is enabled for this tenant; when off, the serialized
        // environment array is identical to a pre-BFF deploy.
        var baseEnv = new List<KeyValuePair<string, string>>
        {
            new("ASPNETCORE_ENVIRONMENT", env == "prod" ? "Production" : "Development"),
            new("ASPNETCORE_URLS", $"http://+:{port}"),
            new("LZ_SYSTEM_KEY", sk),
            new("LZ_TENANT_KEY", tk),
            new("LZ_ENVIRONMENT", env),
            new("LZ_SERVICE_NAME", serviceName),
            new("AWS_REGION", tenantConfig.Region ?? "us-west-2"),
            new("LZ_TENANT_SECRET", $"{sk}/{tk}"),
        };

        // Resolve any BFF env outputs (StackReference-backed) alongside the
        // image URI so the whole container definition serializes in one Apply.
        var bffEnv = BffWiring.IsEnabled(tenantConfig)
            ? BffWiring.BuildEnv(tenantConfig, this)
            : new List<(string Name, Output<string> Value)>();
        var bffNames = bffEnv.Select(e => e.Name).ToArray();
        var bffValueOutputs = Output.All(bffEnv.Select(e => e.Value).ToArray());

        // Auth pool env: LZ_AUTH_{POOL}_USERPOOLID for every Cognito pool. The
        // AppHost's DiscoverAuthenticators REQUIRES at least one of these or it
        // throws "No authenticators configured" and the container crash-loops.
        // Read the combined poolName->userPoolId map the foundation stack exports.
        // Always emitted — the backend needs auth regardless of the BFF. (The
        // EcsExpress topology previously omitted this entirely.)
        var foundationAuthRef = new StackReference(
            $"{prefix}-auth-foundation-ref",
            new StackReferenceArgs { Name = $"organization/lz-{sk}/{sk}-{env}" },
            new CustomResourceOptions { Parent = this });
        var authUserPoolIdsJson = foundationAuthRef.GetOutput("auth_userPoolIdsJson")
            .Apply(v => v as string ?? "{}");

        var containerDefs = Output.Tuple(imageUri, bffValueOutputs, authUserPoolIdsJson).Apply(t =>
        {
            var image = t.Item1;
            var bffValues = t.Item2;
            var authJson = t.Item3;

            var envList = baseEnv.Select(kv => new { name = kv.Key, value = kv.Value }).ToList();

            // LZ_AUTH_{POOL}_USERPOOLID from the foundation pool map.
            try
            {
                var poolIds = System.Text.Json.JsonSerializer
                    .Deserialize<System.Collections.Generic.Dictionary<string, string>>(authJson)
                    ?? new System.Collections.Generic.Dictionary<string, string>();
                foreach (var kv in poolIds)
                    if (!string.IsNullOrEmpty(kv.Value))
                        envList.Add(new { name = $"LZ_AUTH_{kv.Key.ToUpperInvariant()}_USERPOOLID", value = kv.Value });
            }
            catch { /* malformed map -> AppHost will surface the misconfig */ }

            for (int i = 0; i < bffNames.Length; i++)
                envList.Add(new { name = bffNames[i], value = bffValues[i] });

            return System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = serviceName,
                    image = image,
                    portMappings = new[] { new { containerPort = port, protocol = "tcp" } },
                    environment = envList.ToArray(),
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
                    // No container-level healthCheck: the base `mcr.microsoft.com/dotnet/aspnet`
                    // image ships no curl/wget, so a curl-based command fails on every probe and
                    // ECS kills the task ~90s in (after startPeriod) even though the app is fine.
                    // The ALB target-group health check (HTTP HealthCheckPath, below) is the
                    // authoritative health source for traffic routing AND ECS task health when a
                    // load balancer is attached; HealthCheckGracePeriodSeconds covers cold start.
                },
            });
        });

        var taskDef = new TaskDefinition($"{prefix}-task-def", new TaskDefinitionArgs
        {
            Family = prefix,
            Cpu = cpu.ToString(),
            Memory = memory.ToString(),
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            ExecutionRoleArn = executionRole.Arn,
            TaskRoleArn = taskRole.Arn,
            ContainerDefinitions = containerDefs,
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
            // With the load balancer attached and no container-level healthCheck, ECS uses the
            // ALB target-group health check for task health. Give cold starts room before it
            // governs, so a slow first boot isn't killed by the deployment circuit breaker.
            HealthCheckGracePeriodSeconds = 120,
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
