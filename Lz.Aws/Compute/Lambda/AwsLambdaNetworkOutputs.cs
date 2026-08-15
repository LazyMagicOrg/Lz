using System.Collections.Immutable;
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
/// Serverless (no-VPC) network outputs — ex-apprunner, now the Lambda
/// topology's network shape. Only Route 53 and ACM cert
/// are needed for domain management and CloudFront HTTPS.
/// INetworkOutputs fields that don't apply return empty stubs.
/// </summary>
public class AwsLambdaNetworkOutputs : INetworkOutputs
{
    // INetworkOutputs — stubs (no VPC in the serverless topology)
    public required Output<string> NetworkId { get; init; }
    public required Output<ImmutableArray<string>> PrivateSubnetIds { get; init; }
    public required Output<ImmutableArray<string>> PublicSubnetIds { get; init; }
    public required Output<string> PrivateDnsZoneId { get; init; }
    public required Output<string> PublicDnsZoneId { get; init; }

    // serverless-lineage-specific — only what's actually needed
    public required Output<string> CertificateArn { get; init; }
}
