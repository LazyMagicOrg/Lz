using System.Linq;
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

        // DynamoDB — scoped to this system's tables. MUST mirror the EcsExpress task
        // role (AwsEcsExpressTenantServiceComponent): in addition to the hyphenated
        // {arnPrefix} ({sk}-{suffix}-{env}-*) system tables, the tenant-scoped tables
        // use UNDERSCORE naming — {sk}_{tk} (tenant), {sk}_{tk}_{subtenant} (data),
        // and {sk}_{tk}_bff / {sk}_{tk}_cbff (BFF session stores). Without the
        // underscore patterns, every BFF login 500s at the session PutItem
        // (DynamoBffSessionStore.CreateAsync) and server-initiated repo access
        // (e.g. anonymous PublicModule reads, subtenant KVS seeding) is denied.
        new RolePolicy($"{prefix}-dynamodb", new RolePolicyArgs
        {
            Role = execRole.Id,
            Policy = dbOutputs.TableArnPrefix.Apply(arnPrefix => $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [""dynamodb:GetItem"",""dynamodb:PutItem"",""dynamodb:UpdateItem"",""dynamodb:DeleteItem"",""dynamodb:Query"",""dynamodb:Scan"",""dynamodb:BatchGetItem"",""dynamodb:BatchWriteItem""],
                    ""Resource"": [
                        ""{arnPrefix}"", ""{arnPrefix}/index/*"",
                        ""arn:aws:dynamodb:*:*:table/{sk}_{tk}"", ""arn:aws:dynamodb:*:*:table/{sk}_{tk}/index/*"",
                        ""arn:aws:dynamodb:*:*:table/{sk}_{tk}_*"", ""arn:aws:dynamodb:*:*:table/{sk}_{tk}_*/index/*""
                    ]
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

        // Base (always-present) function env.
        var baseVars = new System.Collections.Generic.Dictionary<string, string>
        {
            { "ASPNETCORE_ENVIRONMENT", env == "prod" ? "Production" : "Development" },
            { "SYSTEM_KEY", sk },
            { "TENANT_KEY", tk },
            { "ENVIRONMENT", env },
            // Lambda Web Adapter: the same ASP.NET container listens on this port.
            { "AWS_LWA_PORT", container.Port.ToString() },
        };

        // Auth pool env: LZ_AUTH_{POOL}_USERPOOLID for every Cognito pool, read from
        // the foundation stack's auth_userPoolIdsJson map — the SAME source and vars
        // the EcsExpress task definition injects (AwsEcsExpressTenantServiceComponent).
        // REQUIRED regardless of the BFF: AppHost.DiscoverAuthenticators throws
        // "No authenticators configured" and the container aborts on EVERY invocation
        // (Lambda init error → 502) without at least one of these. The Fargate task
        // gets them via its container definition; the Lambda function must too, or the
        // image that runs green on Fargate crash-loops on Lambda.
        var foundationAuthRef = new StackReference(
            $"{prefix}-auth-foundation-ref",
            new StackReferenceArgs { Name = $"organization/lz-{sk}/{sk}-{env}" },
            new CustomResourceOptions { Parent = this });
        var authUserPoolIdsJson = foundationAuthRef.GetOutput("auth_userPoolIdsJson")
            .Apply(v => v as string ?? "{}");

        // BFF env vars (flag-gated) — Output-valued; appended only when enabled so a
        // non-BFF tenant's Variables map is identical to a pre-BFF deploy.
        var bffEnv = Lz.Aws.AppRunner.BffWiring.IsEnabled(tenantConfig)
            ? Lz.Aws.AppRunner.BffWiring.BuildEnv(tenantConfig, this)
            : new System.Collections.Generic.List<(string Name, Output<string> Value)>();
        var bffNames = bffEnv.Select(e => e.Name).ToArray();
        var bffValueOutputs = Output.All(bffEnv.Select(e => e.Value).ToArray());

        // Origin-verification secret (stable across deploys — regenerating would open a
        // mismatch window between the function env and the CloudFront origin header
        // while the distribution propagates). CloudFront injects it as x-origin-verify
        // on the api origin; OriginVerifyMiddleware (LZ_ORIGIN_VERIFY) enforces it.
        var originVerify = new Pulumi.Random.RandomPassword($"{prefix}-origin-verify", new()
        {
            Length = 48,
            Special = false, // header-safe alphanumerics
        }, new CustomResourceOptions { Parent = this });

        var fnEnv = new FunctionEnvironmentArgs
        {
            Variables = Output.Tuple(authUserPoolIdsJson, bffValueOutputs, originVerify.Result).Apply(t =>
            {
                var authJson = t.Item1;
                var bffValues = t.Item2;
                var vars = new System.Collections.Generic.Dictionary<string, string>(baseVars)
                {
                    ["LZ_ORIGIN_VERIFY"] = t.Item3,
                };

                // LZ_AUTH_{POOL}_USERPOOLID from the foundation pool map.
                try
                {
                    var poolIds = System.Text.Json.JsonSerializer
                        .Deserialize<System.Collections.Generic.Dictionary<string, string>>(authJson)
                        ?? new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var kv in poolIds)
                        if (!string.IsNullOrEmpty(kv.Value))
                            vars[$"LZ_AUTH_{kv.Key.ToUpperInvariant()}_USERPOOLID"] = kv.Value;
                }
                catch { /* malformed map -> AppHost surfaces the misconfig */ }

                for (int i = 0; i < bffNames.Length; i++)
                    vars[bffNames[i]] = bffValues[i];

                return System.Collections.Immutable.ImmutableDictionary.CreateRange(vars);
            }),
        };

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

        // PUBLIC Function URL (AuthType=NONE), protected by origin verification.
        // AWS_IAM + CloudFront OAC was tried and CANNOT carry a REST API: OAC signing
        // rejects PUT/PATCH/DELETE outright and requires viewers to send
        // x-amz-content-sha256 on POST (AWS-documented; confirmed live 2026-07-03).
        // Instead the URL is public and every request is gated by the x-origin-verify
        // secret that only CloudFront injects (see AwsLambdaApiOriginHolder).
        var fnUrl = new FunctionUrl($"{prefix}-url", new FunctionUrlArgs
        {
            FunctionName = fn.Name,
            AuthorizationType = "NONE",
        }, new CustomResourceOptions { Parent = this });

        // AuthType=NONE requires an explicit public InvokeFunctionUrl grant (AWS adds
        // this automatically in the console; IaC must create it). The app-level
        // origin-verify gate is what actually refuses non-CloudFront callers.
        new Permission($"{prefix}-url-public", new PermissionArgs
        {
            Action = "lambda:InvokeFunctionUrl",
            Function = fn.Name,
            Principal = "*",
            FunctionUrlAuthType = "NONE",
        }, new CustomResourceOptions { Parent = this });

        // DUAL AUTH (AWS, Oct 2025): URL invocation ALSO requires lambda:InvokeFunction —
        // without this second public statement every request (any verb) gets the Function
        // URL's own 403 "Forbidden", exactly like the AWS_IAM/OAC case (verified live
        // 2026-07-03, twice). AWS's canonical public statement scopes it with the
        // Bool condition lambda:InvokedViaFunctionUrl=true; Pulumi's lambda.Permission
        // cannot express that condition, so this grant is broader (direct SDK invokes
        // are also allowed) — acceptable because the app's origin-verify gate rejects
        // any payload lacking the CloudFront-injected secret; tighten via WAF or a raw
        // policy resource if needed.
        new Permission($"{prefix}-url-public-fn", new PermissionArgs
        {
            Action = "lambda:InvokeFunction",
            Function = fn.Name,
            Principal = "*",
        }, new CustomResourceOptions { Parent = this });

        var fullUrl = fnUrl.FunctionUrlResult;
        var host = fnUrl.FunctionUrlResult.Apply(u => new System.Uri(u).Host);

        // Publish the API host (the public service) to the CDN component.
        if (definition.IngressType == IngressType.Public)
        {
            _originHolder.FunctionName = fn.Name;
            _originHolder.FunctionUrlHost = host;
            _originHolder.OriginVerifySecret = originVerify.Result;
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
