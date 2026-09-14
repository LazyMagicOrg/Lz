using Amazon.S3.Model;

namespace Lz.Aws;

/// <summary>
/// ONE WAY TO ASK FOR A BUCKET, for every command that creates one (DecoupledCd.md §14.2). The pipeline's two bootstrap
/// commands set the region unconditionally, while <c>AwsStateBootstrapper</c> in the same library already left it off for
/// <c>us-east-1</c>, S3's default region; a sibling system there would have failed half-way through a pipeline apply.
/// </summary>
public static class S3BucketRequests
{
    /// <summary>A request to create <paramref name="bucket"/> in <paramref name="region"/> — with no region named for us-east-1.</summary>
    public static PutBucketRequest Create(string bucket, string region)
        => new()
        {
            BucketName = bucket,
            BucketRegionName = string.Equals(region, "us-east-1", StringComparison.Ordinal) ? null : region,
        };
}
