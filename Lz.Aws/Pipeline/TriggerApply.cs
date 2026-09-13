using System.Text.Json.Nodes;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Lambda;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Applies the trigger's two halves (DecoupledCd.md §4.3, P2 stage D): the build account's forwarding, from
/// <see cref="PipelineBootstrapper"/>, and the target account's bus, rule and start function, from
/// <see cref="DeployerBootstrapper"/>.
///
/// <para>IT READS NO CONFIG. Every name and document comes from a plan, so the byte-identical guard's allowlist does not
/// grow, and what is written is what the dry run printed.</para>
///
/// <para>EVERYTHING IS READ BACK. A rule that exists with the wrong target, a policy AWS stored differently, or a queue that
/// an alarm cannot see fails quietly at the first build — so each piece is checked after it is written, and a mismatch
/// stops the apply with what was found.</para>
/// </summary>
internal static class TriggerApply
{
    /// <summary>The single target id on each trigger rule. Any other target found on the rule is removed.</summary>
    public const string TargetId = "lz-trigger";

    /// <summary>The start function's permission statement for the rule that invokes it.</summary>
    public const string InvokePermissionSid = "InvokedByTheTriggerRule";

    /// <summary>How long a dead-letter queue keeps a failed event: fourteen days, SQS's maximum.</summary>
    public const int DeadLetterRetentionSeconds = 1209600;

    // ---------------------------------------------------------------------------------------------
    //  THE BUILD ACCOUNT
    // ---------------------------------------------------------------------------------------------

    public static async Task ForwardRecordsAsync(
        IAmazonS3 s3, IAmazonIdentityManagementService iam, IAmazonEventBridge events, IAmazonSQS sqs,
        IAmazonCloudWatch cloudWatch, string recordStore, RecordForwardingPlan plan)
    {
        await EnableEventBridgeDeliveryAsync(s3, recordStore);

        var queueArn = await EnsureDeadLetterQueueAsync(sqs, plan.DeadLetterQueueName, plan.DeadLetterQueuePolicy);
        RequireEqual("dead-letter queue ARN", plan.DeadLetterQueueArn, queueArn);
        await EnsureAlarmAsync(cloudWatch, plan.AlarmName, plan.DeadLetterQueueName,
            "A build record's event could not be forwarded to its environment's trigger bus. The message says why " +
            "(ERROR_CODE, ERROR_MESSAGE); the build's deploy was not started.");

        var roleArn = await EnsureForwarderRoleAsync(iam, plan);
        RequireEqual("forwarding role ARN", plan.RoleArn, roleArn);

        await EnsureRuleAsync(events, eventBusName: null, plan.RuleName, plan.RuleArn, plan.EventPattern,
            "lz pipeline: forwards new image build records to one environment's deployer trigger bus.");

        await EnsureOnlyTargetAsync(events, eventBusName: null, plan.RuleName, new Target
        {
            Id = TargetId,
            Arn = plan.TargetBusArn,
            // AWS requires a role on a rule that targets another account's bus (for targets created after 2023-03-02).
            RoleArn = roleArn,
            DeadLetterConfig = new Amazon.EventBridge.Model.DeadLetterConfig { Arn = queueArn },
        });

        Console.WriteLine($"  forwarding: {plan.RuleName} -> {plan.TargetBusArn}");
        Console.WriteLine($"      as {plan.RoleName}; undeliverable events to {plan.DeadLetterQueueName} (alarm {plan.AlarmName}).");
    }

