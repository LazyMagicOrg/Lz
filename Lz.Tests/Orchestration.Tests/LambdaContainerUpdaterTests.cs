using Lz.Aws.Compute.Lambda;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// Pure decisions behind the Lambda-topology <c>lz updatecontainer</c> path
/// (<see cref="AwsLambdaContainerUpdater"/>): topology dispatch, digest
/// extraction from a Lambda ResolvedImageUri, and image-URI retagging. The SDK
/// calls stay thin — pinning these pins the routing and comparison behaviour.
/// The hook exists because Lambda resolves the image digest at
/// UpdateFunctionCode time: a pushed :latest is invisible to the function (and
/// to a tenant Pulumi re-deploy) until an explicit code update.
/// </summary>
public class LambdaContainerUpdaterTests
{
    // ---- topology dispatch ----

    [Theory]
    [InlineData("lambda-cognito-dynamodb", true)]
    [InlineData("lambda-anything-else", true)]
    [InlineData("ecs-fargate-cognito-dynamodb", false)]
    [InlineData("ecs-fargate-keycloak", false)]
    [InlineData("apprunner-cognito-dynamodb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLambdaTopology_RoutesOnlyLambdaPrefixes(string? topology, bool expected)
        => Assert.Equal(expected, AwsLambdaContainerUpdater.IsLambdaTopology(topology));

    // ---- digest extraction (Code.ResolvedImageUri = {repo-uri}@sha256:...) ----

    [Fact]
    public void ExtractDigest_ReadsTheShaAfterTheAt()
    {
        const string resolved =
            "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-dev-mp-aiphost@sha256:3067d294385eef3b42ef48ccf30f989b07a70f13745c6dcf31f4d8fcba122cfb";
        Assert.Equal(
            "sha256:3067d294385eef3b42ef48ccf30f989b07a70f13745c6dcf31f4d8fcba122cfb",
            AwsLambdaContainerUpdater.ExtractDigest(resolved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign-here")]
    [InlineData("trailing-at@")]
    public void ExtractDigest_UnparsableInputs_YieldNull(string? uri)
        => Assert.Null(AwsLambdaContainerUpdater.ExtractDigest(uri));

    // ---- image-URI retag ({repo-uri}:{tag}) ----

    [Fact]
    public void RetagImageUri_SwapsTheTag_PreservingTheDeployedRepoUri()
    {
        const string deployed = "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-dev-mp-aiphost:latest";
        Assert.Equal(
            "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-dev-mp-aiphost:v2",
            AwsLambdaContainerUpdater.RetagImageUri(deployed, "v2"));
    }

    [Fact]
    public void RetagImageUri_UntaggedUri_AppendsTheTag()
    {
        const string deployed = "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-dev-mp-aiphost";
        Assert.Equal(deployed + ":latest",
            AwsLambdaContainerUpdater.RetagImageUri(deployed, "latest"));
    }

    [Fact]
    public void RetagImageUri_SameTag_IsIdentity_TheReResolveCase()
    {
        // The common roll: URI string unchanged — Lambda still re-resolves the digest.
        const string deployed = "acct.dkr.ecr.us-west-2.amazonaws.com/repo:latest";
        Assert.Equal(deployed, AwsLambdaContainerUpdater.RetagImageUri(deployed, "latest"));
    }

    [Fact]
    public void RetagImageUri_NullOrEmpty_YieldsNull()
    {
        Assert.Null(AwsLambdaContainerUpdater.RetagImageUri(null, "latest"));
        Assert.Null(AwsLambdaContainerUpdater.RetagImageUri("", "latest"));
    }
}
