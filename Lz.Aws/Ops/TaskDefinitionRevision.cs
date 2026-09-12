using System.Text.RegularExpressions;
using Amazon.ECS.Model;

namespace Lz.Aws.Ops;

/// <summary>
/// Registering a new revision of a task definition that differs only in one container's image.
///
/// <para>SHARED BY TWO CALLERS ON PURPOSE: <c>lz updatecontainer</c> (<see cref="AwsContainerUpdater"/>)
/// and the decoupled-CD deployer's Prepare function. RegisterTaskDefinition inherits nothing from the
/// previous revision — every field it is not given is silently dropped — so the field copy is exactly
/// the code that must not exist twice. A field added to one copy and not the other would make the
/// pipeline deploy a task that differs from what <c>updatecontainer</c> would have deployed, in a way
/// that looks unrelated to either.</para>
///
/// <para>No I/O here: the caller describes the definition (with <c>Include = TAGS</c>, or the tags
/// come back empty) and registers the request.</para>
/// </summary>
public static class TaskDefinitionRevision
{
    private static readonly Regex Digest = new(@"^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// The request that re-registers <paramref name="definition"/> as it now stands.
    ///
    /// <para>EVERY REGISTER-ABLE FIELD IS COPIED — all seventeen <see cref="RegisterTaskDefinitionRequest"/>
    /// carries in AWSSDK.ECS 4.0.14 — and a test enumerates the request type by reflection, so a field
    /// the SDK adds later turns that test red instead of being dropped from every deploy.</para>
    ///
    /// <para>TWO FIELDS ARE NEW TO <c>updatecontainer</c>, which copied fifteen before this was
    /// extracted: <c>EnableFaultInjection</c> and <c>InferenceAccelerators</c>. A definition setting
    /// either would have lost it on every image update. None of this system's definitions set them,
    /// so nothing deployed changes. Found by listing the request's properties from the SDK's own
    /// documentation file while extracting this, not from a report.</para>
    ///
    /// <para>The container definitions carry environment values including a plaintext client secret.
    /// The request must never be logged or serialized anywhere it would be kept.</para>
    /// </summary>
    public static RegisterTaskDefinitionRequest RegisterRequestFor(TaskDefinition definition, List<Tag>? tags)
        => new()
        {
            Family = definition.Family,
            TaskRoleArn = definition.TaskRoleArn,
            ExecutionRoleArn = definition.ExecutionRoleArn,
            NetworkMode = definition.NetworkMode,
            ContainerDefinitions = definition.ContainerDefinitions,
            Volumes = definition.Volumes,
            PlacementConstraints = definition.PlacementConstraints,
            RequiresCompatibilities = definition.RequiresCompatibilities,
            Cpu = definition.Cpu,
            Memory = definition.Memory,
            PidMode = definition.PidMode,
            IpcMode = definition.IpcMode,
            ProxyConfiguration = definition.ProxyConfiguration,
            EphemeralStorage = definition.EphemeralStorage,
            RuntimePlatform = definition.RuntimePlatform,
            EnableFaultInjection = definition.EnableFaultInjection,
            InferenceAccelerators = definition.InferenceAccelerators,
            Tags = tags,
        };

    /// <summary>
    /// The repository URI of <paramref name="currentImage"/> pinned to <paramref name="digest"/>,
    /// whatever it is pinned to now — a tag, an existing digest, or nothing.
    /// </summary>
    public static string RepinImage(string currentImage, string digest)
    {
        var repoUri = currentImage;
        var at = repoUri.LastIndexOf('@');
        if (at >= 0) repoUri = repoUri[..at];
        else
        {
            var colon = repoUri.LastIndexOf(':');
            var slash = repoUri.LastIndexOf('/');
            if (colon > slash) repoUri = repoUri[..colon];
        }
        return $"{repoUri}@{digest}";
    }

    /// <summary>
    /// The one container named <paramref name="containerName"/>. The deployer selects by NAME, which is
    /// exact, rather than by "whose image mentions the repository", which is how
    /// <c>updatecontainer</c> finds it — the pipeline may be moving a container to a different
    /// repository, so its current image cannot be what identifies it.
    /// </summary>
    public static ContainerDefinition SingleContainerNamed(TaskDefinition definition, string containerName)
    {
        var matches = (definition.ContainerDefinitions ?? new List<ContainerDefinition>())
            .Where(c => string.Equals(c.Name, containerName, StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"task definition {definition.Family}:{definition.Revision} has no container named " +
                $"'{containerName}'."),
            _ => throw new InvalidOperationException(
                $"task definition {definition.Family}:{definition.Revision} has {matches.Count} containers " +
                $"named '{containerName}'; which one to change is ambiguous."),
        };
    }

    /// <summary>
    /// <c>{registry}/{repository}@{digest}</c>, refusing anything that is not exactly that shape. The
    /// deployer builds the image it deploys from parts, so each part is checked rather than trusted.
    /// </summary>
    public static string PinnedImage(string registry, string repository, string digest)
    {
        if (!Regex.IsMatch(registry, @"^\d{12}\.dkr\.ecr\.[a-z0-9-]+\.amazonaws\.com$"))
            throw new InvalidOperationException(
                $"'{registry}' is not an ECR registry host ({{account}}.dkr.ecr.{{region}}.amazonaws.com).");

        if (!Regex.IsMatch(repository, @"^[a-z0-9]+(?:[._/-][a-z0-9]+)*$"))
            throw new InvalidOperationException($"'{repository}' is not a repository name.");

        if (!Digest.IsMatch(digest))
            throw new InvalidOperationException(
                $"'{digest}' is not a sha256 digest. A tag is a mutable pointer and cannot be deployed.");

        return $"{registry}/{repository}@{digest}";
    }
}
