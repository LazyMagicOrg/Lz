using Lz.Aws.Config;

namespace Lz.Aws.Compute.Fargate;

/// <summary>
/// Whether an ECS deployment should be gated on a CloudWatch alarm, derived purely from config.
/// <see cref="Target5xxPerMinute"/> is the threshold the alarm compares against with
/// <c>GreaterThanThreshold</c>, so <c>0</c> means "roll back on any 5xx at all".
/// </summary>
public readonly record struct DeploymentAlarmDecision(bool Enabled, int Target5xxPerMinute)
{
    /// <summary>Configure nothing — the byte-identical, no-opt-in baseline.</summary>
    public static readonly DeploymentAlarmDecision None = new(false, 0);
}

/// <summary>
/// The pure decision behind alarm-backed deployment rollback. SDK-free and Pulumi-free on purpose,
/// exactly like <see cref="Lz.Aws.Storage.BucketDurabilityPolicy"/>: the Fargate component only
/// translates it.
///
/// <para>WHY THIS EXISTS. The deployment circuit breaker is already armed
/// (<c>Enable = true, Rollback = true</c>) and has fired for real — once on 2026-08-14, rolling back
/// in 20m49s of which 20m33s was detection. But it only ever fires on a task that fails to START or
/// fails its health check. A container that boots, answers <c>/health</c>, and then returns 5xx to
/// every real request is, to the breaker, a completely successful deployment. That is the gap this
/// closes, and ECS's alarm option is the mechanism AWS provides for it: the same rollback machinery,
/// triggered by a metric instead of by a failed start.</para>
///
/// <para>THIS IS AN ACTUATOR, NOT A NOTIFICATION. The alarm exists to roll a deployment back; it is
/// deliberately not wired to a topic or a subscription. A system with no alerting plane can still
/// use it, and adding one later does not change this decision.</para>
///
/// <para>IT IS SILENT WITHOUT TRAFFIC, and that is correct rather than a limitation. The alarm
/// treats missing data as not-breaching, so a deployment nobody exercises will not roll back — if
/// nothing is broken for anyone, there is nothing to undo. The corollary is worth stating plainly:
/// in an environment with no traffic during the deployment window this protects nothing. It earns
/// its keep exactly where requests are arriving.</para>
/// </summary>
public static class DeploymentAlarmPolicy
{
    /// <summary>
    /// Decision for the tenant Fargate service. A null <paramref name="fargate"/> — or the knob left
    /// unset, which is the default — yields <see cref="DeploymentAlarmDecision.None"/>, so an
    /// un-opted-in system creates no alarm, sets no <c>Alarms</c> on the service, and emits a
    /// byte-identical plan.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The threshold is negative. Refused rather than coerced to "off": a negative threshold can
    /// only be a mistake, and silently disabling the rollback someone asked for is the worse
    /// outcome of the two.
    /// </exception>
    public static DeploymentAlarmDecision ForTenantService(FargateConfig? fargate)
    {
        var threshold = fargate?.RollbackOnTarget5xxPerMinute;
        if (threshold is null) return DeploymentAlarmDecision.None;
        if (threshold < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fargate),
                threshold,
                "Fargate.RollbackOnTarget5xxPerMinute must be zero or greater (0 rolls back on any " +
                "5xx). Remove the key to disable alarm-backed rollback.");
        }
        return new DeploymentAlarmDecision(true, threshold.Value);
    }

    /// <summary>
    /// The <c>LoadBalancer</c> dimension value CloudWatch expects for an ALB metric, which is the
    /// ARN's suffix rather than the ARN: <c>app/{name}/{id}</c>.
    ///
    /// <para>This throws on anything it cannot parse, and that is the point. The alternative —
    /// returning null or an empty string and creating the alarm anyway — produces an alarm whose
    /// dimensions match no metric, which sits in <c>INSUFFICIENT_DATA</c> forever and, because
    /// missing data is treated as not-breaching, never fires. That failure is invisible: the alarm
    /// exists, the service references it, and the rollback simply never happens. Failing the deploy
    /// is far better than shipping a guard that cannot fire.</para>
    /// </summary>
    public static string LoadBalancerDimension(string albArn)
    {
        const string marker = ":loadbalancer/";
        var i = albArn?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        var suffix = i >= 0 ? albArn![(i + marker.Length)..] : string.Empty;
        if (suffix.Length == 0)
        {
            throw new FormatException(
                $"Cannot derive the CloudWatch LoadBalancer dimension from ALB ARN '{albArn}': " +
                $"expected it to contain '{marker}' followed by 'app/{{name}}/{{id}}'. Refusing " +
                "rather than creating an alarm whose dimensions match no metric and which could " +
                "therefore never fire.");
        }
        return suffix;
    }
}
