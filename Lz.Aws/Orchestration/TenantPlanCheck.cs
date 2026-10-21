using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace Lz.Aws.Orchestration;

/// <summary>One step of a Pulumi plan, as a preview's engine events report it: the operation, and the inputs before and after.</summary>
public sealed record PlannedStep(
    OperationType Op, string Type, string Urn,
    IReadOnlyDictionary<string, object>? OldInputs, IReadOnlyDictionary<string, object>? NewInputs)
{
    public static PlannedStep From(StepEventMetadata m) => new(
        m.Op, m.Type, m.Urn,
        m.Old?.Inputs is { } o ? new Dictionary<string, object>(o) : null,
        m.New?.Inputs is { } n ? new Dictionary<string, object>(n) : null);
}

/// <summary>
/// A tenant deploy stopped by its image checks (DecoupledCd.md V-J): refused before applying, or set a service to an older
/// build. The reason has been printed; the CLI exits 1 without a stack trace.
/// </summary>
public sealed class TenantDeployRefusedException(string message) : InvalidOperationException(message);

/// <summary>What a running service is set to before a tenant deploy: its container, digest and build time.</summary>
public sealed record RunningServiceImage(string Container, string Digest, string? BuiltAt);

/// <summary>
/// THE TENANT PLAN'S IMAGE CHECKS (DecoupledCd.md V-J), until P3 J makes the digest a Pulumi input: a tenant deploy declares
/// the digest its service runs (<see cref="Lz.Aws.Compute.ImagePinPolicy.ChooseDigest"/>), and these hold the plan and the
/// result to that, rather than trusting the choice.
///
/// <para><b>THE PLAN CANNOT SHOW ITS OWN IMAGE</b> (measured on dev's tenant stack, 2026-10-20): a task definition's
/// <c>containerDefinitions</c> is a secret, since it carries the BFF secrets, and a preview's engine events report it as a
/// masked envelope, the same before and after. So the image is read where the tenant program DECLARES it
/// (<see cref="DeclaredImages"/>, written by the service component as it builds the definition, which is exactly the
/// input Pulumi plans), and whether anything besides the image changed is read from <see cref="DefinitionsHashTag"/>.</para>
///
/// <list type="bullet">
///   <item><see cref="DigestRefusals"/>: BEFORE APPLYING, a plan that replaces a running service's task definition with any
///   other digest, or with one the program never declared, is refused, so a deploy cannot revert the image the deployer or
///   updatecontainer put there.</item>
///   <item><see cref="JudgeAfterApply"/>: AFTER APPLYING, the service set to an older build than before is a revert that got
///   through, and alerts.</item>
///   <item><see cref="Destructive"/>: an image-only task-definition replace is not destructive. A task definition is
///   immutable, so every image change replaces it; counted, every routine roll would trip <c>--fail-on-replace</c> and,
///   once the deployer's Plan state reads it, <c>Pipeline.Approval.RefuseDestructivePlan</c>.</item>
/// </list>
/// </summary>
public static class TenantPlanCheck
{
    public const string TaskDefinitionType = "aws:ecs/taskDefinition:TaskDefinition";

    /// <summary>
    /// The task-definition tag holding a SHA-256 of its container definitions with every <c>image</c> removed, so a plan
    /// can tell an image change from any other while the definitions themselves are secret. A one-way hash over values
    /// that include high-entropy secrets; it identifies a set of definitions and reveals none of them.
    /// </summary>
    public const string DefinitionsHashTag = "lz:defsHash";

    /// <summary>
    /// The image each service's task definition declares, by service name (its family), as this process's tenant program
    /// built it. Written from inside the program's outputs, so concurrent; cleared before each program run.
    /// </summary>
    public static readonly ConcurrentDictionary<string, string> DeclaredImages = new(StringComparer.Ordinal);

