using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.AppRunner; // AwsAppRunnerDatabaseOutputs
using Pulumi;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Lambda;
using Pulumi.Aws.Lambda.Inputs;

namespace Lz.Aws.Lambda;

/// <summary>
/// Per-tenant container Lambda + Function URL for the lambda-cognito-dynamodb
/// topology. Uses the SAME per-tenant ECR image as ecs-fargate-cognito-dynamodb
/// (<c>{sk}-{suffix}-{env}-{tk}-{serviceName}:latest</c>, built by
/// <c>lz deploycontainer</c>). Tenant is injected via the <c>TENANT_KEY</c> env
/// var, matching the Fargate model (the same container runs in both topologies).
/// The Function URL is private (<c>AuthType=AWS_IAM</c>) and reached only through
/// CloudFront OAC; its host + name are published to the CDN via the factory-shared
/// origin holder.
/// </summary>
public class AwsLambdaTenantServiceComponent : ComponentResource, ITenantServiceComponent
{
    private readonly AwsLambdaApiOriginHolder _originHolder;

    public AwsLambdaTenantServiceComponent(AwsLambdaApiOriginHolder originHolder)
        : base("lz:aws:LambdaTenantService", "tenant-service", ResourceArgs.Empty, null)
    {
        _originHolder = originHolder;
    }

    public IServiceOutputs Deploy(
        string serviceName, ServiceDefinition definition, TenantConfig tenantConfig,
        INetworkOutputs network, IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database, ITenantDataOutputs tenantData)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var env = tenantConfig.Environment;
        var suffix = tenantConfig.TenantSuffix;
        var prefix = $"{sk}-{tk}-{serviceName}";
        var container = definition.Container ?? new ContainerOptions();
        var lambdaOpts = definition.Lambda ?? new LambdaOptions();
        var region = tenantConfig.Region ?? "us-west-2";
        var dbOutputs = (AwsAppRunnerDatabaseOutputs)database;

        // Same per-tenant ECR image as the Fargate topology (built by deploycontainer).
        var ecrName = $"{sk}-{suffix}-{env}-{tk}-{serviceName}";
        var identity = Pulumi.Aws.GetCallerIdentity.Invoke();
        var awsRegion = Pulumi.Aws.GetRegion.Invoke();
        var imageUri = identity.Apply(id =>
            $"{id.AccountId}.dkr.ecr.{region}.amazonaws.com/{ecrName}:latest");

        // =====================================================================
        // IAM — Execution role (mirrors the Fargate instance role's permissions)
        // =====================================================================

