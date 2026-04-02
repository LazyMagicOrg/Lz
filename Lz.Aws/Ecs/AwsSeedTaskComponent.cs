using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Shared;
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Ecr;
using Pulumi.Aws.Ecs;
using Pulumi.Aws.Ecs.Inputs;
using Pulumi.Aws.Iam;

namespace Lz.Aws.Ecs;

/// <summary>
/// Deploys seed data infrastructure: ECS Fargate task definition, ECR repository,
/// and IAM roles for the seeder container. The seeder mounts EFS,
/// connects to RDS, and transfers data to/from a shared S3 seed bucket.
/// </summary>
public class AwsSeedTaskComponent : ComponentResource, ISeedTaskComponent
{
    public AwsSeedTaskComponent()
        : base("lz:aws:SeedTask", "seed-task", ResourceArgs.Empty, null)
    {
    }

    public ISeedTaskOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsNetworkOutputs)network;
        var awsDatabase = (AwsDatabaseOutputs)database;
        var awsFileStorage = (AwsFileStorageOutputs)fileStorage;

        // --- ECR Repository ---
        var ecr = new Repository($"{prefix}-seeder", new RepositoryArgs
        {
            Name = $"{prefix}-seeder",
            ImageTagMutability = "MUTABLE",
            ForceDelete = config.Environment == "dev",
            Tags =
            {
                { "Name", $"{prefix}-seeder" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // --- IAM Execution Role (pull image, write logs, read secrets) ---
        var executionRole = new Role($"{prefix}-seeder-exec-role", new RoleArgs
        {
            Name = $"{prefix}-seeder-exec-role",
            AssumeRolePolicy = @"{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{
    ""Effect"": ""Allow"",
    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
    ""Action"": ""sts:AssumeRole""
  }]
}",
            Tags =
            {
                { "Name", $"{prefix}-seeder-exec-role" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        new RolePolicyAttachment($"{prefix}-seeder-exec-ecs-policy", new RolePolicyAttachmentArgs
        {
            Role = executionRole.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy",
        }, opts);

        // Execution role needs Secrets Manager access for secret injection
        new RolePolicy($"{prefix}-seeder-exec-secrets", new RolePolicyArgs
        {
            Role = executionRole.Id,
            Policy = awsDatabase.MasterSecretArn.Apply(secretArn => $@"{{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{{
    ""Effect"": ""Allow"",
    ""Action"": [""secretsmanager:GetSecretValue""],
    ""Resource"": ""{secretArn}""
  }}]
}}"),
        }, opts);

        // --- IAM Task Role (S3, Secrets Manager, EFS, CloudWatch Logs) ---
        var taskRole = new Role($"{prefix}-seeder-task-role", new RoleArgs
        {
            Name = $"{prefix}-seeder-task-role",
            AssumeRolePolicy = @"{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{
    ""Effect"": ""Allow"",
    ""Principal"": { ""Service"": ""ecs-tasks.amazonaws.com"" },
    ""Action"": ""sts:AssumeRole""
  }]
}",
            Tags =
            {
                { "Name", $"{prefix}-seeder-task-role" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // Task role: S3 access for seed bucket
        var seedBucket = config.SeedData?.Bucket ?? $"{prefix}--seeddata-{config.SystemSuffix}";
        new RolePolicy($"{prefix}-seeder-task-s3", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = $@"{{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{{
    ""Effect"": ""Allow"",
    ""Action"": [
      ""s3:GetObject"",
      ""s3:PutObject"",
      ""s3:ListBucket"",
      ""s3:GetBucketLocation""
    ],
    ""Resource"": [
      ""arn:aws:s3:::{seedBucket}"",
      ""arn:aws:s3:::{seedBucket}/*""
    ]
  }}]
}}",
        }, opts);

        // Task role: Secrets Manager + EFS access
        // Grants access to master secret, system secret, AND tenant secrets ({sk}/*)
        // so the seeder can read app user credentials at runtime.
        var accountId = Pulumi.Aws.GetCallerIdentity.Invoke().Apply(id => id.AccountId);
        new RolePolicy($"{prefix}-seeder-task-efs-secrets", new RolePolicyArgs
        {
            Role = taskRole.Id,
            Policy = Output.Tuple(
                awsFileStorage.FileSystemArn,
                awsDatabase.MasterSecretArn,
                awsDatabase.SystemSecretArn,
                accountId
            ).Apply(t => $@"{{
  ""Version"": ""2012-10-17"",
  ""Statement"": [
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""elasticfilesystem:ClientMount"",
        ""elasticfilesystem:ClientWrite"",
        ""elasticfilesystem:ClientRead""
      ],
      ""Resource"": ""{t.Item1}""
    }},
    {{
      ""Effect"": ""Allow"",
      ""Action"": [""secretsmanager:GetSecretValue""],
      ""Resource"": [
        ""{t.Item2}"",
        ""{t.Item3}"",
        ""arn:aws:secretsmanager:{config.Region}:{t.Item4}:secret:{config.SystemKey}/*""
      ]
    }}
  ]
}}"),
        }, opts);

        // --- CloudWatch Log Group ---
        var logGroup = new LogGroup($"{prefix}-seeder-logs", new LogGroupArgs
        {
            Name = $"/ecs/{prefix}-seeder",
            RetentionInDays = config.Environment == "prod" ? 30 : 7,
            Tags =
            {
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // --- EFS Access Point for seeder (root access to all tenant directories) ---
        var ap = new Pulumi.Aws.Efs.AccessPoint($"{prefix}-seeder-ap", new Pulumi.Aws.Efs.AccessPointArgs
        {
            FileSystemId = fileStorage.FileSystemId,
            PosixUser = new Pulumi.Aws.Efs.Inputs.AccessPointPosixUserArgs
            {
                Uid = 1000,
                Gid = 1000,
            },
            RootDirectory = new Pulumi.Aws.Efs.Inputs.AccessPointRootDirectoryArgs
            {
                Path = "/",
                CreationInfo = new Pulumi.Aws.Efs.Inputs.AccessPointRootDirectoryCreationInfoArgs
                {
                    OwnerUid = 1000,
                    OwnerGid = 1000,
                    Permissions = "755",
                },
            },
            Tags =
            {
                { "Name", $"{prefix}-seeder-ap" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // --- Security Group for seeder task ---
        var sg = new SecurityGroup($"{prefix}-seeder-sg", new SecurityGroupArgs
        {
            VpcId = network.NetworkId,
            Description = "Seeder ECS task - EFS + RDS + S3 access",
            Tags =
            {
                { "Name", $"{prefix}-seeder-sg" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // Egress: EFS (NFS port 2049)
        new SecurityGroupRule($"{prefix}-seeder-efs-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 2049,
            ToPort = 2049,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            SourceSecurityGroupId = awsNetwork.EfsSecurityGroupId,
            Description = "EFS access for seeder",
        }, opts);

        // Egress: RDS (PostgreSQL port 5432)
        new SecurityGroupRule($"{prefix}-seeder-rds-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 5432,
            ToPort = 5432,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            SourceSecurityGroupId = awsNetwork.RdsSecurityGroupId,
            Description = "RDS access for seeder",
        }, opts);

        // Egress: HTTPS (for S3, Secrets Manager, ECR)
        new SecurityGroupRule($"{prefix}-seeder-https-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 443,
            ToPort = 443,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            CidrBlocks = { "0.0.0.0/0" },
            Description = "HTTPS for S3 + Secrets Manager + ECR",
        }, opts);

        // Ingress on EFS SG: allow seeder task
        new SecurityGroupRule($"{prefix}-efs-seeder-ingress", new SecurityGroupRuleArgs
        {
            Type = "ingress",
            FromPort = 2049,
            ToPort = 2049,
            Protocol = "tcp",
            SecurityGroupId = awsNetwork.EfsSecurityGroupId,
            SourceSecurityGroupId = sg.Id,
            Description = "Allow seeder ECS task to mount EFS",
        }, opts);

        // Ingress on RDS SG: allow seeder task
        new SecurityGroupRule($"{prefix}-rds-seeder-ingress", new SecurityGroupRuleArgs
        {
            Type = "ingress",
            FromPort = 5432,
            ToPort = 5432,
            Protocol = "tcp",
            SecurityGroupId = awsNetwork.RdsSecurityGroupId,
            SourceSecurityGroupId = sg.Id,
            Description = "Allow seeder ECS task to connect to RDS",
        }, opts);

        // --- ECS Task Definition ---
        var seedBucketRegion = config.SeedData?.Region ?? config.Region;
        var taskDef = new TaskDefinition($"{prefix}-seeder-task", new TaskDefinitionArgs
        {
            Family = $"{prefix}-seeder",
            Cpu = "1024",   // 1 vCPU
            Memory = "4096", // 4 GB
            NetworkMode = "awsvpc",
            RequiresCompatibilities = { "FARGATE" },
            ExecutionRoleArn = executionRole.Arn,
            TaskRoleArn = taskRole.Arn,

            // EFS volume
            Volumes =
            {
                new TaskDefinitionVolumeArgs
                {
                    Name = "efs-data",
                    EfsVolumeConfiguration = new TaskDefinitionVolumeEfsVolumeConfigurationArgs
                    {
                        FileSystemId = fileStorage.FileSystemId,
                        TransitEncryption = "ENABLED",
                        AuthorizationConfig = new TaskDefinitionVolumeEfsVolumeConfigurationAuthorizationConfigArgs
                        {
                            AccessPointId = ap.Id,
                            Iam = "ENABLED",
                        },
                    },
                },
            },

            // Container definition
            ContainerDefinitions = Output.Tuple(
                ecr.RepositoryUrl,
                awsDatabase.Endpoint,
                awsDatabase.Port.Apply(p => p.ToString()),
                logGroup.Name
            ).Apply(t => System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = "seeder",
                    image = $"{t.Item1}:latest",
                    essential = true,
                    mountPoints = new[]
                    {
                        new { sourceVolume = "efs-data", containerPath = "/mnt/efs", readOnly = false }
                    },
                    environment = new[]
                    {
                        new { name = "ETL_MODE", value = "ecs" },
                        new { name = "SYSTEM_KEY", value = config.SystemKey },
                        new { name = "SEED_BUCKET", value = seedBucket },
                        new { name = "AWS_REGION", value = config.Region },
                        new { name = "SEED_BUCKET_REGION", value = seedBucketRegion },
                        new { name = "RDS_HOST", value = t.Item2 },
                        new { name = "RDS_PORT", value = t.Item3 },
                    },
                    logConfiguration = new
                    {
                        logDriver = "awslogs",
                        options = new Dictionary<string, string>
                        {
                            ["awslogs-group"] = t.Item4,
                            ["awslogs-region"] = config.Region,
                            ["awslogs-stream-prefix"] = "seeder"
                        }
                    }
                }
            })),

            Tags =
            {
                { "Name", $"{prefix}-seeder" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions
        {
            Parent = this,
            DependsOn = { logGroup },
        });

        return new AwsSeedTaskOutputs
        {
            TaskFamily = taskDef.Family,
            EcrRepositoryUrl = ecr.RepositoryUrl,
            TaskDefinitionArn = taskDef.Arn,
            TaskRoleArn = taskRole.Arn,
            ExecutionRoleArn = executionRole.Arn,
        };
    }
}
