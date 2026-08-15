using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Iam;
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
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.Fargate;

/// <summary>
/// Foundation-level service IAM deployment for the Fargate lineage (ported
/// unchanged from the retired apprunner topology; the Pulumi type token and the
/// AWS-managed policy ARNs are frozen deployed-state identities). Creates the
/// ECR-access + instance IAM roles; the per-tenant service itself is created by
/// AwsFargateTenantServiceComponent.
/// </summary>
public class AwsFargateServiceComponent : ComponentResource, IServiceComponent
{
    private readonly SystemConfig _config;

    public AwsFargateServiceComponent(SystemConfig config)
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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
        var computeOutputs = (AwsLambdaComputeOutputs)compute;
        var dbOutputs = (AwsDynamoDbOutputs)database;

        // =====================================================================
        // IAM — ECR Access Role (ECR pull access for the service)
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
            // FROZEN deployed-state identity: this is AWS's own managed-policy
            // name (App Runner era) — deliberately not renamed; changing it would
            // alter the deployed role. See Lz/Migrations/AxisRestructure.md.
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSAppRunnerServicePolicyForECRAccess",
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM — Instance Role (runtime permissions for the service)
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

        return new AwsFargateServiceOutputs
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

internal class AwsFargateServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }

    // serverless-lineage-specific (role ARNs)
    public required Output<string> AccessRoleArn { get; init; }
    public required Output<string> InstanceRoleArn { get; init; }
}
