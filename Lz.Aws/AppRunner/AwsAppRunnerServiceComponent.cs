using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Iam;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Foundation-level AppRunner service deployment.
/// Creates IAM roles for AppRunner services (ECR access + instance role).
/// The actual AppRunner service is created per-tenant by AwsAppRunnerTenantServiceComponent.
/// </summary>
public class AwsAppRunnerServiceComponent : ComponentResource, IServiceComponent
{
    private readonly SystemConfig _config;

    public AwsAppRunnerServiceComponent(SystemConfig config)
        : base("lz:aws:AppRunnerService", "service", ResourceArgs.Empty, null)
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
        var sk = _config.SystemKey;
        var env = _config.Environment;
        var prefix = $"{sk}-{env}-{serviceName}";
        var computeOutputs = (AwsAppRunnerComputeOutputs)compute;
        var dbOutputs = (AwsAppRunnerDatabaseOutputs)database;

        // =====================================================================
        // IAM — ECR Access Role (allows AppRunner to pull from ECR)
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
            Tags = Tags(sk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        new RolePolicyAttachment($"{prefix}-ecr-access-policy", new RolePolicyAttachmentArgs
        {
            Role = accessRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSAppRunnerServicePolicyForECRAccess",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — Instance Role (runtime permissions for the AppRunner service)
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
            Tags = Tags(sk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // DynamoDB access
        new RolePolicy($"{prefix}-dynamodb", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = dbOutputs.TableArnPrefix.Apply(arnPrefix => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [
                        ""dynamodb:GetItem"",
                        ""dynamodb:PutItem"",
                        ""dynamodb:UpdateItem"",
                        ""dynamodb:DeleteItem"",
                        ""dynamodb:Query"",
                        ""dynamodb:Scan"",
                        ""dynamodb:BatchGetItem"",
                        ""dynamodb:BatchWriteItem""
                    ],
                    ""Resource"": [
                        ""{arnPrefix}"",
                        ""{arnPrefix}/index/*""
                    ]
                }}]
            }}"),
        }, new CustomResourceOptions { Parent = this });

        // S3 access for assets
        new RolePolicyAttachment($"{prefix}-s3-readonly", new RolePolicyAttachmentArgs
        {
            Role = instanceRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess",
        }, new CustomResourceOptions { Parent = this });

        // CloudWatch Logs
        new RolePolicyAttachment($"{prefix}-logs", new RolePolicyAttachmentArgs
        {
            Role = instanceRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/CloudWatchLogsFullAccess",
        }, new CustomResourceOptions { Parent = this });

        // Bedrock access (for AI features)
        new RolePolicy($"{prefix}-bedrock", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Action"": [""bedrock:InvokeModel"", ""bedrock:InvokeModelWithResponseStream""],
                    ""Resource"": ""*""
                }]
            }",
        }, new CustomResourceOptions { Parent = this });

        // Cognito + CloudFront access, scoped to this AWS account (Cognito also
        // to the service's region). Bedrock stays at Resource: "*" because
        // cross-region foundation-model ARNs aren't known at policy time.
        var callerIdAr = Pulumi.Aws.GetCallerIdentity.Invoke();
        var awsRegionAr = Pulumi.Aws.GetRegion.Invoke();
        new RolePolicy($"{prefix}-cognito", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = Output.Tuple(callerIdAr.Apply(c => c.AccountId), awsRegionAr.Apply(r => r.Name))
                .Apply(ids => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [{{
                        ""Effect"": ""Allow"",
                        ""Action"": [""cognito-idp:AdminCreateUser"", ""cognito-idp:AdminDeleteUser"", ""cognito-idp:AdminGetUser"", ""cognito-idp:AdminUpdateUserAttributes"", ""cognito-idp:ListUsers""],
                        ""Resource"": ""arn:aws:cognito-idp:{ids.Item2}:{ids.Item1}:userpool/*""
                    }}]
                }}"),
        }, new CustomResourceOptions { Parent = this });

        new RolePolicy($"{prefix}-cloudfront", new RolePolicyArgs
        {
            Role = instanceRole.Id,
            Policy = callerIdAr.Apply(c => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""cloudfront:CreateInvalidation"", ""cloudfront:GetDistribution""],
                    ""Resource"": ""arn:aws:cloudfront::{c.AccountId}:distribution/*""
                }}]
            }}"),
        }, new CustomResourceOptions { Parent = this });

        return new AwsAppRunnerServiceOutputs
        {
            ServiceId = Output.Create($"{prefix}-foundation"),
            Endpoint = Output.Create(""),
            AccessRoleArn = accessRole.Arn,
            InstanceRoleArn = instanceRole.Arn,
        };
    }

    private static InputMap<string> Tags(string systemKey, string serviceName) => new()
    {
        { "System", systemKey },
        { "Service", serviceName },
        { "ManagedBy", "lz-pulumi" },
    };
}

internal class AwsAppRunnerServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }

    // AppRunner-specific
    public required Output<string> AccessRoleArn { get; init; }
    public required Output<string> InstanceRoleArn { get; init; }
}
