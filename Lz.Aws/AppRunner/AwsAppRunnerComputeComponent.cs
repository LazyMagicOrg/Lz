using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.AppRunner;
using Pulumi.Aws.Ecr;

namespace Lz.Aws.AppRunner;

/// <summary>
/// AppRunner "compute environment" — creates the shared ECR repository
/// and auto-scaling configuration used by all AppRunner services.
/// There is no cluster concept in AppRunner; compute is per-service.
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
        var appRunner = config.AppRunner ?? new AppRunnerConfig();

        // =====================================================================
        // ECR REPOSITORY — shared across all AppRunner services
        // =====================================================================

        var ecrRepo = new Repository($"{prefix}-ecr", new RepositoryArgs
        {
            Name = $"{sk}-{suffix}-{env}-apphost",
            ImageTagMutability = "MUTABLE",
            ForceDelete = env == "dev",
            Tags =
            {
                { "System", sk },
                { "Environment", env },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // Lifecycle policy — keep only last 5 untagged images
        new LifecyclePolicy($"{prefix}-ecr-lifecycle", new LifecyclePolicyArgs
        {
            Repository = ecrRepo.Name,
            Policy = @"{
                ""rules"": [{
                    ""rulePriority"": 1,
                    ""description"": ""Keep only 5 untagged images"",
                    ""selection"": {
                        ""tagStatus"": ""untagged"",
                        ""countType"": ""imageCountMoreThan"",
                        ""countNumber"": 5
                    },
                    ""action"": { ""type"": ""expire"" }
                }]
            }",
        }, new CustomResourceOptions { Parent = this });

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
            EcrRepositoryUrl = ecrRepo.RepositoryUrl,
            EcrRepositoryArn = ecrRepo.Arn,
        };
    }
}
