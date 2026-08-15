using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.Rds;
using Pulumi.Aws.SecretsManager;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// AWS RDS PostgreSQL component — creates the database, subnet group,
/// and system secret (Keycloak admin credentials).
/// </summary>
public class AwsRdsComponent : ComponentResource, IDatabaseComponent
{
    public AwsRdsComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
        : base("lz:aws:Rds", "database", ResourceArgs.Empty, null)
    {
    }

    public IDatabaseOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsFargateAlbNetworkOutputs)network;
        var ecs = config.Aws().ECS ?? new EcsConfig();

        // DB Subnet Group
        var subnetGroup = new SubnetGroup($"{prefix}-db-subnet-group", new SubnetGroupArgs
        {
            Name = $"{prefix}-db-subnet-group",
            Description = "Subnet group for RDS PostgreSQL (private subnets)",
            SubnetIds = network.PrivateSubnetIds.Apply(ids => ids.AsEnumerable().ToList()),
            Tags =
            {
                { "Name", $"{prefix}-db-subnet-group" },
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // RDS PostgreSQL Instance
        var db = new Instance($"{prefix}-db", new InstanceArgs
        {
            Identifier = $"{prefix}-db",
            Engine = "postgres",
            EngineVersion = ecs.DbEngineVersion,
            InstanceClass = ecs.DbInstanceClass,
            AllocatedStorage = ecs.DbAllocatedStorage,
            StorageType = "gp3",
            StorageEncrypted = true,
            Port = 5432,
            ManageMasterUserPassword = true,
            Username = "dbadmin",
            DbSubnetGroupName = subnetGroup.Name,
            VpcSecurityGroupIds = { awsNetwork.RdsSecurityGroupId },
            PubliclyAccessible = false,
            MultiAz = ecs.DbMultiAZ,
            ApplyImmediately = ecs.DbChangesApplyImmediately,
            BackupRetentionPeriod = 7,
            BackupWindow = "03:00-04:00",
            MaintenanceWindow = "sun:04:00-sun:05:00",
            DeletionProtection = config.Environment != "dev",
            EnabledCloudwatchLogsExports = { "postgresql" },
            PerformanceInsightsEnabled = true,
            PerformanceInsightsRetentionPeriod = 7,
            SkipFinalSnapshot = config.Environment == "dev",
            FinalSnapshotIdentifier = config.Environment != "dev" ? $"{prefix}-db-final" : null!,
            Tags =
            {
                { "Name", $"{prefix}-db" },
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this, Protect = config.Environment is "prod" or "staging" });

        // KMS key for system secret — required for cross-account Secrets Manager access.
        // Default aws/secretsmanager key cannot be used cross-account.
        Output<string>? kmsKeyArn = null;
        if (config.Aws().TrustedAccountIds.Count > 0)
        {
            var localAccountId = Pulumi.Aws.GetCallerIdentity.Invoke().Apply(id => id.AccountId);

            var accountPrincipals = config.Aws().TrustedAccountIds
                .Select(id => $@"""arn:aws:iam::{id}:root""")
                .ToList();

            var kmsKey = new Pulumi.Aws.Kms.Key($"{prefix}-secrets-key", new Pulumi.Aws.Kms.KeyArgs
            {
                Description = $"Encryption key for {prefix}/system secret (cross-account access)",
                EnableKeyRotation = true,
                Policy = localAccountId.Apply(localId => $@"{{
                    ""Version"": ""2012-10-17"",
                    ""Statement"": [
                        {{
                            ""Sid"": ""AllowLocalAccountFull"",
                            ""Effect"": ""Allow"",
                            ""Principal"": {{ ""AWS"": ""arn:aws:iam::{localId}:root"" }},
                            ""Action"": ""kms:*"",
                            ""Resource"": ""*""
                        }},
                        {{
                            ""Sid"": ""AllowCrossAccountDecrypt"",
                            ""Effect"": ""Allow"",
                            ""Principal"": {{ ""AWS"": [{string.Join(", ", accountPrincipals)}] }},
                            ""Action"": [
                                ""kms:Decrypt"",
                                ""kms:DescribeKey""
                            ],
                            ""Resource"": ""*""
                        }}
                    ]
                }}"),
                Tags =
                {
                    { "System", config.SystemKey },
                    { "Environment", config.Environment },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, opts);

            new Pulumi.Aws.Kms.Alias($"{prefix}-secrets-key-alias", new Pulumi.Aws.Kms.AliasArgs
            {
                Name = "alias/shared-secrets-key",
                TargetKeyId = kmsKey.Id,
            }, opts);

            kmsKeyArn = kmsKey.Arn;
        }

        // System Secret (Keycloak admin credentials + Tailscale auth key placeholder)
        var systemSecretArgs = new SecretArgs
        {
            Name = $"{prefix}/system",
            Description = $"System credentials for {prefix}",
            Tags =
            {
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        };
        if (kmsKeyArn != null)
            systemSecretArgs.KmsKeyId = kmsKeyArn;

        var systemSecret = new Secret($"{prefix}-system-secret", systemSecretArgs, new CustomResourceOptions
        {
            Parent = opts.Parent,
            RetainOnDelete = true, // Always retain — avoids AWS scheduled-deletion conflicts on recreate
            Protect = config.Environment is "prod" or "staging",
        });

        // Initial secret value — tailscale keys only in shared deployment (not cross-account consumers)
        var secretJson = string.IsNullOrEmpty(config.Aws().SharedSecretArn)
            ? @$"{{
                ""keycloak-admin-username"": ""admin"",
                ""keycloak-admin-password"": ""{prefix}-admin-changeme"",
                ""tailscale-auth-key"": """",
                ""tailscale-api-key"": """",
                ""tailscale-oidc-client-secret"": """"
            }}"
            : @$"{{
                ""keycloak-admin-username"": ""admin"",
                ""keycloak-admin-password"": ""{prefix}-admin-changeme""
            }}";

        new SecretVersion($"{prefix}-system-secret-version", new SecretVersionArgs
        {
            SecretId = systemSecret.Id,
            SecretString = secretJson,
        }, new CustomResourceOptions
        {
            Parent = opts.Parent,
            IgnoreChanges = { "secretString" },
        });

        // Resource policy for cross-account access (shared deployment only)
        if (config.Aws().TrustedAccountIds.Count > 0)
        {
            var statements = config.Aws().TrustedAccountIds.Select(accountId =>
                $@"{{
                    ""Effect"": ""Allow"",
                    ""Principal"": {{ ""AWS"": ""arn:aws:iam::{accountId}:root"" }},
                    ""Action"": ""secretsmanager:GetSecretValue"",
                    ""Resource"": ""*""
                }}");

            var policy = $@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{string.Join(",", statements)}]
            }}";

            new SecretPolicy($"{prefix}-system-secret-policy", new SecretPolicyArgs
            {
                SecretArn = systemSecret.Arn,
                Policy = policy,
            }, opts);
        }

        // =================================================================
        // SYSTEM-INIT TASK DEFINITION
        // Runs psql to CREATE DATABASE keycloak (idempotent).
        // Executed post-deploy by the orchestrator, not by Pulumi.
        // =================================================================

        var initLogGroup = new LogGroup($"{prefix}-system-init-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}/system-init",
            RetentionInDays = ecs.LogRetentionDays,
            Tags =
            {
                { "System", prefix },
                { "Service", "system-init" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        var initExecutionRole = new Role($"{prefix}-system-init-execution-role", new RoleArgs
        {
            Name = $"{prefix}-system-init-execution-role",
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
                    Policy = db.MasterUserSecrets.Apply(s => $@"{{
                        ""Version"": ""2012-10-17"",
                        ""Statement"": [{{
                            ""Effect"": ""Allow"",
                            ""Action"": [""secretsmanager:GetSecretValue""],
                            ""Resource"": [""{s[0].SecretArn}"", ""arn:aws:secretsmanager:*:*:secret:rds!*""]
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

        var initTaskFamily = $"{prefix}-system-init";

        var initTaskDef = new TaskDefinition($"{prefix}-system-init-task", new TaskDefinitionArgs
        {
            Family = initTaskFamily,
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            Cpu = "256",
            Memory = "512",
            ExecutionRoleArn = initExecutionRole.Arn,
            ContainerDefinitions = Output.Tuple(
                db.Endpoint.Apply(e => e.Split(':')[0]),
                db.MasterUserSecrets.Apply(s => s[0].SecretArn!)
            ).Apply(t =>
            {
                var (dbHost, masterSecretArn) = t;
                return System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = "system-init",
                        image = "postgres:16-alpine",
                        essential = true,
                        command = new[]
                        {
                            "/bin/sh", "-c",
                            "echo '=== Creating Keycloak Database ===' && " +
                            "if psql -h \"$DB_HOST\" -U \"$DB_USERNAME\" -d postgres -tc \"SELECT 1 FROM pg_database WHERE datname='keycloak'\" | grep -q 1; then " +
                            "echo \"Database 'keycloak' already exists\"; " +
                            "else " +
                            "psql -h \"$DB_HOST\" -U \"$DB_USERNAME\" -d postgres -c \"CREATE DATABASE keycloak\" && " +
                            "echo \"Created database 'keycloak'\"; " +
                            "fi && " +
                            "echo '=== System init complete ==='",
                        },
                        environment = new[]
                        {
                            new { name = "DB_HOST", value = dbHost },
                        },
                        secrets = new[]
                        {
                            new { name = "DB_USERNAME", valueFrom = $"{masterSecretArn}:username::" },
                            new { name = "PGPASSWORD", valueFrom = $"{masterSecretArn}:password::" },
                        },
                        logConfiguration = new
                        {
                            logDriver = "awslogs",
                            options = new Dictionary<string, string>
                            {
                                ["awslogs-group"] = $"/ecs/{prefix}/system-init",
                                ["awslogs-region"] = config.Region,
                                ["awslogs-stream-prefix"] = "init",
                            },
                        },
                    },
                });
            }),
            Tags =
            {
                { "System", prefix },
                { "Service", "system-init" },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        return new Lz.Aws.Shared.AwsDatabaseOutputs
        {
            Endpoint = db.Endpoint.Apply(e => e.Split(':')[0]),
            Port = Output.Create(5432),
            AdminSecretId = db.MasterUserSecrets.Apply(s => s[0].SecretArn!),
            DbInstanceIdentifier = db.Identifier,
            MasterSecretArn = db.MasterUserSecrets.Apply(s => s[0].SecretArn!),
            SystemSecretArn = systemSecret.Arn,
            DbSubnetGroupName = subnetGroup.Name,
            InitTaskFamily = Output.Create(initTaskFamily),
        };
    }
}
