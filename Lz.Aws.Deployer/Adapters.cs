using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  THE ONLY CODE HERE THAT TALKS TO AWS, and it decides nothing. Each adapter turns one SDK call into
//  one of the plain records the steps read — including the two ways "not there" arrives, which the
//  SDK reports as exceptions and the steps need as null.
// ---------------------------------------------------------------------------------------------

/// <summary>Clients are created once per execution environment and reused across invocations.</summary>
internal static class Clients
{
    // Region and credentials come from the function's environment (AWS_REGION and the role).
    public static readonly Lazy<IAmazonS3> S3 = new(() => new AmazonS3Client());
    public static readonly Lazy<IAmazonECR> Ecr = new(() => new AmazonECRClient());
    public static readonly Lazy<IAmazonECS> Ecs = new(() => new AmazonECSClient());
}

internal sealed class S3RecordStore(IAmazonS3 s3) : IRecordStore
{
    // A build record is a few hundred bytes. Anything this large is not one, and is not read.
    private const long MaxRecordBytes = 1024 * 1024;

    public async Task<string?> ReadAsync(string bucket, string key)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
            if (response.ContentLength > MaxRecordBytes)
                throw new DeployRefused("record",
                    $"s3://{bucket}/{key} is {response.ContentLength} bytes; a build record is not that large.");

            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

internal sealed class S3EvidenceWriter(IAmazonS3 s3) : IEvidenceWriter
{
    public async Task<bool> PutOnceAsync(string bucket, string key, string body)
    {
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = body,
                ContentType = "application/json",
                // Written once and never replaced — the same conditional write the Record state uses.
                IfNoneMatch = "*",
            });
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }
}

internal sealed class EcrImages(IAmazonECR ecr) : IRegistryImages
{
    public async Task<RegistryImage?> DescribeAsync(string repository, string digest)
    {
        try
        {
            var response = await ecr.DescribeImagesAsync(new DescribeImagesRequest
            {
                RepositoryName = repository,
                ImageIds = new List<ImageIdentifier> { new() { ImageDigest = digest } },
            });

            // SDK v4 returns null, not an empty list, for a collection with no members.
            var image = response.ImageDetails?.FirstOrDefault(
                d => string.Equals(d.ImageDigest, digest, StringComparison.Ordinal));
            if (image is null) return null;

            var (status, counts) = await ScanAsync(repository, digest);
            return new RegistryImage(image.ImageDigest, status, counts);
        }
        catch (ImageNotFoundException)
        {
            return null;
        }
        catch (RepositoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The image's scan, from <c>DescribeImageScanFindings</c> — NOT from <c>DescribeImages</c>, whose
    /// scan attributes AWS says the current basic scanning "doesn't use … to return scan results".
    /// Reading them refused every image at [scan] on 2026-09-12, although every image had a COMPLETE scan
    /// (measured the same day, in both registries). Every page is read, because the
    /// summary's scope is undocumented (<see cref="DeployVerification.SeverityCounts"/>).
    /// </summary>
    private async Task<(string? Status, IReadOnlyDictionary<string, int>? Counts)> ScanAsync(string repository, string digest)
    {
        string? status = null;
        IReadOnlyDictionary<string, int>? summary = null;
        var severities = new List<string?>();
        string? next = null;

        try
        {
            do
            {
                var page = await ecr.DescribeImageScanFindingsAsync(new DescribeImageScanFindingsRequest
                {
                    RepositoryName = repository,
                    ImageId = new ImageIdentifier { ImageDigest = digest },
                    MaxResults = 1000,
                    NextToken = next,
                });

                status ??= page.ImageScanStatus?.Status?.Value;
                summary ??= page.ImageScanFindings?.FindingSeverityCounts;
                if (page.ImageScanFindings?.Findings is { } findings)
                    severities.AddRange(findings.Select(f => f.Severity?.Value));
                if (page.ImageScanFindings?.EnhancedFindings is { } enhanced)
                    severities.AddRange(enhanced.Select(f => f.Severity));

                next = page.NextToken;
            }
            while (!string.IsNullOrEmpty(next));
        }
        catch (ScanNotFoundException)
        {
            // No scan of this image exists yet. The step waits for scan-on-push.
            return (null, null);
        }

        return (status, DeployVerification.SeverityCounts(summary, severities));
    }
}

internal sealed class EcsServices(IAmazonECS ecs) : IServices
{
    public async Task<ServiceSnapshot?> DescribeAsync(string cluster, string service)
    {
        var response = await ecs.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = cluster,
            Services = new List<string> { service },
        });

        // Only ACTIVE counts: a DRAINING or INACTIVE service is still returned by name, and rolling
        // one would be rolling something torn down.
        var s = response.Services?.FirstOrDefault(x => x.Status == "ACTIVE");
        if (s is null || string.IsNullOrEmpty(s.TaskDefinition)) return null;

        return new ServiceSnapshot(
            s.TaskDefinition,
            s.Deployments?.Select(d => new DeploymentSnapshot(d.Status, d.TaskDefinition, d.RolloutState?.Value)).ToList());
    }

    public async Task<IReadOnlyList<TaskSnapshot>> RunningTasksAsync(string cluster, string service)
    {
        var arns = new List<string>();
        string? next = null;
        do
        {
            var page = await ecs.ListTasksAsync(new ListTasksRequest
            {
                Cluster = cluster,
                ServiceName = service,
                DesiredStatus = DesiredStatus.RUNNING,
                NextToken = next,
            });
            arns.AddRange(page.TaskArns ?? new List<string>());
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        var tasks = new List<TaskSnapshot>();
        foreach (var chunk in arns.Chunk(100))
        {
            var described = await ecs.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = cluster,
                Tasks = chunk.ToList(),
            });

            tasks.AddRange((described.Tasks ?? new List<Amazon.ECS.Model.Task>()).Select(t =>
                new TaskSnapshot(
                    t.LastStatus,
                    t.Containers?.Select(c => new ContainerSnapshot(c.Name, c.ImageDigest)).ToList())));
        }

        return tasks;
    }
}

internal sealed class HookReads(IAmazonECS ecs, IAmazonECR ecr) : IHookReads
{
    public async Task<string?> TaskDefinitionOfRevisionAsync(string serviceRevisionArn)
    {
        var response = await ecs.DescribeServiceRevisionsAsync(new DescribeServiceRevisionsRequest
        {
            ServiceRevisionArns = new List<string> { serviceRevisionArn },
        });

        return response.ServiceRevisions?.FirstOrDefault(
            r => string.Equals(r.ServiceRevisionArn, serviceRevisionArn, StringComparison.Ordinal))?.TaskDefinition;
    }

    public async Task<IReadOnlyList<string?>> ContainerImagesAsync(string taskDefinitionArn)
    {
        var response = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = taskDefinitionArn,
        });

        // IMAGES ONLY. The definition also holds environment values including a client secret; nothing
        // but the image strings leaves this method.
        return (response.TaskDefinition?.ContainerDefinitions ?? new List<ContainerDefinition>())
            .Select(c => (string?)c.Image)
            .ToList();
    }

    public async Task<(string Username, string Password)> RegistryCredentialsAsync()
    {
        var response = await ecr.GetAuthorizationTokenAsync(new GetAuthorizationTokenRequest());
        var token = response.AuthorizationData?.FirstOrDefault()?.AuthorizationToken
            ?? throw new InvalidOperationException("ECR returned no authorization token.");

        return SignatureHook.DecodeRegistryToken(token);
    }
}
