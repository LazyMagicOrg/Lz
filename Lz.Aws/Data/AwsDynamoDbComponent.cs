using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.DynamoDB;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Data;

/// <summary>
/// DynamoDB component for the Cognito+DynamoDB topologies (Fargate + Lambda).
/// Creates DynamoDB tables defined in the system's schema configuration.
/// Tables are created at foundation level and shared across all tenants
/// (multi-tenancy via partition key prefix, not separate tables).
/// </summary>
public class AwsDynamoDbComponent : ComponentResource, IDatabaseComponent
{
    public AwsDynamoDbComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
        : base("lz:aws:DynamoDB", "database", ResourceArgs.Empty, null)
    {
    }

    public IDatabaseOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var prefix = $"{sk}-{env}";

        // DynamoDB tables are defined by the consuming system (BCSystem).
        // At this level we create a placeholder — actual tables are created
        // by the system plugin or as part of tenant deployment.
        // For now, we just record the table ARN prefix for IAM policies.

        var tableArnPrefix = Pulumi.Aws.GetRegion.Invoke(new Pulumi.Aws.GetRegionInvokeArgs())
            .Apply(r => Pulumi.Aws.GetCallerIdentity.Invoke(new Pulumi.Aws.GetCallerIdentityInvokeArgs())
                .Apply(id => $"arn:aws:dynamodb:{r.Name}:{id.AccountId}:table/{sk}-{suffix}-{env}-*"));

        return new AwsDynamoDbOutputs
        {
            // DynamoDB regional endpoint
            Endpoint = Pulumi.Aws.GetRegion.Invoke(new Pulumi.Aws.GetRegionInvokeArgs())
                .Apply(r => $"dynamodb.{r.Name}.amazonaws.com"),
            Port = Output.Create(443),
            AdminSecretId = Output.Create(""), // DynamoDB uses IAM, not secrets
            TableArnPrefix = tableArnPrefix,
        };
    }
}
