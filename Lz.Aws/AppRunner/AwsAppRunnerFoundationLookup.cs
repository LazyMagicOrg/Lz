using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Looks up existing foundation resources created by deploysystem.
/// Minimal for AppRunner — no VPC to look up. Resolves Route 53 zone and
/// DynamoDB endpoint for the tenant phase. ECR repos are per-tenant and
/// imperatively created by <c>lz deploycontainer</c>, not looked up here.
/// </summary>
public static class AwsAppRunnerFoundationLookup
{
    public static (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        Lookup(SystemConfig config)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var prefix = $"{sk}-{env}";

        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = config.SystemDomain });

        // Look up the auto-scaling configuration
        // AppRunner auto-scaling configs are looked up by name convention
        var autoScalingName = $"{prefix}-autoscaling";

        var emptySubnets = Output.Create(ImmutableArray<string>.Empty);

        var network = new AwsAppRunnerNetworkOutputs
        {
            NetworkId = Output.Create(""),
            PrivateSubnetIds = emptySubnets,
            PublicSubnetIds = emptySubnets,
            PrivateDnsZoneId = Output.Create(""),
            PublicDnsZoneId = publicZone.Apply(z => z.ZoneId),
            CertificateArn = Output.Create(""),
        };

        var compute = new AwsAppRunnerComputeOutputs
        {
            ClusterId = Output.Create($"{prefix}-apprunner"),
            PublicIngressEndpoint = Output.Create(""),
            InternalIngressEndpoint = Output.Create(""),
            AutoScalingConfigArn = Output.Create(""), // Looked up at service creation time
        };

        // DynamoDB table ARN prefix for IAM policies
        var tableArnPrefix = Pulumi.Aws.GetCallerIdentity.Invoke(new Pulumi.Aws.GetCallerIdentityInvokeArgs())
            .Apply(id => $"arn:aws:dynamodb:{config.Region}:{id.AccountId}:table/{sk}-{suffix}-{env}-*");

        var database = new AwsAppRunnerDatabaseOutputs
        {
            Endpoint = Output.Create($"dynamodb.{config.Region}.amazonaws.com"),
            Port = Output.Create(443),
            AdminSecretId = Output.Create(""),
            TableArnPrefix = tableArnPrefix,
        };

        var fileStorage = new AwsAppRunnerFileStorageOutputs
        {
            FileSystemId = Output.Create(""),
        };

        return (network, compute, database, fileStorage);
    }
}
