using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Lambda;
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
/// <para>IT STOPS SHORT OF MAKING THE DEPLOYER LIVE, deliberately. The role, the evidence store and
/// the state machine are created; the EventBridge rule that starts an execution when a build record
/// lands is NOT. The machine references three Lambdas that do not exist yet (P2 stage C), and
/// wiring the trigger before them would mean a real build record starting an execution that dies on
/// its first state. Inert and inspectable beats live and broken — and an un-triggered machine
/// harms nothing, while a half-wired one produces failed executions nobody asked for.</para>
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

        Print(config, plan, accountId, profile, apply);

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
        var roleArn = await EnsureRoleAsync(iam, plan, accountId);
        var missing = await ReportMissingFunctionsAsync(lambda, plan.Functions);
        await EnsureStateMachineAsync(sfn, plan, roleArn, accountId, region);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Deployer bootstrap complete.");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine("THE DEPLOYER IS NOT LIVE, and nothing here made it so:");
        Console.WriteLine("  - no EventBridge rule starts it when a build record lands");
        Console.WriteLine("  - no reconciler schedule enumerates the build-record store");
        Console.WriteLine("  - no artifact-without-a-record anomaly alarm");
        if (missing.Count > 0)
        {
            Console.WriteLine($"  - {missing.Count} of its {plan.Functions.Count} Lambda functions do not exist:");
            foreach (var fn in missing) Console.WriteLine($"      {fn}");
        }
        Console.WriteLine("Wiring the trigger before those exist would mean a real build record");
        Console.WriteLine("starting an execution that dies on its first state. See DecoupledCd.md P2.");
    }

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
        SystemConfig config, PipelineDeployer plan, string accountId, string? profile, bool apply)
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
        Console.WriteLine($"  functions it references: {string.Join(", ", plan.Functions)}");
        Console.WriteLine();
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

    private static async Task<string> EnsureRoleAsync(
        IAmazonIdentityManagementService iam, PipelineDeployer plan, string accountId)
    {
        var trust = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Service = "states.amazonaws.com" },
                    Action = "sts:AssumeRole",
                },
            },
        });

        var arn = $"arn:aws:iam::{accountId}:role/{plan.RoleName}";

        try
        {
            await iam.GetRoleAsync(new GetRoleRequest { RoleName = plan.RoleName });
            Console.WriteLine($"  role '{plan.RoleName}' already exists — updating its policies.");
            await iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest
            {
                RoleName = plan.RoleName, PolicyDocument = trust,
            });
        }
        catch (NoSuchEntityException)
        {
            await iam.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = plan.RoleName,
                AssumeRolePolicyDocument = trust,
                Description = "lz decoupled-CD deployer. Rolls the service image by digest.",
                MaxSessionDuration = 3600,
            });
            Console.WriteLine($"  role '{plan.RoleName}' created.");
        }

        // TWO POLICIES, NOT ONE. The Deny is separate so it is visible in the console rather than
        // buried mid-document, and because an explicit Deny beats any Allow — including a future
        // one nobody connects to this decision (§5.5).
        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = plan.RoleName,
            PolicyName = $"{plan.RoleName}-deploy",
            PolicyDocument = plan.RolePolicy,
        });
        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = plan.RoleName,
            PolicyName = $"{plan.RoleName}-deny-self-rewrite",
            PolicyDocument = plan.DenyPolicy,
        });
        Console.WriteLine("      deploy policy + explicit self-rewrite Deny applied.");

        return arn;
    }

    /// <summary>
    /// Which of the definition's Lambdas do not exist. REPORTED, not refused: the machine is inert
    /// until something triggers it, and nothing here wires a trigger — so creating it with its
    /// functions missing is a state that can be inspected rather than one that can misfire.
    /// </summary>
    private static async Task<List<string>> ReportMissingFunctionsAsync(
        IAmazonLambda lambda, IReadOnlyList<string> functions)
    {
        var missing = new List<string>();
        foreach (var fn in functions)
        {
            try
            {
                await lambda.GetFunctionAsync(new Amazon.Lambda.Model.GetFunctionRequest { FunctionName = fn });
            }
            catch (Amazon.Lambda.Model.ResourceNotFoundException)
            {
                missing.Add(fn);
            }
        }
        return missing;
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
