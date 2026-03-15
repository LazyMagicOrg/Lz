using System.IO.Compression;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Ecs;
using Lz.Aws.Shared;
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Lambda;

namespace Lz.Aws.Lambda;

/// <summary>
/// Deploys a gate-checker Lambda that runs inside the VPC with EFS mount
/// and RDS access. Used by AwsTransitionChecker to verify EFS data and
/// database tables exist at gate-check time.
/// </summary>
public class AwsGateCheckerLambdaComponent : ComponentResource, IGateCheckerComponent
{
    public AwsGateCheckerLambdaComponent()
        : base("lz:aws:GateChecker", "gate-checker", ResourceArgs.Empty, null)
    {
    }

    public IGateCheckerOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsNetworkOutputs)network;
        var awsDatabase = (AwsDatabaseOutputs)database;

        // --- Security Group ---
        var sg = new SecurityGroup($"{prefix}-gate-checker-sg", new SecurityGroupArgs
        {
            VpcId = network.NetworkId,
            Description = "Gate-checker Lambda - EFS + RDS + Secrets Manager access",
            Tags =
            {
                { "Name", $"{prefix}-gate-checker-sg" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // Egress: EFS (NFS port 2049)
        new SecurityGroupRule($"{prefix}-gate-checker-efs-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 2049,
            ToPort = 2049,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            SourceSecurityGroupId = awsNetwork.EfsSecurityGroupId,
            Description = "EFS access for gate checks",
        }, opts);

        // Egress: RDS (PostgreSQL port 5432)
        new SecurityGroupRule($"{prefix}-gate-checker-rds-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 5432,
            ToPort = 5432,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            SourceSecurityGroupId = awsNetwork.RdsSecurityGroupId,
            Description = "RDS access for gate checks",
        }, opts);

        // Egress: HTTPS (for Secrets Manager API calls)
        new SecurityGroupRule($"{prefix}-gate-checker-https-egress", new SecurityGroupRuleArgs
        {
            Type = "egress",
            FromPort = 443,
            ToPort = 443,
            Protocol = "tcp",
            SecurityGroupId = sg.Id,
            CidrBlocks = { "0.0.0.0/0" },
            Description = "HTTPS for Secrets Manager API",
        }, opts);

        // Ingress on EFS SG: allow gate-checker Lambda
        new SecurityGroupRule($"{prefix}-efs-gate-checker-ingress", new SecurityGroupRuleArgs
        {
            Type = "ingress",
            FromPort = 2049,
            ToPort = 2049,
            Protocol = "tcp",
            SecurityGroupId = awsNetwork.EfsSecurityGroupId,
            SourceSecurityGroupId = sg.Id,
            Description = "Allow gate-checker Lambda to mount EFS",
        }, opts);

        // Ingress on RDS SG: allow gate-checker Lambda
        new SecurityGroupRule($"{prefix}-rds-gate-checker-ingress", new SecurityGroupRuleArgs
        {
            Type = "ingress",
            FromPort = 5432,
            ToPort = 5432,
            Protocol = "tcp",
            SecurityGroupId = awsNetwork.RdsSecurityGroupId,
            SourceSecurityGroupId = sg.Id,
            Description = "Allow gate-checker Lambda to connect to RDS",
        }, opts);

        // --- EFS Access Point (root path for checking any tenant directory) ---
        var ap = new AccessPoint($"{prefix}-gate-checker-ap", new AccessPointArgs
        {
            FileSystemId = fileStorage.FileSystemId,
            PosixUser = new AccessPointPosixUserArgs
            {
                Uid = 1000,
                Gid = 1000,
            },
            RootDirectory = new AccessPointRootDirectoryArgs
            {
                Path = "/",
                CreationInfo = new AccessPointRootDirectoryCreationInfoArgs
                {
                    OwnerUid = 1000,
                    OwnerGid = 1000,
                    Permissions = "755",
                },
            },
            Tags =
            {
                { "Name", $"{prefix}-gate-checker-ap" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // --- IAM Role ---
        var role = new Role($"{prefix}-gate-checker-role", new RoleArgs
        {
            AssumeRolePolicy = @"{
  ""Version"": ""2012-10-17"",
  ""Statement"": [{
    ""Effect"": ""Allow"",
    ""Principal"": { ""Service"": ""lambda.amazonaws.com"" },
    ""Action"": ""sts:AssumeRole""
  }]
}",
            Tags =
            {
                { "Name", $"{prefix}-gate-checker-role" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // VPC execution managed policy
        new RolePolicyAttachment($"{prefix}-gate-checker-vpc-policy", new RolePolicyAttachmentArgs
        {
            Role = role.Name,
            PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole",
        }, opts);

        // EFS + Secrets Manager inline policy
        // init_config needs: EFS write, Secrets Manager read/write for tenant secrets
        var inlinePolicy = new RolePolicy($"{prefix}-gate-checker-inline", new RolePolicyArgs
        {
            Role = role.Id,
            Policy = Output.Tuple(
                ((AwsFileStorageOutputs)fileStorage).FileSystemArn,
                awsDatabase.MasterSecretArn
            ).Apply(t => $@"{{
  ""Version"": ""2012-10-17"",
  ""Statement"": [
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""elasticfilesystem:ClientMount"",
        ""elasticfilesystem:ClientRead"",
        ""elasticfilesystem:ClientWrite"",
        ""elasticfilesystem:DescribeMountTargets""
      ],
      ""Resource"": ""{t.Item1}""
    }},
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""secretsmanager:GetSecretValue""
      ],
      ""Resource"": ""{t.Item2}""
    }},
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""secretsmanager:GetSecretValue"",
        ""secretsmanager:PutSecretValue"",
        ""secretsmanager:UpdateSecret""
      ],
      ""Resource"": ""arn:aws:secretsmanager:*:*:secret:{prefix}/*""
    }}
  ]
}}"),
        }, opts);

        // --- CloudWatch Log Group ---
        var functionName = $"{prefix}-gate-checker";
        var logGroup = new LogGroup($"{prefix}-gate-checker-logs", new LogGroupArgs
        {
            Name = $"/aws/lambda/{functionName}",
            RetentionInDays = config.Environment == "prod" ? 30 : 7,
            Tags =
            {
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        // --- Build Lambda package ---
        var zipPath = EnsureLambdaPackageBuilt();

        // --- Lambda Function ---
        var lambda = new Function($"{prefix}-gate-checker", new FunctionArgs
        {
            Name = functionName,
            Runtime = Pulumi.Aws.Lambda.Runtime.Python3d12,
            Handler = "handler.handler",
            Role = role.Arn,
            Timeout = 900, // init_config may extract Default.zip + DB operations + EFS writes
            MemorySize = 256,
            Code = new FileArchive(zipPath),
            VpcConfig = new Pulumi.Aws.Lambda.Inputs.FunctionVpcConfigArgs
            {
                SubnetIds = network.PrivateSubnetIds,
                SecurityGroupIds = { sg.Id },
            },
            FileSystemConfig = new Pulumi.Aws.Lambda.Inputs.FunctionFileSystemConfigArgs
            {
                Arn = ap.Arn,
                LocalMountPath = "/mnt/efs",
            },
            Environment = new Pulumi.Aws.Lambda.Inputs.FunctionEnvironmentArgs
            {
                Variables =
                {
                    { "EFS_MOUNT_PATH", "/mnt/efs" },
                    { "SYSTEM_KEY", config.SystemKey },
                    { "RDS_SECRET_ARN", awsDatabase.MasterSecretArn },
                    { "RDS_HOST", awsDatabase.Endpoint },
                    { "RDS_PORT", awsDatabase.Port.Apply(p => p.ToString()) },
                },
            },
            Tags =
            {
                { "Name", functionName },
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions
        {
            Parent = this,
            DependsOn = { logGroup, inlinePolicy },
        });

        return new AwsGateCheckerOutputs
        {
            FunctionName = lambda.Name,
            FunctionArn = lambda.Arn,
        };
    }

    /// <summary>
    /// Build the gate-checker Lambda zip package.
    /// Installs pip dependencies and bundles handler.py into a zip file.
    /// </summary>
    private static string EnsureLambdaPackageBuilt()
    {
        // Locate the GateChecker source directory relative to the Lz.Aws assembly
        var assemblyDir = Path.GetDirectoryName(typeof(AwsGateCheckerLambdaComponent).Assembly.Location)!;

        // Walk up to find the Lz.Aws project root (contains Lambda/GateChecker/)
        var projectDir = FindProjectRoot(assemblyDir);
        var sourceDir = Path.Combine(projectDir, "Lambda", "GateChecker");
        var buildDir = Path.Combine(sourceDir, "build");
        var zipPath = Path.Combine(buildDir, "gate-checker.zip");

        // Skip if zip already exists and is newer than handler.py and bin/lib dirs
        var handlerPath = Path.Combine(sourceDir, "handler.py");
        var binSourceDir = Path.Combine(sourceDir, "bin");
        var libSourceDir = Path.Combine(sourceDir, "lib");
        if (File.Exists(zipPath)
            && File.GetLastWriteTimeUtc(zipPath) > File.GetLastWriteTimeUtc(handlerPath)
            && !HasNewerFiles(binSourceDir, zipPath)
            && !HasNewerFiles(libSourceDir, zipPath))
        {
            Log.Info($"Gate-checker Lambda package up-to-date: {zipPath}");
            return zipPath;
        }

        Log.Info("Building gate-checker Lambda package...");

        var stageDir = Path.Combine(buildDir, "stage");
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);

        // Install pip dependencies
        var requirementsPath = Path.Combine(sourceDir, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            var pipProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pip",
                Arguments = $"install -r \"{requirementsPath}\" -t \"{stageDir}\" --quiet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            pipProcess?.WaitForExit();
            if (pipProcess?.ExitCode != 0)
            {
                var stderr = pipProcess?.StandardError.ReadToEnd();
                throw new Exception($"pip install failed: {stderr}");
            }
        }

        // Copy handler.py into stage
        File.Copy(handlerPath, Path.Combine(stageDir, "handler.py"), overwrite: true);

        // Copy bin/ and lib/ directories if present (psql binary and shared libraries)
        CopyDirectoryIfExists(binSourceDir, Path.Combine(stageDir, "bin"));
        CopyDirectoryIfExists(libSourceDir, Path.Combine(stageDir, "lib"));

        // Create zip
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(stageDir, zipPath);

        // Clean up stage dir
        Directory.Delete(stageDir, true);

        Log.Info($"Gate-checker Lambda package built: {zipPath}");
        return zipPath;
    }

    /// <summary>
    /// Check if any file in a directory is newer than a reference file.
    /// Returns false if the directory doesn't exist.
    /// </summary>
    private static bool HasNewerFiles(string dir, string referenceFile)
    {
        if (!Directory.Exists(dir)) return false;
        var refTime = File.GetLastWriteTimeUtc(referenceFile);
        return Directory.GetFiles(dir).Any(f => File.GetLastWriteTimeUtc(f) > refTime);
    }

    /// <summary>
    /// Copy all files from a source directory into a destination directory.
    /// Logs the count of files copied. No-op if source doesn't exist.
    /// </summary>
    private static void CopyDirectoryIfExists(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(destDir);
        var files = Directory.GetFiles(sourceDir);
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
        Log.Info($"Bundled {files.Length} file(s) from {Path.GetFileName(sourceDir)}/");
    }

    /// <summary>
    /// Walk up from the assembly output directory to find the Lz.Aws project root.
    /// </summary>
    private static string FindProjectRoot(string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Lz.Aws.csproj")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: try relative to current working directory
        var cwd = Directory.GetCurrentDirectory();
        var candidate = Path.Combine(cwd, "..", "Lzm", "Lz.Aws");
        if (Directory.Exists(candidate))
            return Path.GetFullPath(candidate);

        throw new Exception(
            "Could not locate Lz.Aws project root. " +
            "Ensure the Lambda/GateChecker directory exists alongside Lz.Aws.csproj.");
    }
}
