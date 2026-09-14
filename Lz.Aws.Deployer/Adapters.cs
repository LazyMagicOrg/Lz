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
    public static readonly Lazy<Amazon.StepFunctions.IAmazonStepFunctions> Sfn = new(() => new Amazon.StepFunctions.AmazonStepFunctionsClient());
    public static readonly Lazy<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService> Sns =
        new(() => new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient());
    // CloudFront is global; its API is reached through us-east-1, as lz's CLI reaches it.
    public static readonly Lazy<Amazon.CloudFront.IAmazonCloudFront> CloudFront =
        new(() => new Amazon.CloudFront.AmazonCloudFrontClient(Amazon.RegionEndpoint.USEast1));
}

/// <summary>The artifact store in the build account, read by version (P4 stage C).</summary>
internal sealed class S3ArtifactObjects(IAmazonS3 s3) : IArtifactObjects
{
    public async Task<ArtifactHead?> HeadAsync(string bucket, string key, string versionId)
    {
        try
        {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId,
                // Without it S3 returns no checksum at all.
                ChecksumMode = ChecksumMode.ENABLED,
            });
            return new ArtifactHead(head.VersionId, head.ChecksumSHA256, head.ChecksumType?.Value, head.ContentLength);
        }
        // NO SUCH VERSION arrives as either: a 404, or the 400 S3 answers for a version id it never issued (measured
        // 2026-09-14). A 403 is a grant that is missing, not a version that is, and it throws.
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public async Task<(string? VersionId, long Bytes)> DownloadAsync(string bucket, string key, string versionId, string path)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            ChecksumMode = ChecksumMode.ENABLED,
        });

        await using (var file = File.Create(path))
            await response.ResponseStream.CopyToAsync(file);

        return (response.VersionId, new FileInfo(path).Length);
    }
}

/// <summary>A web app's bucket in this account (P4 stage C, P-7).</summary>
internal sealed class S3AppBucket(IAmazonS3 s3) : IAppBucket
{
    // A marker is a few hundred bytes. Anything this large is not one, and is not read.
    private const long MaxMarkerBytes = 64 * 1024;

    public async System.Threading.Tasks.Task EnsureAsync(string bucket, string region, string policyJson, bool versioning, int? noncurrentExpirationDays)
    {
        bool exists;
        try
        {
            await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
            exists = true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            exists = false;
        }

        if (!exists)
            await s3.PutBucketAsync(Lz.Aws.S3BucketRequests.Create(bucket, region));

        // EVERY RUN, not only on creation: the deploy owns its bucket's hardening (P-7), and a write that fails throws —
        // deploywebapp swallows a failed policy write, which is what DecoupledCd.md's survey found.
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true, IgnorePublicAcls = true, BlockPublicPolicy = true, RestrictPublicBuckets = true,
            },
        });
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policyJson });

        // THE DURABILITY DECISION, as BucketDurabilityEnsurer applies it for deploywebapp.
        if (!versioning) return;
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        if (noncurrentExpirationDays is int days)
            await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucket,
                Configuration = new LifecycleConfiguration
                {
                    Rules = new List<LifecycleRule>
                    {
                        new()
                        {
                            Id = DeployBundleSettings.NoncurrentExpiryRuleId,
                            Status = LifecycleRuleStatus.Enabled,
                            Filter = new LifecycleFilter(),
                            NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = days },
                        },
                    },
                },
            });
    }

    public async Task<MarkerRead?> ReadMarkerAsync(string bucket, string key)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
            if (response.ContentLength > MaxMarkerBytes)
                throw new DeployRefused("marker", $"s3://{bucket}/{key} is {response.ContentLength} bytes; a deploy marker is not that large.");

            using var reader = new StreamReader(response.ResponseStream);
            return new MarkerRead(await reader.ReadToEndAsync(), response.ETag);
        }
        // No such key, or no such bucket yet: nothing has been deployed there. A denial throws.
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string?> WriteMarkerAsync(string bucket, string key, string json, string? ifMatchETag)
    {
        try
        {
            var request = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = json, ContentType = "application/json" };
            if (ifMatchETag is null) request.IfNoneMatch = "*";
            else request.IfMatch = ifMatchETag;

            return (await s3.PutObjectAsync(request)).ETag;
        }
        // 412: the marker is no longer what was read. 409: another conditional write to it was in flight at the same moment.
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token });
            keys.AddRange((page.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);

        return keys;
    }

    public async Task<Lz.Aws.Webapp.StoredObject?> HeadAsync(string bucket, string key)
    {
        try
        {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                ChecksumMode = ChecksumMode.ENABLED,
            });

            // S3's SHA-256 counts only when it is the whole object's.
            var sha256 = head.ChecksumSHA256 is { } b64 && head.ChecksumType?.Value == "FULL_OBJECT"
                ? Convert.ToHexStringLower(Convert.FromBase64String(b64))
                : null;

            return new Lz.Aws.Webapp.StoredObject(key, sha256, head.Headers.CacheControl, head.Headers.ContentType, head.Headers.ContentEncoding);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async System.Threading.Tasks.Task PutAsync(string bucket, string key, string filePath, Lz.Aws.Webapp.WebappObjectHeaders headers, string sha256Base64)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = filePath,
            ContentType = headers.ContentType,
            // The SHA-256 S3 checks the bytes against and keeps, which the next deploy and VerifyBundle compare.
            ChecksumSHA256 = sha256Base64,
        };
        request.Headers.CacheControl = headers.CacheControl;
        if (headers.ContentEncoding is { } encoding)
            request.Headers.ContentEncoding = encoding;

        await s3.PutObjectAsync(request);
    }

    public async System.Threading.Tasks.Task DeleteAsync(string bucket, IReadOnlyList<string> keys)
    {
        foreach (var chunk in keys.Chunk(1000))
        {
            var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = chunk.Select(k => new KeyVersion { Key = k }).ToList(),
                Quiet = true,
            });

            if (response.DeleteErrors is { Count: > 0 } errors)
                throw new InvalidOperationException(
                    $"{errors.Count} of {chunk.Length} deletes from {bucket} failed, e.g. {errors[0].Key}: {errors[0].Code}.");
        }
    }
}

