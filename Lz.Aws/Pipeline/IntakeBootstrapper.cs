using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.IdentityManagement;
using Amazon.Lambda;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Applies <see cref="IntakeBuildPlan"/> in the build account (DecoupledCd.md V-B), from <c>lz bootstrappipeline --apply</c>, after
/// the pipeline's own stores and roles. Every resource is created or corrected and, where the API allows, read back.
///
/// <para><b>THE SECRET IS THE OWNER'S.</b> Nothing here writes the App's key (V-17). When the secret does not exist yet,
/// everything else is created and the one-minute schedule is left DISABLED, since every poll would fail without the key; a
/// re-run once the secret exists enables it.</para>
/// </summary>
public static class IntakeBootstrapper
{
    public static void Print(IntakeBuildPlan plan)
    {
        Console.WriteLine();
        Console.WriteLine("  intake (DecoupledCd V-B, dry: fetches and checks, pushes nothing, writes no build record):");
        foreach (var r in plan.Repositories)
            Console.WriteLine($"    {r.Repo} ({r.RepositoryId}), {r.WorkflowPath}, {r.Mode}: {string.Join(", ", r.Artifacts.Select(a => $"{a}-{{attempt}}"))}");
        Console.WriteLine($"    evidence:   {plan.EvidenceStore}  (write-once)");
        Console.WriteLine($"    scratch:    {plan.ScratchBucket}  (objects expire after {plan.ScratchExpirationDays} day; only {plan.Functions.Single(f => f.Handler == DeployerHandlers.IntakeFetch).RoleName} writes)");
        Console.WriteLine($"    App key:    Secrets Manager {plan.AppSecretName}  (written by the owner, never by lz)");
        Console.WriteLine($"    machine:    {plan.StateMachineName}  (CheckRun, FetchAndStage, then taken-in, refused or fault)");
        foreach (var f in plan.Functions)
            Console.WriteLine($"    {f.Name,-28} {f.TimeoutSeconds,4}s {f.MemoryMb,5} MB  role {f.RoleName}");
        Console.WriteLine($"    schedule:   {plan.ScheduleRuleName} {plan.ScheduleExpression} -> {plan.PollerFunctionName} (one at a time, ticks at most {plan.PollerMaximumEventAgeSeconds} s old, never retried)");
        Console.WriteLine($"    alerts:     {plan.AlertsTopicName}; alarms {string.Join(", ", plan.Alarms.Select(a => a.Name))}");
    }

