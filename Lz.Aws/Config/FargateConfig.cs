namespace Lz.Aws.Config;

/// <summary>
/// ECS Fargate task sizing + health-check settings for topologies that run
/// tasks on Fargate (today: <c>ecs-fargate-cognito-dynamodb</c>;
/// <c>ecs-fargate-keycloak</c> uses <see cref="EcsConfig"/> for its richer
/// per-service shape).
/// </summary>
/// <remarks>
/// Historically these knobs lived under a legacy YAML block inherited from the
/// retired apprunner topology (the Fargate topology was ported from it and
/// reused its config class by accident). The legacy block and its fallback
/// were removed in 0.11.0 — declare <c>Fargate:</c>.
/// </remarks>
public class FargateConfig
{
    /// <summary>Fargate task CPU units (256, 512, 1024, 2048, 4096).</summary>
    public int Cpu { get; set; } = 1024;

    /// <summary>Fargate task memory in MB.</summary>
    public int Memory { get; set; } = 2048;

    /// <summary>Container listen port.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>ALB target-group health-check path.</summary>
    public string HealthCheckPath { get; set; } = "/health";

    /// <summary>CloudWatch log retention for the task's log group.</summary>
    public int LogRetentionDays { get; set; } = 3;

    /// <summary>Desired number of Fargate tasks for the ECS service.</summary>
    public int DesiredCount { get; set; } = 1;

    /// <summary>
    /// When set, the ECS service gates its deployments on a CloudWatch alarm over the target
    /// group's <c>HTTPCode_Target_5XX_Count</c>, rolling back when more than this many 5xx
    /// responses occur in a minute. <c>0</c> means any 5xx at all. Null (the default) creates no
    /// alarm and sets no <c>Alarms</c> on the service, so the emitted plan is byte-identical.
    /// <para>
    /// This closes the one gap in the deployment circuit breaker, which is already armed: the
    /// breaker fires only when a task fails to start or fails its health check, so a container
    /// that boots, answers the health path, and then 5xx-es on every real request looks like a
    /// successful deployment to it. See
    /// <see cref="Lz.Aws.Compute.Fargate.DeploymentAlarmPolicy"/> for why the alarm is an actuator
    /// rather than a notification, and why it is deliberately silent when no traffic is arriving.
    /// </para>
    /// <para>
    /// Settable at either level: the tenant's <c>Fargate:</c> block overrides the system's, which
    /// overrides the default. That was not true before 2026-09-06 — the component could not see the
    /// system half at all — so a system-level value would have armed no alarm and reported no error.
    /// </para>
    /// </summary>
    public int? RollbackOnTarget5xxPerMinute { get; set; }
}