/// <summary>CloudFront invalidations (P4 stage C).</summary>
internal sealed class CloudFrontInvalidations(Amazon.CloudFront.IAmazonCloudFront cloudFront) : IInvalidations
{
    public async Task<string> CreateAsync(string distributionId, string path, string callerReference)
    {
        var response = await cloudFront.CreateInvalidationAsync(new Amazon.CloudFront.Model.CreateInvalidationRequest
        {
            DistributionId = distributionId,
            InvalidationBatch = new Amazon.CloudFront.Model.InvalidationBatch
            {
                CallerReference = callerReference,
                Paths = new Amazon.CloudFront.Model.Paths { Quantity = 1, Items = new List<string> { path } },
            },
        });
        return response.Invalidation.Id;
    }

    public async Task<string?> StatusAsync(string distributionId, string invalidationId)
        => (await cloudFront.GetInvalidationAsync(new Amazon.CloudFront.Model.GetInvalidationRequest
        {
            DistributionId = distributionId,
            Id = invalidationId,
        })).Invalidation?.Status;
}

/// <summary>The sweep's view of a repository: every image and artifact, all pages.</summary>
internal sealed class EcrRepositoryImages(IAmazonECR ecr) : IRepositoryImages
{
    public async Task<IReadOnlyList<RepositoryImage>> ListAsync(string repository)
    {
        var all = new List<RepositoryImage>();
        string? token = null;
        do
        {
            var page = await ecr.DescribeImagesAsync(new DescribeImagesRequest { RepositoryName = repository, NextToken = token });
            foreach (var detail in page.ImageDetails ?? new List<ImageDetail>())
            {
                // No push time would leave the image out of every window, silently; it is a fault instead.
                var pushedAt = detail.ImagePushedAt
                    ?? throw new InvalidOperationException($"ECR listed {detail.ImageDigest} in {repository} with no push time.");
                var utc = pushedAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(pushedAt, DateTimeKind.Utc) : pushedAt.ToUniversalTime();

                all.Add(new RepositoryImage(
                    detail.ImageDigest, detail.ImageManifestMediaType, detail.ArtifactMediaType, new DateTimeOffset(utc),
                    detail.ImageTags ?? new List<string>()));
            }

            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        return all;
    }
}

/// <summary>
/// The sweep's view of the artifact store (P4 stage D): every version under a prefix, all pages. A delete marker is left out —
/// it holds no bytes, so nothing could deploy it.
/// </summary>
internal sealed class S3ArtifactVersions(IAmazonS3 s3) : IArtifactVersions
{
    public async Task<IReadOnlyList<StoredVersion>> ListAsync(string bucket, string prefix)
    {
        var all = new List<StoredVersion>();
        string? keyMarker = null;
        string? versionMarker = null;
        do
        {
            var page = await s3.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                Prefix = prefix,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
            });

            foreach (var version in page.Versions ?? new List<S3ObjectVersion>())
            {
                if (version.IsDeleteMarker == true) continue;

                // No time would leave the version out of every window, silently; it is a fault instead.
                var modified = version.LastModified
                    ?? throw new InvalidOperationException($"S3 listed s3://{bucket}/{version.Key} version {version.VersionId} with no time.");
                var utc = modified.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(modified, DateTimeKind.Utc) : modified.ToUniversalTime();

                all.Add(new StoredVersion(version.Key, version.VersionId, new DateTimeOffset(utc)));
            }

            (keyMarker, versionMarker) = page.IsTruncated == true ? (page.NextKeyMarker, page.NextVersionIdMarker) : (null, null);
        } while (keyMarker != null);

        return all;
    }
}

/// <summary>Keys under a prefix, after a key, all pages.</summary>
internal sealed class S3RecordKeys(IAmazonS3 s3) : IRecordKeys
{
    public async Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, string startAfter)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                // S3 ignores StartAfter once a continuation token is given; it is sent on the first page only.
                StartAfter = token is null ? startAfter : null,
                ContinuationToken = token,
            });
            keys.AddRange((page.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);

        return keys;
    }
}