    /// <summary>
    /// Turn on the bucket's delivery of its events to EventBridge, keeping every other notification it has.
    ///
    /// <para>READ, MERGE, WRITE: <c>PutBucketNotificationConfiguration</c> "replaces the existing notification configuration",
    /// so writing only the EventBridge element would delete any topic, queue or function notification already there.
    /// AWS notes delivery takes about five minutes to take effect.</para>
    /// </summary>
    public static async Task EnableEventBridgeDeliveryAsync(IAmazonS3 s3, string bucket)
    {
        var current = await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket });
        if (current.EventBridgeConfiguration != null)
        {
            Console.WriteLine($"  record store '{bucket}': EventBridge delivery already on.");
            return;
        }

        // SDK v4: a collection with no members is null. Each is passed back as it came.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            TopicConfigurations = current.TopicConfigurations,
            QueueConfigurations = current.QueueConfigurations,
            LambdaFunctionConfigurations = current.LambdaFunctionConfigurations,
            EventBridgeConfiguration = new EventBridgeConfiguration(),
        });

        var after = await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket });
        if (after.EventBridgeConfiguration is null)
            throw new InvalidOperationException($"EventBridge delivery on '{bucket}' was written but does not read back as on.");

        var kept = (current.TopicConfigurations?.Count ?? 0) + (current.QueueConfigurations?.Count ?? 0)
                   + (current.LambdaFunctionConfigurations?.Count ?? 0);
        Console.WriteLine($"  record store '{bucket}': EventBridge delivery turned on ({kept} other notification(s) kept). " +
                          "AWS says it takes about five minutes to take effect.");
    }

    private static async Task<string> EnsureForwarderRoleAsync(IAmazonIdentityManagementService iam, RecordForwardingPlan plan)
    {
        string arn;
        try
        {
            arn = (await iam.GetRoleAsync(new GetRoleRequest { RoleName = plan.RoleName })).Role.Arn;
            await iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest
            {
                RoleName = plan.RoleName, PolicyDocument = plan.RoleTrustPolicy,
            });
            Console.WriteLine($"  role '{plan.RoleName}' already exists — trust and permission re-applied.");
        }
        catch (NoSuchEntityException)
        {
            arn = (await iam.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = plan.RoleName,
                AssumeRolePolicyDocument = plan.RoleTrustPolicy,
                Description = "lz pipeline: EventBridge forwards build-record events to one environment's trigger bus.",
                MaxSessionDuration = 3600,
            })).Role.Arn;
            Console.WriteLine($"  role '{plan.RoleName}' created (trusted by EventBridge for {plan.RuleName} only).");
        }

        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = plan.RoleName,
            PolicyName = $"{plan.RoleName}-put-events",
            PolicyDocument = plan.RolePermissionPolicy,
        });

        return arn;
    }

    // ---------------------------------------------------------------------------------------------
    //  THE TARGET ACCOUNT
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The bus, its policy, the queue and alarm, the start function's invoke permission and failure destination, and the
    /// rule — in that order, so the rule never exists before what it delivers to. The function itself is created with the
    /// deployer's others, before this runs.
    /// </summary>
    public static async Task StartOnRecordsAsync(
        IAmazonEventBridge events, IAmazonSQS sqs, IAmazonLambda lambda, IAmazonCloudWatch cloudWatch, DeployerTriggerPlan plan)
    {
        var busArn = await EnsureBusAsync(events, plan.BusName);
        RequireEqual("trigger bus ARN", plan.BusArn, busArn);
        await EnsureBusPolicyAsync(events, plan);

        var queueArn = await EnsureDeadLetterQueueAsync(sqs, plan.DeadLetterQueueName, plan.DeadLetterQueuePolicy);
        RequireEqual("dead-letter queue ARN", plan.DeadLetterQueueArn, queueArn);
        await EnsureAlarmAsync(cloudWatch, plan.AlarmName, plan.DeadLetterQueueName,
            "A build record reached this environment's trigger and no deploy was started: the start function refused or " +
            "failed on it, or EventBridge could not invoke the function. The message says which.");

        await EnsureInvokePermissionAsync(lambda, plan);
        await EnsureFailureDestinationAsync(lambda, plan);

        await EnsureRuleAsync(events, plan.BusName, plan.RuleName, plan.RuleArn, plan.EventPattern,
            "lz pipeline: starts the deployer when the build account forwards a new image build record.");

        await EnsureOnlyTargetAsync(events, plan.BusName, plan.RuleName, new Target
        {
            Id = TargetId,
            Arn = plan.StartFunctionArn,
            DeadLetterConfig = new Amazon.EventBridge.Model.DeadLetterConfig { Arn = queueArn },
        });

        Console.WriteLine($"  trigger: {plan.BusName} / {plan.RuleName} -> {plan.StartFunctionName}");
        foreach (var route in plan.Routes)
            Console.WriteLine($"      {route.RecordPrefix} rolls {string.Join(", ", route.Targets.Select(t => $"{t.Target.Service} ({t.Target.Cluster})"))}");
        Console.WriteLine($"      what fails goes to {plan.DeadLetterQueueName} (alarm {plan.AlarmName}).");
    }

    private static async Task<string> EnsureBusAsync(IAmazonEventBridge events, string name)
    {
        try
        {
            var existing = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
            Console.WriteLine($"  event bus '{name}' already exists.");
            return existing.Arn;
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            var created = await events.CreateEventBusAsync(new CreateEventBusRequest
            {
                Name = name,
                Description = "lz pipeline: build-record events forwarded from the build account; starts the deployer.",
            });
            Console.WriteLine($"  event bus '{name}' created.");
            return created.EventBusArn;
        }
    }

    /// <summary>
    /// The bus policy, written whole. Whether <c>PutPermission</c>'s <c>Policy</c> replaces a bus's policy or adds to it is
    /// not stated in its reference, so the read-back decides: a policy that is not exactly the planned one after a put is
    /// cleared and put again, and one that still is not stops the apply.
    /// </summary>
    private static async Task EnsureBusPolicyAsync(IAmazonEventBridge events, DeployerTriggerPlan plan)
    {
        async Task<string?> ReadAsync()
            => (await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = plan.BusName })).Policy;

        var current = await ReadAsync();
        if (CrossAccount.SamePolicy(plan.BusPolicy, current))
        {
            Console.WriteLine("      bus policy already admits only the build account's forwarding role.");
            return;
        }

        await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = plan.BusName, Policy = plan.BusPolicy });
        var after = await ReadAsync();

        if (!CrossAccount.SamePolicy(plan.BusPolicy, after))
        {
            await events.RemovePermissionAsync(new Amazon.EventBridge.Model.RemovePermissionRequest { EventBusName = plan.BusName, RemoveAllPermissions = true });
            await events.PutPermissionAsync(new PutPermissionRequest { EventBusName = plan.BusName, Policy = plan.BusPolicy });
            after = await ReadAsync();

            if (!CrossAccount.SamePolicy(plan.BusPolicy, after))
                throw new InvalidOperationException(
                    $"the policy on '{plan.BusName}' does not read back as the one written.\n  written: {plan.BusPolicy}\n  read:    {after}");
        }

        Console.WriteLine("      bus policy written and read back: only the build account's forwarding role may put events.");
    }

    private static async Task EnsureInvokePermissionAsync(IAmazonLambda lambda, DeployerTriggerPlan plan)
    {
        string? policy = null;
        try
        {
            policy = (await lambda.GetPolicyAsync(new Amazon.Lambda.Model.GetPolicyRequest { FunctionName = plan.StartFunctionName })).Policy;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            // No policy yet.
        }

        if (InvokePermissionIsFor(policy, plan.RuleArn))
        {
            Console.WriteLine($"  function '{plan.StartFunctionName}': invoke permission for {plan.RuleName} already present.");
            return;
        }

        if (policy != null && policy.Contains($"\"{InvokePermissionSid}\"", StringComparison.Ordinal))
            await lambda.RemovePermissionAsync(new Amazon.Lambda.Model.RemovePermissionRequest
            {
                FunctionName = plan.StartFunctionName, StatementId = InvokePermissionSid,
            });

        await lambda.AddPermissionAsync(new Amazon.Lambda.Model.AddPermissionRequest
        {
            FunctionName = plan.StartFunctionName,
            StatementId = InvokePermissionSid,
            Action = "lambda:InvokeFunction",
            Principal = "events.amazonaws.com",
            SourceArn = plan.RuleArn,
        });
        Console.WriteLine($"  function '{plan.StartFunctionName}': EventBridge may invoke it for {plan.RuleName} only.");
    }

    /// <summary>Does a function's resource policy hold our statement, allowing EventBridge for exactly this rule?</summary>
    public static bool InvokePermissionIsFor(string? policy, string ruleArn)
    {
        if (string.IsNullOrWhiteSpace(policy)) return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(policy);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        var statements = root?["Statement"] switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => new List<JsonObject> { single },
            _ => new List<JsonObject>(),
        };

        var ours = statements.SingleOrDefault(s => s["Sid"]?.GetValue<string>() == InvokePermissionSid);
        if (ours is null) return false;

        var principal = ours["Principal"] is JsonObject p ? p["Service"]?.GetValue<string>() : null;
        var sourceArn = (ours["Condition"] as JsonObject)?["ArnLike"]?["AWS:SourceArn"]?.GetValue<string>();
        return ours["Effect"]?.GetValue<string>() == "Allow"
               && principal == "events.amazonaws.com"
               && sourceArn == ruleArn;
    }

    /// <summary>
    /// Where a failed asynchronous invocation goes: the dead-letter queue, after Lambda's retries. Lambda checks the
    /// function's role can send there, and a role policy written moments earlier is not always visible yet, so that one
    /// refusal is retried briefly.
    /// </summary>
    private static async Task EnsureFailureDestinationAsync(IAmazonLambda lambda, DeployerTriggerPlan plan)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await lambda.PutFunctionEventInvokeConfigAsync(new Amazon.Lambda.Model.PutFunctionEventInvokeConfigRequest
                {
                    FunctionName = plan.StartFunctionName,
                    MaximumRetryAttempts = plan.MaximumRetryAttempts,
                    MaximumEventAgeInSeconds = plan.MaximumEventAgeSeconds,
                    DestinationConfig = new Amazon.Lambda.Model.DestinationConfig
                    {
                        OnFailure = new Amazon.Lambda.Model.OnFailure { Destination = plan.DeadLetterQueueArn },
                    },
                });
                break;
            }
            catch (Amazon.Lambda.Model.InvalidParameterValueException ex)
                when (attempt < 10 && ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        var read = await lambda.GetFunctionEventInvokeConfigAsync(new Amazon.Lambda.Model.GetFunctionEventInvokeConfigRequest
        {
            FunctionName = plan.StartFunctionName,
        });
        RequireEqual("the start function's failure destination", plan.DeadLetterQueueArn, read.DestinationConfig?.OnFailure?.Destination);
        Console.WriteLine($"      failed invocations: {read.MaximumRetryAttempts} retries within {read.MaximumEventAgeInSeconds} s, then {plan.DeadLetterQueueName}.");
    }

    // ---------------------------------------------------------------------------------------------
    //  SHARED
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The rule with this pattern, enabled. <c>PutRule</c> creates or updates it; the ARN it returns is checked against the
    /// plan, since a rule on a custom bus carries the bus in its ARN and the queue and function policies name that ARN.
    /// </summary>
    private static async Task EnsureRuleAsync(
        IAmazonEventBridge events, string? eventBusName, string name, string expectedArn, string pattern, string description)
    {
        var put = await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventBusName = eventBusName,
            EventPattern = pattern,
            State = RuleState.ENABLED,
            Description = description,
        });
        RequireEqual($"rule {name}'s ARN", expectedArn, put.RuleArn);

        var read = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = name, EventBusName = eventBusName });
        if (read.State != RuleState.ENABLED || !CrossAccount.SamePolicy(pattern, read.EventPattern))
            throw new InvalidOperationException(
                $"rule {name} reads back {read.State} with pattern {read.EventPattern}; written ENABLED with {pattern}.");

        Console.WriteLine($"  rule '{name}' written and read back: enabled, pattern as planned.");
    }

    /// <summary>The rule's one target, and no other: a target someone added by hand would receive every record event.</summary>
    private static async Task EnsureOnlyTargetAsync(IAmazonEventBridge events, string? eventBusName, string rule, Target target)
    {
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = eventBusName,
            Targets = new List<Target> { target },
        });
        if ((put.FailedEntryCount ?? 0) > 0)
            throw new InvalidOperationException(
                $"rule {rule} refused its target: " +
                string.Join("; ", (put.FailedEntries ?? new List<PutTargetsResultEntry>()).Select(f => $"{f.TargetId} {f.ErrorCode} {f.ErrorMessage}")));

        var targets = await ListTargetsAsync(events, eventBusName, rule);
        var others = targets.Where(t => t.Id != target.Id).Select(t => t.Id).ToList();
        if (others.Count > 0)
        {
            await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = eventBusName, Ids = others });
            Console.WriteLine($"      removed {others.Count} target(s) this command did not write: {string.Join(", ", others)}.");
        }

        var ours = targets.SingleOrDefault(t => t.Id == target.Id);
        if (ours is null
            || ours.Arn != target.Arn
            || ours.RoleArn != target.RoleArn
            || ours.DeadLetterConfig?.Arn != target.DeadLetterConfig?.Arn)
            throw new InvalidOperationException(
                $"rule {rule}'s target reads back as {ours?.Arn ?? "(none)"} (role {ours?.RoleArn ?? "none"}, dead-letter " +
                $"{ours?.DeadLetterConfig?.Arn ?? "none"}); written {target.Arn} (role {target.RoleArn ?? "none"}, dead-letter " +
                $"{target.DeadLetterConfig?.Arn ?? "none"}).");
    }

    private static async Task<List<Target>> ListTargetsAsync(IAmazonEventBridge events, string? eventBusName, string rule)
    {
        var all = new List<Target>();
        string? token = null;
        do
        {
            var page = await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest
            {
                Rule = rule, EventBusName = eventBusName, NextToken = token,
            });
            all.AddRange(page.Targets ?? new List<Target>());
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>
    /// Remove a trigger rule an earlier run created, when the trigger is off. Its targets go first — a rule with targets
    /// cannot be deleted. Everything else (the bus, queues, role, function) is left: without the rule nothing reaches it.
    /// </summary>
    public static async Task RemoveRuleAsync(IAmazonEventBridge events, string? eventBusName, string rule, string why)
    {
        try
        {
            await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = eventBusName });
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            Console.WriteLine($"  trigger rule '{rule}': not present ({why}).");
            return;
        }

        var targets = await ListTargetsAsync(events, eventBusName, rule);
        if (targets.Count > 0)
            await events.RemoveTargetsAsync(new RemoveTargetsRequest
            {
                Rule = rule, EventBusName = eventBusName, Ids = targets.Select(t => t.Id).ToList(),
            });

        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = eventBusName });

        try
        {
            await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = eventBusName });
            throw new InvalidOperationException($"trigger rule '{rule}' was deleted but still reads back.");
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  trigger rule '{rule}' removed ({why}).");
            Console.ResetColor();
        }
    }

    private static async Task<string> EnsureDeadLetterQueueAsync(IAmazonSQS sqs, string name, string policy)
    {
        var attributes = new Dictionary<string, string>
        {
            ["Policy"] = policy,
            ["MessageRetentionPeriod"] = DeadLetterRetentionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        string url;
        try
        {
            url = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = attributes });
            Console.WriteLine($"  queue '{name}' already exists — policy and retention re-applied.");
        }
        catch (QueueDoesNotExistException)
        {
            url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes })).QueueUrl;
            Console.WriteLine($"  queue '{name}' created (14-day retention).");
        }

        var read = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = new List<string> { "QueueArn", "Policy" },
        });
        if (!CrossAccount.SamePolicy(policy, read.Policy))
            throw new InvalidOperationException($"queue '{name}''s policy does not read back as written.\n  written: {policy}\n  read:    {read.Policy}");

        return read.QueueARN;
    }

    /// <summary>
    /// ALARM when the queue holds anything. It notifies nobody: no channel exists yet, and a CloudWatch alarm is where the
    /// state is visible until one does (DecoupledCd.md §14, D3).
    /// </summary>
    private static async Task EnsureAlarmAsync(IAmazonCloudWatch cloudWatch, string alarm, string queue, string description)
    {
        await cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
        {
            AlarmName = alarm,
            AlarmDescription = description,
            Namespace = "AWS/SQS",
            MetricName = "ApproximateNumberOfMessagesVisible",
            Dimensions = new List<Dimension> { new() { Name = "QueueName", Value = queue } },
            Statistic = Statistic.Maximum,
            Period = 300,
            EvaluationPeriods = 1,
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.GreaterThanOrEqualToThreshold,
            // An empty queue that has seen no traffic reports no data; that is not a failure.
            TreatMissingData = "notBreaching",
        });
        Console.WriteLine($"  alarm '{alarm}': in ALARM while {queue} holds a message.");
    }

    private static void RequireEqual(string what, string expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{what} is {actual ?? "(none)"}; the plan expects {expected}.");
    }
}
