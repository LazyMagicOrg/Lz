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
}
