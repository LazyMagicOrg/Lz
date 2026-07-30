using Lz.Core.Config;
using Pulumi;
using Pulumi.Aws.OpenSearch;
using Pulumi.Aws.OpenSearch.Inputs;

namespace Lz.Aws.VectorStore;

/// <summary>
/// OpenSearch Serverless (aoss) collection for the semantic-matching backend,
/// provisioned inside the FOUNDATION stack when systemconfig opts in via
/// <c>VectorStore:</c> (see <see cref="VectorStoreConfig"/> — absent = this
/// component is never constructed).
///
/// <para>Resources: encryption security policy (AWS-owned key) → network
/// security policy (public endpoint, SigV4-only auth) → (when ScaleToZero) a
/// NEXTGEN collection group (min 0 OCU = scale-to-zero, Max*Ocu cost ceiling,
/// StandbyReplicas=ENABLED — NEXTGEN rejects DISABLED) → the collection →
/// data-access policy for the configured principals. The tenant service role
/// gets its own per-tenant data-access policy in the tenant stack.</para>
///
/// <para>Topology-agnostic (plain AWS); instantiated by
/// <c>SystemDeployment.DeployFoundation</c>. The index/pipeline layout INSIDE
/// the collection is not deploy-owned — the service's index bootstrapper
/// creates indices idempotently at host startup.</para>
/// </summary>
public class AwsVectorStoreComponent : ComponentResource
{
    /// <summary>The collection's HTTPS endpoint (exported as <c>vectorStoreEndpoint</c>).</summary>
    public Output<string> CollectionEndpoint { get; }

    /// <summary>The collection ARN (exported for the tenant role's scoped aoss IAM statement).</summary>
    public Output<string> CollectionArn { get; }

    /// <summary>The resolved collection name (deterministic from config).</summary>
    public string CollectionName { get; }

    public AwsVectorStoreComponent(SystemConfig config)
        : base("lz:aws:VectorStore", "vector-store", ResourceArgs.Empty, null)
    {
        var vs = config.VectorStore ?? throw new InvalidOperationException(
            "AwsVectorStoreComponent constructed without a VectorStore config section.");
        var name = VectorStorePolicy.CollectionName(config);
        CollectionName = name;

        // aoss refuses to create a collection without a matching encryption
        // policy, so the collection depends on it explicitly.
        var encryption = new ServerlessSecurityPolicy($"{name}-encryption", new ServerlessSecurityPolicyArgs
        {
            Name = name,
            Type = "encryption",
            Description = $"lz: AWS-owned-key encryption for {name}",
            Policy = VectorStorePolicy.EncryptionPolicyJson(name),
        }, new CustomResourceOptions { Parent = this });

        new ServerlessSecurityPolicy($"{name}-network", new ServerlessSecurityPolicyArgs
        {
            Name = name,
            Type = "network",
            Description = $"lz: public endpoint (SigV4 auth) for {name}",
            Policy = VectorStorePolicy.NetworkPolicyJson(name),
        }, new CustomResourceOptions { Parent = this });

        ServerlessCollectionGroup? group = null;
        if (vs.ScaleToZero)
        {
            group = new ServerlessCollectionGroup($"{name}-group", new ServerlessCollectionGroupArgs
            {
                Name = name,
                Generation = "NEXTGEN",        // min 0 OCU below IS the scale-to-zero
                StandbyReplicas = "ENABLED",   // NEXTGEN rejects DISABLED (moot at zero)
                CapacityLimits = new ServerlessCollectionGroupCapacityLimitArgs
                {
                    MinIndexingCapacityInOcu = 0,
                    MinSearchCapacityInOcu = 0,
                    MaxIndexingCapacityInOcu = vs.MaxIndexingOcu,
                    MaxSearchCapacityInOcu = vs.MaxSearchOcu,
                },
            }, new CustomResourceOptions { Parent = this });
        }

        var collectionArgs = new ServerlessCollectionArgs
        {
            Name = name,
            Type = vs.Type,
            // Classic collections take DISABLED to halve the (always-on) OCU
            // floor for dev; NEXTGEN requires ENABLED.
            StandbyReplicas = vs.ScaleToZero ? "ENABLED" : "DISABLED",
        };
        if (group != null)
            collectionArgs.CollectionGroupName = group.Name;

        var collection = new ServerlessCollection($"{name}-collection", collectionArgs,
            new CustomResourceOptions
            {
                Parent = this,
                DependsOn = group != null
                    ? new Resource[] { encryption, group }
                    : new Resource[] { encryption },
            });

        if (vs.DataAccessPrincipals.Count > 0)
        {
            new ServerlessAccessPolicy($"{name}-data", new ServerlessAccessPolicyArgs
            {
                Name = name,
                Type = "data",
                Description = $"lz: data access for configured principals of {name}",
                Policy = VectorStorePolicy.DataAccessPolicyJson(name, vs.DataAccessPrincipals),
            }, new CustomResourceOptions { Parent = this });
        }

        CollectionEndpoint = collection.CollectionEndpoint;
        CollectionArn = collection.Arn;
        RegisterOutputs();
    }
}
