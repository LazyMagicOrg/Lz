using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;

namespace Lz.Aws.Shared;

/// <summary>
/// AWS gate-checker Lambda outputs.
/// </summary>
public class AwsGateCheckerOutputs : IGateCheckerOutputs
{
    public required Output<string> FunctionName { get; init; }

    // AWS-specific
    public required Output<string> FunctionArn { get; init; }
}
