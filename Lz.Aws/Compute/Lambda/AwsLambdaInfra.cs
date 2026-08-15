using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
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
/// Carries the per-tenant Lambda Function URL from the tenant-service component
/// to the CDN component within a single tenant deploy. Both components are
/// created by the same <see cref="AwsLambdaCognitoDynamodbPlatformFactory"/> instance, and the
/// tenant orchestration runs the tenant service before the CDN, so the CDN reads
/// a populated holder. This keeps the shared <c>ITenantCdnComponent</c> interface
/// unchanged — the other topologies are unaffected.
/// </summary>
public sealed class AwsLambdaApiOriginHolder
{
    /// <summary>Bare host of the API Lambda's Function URL (no scheme/path).</summary>
    public Output<string>? FunctionUrlHost { get; set; }

    /// <summary>Name of the API Lambda function (for the CloudFront invoke permission).</summary>
    public Output<string>? FunctionName { get; set; }

    /// <summary>
    /// Shared origin-verification secret. The Function URL is PUBLIC (AuthType=NONE —
    /// CloudFront OAC/SigV4 cannot carry REST writes to Function URLs: PUT/PATCH/DELETE
    /// are rejected and POST demands a viewer-supplied x-amz-content-sha256). Instead,
    /// CloudFront injects this value as the <c>x-origin-verify</c> origin custom header
    /// and the AppHost (LazyMagic.OIDC.Bff OriginVerifyMiddleware, via LZ_ORIGIN_VERIFY)
    /// rejects requests without it — so the public URL is unusable directly.
    /// </summary>
    public Output<string>? OriginVerifySecret { get; set; }
}

/// <summary>
/// Lambda "compute environment" — there is no foundation-level compute for the
/// Lambda topology. The per-tenant container Lambda IS the compute, created by
/// <see cref="AwsLambdaTenantServiceComponent"/>; its Function URL is surfaced to
/// the CDN per tenant via <see cref="AwsLambdaApiOriginHolder"/>, not via
/// foundation compute. This component therefore provisions nothing.
/// </summary>
public class AwsLambdaComputeComponent : IComputeEnvironmentComponent
{
    public IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = $"{config.SystemKey}-{config.Environment}";
        return new AwsLambdaComputeOutputs
        {
            ClusterId = Output.Create($"{prefix}-lambda"),
            PublicIngressEndpoint = Output.Create(""), // per-tenant Function URL, surfaced via the holder
            InternalIngressEndpoint = Output.Create(""),
            AutoScalingConfigArn = Output.Create(""),
        };
    }
}

/// <summary>
/// Foundation-level service for the Lambda topology — a no-op. The container
/// Lambda, its execution role, and the Function URL are created per tenant by
/// <see cref="AwsLambdaTenantServiceComponent"/>.
/// </summary>
public class AwsLambdaServiceComponent : IServiceComponent
{
    public IServiceOutputs Deploy(
        string serviceName, ServiceDefinition definition, INetworkOutputs network,
        IComputeEnvironmentOutputs compute, IDatabaseOutputs database, IFileStorageOutputs? fileStorage)
        => new AwsLambdaServiceOutputs
        {
            ServiceId = Output.Create($"{serviceName}-foundation"),
            Endpoint = Output.Create(""),
        };
}

internal sealed class AwsLambdaServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }
}
