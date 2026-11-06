using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Orchestration;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <c>lz infradeploy</c>'s parts that are not the deploys themselves (DecoupledCd.md P3 D, the deploy half): the plan that was
/// read and approved, fetched from the evidence store; the rule for when a fresh preview may be applied; and the result,
/// written once under <see cref="InfraPlan.AppliesPrefix"/> for ReadApply.
///
/// <para>APPLY WHAT WAS READ. The Deploy task previews again before it applies, and applies nothing unless that preview takes
/// the same steps as the approved plan - the same stacks, and in each the same operations on the same URNs. Between ReadPlan
/// and the Deploy task, a workstation deploy, a drift or a different program would change what Pulumi does; each is a refusal
/// here, not a surprise there. The window that remains is the seconds between this preview and each stack's update.</para>
/// </summary>
public static class InfraDeploy
{
    public const string Deployed = "deployed", Refused = "refused", Failed = "failed";

    /// <summary>
    /// Why <paramref name="fresh"/> may not be applied as <paramref name="approved"/>; empty when it may. A destructive plan needs
    /// the override, which ReadPlan let through only for an input that carried it.
    /// </summary>
    public static IReadOnlyList<string> WhyNotApply(PlanFile approved, PlanFile fresh, bool allowDestructive)
    {
        var why = new List<string>();
        if (approved.Destructive && !allowDestructive)
            why.Add("the approved plan replaces or deletes, and the execution did not allow a destructive plan");
        if (fresh.Destructive && !allowDestructive)
            why.Add("the plan now replaces or deletes, and the execution did not allow a destructive plan");
        foreach (var p in approved.Plans.Where(p => p.HasChanges && p.Steps.Count == 0))
            why.Add($"the approved plan of {p.Stack} has changes and names no steps, so there is nothing to hold this deploy to");
        foreach (var p in fresh.Plans.Where(p => p.DigestRefusals.Count > 0))
            why.Add($"{p.Stack} would set a running service to another image: {string.Join("; ", p.DigestRefusals)}");
        why.AddRange(approved.DifferencesFrom(fresh));
        return why;
    }

    /// <summary>
    /// A DEPLOY OF A NEWER BUNDLE, or null (P3 E). Once root pushes start executions on their own, two can run close together and
    /// finish out of order; a bundle built before one already deployed would take the stacks back. <paramref name="deployed"/> is
    /// every config deploy recorded under <c>deploys/config/...</c>, with the record's <c>builtAt</c>; a deploy of a build
    /// built after this one supersedes it; the same build again does not. The same rule as Prepare's for an image, without its override: an older bundle is deployed
    /// by pushing it again.
    /// </summary>
    public static string? SupersededBy(IEnumerable<(string Key, string? BuiltAt)> deployed, string builtAt)
        => deployed.Where(d => d.BuiltAt is not null && string.CompareOrdinal(d.BuiltAt, builtAt) > 0)
            .OrderByDescending(d => d.BuiltAt, StringComparer.Ordinal).Select(d => $"{d.Key} (built {d.BuiltAt})").FirstOrDefault();

    /// <summary>Where a config repository's deploys are recorded: Record's key, without the execution.</summary>
    public static string DeploysPrefixFor(string repo) => $"deploys/{BuildRecordFormat.PrefixFor("config", repo)}";

    /// <summary>Every recorded config deploy of <paramref name="repo"/>, with the build each deployed.</summary>
    public static async Task<IReadOnlyList<(string Key, string? BuiltAt)>> ReadDeployedAsync(IAmazonS3 s3, string bucket, string repo, CancellationToken ct)
    {
        var found = new List<(string, string?)>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = DeploysPrefixFor(repo), ContinuationToken = token }, ct);
            foreach (var o in page.S3Objects ?? new())
            {
                using var response = await s3.GetObjectAsync(bucket, o.Key, ct);
                using var reader = new StreamReader(response.ResponseStream);
                var body = JsonNode.Parse(await reader.ReadToEndAsync(ct));
                found.Add((o.Key, body?["verified"]?["builtAt"]?.GetValue<string>()));
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return found;
    }

    /// <summary>The approved plan and the version read, from the evidence store.</summary>
    public static async Task<(PlanFile Plan, string? VersionId)> ReadApprovedAsync(IAmazonS3 s3, string bucket, string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
        using var reader = new StreamReader(response.ResponseStream);
        return (PlanFile.Parse(await reader.ReadToEndAsync(ct)), response.VersionId);
    }

    /// <summary>The result ReadApply reads: schema 1, camelCase, the second preview as a plan file.</summary>
    public static string ResultJson(
        string outcome, string? reason, string approvedKey, string? approvedVersionId, IReadOnlyList<string> differences,
        IReadOnlyList<string> applied, IReadOnlyList<string> runtime, PlanFile? after, bool allowDestructive)
    {
        var o = new JsonObject
        {
            ["schema"] = 1,
            ["command"] = "infradeploy",
            ["outcome"] = outcome,
            ["reason"] = reason,
            ["approvedPlan"] = new JsonObject { ["key"] = approvedKey, ["versionId"] = approvedVersionId },
            ["allowDestructive"] = allowDestructive,
            ["differences"] = new JsonArray(differences.Select(d => (JsonNode?)d).ToArray()),
            ["applied"] = new JsonArray(applied.Select(a => (JsonNode?)a).ToArray()),
            ["runtime"] = new JsonArray(runtime.Select(r => (JsonNode?)r).ToArray()),
            ["after"] = after is null ? null : JsonNode.Parse(after.ToJson()),
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Write the result once: <c>If-None-Match: *</c>, which the evidence store's policy demands anyway.</summary>
    public static Task PutResultAsync(IAmazonS3 s3, string bucket, string key, string json, CancellationToken ct)
        => InfraPlan.PutPlanAsync(s3, bucket, key, json, ct);
}
