using Lz.Aws;

namespace Lz.Tests.Storage.Tests;

/// <summary>
/// The one request every lz command that creates a bucket with a named region sends (DecoupledCd.md §14.2). S3's default
/// region takes no location constraint; the pipeline's two bootstraps used to send one anyway.
/// </summary>
public class S3BucketRequestsTests
{
    [Fact]
    public void InUsEast1_NoRegionIsNamed()
    {
        var request = S3BucketRequests.Create("scu-artifacts-4df6-b9c6", "us-east-1");

        Assert.Equal("scu-artifacts-4df6-b9c6", request.BucketName);
        Assert.Null(request.BucketRegionName);
    }

    [Theory]
    [InlineData("us-west-2")]
    [InlineData("eu-west-1")]
    [InlineData("us-east-2")]
    public void AnywhereElse_TheRegionIsNamed(string region)
    {
        var request = S3BucketRequests.Create("scu-artifacts-4df6-b9c6", region);

        Assert.Equal("scu-artifacts-4df6-b9c6", request.BucketName);
        Assert.Equal(region, request.BucketRegionName);
    }
}