        var execRole = new Role($"{prefix}-exec", new RoleArgs
        {
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""lambda.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // CloudWatch Logs (managed policy for Lambda).
        new RolePolicyAttachment($"{prefix}-basic", new RolePolicyAttachmentArgs
        {
            Role = execRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole",
        }, new CustomResourceOptions { Parent = this });

        // DynamoDB — scoped to this system's tables.
        new RolePolicy($"{prefix}-dynamodb", new RolePolicyArgs
        {
            Role = execRole.Id,
            Policy = dbOutputs.TableArnPrefix.Apply(arnPrefix => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""dynamodb:GetItem"",""dynamodb:PutItem"",""dynamodb:UpdateItem"",""dynamodb:DeleteItem"",""dynamodb:Query"",""dynamodb:Scan"",""dynamodb:BatchGetItem"",""dynamodb:BatchWriteItem""],
                    ""Resource"": [""{arnPrefix}"", ""{arnPrefix}/index/*""]
                }}]
            }}"),
        }, new CustomResourceOptions { Parent = this });

        // S3 — scoped to this system's buckets only.
        new RolePolicy($"{prefix}-s3", new RolePolicyArgs
        {
            Role = execRole.Id,
            Policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""s3:GetObject"",""s3:PutObject"",""s3:DeleteObject"",""s3:ListBucket""],
                    ""Resource"": [""arn:aws:s3:::{sk}-*"", ""arn:aws:s3:::{sk}-*/*""]
                }}]
            }}",
        }, new CustomResourceOptions { Parent = this });

        // Bedrock (Resource:* — cross-region model ARNs unknown at policy time);
        // Cognito + CloudFront scoped to this account.
        new RolePolicy($"{prefix}-extra", new RolePolicyArgs
        {
            Role = execRole.Id,
            Policy = Output.Tuple(identity.Apply(c => c.AccountId), awsRegion.Apply(r => r.Name))
                .Apply(ids => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [
                        {{ ""Effect"": ""Allow"", ""Action"": [""bedrock:InvokeModel"",""bedrock:InvokeModelWithResponseStream""], ""Resource"": ""*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""cognito-idp:AdminCreateUser"",""cognito-idp:AdminDeleteUser"",""cognito-idp:AdminGetUser"",""cognito-idp:AdminUpdateUserAttributes"",""cognito-idp:ListUsers""],
                           ""Resource"": ""arn:aws:cognito-idp:{ids.Item2}:{ids.Item1}:userpool/*"" }},
                        {{ ""Effect"": ""Allow"",
                           ""Action"": [""cloudfront:CreateInvalidation"",""cloudfront:GetDistribution""],
                           ""Resource"": ""arn:aws:cloudfront::{ids.Item1}:distribution/*"" }}
                    ]
                }}"),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // BFF IAM (additive, flag-gated) — §8.4, §8.5
        // =====================================================================
        // Two gaps the Lambda role has vs EcsExpress that the BFF needs:
        //   1. secretsmanager:GetSecretValue on {sk}/{tk}* (EcsExpress already
        //      grants this; Lambda did not — §8.5).
        //   2. SSM + KMS for the Data Protection key ring SecureString at
        //      /{sk}/{env}/bff/dataprotection (§8.4).
        // Created ONLY when the BFF is enabled for this tenant — a non-BFF
        // tenant gets no new policy, so its plan is unchanged.
        if (Lz.Aws.AppRunner.BffWiring.IsEnabled(tenantConfig))
        {
            var dpParam = Lz.Aws.AppRunner.BffWiring.DataProtectionParamPath(sk, env);
            new RolePolicy($"{prefix}-bff", new RolePolicyArgs
            {
                Role = execRole.Id,
                Policy = Output.Tuple(identity.Apply(c => c.AccountId), awsRegion.Apply(r => r.Name))
                    .Apply(ids => $@"{{
                        ""Version"": ""2012-10-17"",
                        ""Statement"": [
                            {{ ""Effect"": ""Allow"",
                               ""Action"": [""secretsmanager:GetSecretValue"",""secretsmanager:DescribeSecret""],
                               ""Resource"": ""arn:aws:secretsmanager:{ids.Item2}:{ids.Item1}:secret:{sk}/{tk}*"" }},
                            {{ ""Effect"": ""Allow"",
                               ""Action"": [""ssm:GetParameter"",""ssm:GetParameters"",""ssm:GetParametersByPath"",""ssm:PutParameter""],
                               ""Resource"": ""arn:aws:ssm:{ids.Item2}:{ids.Item1}:parameter{dpParam}*"" }},
                            {{ ""Effect"": ""Allow"",
                               ""Action"": [""kms:Encrypt"",""kms:Decrypt"",""kms:GenerateDataKey""],
                               ""Resource"": ""*"",
                               ""Condition"": {{ ""StringEquals"": {{ ""kms:ViaService"": ""ssm.{ids.Item2}.amazonaws.com"" }} }} }}
                        ]
                    }}"),
            }, new CustomResourceOptions { Parent = this });
        }

        // =====================================================================
        // CONTAINER LAMBDA (same image as Fargate) + private Function URL
        // =====================================================================

        // Base (always-present) function env. BFF env vars are appended ONLY
        // when the BFF is enabled for this tenant; when off, the Variables map
        // is identical to a pre-BFF deploy.
        var fnEnv = new FunctionEnvironmentArgs
        {
            Variables =
            {
                { "ASPNETCORE_ENVIRONMENT", env == "prod" ? "Production" : "Development" },
                { "SYSTEM_KEY", sk },
                { "TENANT_KEY", tk },
                { "ENVIRONMENT", env },
                // Lambda Web Adapter: the same ASP.NET container listens on this port.
                { "AWS_LWA_PORT", container.Port.ToString() },
            },
        };
        if (Lz.Aws.AppRunner.BffWiring.IsEnabled(tenantConfig))
        {
            foreach (var (name, value) in Lz.Aws.AppRunner.BffWiring.BuildEnv(tenantConfig, this))
                fnEnv.Variables.Add(name, value);
        }

        var fn = new Function($"{prefix}-fn", new FunctionArgs
        {
            Name = prefix,
            PackageType = "Image",
            ImageUri = imageUri,
            Role = execRole.Arn,
            Timeout = lambdaOpts.Timeout > 0 ? lambdaOpts.Timeout : 30,
            MemorySize = lambdaOpts.MemorySize > 0 ? lambdaOpts.MemorySize : 1024,
            Architectures = { "x86_64" },
            Environment = fnEnv,
            Tags = Tags(sk, tk, serviceName),
        }, new CustomResourceOptions { Parent = this });

        // Private Function URL — reachable only via CloudFront OAC (SigV4).
        var fnUrl = new FunctionUrl($"{prefix}-url", new FunctionUrlArgs
        {
            FunctionName = fn.Name,
            AuthorizationType = "AWS_IAM",
        }, new CustomResourceOptions { Parent = this });

        var fullUrl = fnUrl.FunctionUrlResult;
        var host = fnUrl.FunctionUrlResult.Apply(u => new System.Uri(u).Host);

        // Publish the API host (the public service) to the CDN component.
        if (definition.IngressType == IngressType.Public)
        {
            _originHolder.FunctionName = fn.Name;
            _originHolder.FunctionUrlHost = host;
        }

        return new AwsLambdaServiceOutputs
        {
            ServiceId = fn.Id,
            Endpoint = fullUrl,
        };
    }

    private static InputMap<string> Tags(string sk, string tk, string serviceName) => new()
    {
        { "System", sk }, { "Tenant", tk }, { "Service", serviceName }, { "ManagedBy", "lz-pulumi" },
    };
}