    /// <summary>The <see cref="DefinitionsHashTag"/> value for a set of container definitions, as JSON.</summary>
    public static string DefinitionsHash(string containerDefinitionsJson)
    {
        var defs = JsonNode.Parse(containerDefinitionsJson) as JsonArray
            ?? throw new ArgumentException("container definitions are a JSON array", nameof(containerDefinitionsJson));
        foreach (var d in defs.OfType<JsonObject>()) d.Remove("image");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(defs)))).ToLowerInvariant();
    }

    /// <summary>The steps a replace-or-delete count includes, an image-only task-definition replace left out.</summary>
    public static IReadOnlyList<PlannedStep> Destructive(IEnumerable<PlannedStep> steps)
        => steps.Where(s => s.Op is OperationType.Replace or OperationType.Delete && !IsImageOnlyTaskDefinitionReplace(s)).ToList();

    /// <summary>
    /// A task-definition replace that changes nothing but its containers' images: every input equal, the container
    /// definitions by <see cref="DefinitionsHashTag"/> (present on both sides and the same), the tags apart from
    /// <c>lz:builtAt</c>, which moves with the image. Anything else that differs stays counted, as does a revision
    /// registered before the hash existed.
    /// </summary>
    public static bool IsImageOnlyTaskDefinitionReplace(PlannedStep s)
        => s.Type == TaskDefinitionType && s.Op == OperationType.Replace && WhyNotImageOnly(s) is null;

    /// <summary>Why a task-definition replace counts as more than an image change, or null when it is only that.</summary>
    public static string? WhyNotImageOnly(PlannedStep s)
    {
        if (s.OldInputs is null || s.NewInputs is null) return "its inputs are not in the plan";
        if (HashOf(s.NewInputs) is not { } after) return $"the new revision's {DefinitionsHashTag} is not known yet";
        if (HashOf(s.OldInputs) is not { } before) return $"the revision it replaces carries no {DefinitionsHashTag}, so what else changed is unknown";
        if (before != after) return "its container definitions change apart from the image";

        foreach (var key in s.OldInputs.Keys.Union(s.NewInputs.Keys))
        {
            if (key is "containerDefinitions" || key.StartsWith("__", StringComparison.Ordinal)) continue;
            s.OldInputs.TryGetValue(key, out var was);
            s.NewInputs.TryGetValue(key, out var now);
            if (key is "tags" or "tagsAll" ? Canonical(WithoutBuiltAt(was)) != Canonical(WithoutBuiltAt(now)) : Canonical(was) != Canonical(now))
                return $"its {key} changes";
        }
        return null;
    }

    /// <summary>
    /// For each running service whose task definition the plan creates or replaces, a refusal when the image the program
    /// declares for it is not the digest it runs, or was never declared. Keyed by task-definition family, which is the
    /// service's name.
    /// </summary>
    public static IReadOnlyList<string> DigestRefusals(
        IEnumerable<PlannedStep> steps, IReadOnlyDictionary<string, RunningServiceImage> running, IReadOnlyDictionary<string, string> declared)
    {
        var refusals = new List<string>();
        foreach (var s in steps.Where(s => s.Type == TaskDefinitionType && s.Op is OperationType.Create or OperationType.Replace))
        {
            if (s.NewInputs?.TryGetValue("family", out var f) != true || f?.ToString() is not { } family
                || !running.TryGetValue(family, out var service))
                continue;

            if (!declared.TryGetValue(family, out var image))
                refusals.Add($"{family}: the plan replaces its task definition with an image the program never declared, so it cannot be shown to be {Short(service.Digest)}, which the service runs.");
            else if (DigestOf(image) != service.Digest)
                refusals.Add($"{family}: the plan pins {image}, but the service runs {Short(service.Digest)}. Applying it would roll the service to another image.");
        }
        return refusals;
    }

    /// <summary>The verdict on a service after a tenant deploy, against what it was set to before.</summary>
    public enum AfterApply { Unchanged, Newer, Older, Unordered }

    public static (AfterApply Verdict, string Detail) JudgeAfterApply(string service, RunningServiceImage before, string? afterDigest, string? afterBuiltAt)
    {
        if (afterDigest == before.Digest)
            return (AfterApply.Unchanged, $"{service} still runs {Short(before.Digest)}");

        var was = Time(before.BuiltAt);
        var now = Time(afterBuiltAt);
        if (was is null || now is null)
            return (AfterApply.Unordered,
                $"{service} moved from {Short(before.Digest)} (built {before.BuiltAt ?? "unknown"}) to {Short(afterDigest)} (built {afterBuiltAt ?? "unknown"}), and the two cannot be ordered");
        return now < was
            ? (AfterApply.Older, $"{service} was set to an OLDER build: {Short(before.Digest)} built {before.BuiltAt} became {Short(afterDigest)} built {afterBuiltAt}")
            : (AfterApply.Newer, $"{service} moved to a newer build: {Short(before.Digest)} built {before.BuiltAt} became {Short(afterDigest)} built {afterBuiltAt}");
    }

    private static DateTimeOffset? Time(string? s)
        => DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? DigestOf(string image) => image.LastIndexOf('@') is var at and >= 0 ? image[(at + 1)..] : null;

    private static string Short(string? digest) => digest is { Length: > 19 } d && d.StartsWith("sha256:") ? d[..19] : digest ?? "nothing";

    private static string? HashOf(IReadOnlyDictionary<string, object> inputs)
        => inputs.TryGetValue("tags", out var tags) && JsonSerializer.SerializeToNode(tags) is JsonObject o
           && o[DefinitionsHashTag] is JsonValue v && v.TryGetValue<string>(out var h) ? h : null;

    private static JsonNode? WithoutBuiltAt(object? tags)
    {
        var node = tags as JsonNode ?? JsonSerializer.SerializeToNode(tags);
        if (node is JsonObject o) o.Remove(Lz.Aws.Pipeline.DeployOrdering.BuiltAtTag);
        return node;
    }

    // Order-independent for objects, so two equal inputs serialized in different key orders compare equal.
    private static string Canonical(object? value)
    {
        var node = value as JsonNode ?? JsonSerializer.SerializeToNode(value);
        return Sort(node)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Sort(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => n?.DeepClone(),
    };
}