/// <summary>
/// Whether an evidence object exists, BY LISTING ITS EXACT KEY rather than reading it, so the sweep needs no read of
/// <c>anomalies/</c> at all — only the list grant scoped to that prefix. A denial throws rather than reading as absence.
/// </summary>
internal sealed class S3EvidenceProbe(IAmazonS3 s3) : IEvidenceProbe
{
    public async Task<bool> ExistsAsync(string bucket, string key)
    {
        var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = key, MaxKeys = 1 });
        return (page.S3Objects ?? new List<S3Object>()).Any(o => o.Key == key);
    }
}

internal sealed class SnsAlerts(Amazon.SimpleNotificationService.IAmazonSimpleNotificationService sns) : IAlertPublisher
{
    // System's Task: Amazon.ECS.Model, imported above, has its own.
    public System.Threading.Tasks.Task PublishAsync(string topicArn, string subject, string message)
        => sns.PublishAsync(new Amazon.SimpleNotificationService.Model.PublishRequest
        {
            TopicArn = topicArn,
            Subject = subject,
            Message = message,
        });
}

/// <summary>The start function's two calls. "The name is taken" and "no such execution" are SDK exceptions; the step needs answers.</summary>
internal sealed class SfnExecutions(Amazon.StepFunctions.IAmazonStepFunctions sfn) : IExecutions
{
    public async Task<bool> StartAsync(string stateMachineArn, string name, string input)
    {
        try
        {
            await sfn.StartExecutionAsync(new Amazon.StepFunctions.Model.StartExecutionRequest
            {
                StateMachineArn = stateMachineArn,
                Name = name,
                Input = input,
            });
            return true;
        }
        catch (Amazon.StepFunctions.Model.ExecutionAlreadyExistsException)
        {
            return false;
        }
    }

    public async Task<string?> InputOfAsync(string executionArn)
    {
        try
        {
            return (await sfn.DescribeExecutionAsync(new Amazon.StepFunctions.Model.DescribeExecutionRequest
            {
                ExecutionArn = executionArn,
            })).Input;
        }
        catch (Amazon.StepFunctions.Model.ExecutionDoesNotExistException)
        {
            return null;
        }
    }
}

internal sealed class S3RecordStore(IAmazonS3 s3) : IRecordStore
{
    // A build record is a few hundred bytes. Anything this large is not one, and is not read.
    private const long MaxRecordBytes = 1024 * 1024;

    public async Task<StoredRecord?> ReadAsync(string bucket, string key)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
            if (response.ContentLength > MaxRecordBytes)
                throw new DeployRefused("record",
                    $"s3://{bucket}/{key} is {response.ContentLength} bytes; a build record is not that large.");

            using var reader = new StreamReader(response.ResponseStream);
            // The version of the bytes just read, from the same response — never from a second call that could see another.
            return new StoredRecord(await reader.ReadToEndAsync(), response.VersionId);
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

internal sealed class EcsTaskDefinitions(IAmazonECS ecs) : ITaskDefinitions
{
    public async Task<(TaskDefinition Definition, List<Amazon.ECS.Model.Tag>? Tags)> DescribeAsync(string taskDefinitionArn)
    {
        var described = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest
        {
            TaskDefinition = taskDefinitionArn,
            // Without this the tags come back empty and the new revision would silently lose them.
            Include = new List<string> { "TAGS" },
        });
        return (described.TaskDefinition, described.Tags);
    }

    public async Task<string> RegisterAsync(RegisterTaskDefinitionRequest request)
        => (await ecs.RegisterTaskDefinitionAsync(request)).TaskDefinition.TaskDefinitionArn;
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

    public async Task<IReadOnlyList<ServiceDeploymentRecord>> RecentDeploymentsAsync(string cluster, string service)
    {
        // Newest first (measured). Ten spans far more than one execution's window; a record older than that
        // cannot be this execution's.
        var listed = await ecs.ListServiceDeploymentsAsync(new ListServiceDeploymentsRequest
        {
            Cluster = cluster,
            Service = service,
            MaxResults = 10,
        });
        var briefs = listed.ServiceDeployments ?? new List<ServiceDeploymentBrief>();

        // A record names its target REVISION; the task definition that revision runs is one more read, batched.
        var revisionArns = briefs.Select(b => b.TargetServiceRevisionArn)
            .Where(arn => !string.IsNullOrEmpty(arn)).Distinct().ToList();
        var taskDefinitionOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (revisionArns.Count > 0)
        {
            var described = await ecs.DescribeServiceRevisionsAsync(new DescribeServiceRevisionsRequest
            {
                ServiceRevisionArns = revisionArns,
            });
            foreach (var revision in described.ServiceRevisions ?? new List<ServiceRevision>())
                taskDefinitionOf[revision.ServiceRevisionArn] = revision.TaskDefinition;
        }

        return briefs.Select(b => new ServiceDeploymentRecord(
                b.TargetServiceRevisionArn is { } arn && taskDefinitionOf.TryGetValue(arn, out var td) ? td : null,
                b.Status?.Value,
                b.StatusReason,
                b.CreatedAt))
            .ToList();
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
