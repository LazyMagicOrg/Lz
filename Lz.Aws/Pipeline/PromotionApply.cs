using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Applies a promoted environment's poller (DecoupledCd.md §12 P5 stage I) and its hold (stage J), from
/// <see cref="DeployerBootstrapper"/>, after the machine the poller starts: one poll at a time, ticks at most a minute old
/// and never retried, the one-minute schedule with the poller its only target, and its two alarms. Like
/// <see cref="AlertsApply"/> it reads no config: every name comes from the plan, and each piece is read back.
/// </summary>
internal static class PromotionApply
{
    public const string ScheduleTargetId = "lz-promotion-poller";
    public const string ScheduleInvokeSid = "InvokedByThePromotionSchedule";

    /// <summary>
    /// THE HOLD, created <c>off</c> where it is missing and never written where it exists (§5.3 rule 5): an operator may have
    /// set it, and a bootstrap that reset it would release a hold on a fault. Missing counts as held, so until this has run
    /// nothing is started.
    /// </summary>
    public static async Task EnsureHoldAsync(IAmazonSimpleSystemsManagement ssm, string name)
    {
        try
        {
            var current = await ssm.GetParameterAsync(new GetParameterRequest { Name = name });
            Console.WriteLine($"  hold '{name}' exists ({current.Parameter.Value}); left as it is.");
        }
        catch (ParameterNotFoundException)
        {
            await ssm.PutParameterAsync(new PutParameterRequest
            {
                Name = name, Value = "off", Type = ParameterType.String, Overwrite = false,
                Description = "lz promotion hold (DecoupledCd.md P5 stage J): anything but 'off', or missing, holds every automated start.",
            });
            Console.WriteLine($"  hold '{name}' created: off.");
        }
    }

    public static async Task WireAsync(IAmazonEventBridge events, IAmazonLambda lambda, IAmazonCloudWatch cloudWatch,
        DeployerPromotionPlan plan, string alertsTopicArn)
    {
        // ONE POLL AT A TIME (§5.3 rule 1). An account at Lambda's floor cannot reserve any, as the intake's poller found
        // (2026-10-21); there two polls choose the same marker and their starts collapse on its execution name, so the refusal
        // is reported, not fatal.
        try
        {
            await lambda.PutFunctionConcurrencyAsync(new Amazon.Lambda.Model.PutFunctionConcurrencyRequest
            {
                FunctionName = plan.PollerFunctionName, ReservedConcurrentExecutions = plan.ReservedConcurrency,
            });
            Console.WriteLine($"  function '{plan.PollerFunctionName}': reserved concurrency {plan.ReservedConcurrency}.");
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
            FunctionName = plan.PollerFunctionName, MaximumEventAgeInSeconds = plan.MaximumEventAgeSeconds, MaximumRetryAttempts = 0,
        });
        Console.WriteLine($"  function '{plan.PollerFunctionName}': events at most {plan.MaximumEventAgeSeconds} s old, never retried.");

        var put = await events.PutRuleAsync(new PutRuleRequest
        {
            Name = plan.ScheduleRuleName, ScheduleExpression = plan.ScheduleExpression, State = RuleState.ENABLED,
            Description = "lz promotion poller, every minute (DecoupledCd.md P5 stage I).",
        });
        var functionArn = (await lambda.GetFunctionConfigurationAsync(new Amazon.Lambda.Model.GetFunctionConfigurationRequest { FunctionName = plan.PollerFunctionName })).FunctionArn;
        await TriggerApply.EnsureOnlyTargetAsync(events, null, plan.ScheduleRuleName, new Amazon.EventBridge.Model.Target { Id = ScheduleTargetId, Arn = functionArn });
        await TriggerApply.EnsureInvokePermissionAsync(lambda, plan.PollerFunctionName, ScheduleInvokeSid, plan.ScheduleRuleName, put.RuleArn);
        var read = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = plan.ScheduleRuleName });
        if (read.State != RuleState.ENABLED || read.ScheduleExpression != plan.ScheduleExpression)
            throw new InvalidOperationException($"rule {plan.ScheduleRuleName} reads back {read.State} on {read.ScheduleExpression}; written ENABLED on {plan.ScheduleExpression}.");
        Console.WriteLine($"  rule '{plan.ScheduleRuleName}' read back: ENABLED, {plan.ScheduleExpression} -> {plan.PollerFunctionName}.");

        var actions = new List<string> { alertsTopicArn };
        foreach (var alarm in plan.Alarms)
        {
            await cloudWatch.PutMetricAlarmAsync(new PutMetricAlarmRequest
            {
                AlarmName = alarm.Name,
                AlarmDescription = $"lz promotion: {alarm.Description}.",
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
            Console.WriteLine($"  alarm '{alarm.Name}': {alarm.Description}.");
        }
    }
}
