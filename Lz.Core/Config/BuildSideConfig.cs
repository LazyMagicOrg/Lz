using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Lz.Core.Config;

/// <summary>
/// THE BUILD-SIDE PIPELINE FILE, <c>pipelineconfig.{systemkey}.yaml</c> (DecoupledCd.md V-21; §15.7 V-A). One file per
/// system, beside the environment configs, holding what every environment shares and none may disagree on: the build
/// account, the registry's hardening, the repositories the build account takes artifacts from, and the ordered
/// promotion chain. Each <c>systemconfig.{systemkey}.{env}.yaml</c> keeps only its own (<c>Enabled</c>,
/// <c>TargetAccountId</c>, <c>EnforceSignatures</c>, <c>Classes</c>, <c>Approval</c>, <c>Scan</c>, <c>Reconciler</c>,
/// <c>Alerts</c>, <c>PostDeployTests</c>), and naming a build-side field there is refused.
///
/// <para>HOW IT REACHES THE PLANNERS. <see cref="ConfigLoader.LoadSystemConfig"/> reads this file and fills the moved
/// fields into the environment's in-memory <see cref="PipelineConfig"/> - <c>ArtifactAccountId</c> from
/// <see cref="BuildAccountId"/>, <c>Registry</c>, <c>Repositories</c>, and <c>DeployOnBuildRecord</c> as "this
/// environment heads the chain" (the chain's first environment is the one fed by build records). Every planner reads
/// the same values it read before the move, so a migrated config plans byte for byte what it planned before.</para>
/// </summary>
public class BuildSideConfig
{
    /// <summary>The build account: where the pipeline builds, signs and keeps records (was <c>Pipeline.ArtifactAccountId</c>).</summary>
    public string? BuildAccountId { get; set; }

    /// <summary>Registry hardening, shared by every environment the images replicate to.</summary>
    public PipelineRegistryConfig? Registry { get; set; }

    /// <summary>The repositories the build account takes artifacts from (the intake's allowlist under v5).</summary>
    public List<PipelineRepositoryConfig>? Repositories { get; set; }

    /// <summary>
    /// The promotion chain, in order: the first environment is fed by build records, each later one only by its
    /// predecessor's pass markers (T-12). <c>[dev]</c> until test joins (P5 G).
    /// </summary>
    public List<PipelineChainEntry>? Chain { get; set; }

    /// <summary>The fields an environment config may no longer name in its <c>Pipeline</c> block.</summary>
    public static readonly string[] MovedFields = { "ArtifactAccountId", "DeployOnBuildRecord", "Repositories", "Registry" };

    public static string FileNameFor(string systemKey) => $"pipelineconfig.{systemKey}.yaml";

