using Amazon.ECS;
using Amazon.ECS.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// The image inputs from outside the deployer's functions (P3 J1): <c>bootstrapdeployer</c> seeds one from what a service runs,
/// <c>updatecontainer</c> writes one after its roll, and <c>deploytenant</c> reads one. The functions use
/// <see cref="ImageInputFromService"/> over their own adapters; this is the same read over the SDK.
/// </summary>
public static class ImageInputEcs
{
    /// <summary>The input naming what the service's ACTIVE revision runs in <paramref name="container"/>; null where there is no such service.</summary>
    public static async Task<ImageInput?> FromServiceAsync(
        IAmazonECS ecs, string cluster, string service, string container, string writer, DateTimeOffset now, CancellationToken ct)
    {
        var described = await ecs.DescribeServicesAsync(new DescribeServicesRequest { Cluster = cluster, Services = new List<string> { service } }, ct);
        var active = described.Services?.FirstOrDefault(s => s.Status == "ACTIVE");
        if (active is null || string.IsNullOrEmpty(active.TaskDefinition)) return null;

        var revision = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = active.TaskDefinition, Include = new List<string> { "TAGS" },
        }, ct);
        var image = Lz.Aws.Ops.TaskDefinitionRevision.SingleContainerNamed(revision.TaskDefinition, container).Image;
        var parsed = ImageInputFormat.ParseImage(image)
            ?? throw new InvalidOperationException($"{service}'s current revision runs '{image}', which names no digest; an input names one.");
        var builtAt = revision.Tags?.FirstOrDefault(t => t.Key == DeployOrdering.BuiltAtTag)?.Value;
        return new ImageInput(parsed.Digest, parsed.Repository, builtAt is not null && DeployOrdering.ParseUtc(builtAt) is not null ? builtAt : null,
            writer, now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    /// <summary>
    /// WHY <c>deploytenant</c> MAY NOT PROCEED with this service's input, or null (P3 J1): an input is required where the pipeline
    /// signs the service's images, and must name exactly the digest the service runs - anything else is a deploy in flight, or one
    /// whose write-back failed, and the tenant program is not the one to settle which image is right.
    /// </summary>
    public static string? WhyNotProceed(string name, ImageInput? input, string runningDigest)
    {
        if (input is null)
            return $"{name} does not exist: where the pipeline signs a service's images it is the service's image input. " +
                   "Run `lz bootstrapdeployer --apply`, which creates it from what the service runs.";
        if (!string.Equals(input.Digest, runningDigest, StringComparison.Ordinal))
            return $"{name} names {input.Repository}@{input.Digest} (written by {input.Writer} at {input.WrittenAt}), but the service runs " +
                   $"{runningDigest}. An image deploy is in flight, or one failed and its write-back did not land. Let it finish, or set the " +
                   "input to what the service should run, then deploy again. Nothing has been changed.";
        return null;
    }
}
