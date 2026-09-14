using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Applies the alerts (P2 stage D3) in the target account, from <see cref="DeployerBootstrapper"/>: the topic and its policy,
/// the rule that sends it failed deploys, and the sweep's schedule and errors alarm.
///
/// <para>IT READS NO CONFIG, like <see cref="TriggerApply"/>, whose rule, target and permission writers it uses: every name
/// and document comes from the plan, and each piece is read back.</para>
///
/// <para>IT SUBSCRIBES NOBODY. A subscription is a person, and confirming one is theirs to do; the bootstrapper reports how
/// many the topic has.</para>
/// </summary>
internal static class AlertsApply
{
    /// <summary>The single target id on each alerts rule.</summary>
    public const string TargetId = "lz-alerts";

    /// <summary>The sweep function's permission statement for the schedule that invokes it.</summary>
    public const string ScheduleInvokeSid = "InvokedByTheCorroborateSchedule";

    /// <summary>The topic and its whole policy — before anything that publishes to it exists.</summary>
    public static async Task EnsureTopicAsync(IAmazonSimpleNotificationService sns, DeployerAlertsPlan plan)
    {
        // CreateTopic is idempotent for a name the account already owns: it returns that topic's ARN.
        var created = await sns.CreateTopicAsync(new CreateTopicRequest { Name = plan.TopicName });
        TriggerApply.RequireEqual("alerts topic ARN", plan.TopicArn, created.TopicArn);

        var current = await PolicyAsync(sns, plan.TopicArn);
        if (CrossAccount.SamePolicy(plan.TopicPolicy, current))
        {
            Console.WriteLine($"  alerts topic '{plan.TopicName}': policy already admits exactly this pipeline's publishers.");
            return;
        }

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = plan.TopicArn,
            AttributeName = "Policy",
            AttributeValue = plan.TopicPolicy,
        });

        var after = await PolicyAsync(sns, plan.TopicArn);
        if (!CrossAccount.SamePolicy(plan.TopicPolicy, after))
            throw new InvalidOperationException(
                $"the policy on '{plan.TopicName}' does not read back as the one written.\n  written: {plan.TopicPolicy}\n  read:    {after}");

        Console.WriteLine($"  alerts topic '{plan.TopicName}': policy written and read back — the failed-deploy rule and this pipeline's alarms may publish.");
    }

    /// <summary>The topic's confirmed and pending subscriptions, or null when there is no topic yet.</summary>
    public static async Task<(int Confirmed, int Pending)?> SubscriptionsAsync(IAmazonSimpleNotificationService sns, string topicArn)
    {
        try
        {
            var attributes = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes
                             ?? new Dictionary<string, string>();

            static int Count(Dictionary<string, string> a, string name)
                => a.TryGetValue(name, out var value) && int.TryParse(value, out var n) ? n : 0;

            return (Count(attributes, "SubscriptionsConfirmed"), Count(attributes, "SubscriptionsPending"));
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The failed-deploy rule, the sweep's errors alarm, and the schedule — the schedule last, after the permission that
    /// lets it invoke the function, so its first run is not refused.
    /// </summary>
    public static async Task WireAsync(
        IAmazonEventBridge events, IAmazonLambda lambda, IAmazonCloudWatch cloudWatch, DeployerAlertsPlan plan)
    {
        await TriggerApply.EnsureRuleAsync(events, eventBusName: null, plan.FailedDeployRuleName, plan.FailedDeployRuleArn,
            plan.FailedDeployPattern, "lz pipeline: sends this environment's failed, timed-out and aborted deploys to its alerts topic.");
        await TriggerApply.EnsureOnlyTargetAsync(events, eventBusName: null, plan.FailedDeployRuleName,
            new Target { Id = TargetId, Arn = plan.TopicArn });

        await EnsureErrorsAlarmAsync(cloudWatch, plan);

        await TriggerApply.EnsureInvokePermissionAsync(
            lambda, plan.CorroborateFunctionName, ScheduleInvokeSid, plan.ScheduleRuleName, plan.ScheduleRuleArn);
        await EnsureScheduleRuleAsync(events, plan);
        await TriggerApply.EnsureOnlyTargetAsync(events, eventBusName: null, plan.ScheduleRuleName,
            new Target { Id = TargetId, Arn = plan.CorroborateFunctionArn });

        Console.WriteLine($"  alerts: failed deploys -> {plan.TopicName} (rule {plan.FailedDeployRuleName});");
        Console.WriteLine($"      {plan.CorroborateFunctionName} runs {plan.ScheduleExpression}; its failures alarm {plan.CorroborateErrorsAlarmName}.");
    }

    /// <summary>
    /// With alerts off: the two rules and the errors alarm an earlier run created. The topic stays — its subscriptions are
    /// people's — and so does the function, which nothing then invokes.
    /// </summary>
    public static async Task RemoveAsync(
        IAmazonEventBridge events, IAmazonCloudWatch cloudWatch, string failedDeployRule, string scheduleRule, string errorsAlarm)
    {
        const string why = "Pipeline.Alerts is off";
        await TriggerApply.RemoveRuleAsync(events, eventBusName: null, failedDeployRule, why);
        await TriggerApply.RemoveRuleAsync(events, eventBusName: null, scheduleRule, why);

        var found = await cloudWatch.DescribeAlarmsAsync(new DescribeAlarmsRequest { AlarmNames = new List<string> { errorsAlarm } });
        if ((found.MetricAlarms ?? new List<MetricAlarm>()).Count == 0)
        {
            Console.WriteLine($"  alarm '{errorsAlarm}': not present ({why}).");
            return;
        }

        await cloudWatch.DeleteAlarmsAsync(new DeleteAlarmsRequest { AlarmNames = new List<string> { errorsAlarm } });
        Console.WriteLine($"  alarm '{errorsAlarm}' removed ({why}).");
    }

    private static async Task EnsureErrorsAlarmAsync(IAmazonCloudWatch cloudWatch, DeployerAlertsPlan plan)
    {
        var actions = new[] { plan.TopicArn };
        await cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
        {
            AlarmName = plan.CorroborateErrorsAlarmName,
            AlarmDescription =
                "The sweep for images no build record names failed. Its log group says why; until it runs again, nothing looks " +
                "for such images.",
            Namespace = "AWS/Lambda",
            MetricName = "Errors",
            Dimensions = new List<Dimension> { new() { Name = "FunctionName", Value = plan.CorroborateFunctionName } },
            Statistic = Statistic.Sum,
            // One sweep's period: a run that failed, including Lambda's retries of it, is one ALARM.
            Period = 900,
            EvaluationPeriods = 1,
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.GreaterThanOrEqualToThreshold,
            // A function that has not been invoked reports no data; that is not a failure.
            TreatMissingData = "notBreaching",
            ActionsEnabled = true,
            AlarmActions = actions.ToList(),
        });

        await TriggerApply.RequireAlarmActionsAsync(cloudWatch, plan.CorroborateErrorsAlarmName, actions);
        Console.WriteLine($"  alarm '{plan.CorroborateErrorsAlarmName}': in ALARM when a sweep fails; notifies {plan.TopicName}.");
    }

    private static async Task EnsureScheduleRuleAsync(IAmazonEventBridge events, DeployerAlertsPlan plan)
    {
        var put = await events.PutRuleAsync(new PutRuleRequest
        {
            Name = plan.ScheduleRuleName,
            ScheduleExpression = plan.ScheduleExpression,
            State = RuleState.ENABLED,
            Description = "lz pipeline: runs the sweep for pipeline images that no build record names.",
        });
        TriggerApply.RequireEqual($"rule {plan.ScheduleRuleName}'s ARN", plan.ScheduleRuleArn, put.RuleArn);

        var read = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = plan.ScheduleRuleName });
        if (read.State != RuleState.ENABLED || read.ScheduleExpression != plan.ScheduleExpression)
            throw new InvalidOperationException(
                $"rule {plan.ScheduleRuleName} reads back {read.State} on {read.ScheduleExpression}; written ENABLED on {plan.ScheduleExpression}.");

        Console.WriteLine($"  rule '{plan.ScheduleRuleName}' written and read back: enabled, {plan.ScheduleExpression}.");
    }

    private static async Task<string?> PolicyAsync(IAmazonSimpleNotificationService sns, string topicArn)
    {
        var attributes = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes;
        return attributes != null && attributes.TryGetValue("Policy", out var policy) ? policy : null;
    }
}