    /// <summary>Every problem with the file, named; empty when it is sound.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(BuildAccountId))
            p.Add("BuildAccountId is required");
        else if (!Regex.IsMatch(BuildAccountId, @"^\d{12}$"))
            p.Add($"BuildAccountId is '{BuildAccountId}', not a 12-digit AWS account id");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Chain ?? new())
        {
            if (string.IsNullOrWhiteSpace(entry.Environment)) { p.Add("a Chain entry has no Environment"); continue; }
            if (!seen.Add(entry.Environment)) p.Add($"Chain names '{entry.Environment}' twice");
            if (entry.AccountId is null || !Regex.IsMatch(entry.AccountId, @"^\d{12}$"))
                p.Add($"Chain entry '{entry.Environment}' has AccountId '{entry.AccountId}', not a 12-digit AWS account id");
            else if (entry.AccountId == BuildAccountId)
                p.Add($"Chain entry '{entry.Environment}' is the build account; an environment runs in its own account");
        }

        foreach (var repo in Repositories ?? new())
        {
            var name = repo.Repo ?? "(unnamed)";
            if (string.IsNullOrWhiteSpace(repo.Repo)) p.Add("a repository has no Repo");
            if (repo.Intake is { } i && i is not ("dry" or "live")) p.Add($"{name}: Intake is '{i}', not dry or live");
            if (repo.Verify is { } v && v is not ("dry" or "live")) p.Add($"{name}: Verify is '{v}', not dry or live");
            if (repo.Verify == "live" && repo.Intake != "live")
                p.Add($"{name}: Verify is live but Intake is not; Verify goes live last (V-D)");
            if (repo.Intake is not null && (repo.RepositoryId is null || repo.OwnerId is null || string.IsNullOrWhiteSpace(repo.WorkflowPath)))
                p.Add($"{name}: an Intake mode needs RepositoryId, OwnerId and WorkflowPath, which the run check compares by id");
            p.AddRange(Gate1Problems(name, repo));
        }
        return p;
    }

    /// <summary>Gate 1's settings (V-C): all of them or none, and each in a form the build can check.</summary>
    private static IEnumerable<string> Gate1Problems(string name, PipelineRepositoryConfig repo)
    {
        if (repo.TestBundle is null && repo.Gate1 is null) yield break;
        if (string.IsNullOrWhiteSpace(repo.TestBundle)) { yield return $"{name}: Gate1 is set without a TestBundle to run"; yield break; }
        if (repo.Gate1 is not { } g) { yield return $"{name}: TestBundle is set without Gate1 to say how it runs"; yield break; }
        if (string.IsNullOrWhiteSpace(g.Run) || g.Run.StartsWith('/') || g.Run.Contains(".."))
            yield return $"{name}: Gate1.Run '{g.Run}' is not a path inside the bundle";
        if (g.BuildRoot is { } root && (!Regex.IsMatch(root, "^/[A-Za-z0-9._/-]+$") || root.Contains("..")))
            yield return $"{name}: Gate1.BuildRoot '{root}' is not an absolute path";
        if (string.IsNullOrWhiteSpace(g.Filter)) yield return $"{name}: Gate1.Filter is required; Gate 1 never runs the whole suite by default";
        if (g.SdkVersion is null || !Regex.IsMatch(g.SdkVersion, @"^\d+\.\d+\.\d+$"))
            yield return $"{name}: Gate1.SdkVersion '{g.SdkVersion}' is not an exact SDK version";
        if (g.SdkSha512 is null || !Regex.IsMatch(g.SdkSha512, "^[0-9a-f]{128}$"))
            yield return $"{name}: Gate1.SdkSha512 is not a lower-case SHA-512";
        foreach (var (key, value) in g.Environment ?? new())
        {
            if (!Regex.IsMatch(key, "^[A-Z_][A-Z0-9_]*$")) yield return $"{name}: Gate1.Environment name '{key}' is not an environment variable's";
            else if (key.StartsWith("BUNDLE_") || key.StartsWith("GATE1_") || key.StartsWith("SDK_"))
                yield return $"{name}: Gate1.Environment '{key}' is one of Gate 1's own names";
            if (value is null) yield return $"{name}: Gate1.Environment '{key}' has no value";
        }
        foreach (var image in g.Images ?? new())
        {
            if (image.From is null || !Regex.IsMatch(image.From, "@sha256:[0-9a-f]{64}$"))
                yield return $"{name}: Gate1 image '{image.From}' is not pulled by digest";
            if (string.IsNullOrWhiteSpace(image.As)) yield return $"{name}: Gate1 image '{image.From}' has no As";
        }
    }

    /// <summary>
    /// The moved fields an environment config's YAML still names in its <c>Pipeline</c> block. Read from the YAML, not
    /// the deserialized object, because <c>DeployOnBuildRecord: false</c> and an absent key deserialize alike.
    /// </summary>
    public static IReadOnlyList<string> MovedFieldsNamedIn(string systemConfigYaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(systemConfigYaml)) stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return Array.Empty<string>();
        var pipeline = root.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "Pipeline" }).Value as YamlMappingNode;
        if (pipeline is null) return Array.Empty<string>();
        return pipeline.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!)
            .Where(k => MovedFields.Contains(k, StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// Fills the moved fields into one environment's in-memory <see cref="PipelineConfig"/>. A chain entry for this
    /// environment must name the account the environment deploys into; anything else is two files disagreeing about
    /// where an environment is.
    /// </summary>
    public void MergeInto(PipelineConfig pipeline, string environment)
    {
        pipeline.ArtifactAccountId = BuildAccountId;
        pipeline.Registry = Registry;
        pipeline.Repositories = Repositories;

        var entry = Chain?.FirstOrDefault(c => string.Equals(c.Environment, environment, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && pipeline.TargetAccountId is { } target && !string.Equals(entry.AccountId, target, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the promotion chain places '{environment}' in account {entry.AccountId}, but its systemconfig deploys " +
                $"into {target} (Pipeline.TargetAccountId). One of the two files is wrong.");

        pipeline.DeployOnBuildRecord = Chain is { Count: > 0 }
            && string.Equals(Chain[0].Environment, environment, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One environment in the promotion chain.</summary>
public class PipelineChainEntry
{
    public string? Environment { get; set; }
    public string? AccountId { get; set; }
}
