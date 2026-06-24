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
/// by the Lambda Function URL, reached through a Lambda-type OAC (SigV4), and a
/// scoped invoke permission is granted to this distribution.
/// See Platform/LambdaTopology.md.
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

        // OAC for the Function URL: CloudFront SigV4-signs each origin request,
        // so the Function URL is private to this distribution.
        var oac = new OriginAccessControl($"{prefix}-api-oac", new OriginAccessControlArgs
        {
            Name = $"{prefix}-api-oac",
            Description = $"OAC for {domain} Lambda Function URL",
            OriginAccessControlOriginType = "lambda",
            SigningBehavior = "always",
            SigningProtocol = "sigv4",
        }, new CustomResourceOptions { Parent = this });

        return new ApiOriginSpec
        {
            OriginId = "api-origin",
            Origin = new DistributionOriginArgs
            {
                OriginId = "api-origin",
                DomainName = host,
                OriginAccessControlId = oac.Id,
                CustomOriginConfig = new DistributionOriginCustomOriginConfigArgs
                {
                    HttpPort = 80, HttpsPort = 443,
                    OriginProtocolPolicy = "https-only",
                    OriginSslProtocols = { "TLSv1.2" },
                },
            },
        };
    }

    protected override void ConfigureApiOriginAccess(
        string prefix, Distribution distribution, IComputeEnvironmentOutputs compute)
    {
        if (_originHolder.FunctionName is null) return;

        // Allow ONLY this distribution to invoke the Function URL (OAC + SigV4).
        new Pulumi.Aws.Lambda.Permission($"{prefix}-cf-invoke", new Pulumi.Aws.Lambda.PermissionArgs
        {
            Action = "lambda:InvokeFunctionUrl",
            Function = _originHolder.FunctionName!,
            Principal = "cloudfront.amazonaws.com",
            SourceArn = distribution.Arn,
            FunctionUrlAuthType = "AWS_IAM",
        }, new CustomResourceOptions { Parent = this });
    }
}
