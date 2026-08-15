using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.Lambda;

/// <summary>
/// No-cluster compute-environment outputs for the serverless lineage
/// (lambda-cognito-dynamodb; ex-apprunner) — compute is per-service.
/// ECR repos are per-tenant and imperatively created by
/// <c>lz deploycontainer</c>; they aren't surfaced here.
/// </summary>
public class AwsLambdaComputeOutputs : IComputeEnvironmentOutputs
{
    public required Output<string> ClusterId { get; init; }
    public required Output<string> PublicIngressEndpoint { get; init; }
    public required Output<string> InternalIngressEndpoint { get; init; }

    // serverless-lineage-specific
    public required Output<string> AutoScalingConfigArn { get; init; }
}
