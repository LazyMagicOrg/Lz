using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.AppRunner;

namespace Lz.Aws.AppRunner;

/// <summary>
/// AppRunner "compute environment" — creates the auto-scaling configuration
/// used by AppRunner services. There is no cluster concept in AppRunner;
/// compute is per-service. ECR repos are per-tenant and imperatively created
/// by <c>lz deploycontainer</c>, not by this component.
/// </summary>
public class AwsAppRunnerComputeComponent : ComponentResource, IComputeEnvironmentComponent
{
    public AwsAppRunnerComputeComponent()
        : base("lz:aws:AppRunnerCompute", "compute", ResourceArgs.Empty, null)
    {
    }

    public IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var prefix = $"{sk}-{env}";
        var appRunner = config.Aws().AppRunner ?? new AppRunnerConfig();

        // =====================================================================
        // AUTO-SCALING CONFIGURATION
        // =====================================================================

        var autoScaling = new AutoScalingConfigurationVersion($"{prefix}-autoscaling",
            new AutoScalingConfigurationVersionArgs
            {
                AutoScalingConfigurationName = $"{prefix}-autoscaling",
                MaxConcurrency = appRunner.MaxConcurrency,
                MinSize = appRunner.MinSize,
                MaxSize = appRunner.MaxSize,
                Tags =
                {
                    { "System", sk },
                    { "Environment", env },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, new CustomResourceOptions { Parent = this });

        return new AwsAppRunnerComputeOutputs
        {
            // Cloud-agnostic outputs — AppRunner doesn't have a "cluster"
            ClusterId = Output.Create($"{prefix}-apprunner"),
            PublicIngressEndpoint = Output.Create(""), // Per-service, not global
            InternalIngressEndpoint = Output.Create(""),

            // AppRunner-specific
            AutoScalingConfigArn = autoScaling.Arn,
        };
    }
}
