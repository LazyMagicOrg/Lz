using Lz.Core.Interfaces.Outputs;
using Lz.Aws.EcsExpress;
using Pulumi;
using Pulumi.Aws.CloudFront;
using Pulumi.Aws.CloudFront.Inputs;

namespace Lz.Aws.Lambda;

/// <summary>
/// CloudFront for the lambda-cognito-dynamodb topology. Inherits the entire
/// ECSExpress edge — the same ComponentResource type (<c>lz:aws:EcsExpressCloudFront</c>)
/// and the same distribution / assets-bucket / KVS resource names — so switching
/// a deployed stack between ecs-fargate-cognito-dynamodb and lambda-cognito-dynamodb
/// is an in-place update of the SAME distribution (URN-stable), not a replace.
/// Only the API origin differs: the ALB <c>origin.{domain}</c> alias is replaced
/// by the (public) Lambda Function URL carrying the <c>x-origin-verify</c> origin
/// custom header that the AppHost enforces (OriginVerifyMiddleware).
///
/// WHY NOT OAC: a lambda-type OAC (SigV4) was implemented first and proved unable to
/// carry a REST API — the Function URL rejects PUT/PATCH/DELETE outright under OAC
/// and requires viewers to send x-amz-content-sha256 on POST (AWS-documented
/// limitation; confirmed against the live env 2026-07-03). See Platform/LambdaTopology.md.
/// </summary>
public class AwsLambdaCloudFrontComponent : AwsEcsExpressCloudFrontComponent
{
    private readonly AwsLambdaApiOriginHolder _originHolder;

    public AwsLambdaCloudFrontComponent(AwsLambdaApiOriginHolder originHolder)
        : base()
    {
        _originHolder = originHolder;
    }

    protected override ApiOriginSpec BuildApiOrigin(
        string prefix, string domain, IComputeEnvironmentOutputs compute)
    {
        var host = _originHolder.FunctionUrlHost ?? throw new System.InvalidOperationException(
            "Lambda API origin is not set: a Public Lambda service must be deployed before the CDN. " +
            "Ensure the tenant has a host-layer service with IngressType=Public.");
        var secret = _originHolder.OriginVerifySecret ?? throw new System.InvalidOperationException(
            "Lambda origin-verify secret is not set: the tenant service publishes it with the Function URL.");

        return new ApiOriginSpec
        {
            OriginId = "api-origin",
            Origin = new DistributionOriginArgs
            {
                OriginId = "api-origin",
                DomainName = host,
                // The secret CloudFront stamps on every origin request. Origin custom
                // headers OVERRIDE any viewer-supplied header of the same name, so a
                // caller cannot spoof it through the distribution; direct-to-URL calls
                // lack it and are 403'd by the app.
                CustomHeaders =
                {
                    new DistributionOriginCustomHeaderArgs
                    {
                        Name = "x-origin-verify",
                        Value = secret,
                    },
                },
                CustomOriginConfig = new DistributionOriginCustomOriginConfigArgs
                {
                    HttpPort = 80, HttpsPort = 443,
                    OriginProtocolPolicy = "https-only",
                    OriginSslProtocols = { "TLSv1.2" },
                },
            },
        };
    }

    // No ConfigureApiOriginAccess override: the base is a no-op, and with a public
    // Function URL there is no CloudFront invoke permission to grant — the public
    // InvokeFunctionUrl grant lives with the FunctionUrl itself
    // (AwsLambdaTenantServiceComponent), and access control is the origin-verify gate.
}
