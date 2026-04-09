using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.AppRunner;
using Pulumi.Aws.AppRunner.Inputs;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Iam;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Per-tenant AppRunner service deployment.
/// Creates the actual AppRunner service with ECR image,
/// environment variables, and health check configuration.
/// No VPC — AppRunner accesses DynamoDB/S3/Cognito via public endpoints.
/// </summary>
public class AwsAppRunnerTenantServiceComponent : ComponentResource, ITenantServiceComponent
{
    public AwsAppRunnerTenantServiceComponent()
        : base("lz:aws:AppRunnerTenantService", "tenant-service", ResourceArgs.Empty, null)
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
        var computeOutputs = (AwsAppRunnerComputeOutputs)compute;
        var container = definition.Container ?? new ContainerOptions();

        // =====================================================================
        // LOG GROUP
        // =====================================================================

        var logGroup = new LogGroup($"{prefix}-logs", new LogGroupArgs
        {
            Name = $"/aws/apprunner/{prefix}",
            RetentionInDays = tenantConfig.AppRunner?.LogRetentionDays ?? 3,
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "Service", serviceName },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — ECR Access Role
        // =====================================================================

        var accessRole = new Role($"{prefix}-ecr-access", new RoleArgs
        {
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""build.apprunner.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "Service", serviceName },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        new RolePolicyAttachment($"{prefix}-ecr-policy", new RolePolicyAttachmentArgs
        {
            Role = accessRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSAppRunnerServicePolicyForECRAccess",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — Instance Role (runtime permissions)
        // =====================================================================

        var instanceRole = new Role($"{prefix}-instance", new RoleArgs
        {
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""tasks.apprunner.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "Service", serviceName },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // DynamoDB access
        var dbOutputs = (AwsAppRunnerDatabaseOutputs)database;
        new RolePolicy($"{prefix}-dynamodb", new RolePolicyArgs
        {
            Role = instanceRole.Id,
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

        // CloudWatch Logs
        new RolePolicyAttachment($"{prefix}-logs", new RolePolicyAttachmentArgs
        {
            Role = instanceRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/CloudWatchLogsFullAccess",
        }, new CustomResourceOptions { Parent = this });

        // S3 — scoped to this system's buckets only
        new RolePolicy($"{prefix}-s3", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""s3:GetObject"", ""s3:PutObject"", ""s3:DeleteObject"", ""s3:ListBucket""],
                    ""Resource"": [
                        ""arn:aws:s3:::{sk}-*"",
                        ""arn:aws:s3:::{sk}-*/*""
                    ]
                }}]
            }}",
        }, new CustomResourceOptions { Parent = this });

        new RolePolicy($"{prefix}-extra", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [
                    {
                        ""Effect"": ""Allow"",
                        ""Action"": [""bedrock:InvokeModel"", ""bedrock:InvokeModelWithResponseStream""],
                        ""Resource"": ""*""
                    },
                    {
                        ""Effect"": ""Allow"",
                        ""Action"": [
                            ""cognito-idp:AdminCreateUser"", ""cognito-idp:AdminDeleteUser"",
                            ""cognito-idp:AdminGetUser"", ""cognito-idp:AdminUpdateUserAttributes"",
                            ""cognito-idp:ListUsers"", ""cognito-identity:*""
                        ],
                        ""Resource"": ""*""
                    },
                    {
                        ""Effect"": ""Allow"",
                        ""Action"": [""cloudfront:CreateInvalidation"", ""cloudfront:GetDistribution""],
                        ""Resource"": ""*""
                    }
                ]
            }",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // APPRUNNER SERVICE
        // =====================================================================

        var appRunnerService = new Service($"{prefix}-service", new ServiceArgs
        {
            ServiceName = prefix,
            SourceConfiguration = new ServiceSourceConfigurationArgs
            {
                AuthenticationConfiguration = new ServiceSourceConfigurationAuthenticationConfigurationArgs
                {
                    AccessRoleArn = accessRole.Arn,
                },
                ImageRepository = new ServiceSourceConfigurationImageRepositoryArgs
                {
                    ImageIdentifier = computeOutputs.EcrRepositoryUrl.Apply(url => $"{url}:latest"),
                    ImageRepositoryType = "ECR",
                    ImageConfiguration = new ServiceSourceConfigurationImageRepositoryImageConfigurationArgs
                    {
                        Port = container.Port.ToString(),
                        RuntimeEnvironmentVariables =
                        {
                            { "ASPNETCORE_ENVIRONMENT", env == "prod" ? "Production" : "Development" },
                            { "SYSTEM_KEY", sk },
                            { "TENANT_KEY", tk },
                            { "ENVIRONMENT", env },
                        },
                    },
                },
                AutoDeploymentsEnabled = false,
            },
            InstanceConfiguration = new ServiceInstanceConfigurationArgs
            {
                Cpu = (container.Cpu > 0 ? container.Cpu : 1024).ToString(),
                Memory = (container.Memory > 0 ? container.Memory : 2048).ToString(),
                InstanceRoleArn = instanceRole.Arn,
            },
            AutoScalingConfigurationArn = computeOutputs.AutoScalingConfigArn,
            HealthCheckConfiguration = new ServiceHealthCheckConfigurationArgs
            {
                Protocol = "HTTP",
                Path = container.HealthCheckPath,
                Interval = 10,
                Timeout = 5,
                HealthyThreshold = 1,
                UnhealthyThreshold = 5,
            },
            // No VPC — AppRunner uses default public egress for DynamoDB/S3/Cognito
            Tags =
            {
                { "System", sk },
                { "Tenant", tk },
                { "Service", serviceName },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        return new AwsAppRunnerServiceOutputs
        {
            ServiceId = appRunnerService.Id,
            Endpoint = appRunnerService.ServiceUrl.Apply(url => $"https://{url}"),
            AccessRoleArn = accessRole.Arn,
            InstanceRoleArn = instanceRole.Arn,
        };
    }
}
