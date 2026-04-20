using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface ICdnOutputs
{
    Output<string> DistributionId { get; }
    Output<string> DomainName { get; }
    Output<string> WebappBucketId { get; }
    Output<string> ExploreBucketId { get; }
}
