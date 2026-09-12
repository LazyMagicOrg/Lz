using System.IO.Compression;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Creates the in-account deployer, in a TARGET account — <c>scu-dev</c>, not the build account.
///
/// <para>WHY THIS IS A SEPARATE COMMAND from <see cref="PipelineBootstrapper"/>, which §5.5's
/// wording did not anticipate: the two sides have different CARDINALITY. There is one build
/// account per system and one target account per ENVIRONMENT, so the build side is bootstrapped
/// once ever and this side once per environment. Folding them together would be harmless
/// mechanically — everything is idempotent — but it would require build-account write credentials
/// to stand up a test deployer, coupling two privilege domains for no benefit. Bootstrapping is
/// meant to be narrow and time-boxed (§5.5); two commands keep each one pointed at a single
/// account and a single set of rights.</para>
///
/// <para>IT STOPS SHORT OF MAKING THE DEPLOYER LIVE, deliberately. The roles, the evidence store, the
/// functions and the state machine are created; the EventBridge rule that starts an execution when a
/// build record lands is NOT (stage D), and the signature hook is attached to no service (C4's wiring,
/// which moves the deploy plan). Inert and inspectable beats live and broken.</para>
/// </summary>
public static class DeployerBootstrapper
{
    public static async Task BootstrapAsync(SystemConfig config, bool apply)
    {
        // GATED, exactly as bootstrappipeline is. The deployer is the half of the pipeline that
        // runs INSIDE this account and can roll its services; it is not created for a system that
        // has not opted in. Checked before anything is read from AWS.
        if (config.Pipeline is not { Enabled: true })
            throw new InvalidOperationException(
                $"lz bootstrapdeployer refuses: {config.SystemKey}/{config.Environment} has no " +
                "`Pipeline:` block with `Enabled: true`. This command creates a state machine and a " +
                "role that can roll this account's ECS services; it will not do that for a system " +
                "that has not opted in.");

        var profile = config.Profile;
        var region = config.Region;

        var accountId = await ResolveAccountAsync(profile, region);
        var plan = DeployerPlanner.Plan(config, accountId);

        // THE PACKAGES ARE READ BEFORE ANYTHING IS CREATED, so a build without them fails the dry run
        // rather than half-way through an apply.
        var packages = LocatePackages(plan);

        Print(config, plan, accountId, profile, apply, packages);

        if (!apply)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("DRY RUN — nothing was created. Re-run with --apply to create it.");
            Console.ResetColor();
            return;
        }

        // THE MIRROR OF bootstrappipeline's GUARD, and it catches the likelier mistake of the two:
        // running this with the build profile out of habit. The deployer belongs in the account it
        // deploys INTO; creating it in the build account would put a role that can roll ECS services
        // in the one account that is supposed to run no workload.
        if (config.Pipeline?.ArtifactAccountId is { } build && build == accountId)
            throw new InvalidOperationException(
                $"this profile resolves to {accountId}, which is Pipeline.ArtifactAccountId — the " +
                "BUILD account. The deployer belongs in the account it deploys into. Pass the " +
                "target environment's profile (e.g. --profile scu-dev).");

        // AND IT MUST BE THIS ENVIRONMENT'S ACCOUNT, not merely not the build account: this run writes a
        // registry policy admitting the build account's images, and prod's profile against dev's config
        // would admit dev's replication into prod.
        CrossAccount.RequireTargetAccount(config.Pipeline?.TargetAccountId, accountId, "bootstrapdeployer");

