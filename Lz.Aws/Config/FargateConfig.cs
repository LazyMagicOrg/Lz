namespace Lz.Aws.Config;

/// <summary>
/// ECS Fargate task sizing + health-check settings for topologies that run
/// tasks on Fargate (today: <c>ecs-fargate-cognito-dynamodb</c>;
/// <c>ecs-fargate-keycloak</c> uses <see cref="EcsConfig"/> for its richer
/// per-service shape).
/// </summary>
/// <remarks>
/// Historically these knobs lived under the <c>AppRunner:</c> YAML block
/// because the ECS Express topology was ported from the AppRunner topology
/// and reused <see cref="AppRunnerConfig"/> by accident. Systems using
/// <c>ecs-fargate-cognito-dynamodb</c> should declare <c>Fargate:</c>
/// instead — the factory prefers this block when present and falls back
/// to the legacy <c>AppRunner:</c> values only for backward compatibility.
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
}