    public static async Task ApplyAsync(IntakeBuildPlan plan, string? profile, string region, int logRetentionDays)
    {
        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        T Client<T>(Func<Amazon.Runtime.AWSCredentials, Amazon.RegionEndpoint, T> withCreds, Func<Amazon.RegionEndpoint, T> ambient)
            => creds != null ? withCreds(creds, endpoint) : ambient(endpoint);

        using var s3 = Client<IAmazonS3>((c, e) => new AmazonS3Client(c, e), e => new AmazonS3Client(e));
        using var iam = Client<IAmazonIdentityManagementService>((c, e) => new AmazonIdentityManagementServiceClient(c, e), e => new AmazonIdentityManagementServiceClient(e));
        using var lambda = Client<IAmazonLambda>((c, e) => new AmazonLambdaClient(c, e), e => new AmazonLambdaClient(e));
        using var sfn = Client<IAmazonStepFunctions>((c, e) => new AmazonStepFunctionsClient(c, e), e => new AmazonStepFunctionsClient(e));
        using var sns = Client<IAmazonSimpleNotificationService>((c, e) => new AmazonSimpleNotificationServiceClient(c, e), e => new AmazonSimpleNotificationServiceClient(e));
        using var events = Client<IAmazonEventBridge>((c, e) => new AmazonEventBridgeClient(c, e), e => new AmazonEventBridgeClient(e));
        using var cloudWatch = Client<IAmazonCloudWatch>((c, e) => new AmazonCloudWatchClient(c, e), e => new AmazonCloudWatchClient(e));
        using var logs = Client<Amazon.CloudWatchLogs.IAmazonCloudWatchLogs>((c, e) => new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(c, e), e => new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(e));
        using var secrets = Client<IAmazonSecretsManager>((c, e) => new AmazonSecretsManagerClient(c, e), e => new AmazonSecretsManagerClient(e));

        Console.WriteLine();
        Console.WriteLine("Intake (V-B, dry):");
        var packages = DeployerBootstrapper.LocatePackages(plan.Functions.Select(f => f.Package));

        // Stores first: nothing below writes to them, but the functions' first run would.
        await DeployerBootstrapper.EnsureEvidenceStoreAsync(s3, plan.EvidenceStore, region);
        await BucketPolicies.MergeAsync(s3, plan.EvidenceStore, new[] { WriteOnceStore.Deny(plan.EvidenceStore) });
        Console.WriteLine($"      {plan.EvidenceStore}: write-once (LzWriteOnce).");
        await EnsureScratchBucketAsync(s3, plan, region);
        await EnsureTopicAsync(sns, plan);

        var secretExists = await SecretExistsAsync(secrets, plan.AppSecretName);
        Console.WriteLine(secretExists
            ? $"  secret '{plan.AppSecretName}' exists (its value is not read here)."
            : $"  secret '{plan.AppSecretName}' DOES NOT EXIST: the owner writes the intake App's key into it (GitHubApp.md); the schedule stays disabled until then.");

        foreach (var fn in plan.Functions)
        {
            await DeployerBootstrapper.EnsureLogGroupAsync(logs, $"/aws/lambda/{fn.Name}", logRetentionDays);
            var roleArn = await DeployerBootstrapper.EnsureRoleAsync(iam, fn.RoleName, plan.FunctionTrustPolicy,
                $"lz intake: {fn.Name} (DecoupledCd.md V-B).", ($"{fn.RoleName}-grant", fn.Policy), plan.DenyPolicy);
            await DeployerBootstrapper.EnsureFunctionAsync(lambda, fn, roleArn, packages[fn.Package],
                fn.Handler == DeployerHandlers.IntakeFetch ? IntakeBuildPlanner.FetchEphemeralStorageMb : null);
        }

        // ONE POLL AT A TIME (§5.3 rule 1): a reserved concurrency of one, ticks at most a minute old, none retried.
        //
        // AN ACCOUNT AT LAMBDA'S FLOOR CANNOT RESERVE ANY (measured 2026-10-21: scu-cicd and scu-dev both have 10 concurrent
        // executions, and AWS keeps 10 unreserved). For the intake poller one at a time only saves work (§5.3 rule 1): two
        // polls choose the same attempts, and their starts collapse on the execution name; its 50 s timeout is inside the
        // one-minute schedule besides. So the refusal is reported, not fatal. Raising the account's concurrency quota
        // restores the reservation on the next run.
        try
        {
            await lambda.PutFunctionConcurrencyAsync(new Amazon.Lambda.Model.PutFunctionConcurrencyRequest
            {
                FunctionName = plan.PollerFunctionName, ReservedConcurrentExecutions = plan.PollerReservedConcurrency,
            });
            Console.WriteLine($"  function '{plan.PollerFunctionName}': reserved concurrency {plan.PollerReservedConcurrency}.");
        }
        catch (Amazon.Lambda.Model.InvalidParameterValueException ex) when (ex.Message.Contains("UnreservedConcurrentExecution", StringComparison.Ordinal))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  function '{plan.PollerFunctionName}': NO reserved concurrency - the account is at Lambda's floor ({ex.Message.Trim()}). " +
                              "Overlapping polls collapse on the execution name; raise the account's concurrency quota to restore it.");
            Console.ResetColor();
        }
        await lambda.PutFunctionEventInvokeConfigAsync(new Amazon.Lambda.Model.PutFunctionEventInvokeConfigRequest
        {
            FunctionName = plan.PollerFunctionName, MaximumEventAgeInSeconds = plan.PollerMaximumEventAgeSeconds, MaximumRetryAttempts = 0,
        });
        Console.WriteLine($"  function '{plan.PollerFunctionName}': events at most {plan.PollerMaximumEventAgeSeconds} s old, never retried.");

        var machineRoleArn = await DeployerBootstrapper.EnsureRoleAsync(iam, plan.StateMachineRoleName, plan.StateMachineTrustPolicy,
            "lz intake machine (DecoupledCd.md V-B).", ($"{plan.StateMachineRoleName}-grant", plan.StateMachineRolePolicy), plan.DenyPolicy);
        await EnsureStateMachineAsync(sfn, plan, machineRoleArn);

        await EnsureScheduleAsync(events, lambda, plan, enabled: secretExists);
        foreach (var alarm in plan.Alarms) await EnsureAlarmAsync(cloudWatch, plan, alarm);
    }

    private static async Task<bool> SecretExistsAsync(IAmazonSecretsManager secrets, string name)
    {
        try
        {
            await secrets.DescribeSecretAsync(new Amazon.SecretsManager.Model.DescribeSecretRequest { SecretId = name });
            return true;
        }
        catch (Amazon.SecretsManager.Model.ResourceNotFoundException)
        {
            return false;
        }
    }

    private static async Task EnsureScratchBucketAsync(IAmazonS3 s3, IntakeBuildPlan plan, string region)
    {
        var bucket = plan.ScratchBucket;
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await s3.PutBucketAsync(S3BucketRequests.Create(bucket, region));
            Console.WriteLine($"  scratch bucket '{bucket}' created.");
        }

        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration { BlockPublicAcls = true, BlockPublicPolicy = true, IgnorePublicAcls = true, RestrictPublicBuckets = true },
        });
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules = new List<LifecycleRule>
                {
                    new()
                    {
                        Id = "lz-intake-scratch-one-day",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { Prefix = "" },
                        Expiration = new LifecycleRuleExpiration { Days = plan.ScratchExpirationDays },
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 1 },
                    },
                },
            },
        });
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = plan.ScratchBucketPolicy });
        var read = await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket });
        if (!CrossAccount.SamePolicy(plan.ScratchBucketPolicy, read.Policy))
            throw new InvalidOperationException($"the scratch bucket's policy does not read back as written.\n  written: {plan.ScratchBucketPolicy}\n  read:    {read.Policy}");
        Console.WriteLine($"  scratch bucket '{bucket}': public access blocked, objects expire after {plan.ScratchExpirationDays} day, only the fetch role writes, TLS only.");
    }

    private static async Task EnsureTopicAsync(IAmazonSimpleNotificationService sns, IntakeBuildPlan plan)
    {
        var created = await sns.CreateTopicAsync(new CreateTopicRequest { Name = plan.AlertsTopicName });
        TriggerApply.RequireEqual("intake alerts topic ARN", plan.AlertsTopicArn, created.TopicArn);
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = plan.AlertsTopicArn, AttributeName = "Policy", AttributeValue = plan.AlertsTopicPolicy });
        var read = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = plan.AlertsTopicArn })).Attributes?.GetValueOrDefault("Policy");
        if (!CrossAccount.SamePolicy(plan.AlertsTopicPolicy, read))
            throw new InvalidOperationException($"the intake topic's policy does not read back as written.\n  written: {plan.AlertsTopicPolicy}\n  read:    {read}");
        Console.WriteLine($"  topic '{plan.AlertsTopicName}': policy read back; lz subscribes nobody.");
    }

    private static async Task EnsureStateMachineAsync(IAmazonStepFunctions sfn, IntakeBuildPlan plan, string roleArn)
    {
        try
        {
            await sfn.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = plan.StateMachineArn });
            await sfn.UpdateStateMachineAsync(new UpdateStateMachineRequest { StateMachineArn = plan.StateMachineArn, Definition = plan.Definition, RoleArn = roleArn });
            Console.WriteLine($"  state machine '{plan.StateMachineName}' already exists — definition updated.");
        }
        catch (StateMachineDoesNotExistException)
        {
            // Standard: StartExecution's name is the idempotency key the poller and a nudge share (§5.3 rule 1).
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await sfn.CreateStateMachineAsync(new CreateStateMachineRequest
                    {
                        Name = plan.StateMachineName, Definition = plan.Definition, RoleArn = roleArn, Type = StateMachineType.STANDARD,
                    });
                    break;
                }
                catch (InvalidArnException) when (attempt < 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));   // a role created seconds ago is not yet assumable
                }
            }
            Console.WriteLine($"  state machine '{plan.StateMachineName}' created (Standard).");
        }
    }

    private static async Task EnsureScheduleAsync(IAmazonEventBridge events, IAmazonLambda lambda, IntakeBuildPlan plan, bool enabled)
    {
        var state = enabled ? RuleState.ENABLED : RuleState.DISABLED;
        var put = await events.PutRuleAsync(new PutRuleRequest
        {
            Name = plan.ScheduleRuleName, ScheduleExpression = plan.ScheduleExpression, State = state,
            Description = "lz intake: the poller, every minute (DecoupledCd.md §15.4).",
        });
        var functionArn = (await lambda.GetFunctionConfigurationAsync(new Amazon.Lambda.Model.GetFunctionConfigurationRequest { FunctionName = plan.PollerFunctionName })).FunctionArn;
        await TriggerApply.EnsureOnlyTargetAsync(events, null, plan.ScheduleRuleName, new Target { Id = "intake-poller", Arn = functionArn });
        await TriggerApply.EnsureInvokePermissionAsync(lambda, plan.PollerFunctionName, $"{plan.ScheduleRuleName}-invoke", plan.ScheduleRuleName, put.RuleArn);

        var read = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = plan.ScheduleRuleName });
        if (read.State != state || read.ScheduleExpression != plan.ScheduleExpression)
            throw new InvalidOperationException($"rule {plan.ScheduleRuleName} reads back {read.State} on {read.ScheduleExpression}; written {state} on {plan.ScheduleExpression}.");
        Console.WriteLine($"  rule '{plan.ScheduleRuleName}' read back: {read.State}, {plan.ScheduleExpression} -> {plan.PollerFunctionName}.");
    }

    private static async Task EnsureAlarmAsync(IAmazonCloudWatch cloudWatch, IntakeBuildPlan plan, IntakeAlarm alarm)
    {
        var actions = new List<string> { plan.AlertsTopicArn };
        await cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
        {
            AlarmName = alarm.Name,
            AlarmDescription = $"lz intake: {alarm.Description}.",
            Namespace = "AWS/Lambda",
            MetricName = alarm.Metric,
            Dimensions = new List<Dimension> { new() { Name = "FunctionName", Value = alarm.FunctionName } },
            Statistic = new Statistic(alarm.Statistic),
            Period = alarm.PeriodSeconds,
            EvaluationPeriods = 1,
            Threshold = alarm.Threshold,
            ComparisonOperator = new ComparisonOperator(alarm.Comparison),
            TreatMissingData = alarm.TreatMissingData,
            ActionsEnabled = true,
            AlarmActions = actions,
        });
        await TriggerApply.RequireAlarmActionsAsync(cloudWatch, alarm.Name, actions);
        Console.WriteLine($"  alarm '{alarm.Name}': {alarm.Description}; notifies {plan.AlertsTopicName}.");
    }
}