        var creds = AwsCredentialsFactory.Resolve(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        using var s3 = creds != null ? new AmazonS3Client(creds, endpoint) : new AmazonS3Client(endpoint);
        using var iam = creds != null
            ? new AmazonIdentityManagementServiceClient(creds, endpoint)
            : new AmazonIdentityManagementServiceClient(endpoint);
        using var sfn = creds != null
            ? new AmazonStepFunctionsClient(creds, endpoint)
            : new AmazonStepFunctionsClient(endpoint);
        using var lambda = creds != null
            ? new AmazonLambdaClient(creds, endpoint) : new AmazonLambdaClient(endpoint);

        await EnsureEvidenceStoreAsync(s3, plan.EvidenceStore, region);

        // THE REPOSITORIES BEFORE THE PERMISSION, so there is no moment at which the build account may
        // replicate into a repository this account has not hardened. Without ecr:CreateRepository in the
        // policy, replication into a missing repository fails rather than creating an unhardened one.
        using (var ecr = creds != null ? new Amazon.ECR.AmazonECRClient(creds, endpoint) : new Amazon.ECR.AmazonECRClient(endpoint))
        {
            foreach (var repository in plan.ImageRepositories)
                await EcrRepositoryHardening.EnsureAsync(ecr, repository, config.Hygiene?.EcrUntaggedImageRetentionDays ?? 14);

            await ApplyReplicationPermissionAsync(ecr, plan.ReplicationPermission
                ?? throw new InvalidOperationException("the plan has no replication permission; it was planned without an account."));
        }

        var roleArn = await EnsureRoleAsync(iam, plan.RoleName, "states.amazonaws.com",
            "lz decoupled-CD deployer. Rolls the service image by digest.",
            ($"{plan.RoleName}-deploy", plan.RolePolicy), plan.DenyPolicy);

        foreach (var fn in plan.Functions)
        {
            var fnRole = await EnsureRoleAsync(iam, fn.RoleName, "lambda.amazonaws.com",
                $"lz decoupled-CD deployer function {fn.Name}.",
                ($"{fn.RoleName}-grant", fn.Policy), plan.DenyPolicy);

            await EnsureFunctionAsync(lambda, fn, fnRole, packages[fn.Package]);
        }

        await EnsureRoleAsync(iam, plan.HookInvokerRoleName, "ecs.amazonaws.com",
            "Lets ECS invoke the lz signature hook during a service deployment.",
            ($"{plan.HookInvokerRoleName}-invoke", plan.HookInvokerPolicy), plan.DenyPolicy);

        await EnsureStateMachineAsync(sfn, plan, roleArn, accountId, region);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Deployer bootstrap complete.");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine("THE DEPLOYER IS NOT LIVE, and nothing here made it so:");
        Console.WriteLine("  - no EventBridge rule starts it when a build record lands (stage D)");
        Console.WriteLine("  - no reconciler schedule enumerates the build-record store (stage D)");
        Console.WriteLine("  - no artifact-without-a-record anomaly alarm (stage D)");
        Console.WriteLine("  - the signature hook is attached to no ECS service (C4 wiring, which moves the deploy plan)");
        Console.WriteLine();
        var verifyRole = plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify).RoleName;
        Console.WriteLine("THE BUILD ACCOUNT'S HALF is a separate command, run with that account's profile:");
        Console.WriteLine($"  lz bootstrappipeline --apply   (replicates into this account; lets {verifyRole} read image/*)");
        Console.WriteLine("  Only images pushed AFTER it runs replicate here — ECR does not copy what is already there.");
    }

    /// <summary>
    /// The zips each function's code comes from, read into memory, or a refusal naming what is
    /// missing. Looked for next to Lz.Aws.dll, where the build puts them in every load scenario.
    /// </summary>
    private static Dictionary<string, PackageBytes> LocatePackages(PipelineDeployer plan)
    {
        var dir = Path.Combine(Path.GetDirectoryName(typeof(DeployerBootstrapper).Assembly.Location)!, "Lambda");
        var result = new Dictionary<string, PackageBytes>();

        foreach (var package in plan.Functions.Select(f => f.Package).Distinct())
        {
            var path = Path.Combine(dir, DeployerPackages.ZipFor(package));
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"the deployment package {path} does not exist. It is built with Lz.Aws " +
                    "(`dotnet build Lz.slnx`); a build that skipped it cannot create the deployer's functions.");

            var bytes = File.ReadAllBytes(path);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var missing = NotationLayout.MissingFromPackage(zip.Entries.Select(e => e.FullName));
            result[package] = new PackageBytes(path, bytes, missing);
        }

        return result;
    }

    private sealed record PackageBytes(string Path, byte[] Bytes, IReadOnlyList<string> MissingVerifierFiles);

    private static async Task<string> ResolveAccountAsync(string? profile, string region)
    {
        var creds = AwsCredentialsFactory.Resolve(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        using var sts = creds != null
            ? new AmazonSecurityTokenServiceClient(creds, endpoint)
            : new AmazonSecurityTokenServiceClient(endpoint);

        return (await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest())).Account;
    }

    private static void Print(
        SystemConfig config, PipelineDeployer plan, string accountId, string? profile, bool apply,
        IReadOnlyDictionary<string, PackageBytes> packages)
    {
        Console.WriteLine($"=== Deployer bootstrap: {config.SystemKey} / {config.Environment} ===");
        Console.WriteLine($"  account:  {accountId}{(string.IsNullOrEmpty(profile) ? " (ambient credentials)" : $" (profile {profile})")}");
        Console.WriteLine($"  region:   {config.Region}");
        Console.WriteLine($"  mode:     {(apply ? "APPLY" : "dry run")}");
        Console.WriteLine();
        Console.WriteLine($"  state machine: {plan.StateMachineName}  (Standard)");
        Console.WriteLine($"  role:          {plan.RoleName}  + an explicit self-rewrite Deny");
        Console.WriteLine($"  evidence:      {plan.EvidenceStore}");
        Console.WriteLine($"  approval:      {(plan.ApprovalRequired ? "REQUIRED — a waitForTaskToken gate" : "not required (this environment deploys on its own)")}");
        Console.WriteLine();
        Console.WriteLine("  functions (each with its own role and the same Deny):");
        foreach (var fn in plan.Functions)
            Console.WriteLine($"    {fn.Name,-40} {fn.Package}.zip  {fn.TimeoutSeconds}s  {fn.MemoryMb} MB");
        Console.WriteLine($"  hook invoker:  {plan.HookInvokerRoleName}  (assumed by ECS; invokes the hook only)");
        Console.WriteLine();
        Console.WriteLine($"  replicated repositories (immutable tags, AES256, scan-on-push): {string.Join(", ", plan.ImageRepositories)}");
        Console.WriteLine($"  registry policy {CrossAccount.ReplicationSid}: ecr:ReplicateImage from {config.Pipeline?.ArtifactAccountId} into exactly those, never ecr:CreateRepository");
        var target = config.Pipeline?.TargetAccountId;
        Console.WriteLine(target == accountId
            ? $"  target account: {accountId} matches Pipeline.TargetAccountId"
            : $"  target account: THIS PROFILE RESOLVES TO {accountId}, BUT Pipeline.TargetAccountId IS {target ?? "not set"}; apply will refuse");
        Console.WriteLine();

        foreach (var (name, package) in packages.OrderBy(p => p.Key))
        {
            Console.WriteLine($"  package {name}.zip: {package.Bytes.Length / 1024} KB");
            if (name == DeployerPackages.SignatureHook && package.MissingVerifierFiles.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("    WITHOUT THE VERIFIER — missing " + string.Join(", ", package.MissingVerifierFiles));
                Console.WriteLine("    The hook will answer FAILED to every deployment until Notation is packaged.");
                Console.WriteLine("    Harmless while it is attached to nothing; not usable until it is.");
                Console.ResetColor();
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Merge the replication permission into this registry's policy by Sid, preserving every other
    /// statement: the registry has one policy, and it may already grant something else.
    /// </summary>
    private static async Task ApplyReplicationPermissionAsync(
        Amazon.ECR.IAmazonECR ecr, System.Text.Json.Nodes.JsonObject statement)
    {
        string? existing = null;
        try
        {
            var response = await ecr.GetRegistryPolicyAsync(new Amazon.ECR.Model.GetRegistryPolicyRequest());

            // S3's SDK hands back "no policy" as a 404 RESPONSE rather than an exception (measured on the
            // build account's first apply). ECR's raised RegistryPolicyNotFoundException on dev's first apply —
            // the registry policy written was clean — but a response that is not a success is refused here
            // all the same, rather than merged into as though it were a policy.
            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                throw new InvalidOperationException(
                    $"reading the registry policy returned {(int)response.HttpStatusCode}; refusing to write over it.");

            existing = response.PolicyText;
        }
        catch (Amazon.ECR.Model.RegistryPolicyNotFoundException)
        {
            // No registry policy yet: the merge starts from an empty document.
        }

        var merged = CrossAccount.MergeBySid(existing, new[] { statement });
        await ecr.PutRegistryPolicyAsync(new Amazon.ECR.Model.PutRegistryPolicyRequest { PolicyText = merged });

        var total = System.Text.Json.Nodes.JsonNode.Parse(merged)!["Statement"]!.AsArray().Count;
        Console.WriteLine($"  registry policy written: ecr:ReplicateImage for the build account; {total - 1} other statement(s) preserved.");
    }

    private static async Task EnsureEvidenceStoreAsync(IAmazonS3 s3, string bucket, string region)
    {
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
            Console.WriteLine($"  evidence store '{bucket}' already exists. Skipping creation.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, BucketRegionName = region });
            Console.WriteLine($"  evidence store '{bucket}' created.");
        }

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true, BlockPublicPolicy = true,
                IgnorePublicAcls = true, RestrictPublicBuckets = true,
            },
        });
        Console.WriteLine("      versioning on, public access blocked.");
    }

    /// <summary>
    /// A role trusted by one service principal, with its grant and the self-rewrite Deny re-put on
    /// every run — so a policy that drifted from the plan is corrected, not skipped.
    ///
    /// <para>TWO POLICIES, NOT ONE. The Deny is separate so it is visible in the console rather than
    /// buried mid-document, and because an explicit Deny beats any Allow — including a future one
    /// nobody connects to this decision (§5.5).</para>
    /// </summary>
    private static async Task<string> EnsureRoleAsync(
        IAmazonIdentityManagementService iam, string roleName, string servicePrincipal, string description,
        (string Name, string Document) grant, string denyPolicy)
    {
        var trust = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Service = servicePrincipal },
                    Action = "sts:AssumeRole",
                },
            },
        });

        string arn;
        try
        {
            arn = (await iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName })).Role.Arn;
            Console.WriteLine($"  role '{roleName}' already exists — updating its policies.");
            await iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest
            {
                RoleName = roleName, PolicyDocument = trust,
            });
        }
        catch (NoSuchEntityException)
        {
            arn = (await iam.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = roleName,
                AssumeRolePolicyDocument = trust,
                Description = description,
                MaxSessionDuration = 3600,
            })).Role.Arn;
            Console.WriteLine($"  role '{roleName}' created (trusted by {servicePrincipal}).");
        }

        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = roleName, PolicyName = grant.Name, PolicyDocument = grant.Document,
        });
        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = roleName, PolicyName = $"{roleName}-deny-self-rewrite", PolicyDocument = denyPolicy,
        });
        Console.WriteLine("      grant + explicit self-rewrite Deny applied.");

        return arn;
    }

    /// <summary>
    /// Create the function, or bring an existing one to the plan: code first, then configuration,
    /// waiting for each update to settle — Lambda refuses a configuration change while a code update
    /// is still in progress.
    /// </summary>
    private static async Task EnsureFunctionAsync(
        IAmazonLambda lambda, DeployerFunction fn, string roleArn, PackageBytes package)
    {
        var environment = new Amazon.Lambda.Model.Environment
        {
            Variables = new Dictionary<string, string>(fn.Environment),
        };

        bool exists;
        try
        {
            await lambda.GetFunctionConfigurationAsync(new GetFunctionConfigurationRequest { FunctionName = fn.Name });
            exists = true;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            exists = false;
        }

        if (!exists)
        {
            // A ROLE CREATED SECONDS AGO IS NOT YET ASSUMABLE BY LAMBDA — IAM is eventually consistent,
            // and CreateFunction checks the role immediately. Retried on exactly that refusal and no
            // other, so a genuinely bad role still fails at once.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await lambda.CreateFunctionAsync(new CreateFunctionRequest
                    {
                        FunctionName = fn.Name,
                        Runtime = Runtime.Dotnet10,
                        Handler = fn.Handler,
                        Role = roleArn,
                        Code = new FunctionCode { ZipFile = new MemoryStream(package.Bytes) },
                        Timeout = fn.TimeoutSeconds,
                        MemorySize = fn.MemoryMb,
                        Architectures = new List<string> { Architecture.X86_64 },
                        Environment = environment,
                        PackageType = PackageType.Zip,
                        Description = "lz decoupled-CD deployer (DecoupledCd.md §5).",
                    });
                    break;
                }
                catch (InvalidParameterValueException ex)
                    when (attempt < 10 && ex.Message.Contains("cannot be assumed", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }

            await WaitForSettledAsync(lambda, fn.Name);
            Console.WriteLine($"  function '{fn.Name}' created.");
            return;
        }

        await lambda.UpdateFunctionCodeAsync(new UpdateFunctionCodeRequest
        {
            FunctionName = fn.Name,
            ZipFile = new MemoryStream(package.Bytes),
            Architectures = new List<string> { Architecture.X86_64 },
        });
        await WaitForSettledAsync(lambda, fn.Name);

        await lambda.UpdateFunctionConfigurationAsync(new UpdateFunctionConfigurationRequest
        {
            FunctionName = fn.Name,
            Runtime = Runtime.Dotnet10,
            Handler = fn.Handler,
            Role = roleArn,
            Timeout = fn.TimeoutSeconds,
            MemorySize = fn.MemoryMb,
            Environment = environment,
        });
        await WaitForSettledAsync(lambda, fn.Name);
        Console.WriteLine($"  function '{fn.Name}' already exists — code and configuration updated.");
    }

    private static async Task WaitForSettledAsync(IAmazonLambda lambda, string name)
    {
        for (var i = 0; i < 45; i++)
        {
            var c = await lambda.GetFunctionConfigurationAsync(new GetFunctionConfigurationRequest { FunctionName = name });

            if (c.State == State.Failed || c.LastUpdateStatus == LastUpdateStatus.Failed)
                throw new InvalidOperationException(
                    $"function '{name}' failed to settle: {c.StateReason ?? c.LastUpdateStatusReason}");

            if (c.State == State.Active && c.LastUpdateStatus != LastUpdateStatus.InProgress)
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException($"function '{name}' did not settle within 90 seconds.");
    }

    private static async Task EnsureStateMachineAsync(
        IAmazonStepFunctions sfn, PipelineDeployer plan, string roleArn, string accountId, string region)
    {
        var arn = $"arn:aws:states:{region}:{accountId}:stateMachine:{plan.StateMachineName}";

        try
        {
            await sfn.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = arn });
            // UPDATED rather than skipped: the definition is the deploy procedure, so a machine that
            // drifted from the plan is exactly what a re-run should correct.
            await sfn.UpdateStateMachineAsync(new UpdateStateMachineRequest
            {
                StateMachineArn = arn,
                Definition = plan.Definition,
                RoleArn = roleArn,
            });
            Console.WriteLine($"  state machine '{plan.StateMachineName}' already exists — definition updated.");
        }
        catch (StateMachineDoesNotExistException)
        {
            await sfn.CreateStateMachineAsync(new CreateStateMachineRequest
            {
                Name = plan.StateMachineName,
                Definition = plan.Definition,
                RoleArn = roleArn,
                // STANDARD, not Express: .waitForTaskToken — which the approval gate is — does not
                // exist on Express, and StartExecution's name-based idempotency is Standard-only.
                // That idempotency is what absorbs S3's at-least-once delivery (§5.1).
                Type = StateMachineType.STANDARD,
            });
            Console.WriteLine($"  state machine '{plan.StateMachineName}' created (Standard).");
        }
    }
}
