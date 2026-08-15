using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using System.IO.Compression;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Shared;
using Pulumi;
using Pulumi.Aws.CloudWatch;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Lambda;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;

namespace Lz.Aws.Ops;

/// <summary>
/// Deploys a gate-checker Lambda that runs inside the VPC with EFS mount
/// and RDS access. Used by AwsFargateAlbTransitionChecker to verify EFS data and
/// database tables exist at gate-check time.
/// </summary>
public class AwsGateCheckerLambdaComponent : ComponentResource, IGateCheckerComponent
{
    public AwsGateCheckerLambdaComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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
        var awsNetwork = (AwsFargateAlbNetworkOutputs)network;
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
        // Uses UID 0 (root) because the EFS root directory is owned by root (0:0, 755).
        // The gate-checker must create tenant prefix directories (e.g. /med-monro-test/)
        // under the root before tenant access points have been exercised, so it needs
        // root-level write access. Files it creates inside tenant subdirectories will be
        // owned by UID 0, but the tenant access points (UID 1000) override ownership
        // when ECS tasks mount them.
        var ap = new AccessPoint($"{prefix}-gate-checker-ap", new AccessPointArgs
        {
            FileSystemId = fileStorage.FileSystemId,
            PosixUser = new AccessPointPosixUserArgs
            {
                Uid = 0,
                Gid = 0,
            },
            RootDirectory = new AccessPointRootDirectoryArgs
            {
                Path = "/",
                CreationInfo = new AccessPointRootDirectoryCreationInfoArgs
                {
                    OwnerUid = 0,
                    OwnerGid = 0,
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

        // EFS + Secrets Manager + S3 (themes bucket) inline policy
        // init_config needs: EFS write, Secrets Manager read/write for tenant secrets
        // deploy_theme needs: S3 read for theme tarballs
        var themesBucketName = $"keycloak-themes-{config.SystemSuffix}";
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
    }},
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""s3:GetObject""
      ],
      ""Resource"": ""arn:aws:s3:::{themesBucketName}/*""
    }},
    {{
      ""Effect"": ""Allow"",
      ""Action"": [
        ""s3:ListBucket"",
        ""s3:GetObject"",
        ""s3:PutObject""
      ],
      ""Resource"": [
        ""arn:aws:s3:::{prefix}-*--media--*"",
        ""arn:aws:s3:::{prefix}-*--media--*/*""
      ]
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
                    { "LZ_VERSION", typeof(AwsGateCheckerLambdaComponent).Assembly.GetName().Version?.ToString() ?? "unknown" },
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
    /// Locate the gate-checker Lambda zip package.
    /// Primary: use the pre-built zip distributed alongside the assembly (via Content item).
    /// Fallback: build from source when running from the development repo.
    /// </summary>
    private static string EnsureLambdaPackageBuilt()
    {
        // 1. Check for pre-built zip next to the assembly (shipped with the tool package)
        var assemblyDir = Path.GetDirectoryName(typeof(AwsGateCheckerLambdaComponent).Assembly.Location)!;
        var packagedZip = Path.Combine(assemblyDir, "Lambda", "gate-checker.zip");

        if (File.Exists(packagedZip))
        {
            Log.Info($"Using packaged gate-checker Lambda: {packagedZip}");
            return packagedZip;
        }

        // 2. Fall back to source-build (development scenario)
        Log.Info("Packaged gate-checker.zip not found next to assembly, building from source...");
        return BuildLambdaFromSource(assemblyDir);
    }

    /// <summary>
    /// Build the gate-checker Lambda zip from source (development only).
    /// Installs pip dependencies and bundles handler.py into a zip file.
    /// </summary>
    private static string BuildLambdaFromSource(string assemblyDir)
    {
        var projectDir = FindProjectRoot(assemblyDir);
        var sourceDir = Path.Combine(projectDir, "Lambda", "GateChecker");
        var buildDir = Path.Combine(sourceDir, "build");
        var zipPath = Path.Combine(buildDir, "gate-checker.zip");

        // Skip if zip already exists and is newer than handler.py and bin/lib dirs
        var handlerPath = Path.Combine(sourceDir, "handler.py");
        var binSourceDir = Path.Combine(sourceDir, "bin");
        var libSourceDir = Path.Combine(sourceDir, "lib");
        var psqlPath = Path.Combine(binSourceDir, "psql");
        if (File.Exists(zipPath)
            && File.Exists(psqlPath)
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

        // Ensure psql binary and shared libraries are extracted from Amazon Linux 2023
        EnsurePsqlExtracted(binSourceDir, libSourceDir);

        // Copy bin/ and lib/ directories into Lambda package
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
    /// Extract psql binary and shared libraries from Amazon Linux 2023 via Docker.
    /// Runs only once — skips if bin/psql already exists.
    /// </summary>
    private static void EnsurePsqlExtracted(string binDir, string libDir)
    {
        var psqlPath = Path.Combine(binDir, "psql");
        if (File.Exists(psqlPath))
        {
            Log.Info("psql binary already extracted, skipping Docker extraction.");
            return;
        }

        Log.Info("Extracting psql from Amazon Linux 2023 via Docker...");
        Console.WriteLine("  Extracting psql binary from Amazon Linux 2023 (one-time)...");

        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(libDir);

        // Convert Windows paths to Docker-compatible mount paths
        var binMount = binDir.Replace('\\', '/');
        var libMount = libDir.Replace('\\', '/');

        var script =
            "set -e; " +
            "dnf install -y postgresql15 > /dev/null 2>&1; " +
            "cp /usr/bin/psql /out/bin/; " +
            "chmod +x /out/bin/psql; " +
            "for lib in $(ldd /usr/bin/psql | grep '=> /' | awk '{print $3}'); do " +
            "cp \"$lib\" /out/lib/ 2>/dev/null || true; " +
            "done; " +
            "echo \"psql version: $(psql --version)\"";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm --platform linux/amd64 -v \"{binMount}:/out/bin\" -v \"{libMount}:/out/lib\" amazonlinux:2023 bash -c \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var process = System.Diagnostics.Process.Start(psi);
        var stdout = process?.StandardOutput.ReadToEnd() ?? "";
        var stderr = process?.StandardError.ReadToEnd() ?? "";
        process?.WaitForExit();

        if (process?.ExitCode != 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  WARNING: Could not extract psql via Docker (exit {process?.ExitCode}).");
            Console.WriteLine("  Database restore from database.sql will not be available.");
            Console.WriteLine("  Ensure Docker Desktop is running with Linux containers.");
            Console.ResetColor();
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.Warn($"Docker psql extraction stderr: {stderr}");
            return;
        }

        // Log the version line from stdout
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Contains("psql version:"))
            {
                Console.WriteLine($"  {line.Trim()}");
                break;
            }
        }

        Log.Info($"psql extracted: {Directory.GetFiles(binDir).Length} bin, {Directory.GetFiles(libDir).Length} lib files");
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
        // Check common sibling directory names for the Lz repo
        foreach (var siblingName in new[] { "Lzm", "_lz" })
        {
            var candidate = Path.Combine(cwd, "..", siblingName, "Lz.Aws");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        // Also check if cwd itself is inside the Lz repo (e.g. running from the repo root)
        var cwdCandidate = Path.Combine(cwd, "Lz.Aws");
        if (Directory.Exists(cwdCandidate) && File.Exists(Path.Combine(cwdCandidate, "Lz.Aws.csproj")))
            return Path.GetFullPath(cwdCandidate);

        throw new Exception(
            "Could not locate Lz.Aws project root. " +
            "Ensure the Lambda/GateChecker directory exists alongside Lz.Aws.csproj.");
    }
}
