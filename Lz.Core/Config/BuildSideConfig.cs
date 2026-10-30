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

    /// <summary>
    /// The suffix every build-account name carries (DecoupledCd.md §12 P5 stage A): the record and artifact stores, the
    /// neutral image repository and the intake's stores. One value for the system, so every environment in the chain names
    /// the same build side; required once the chain has more than one environment. Absent, each environment's own
    /// SystemSuffix is used, as before P5.
    /// </summary>
    public string? BuildSuffix { get; set; }

    /// <summary>A suffix as AWS names take it: lowercase letters and digits, in groups joined by single hyphens.</summary>
    public const string SuffixPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>The repositories the build account takes artifacts from (the intake's allowlist under v5).</summary>
    public List<PipelineRepositoryConfig>? Repositories { get; set; }

    /// <summary>
    /// The promotion chain, in order: the first environment is fed by build records, each later one only by its
    /// predecessor's pass markers (T-12). <c>[dev]</c> until test joins (P5 G).
    /// </summary>
    public List<PipelineChainEntry>? Chain { get; set; }

    /// <summary>The fields an environment config may no longer name in its <c>Pipeline</c> block.</summary>
    public static readonly string[] MovedFields = { "ArtifactAccountId", "DeployOnBuildRecord", "Repositories", "Registry", "BuildSuffix", "Outcomes" };

    public static string FileNameFor(string systemKey) => $"pipelineconfig.{systemKey}.yaml";

    /// <summary>Every problem with the file, named; empty when it is sound.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(BuildAccountId))
            p.Add("BuildAccountId is required");
        else if (!Regex.IsMatch(BuildAccountId, @"^\d{12}$"))
            p.Add($"BuildAccountId is '{BuildAccountId}', not a 12-digit AWS account id");

        if (BuildSuffix is { } suffix && !Regex.IsMatch(suffix, SuffixPattern))
            p.Add($"BuildSuffix is '{suffix}', not lowercase letters and digits in hyphen-joined groups");
        var promoted = Chain is { Count: > 1 };
        if (promoted && string.IsNullOrWhiteSpace(BuildSuffix))
            p.Add("the Chain has more than one environment, so BuildSuffix is required: every environment names the build side by it, not by its own SystemSuffix");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Chain ?? new())
        {
            if (promoted && !string.IsNullOrWhiteSpace(entry.Environment) && string.IsNullOrWhiteSpace(entry.EvidenceStore))
                p.Add($"Chain entry '{entry.Environment}' names no EvidenceStore; in a chain of more than one, the next environment reads its pass markers there");
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
            if (repo.Class == "site") p.AddRange(SiteProblems(name, repo, Chain));
            if (repo.Class == "tests") p.AddRange(TestsProblems(name, repo));
            if (repo.BuiltFrom is { } bf && !Regex.IsMatch(bf, "^[a-z][a-z0-9-]*$"))
                p.Add($"{name}: BuiltFrom '{bf}' is not an artifact's base name");
            if (repo.BuiltFrom is { } same && (same == repo.TestBundle || (repo.Artifacts ?? new()).Contains(same)))
                p.Add($"{name}: BuiltFrom '{same}' names an artifact that is already the image or the test bundle");
            if (repo.Sbom is { } sb && (!Regex.IsMatch(sb, "^[a-z][a-z0-9-]*$") || sb == repo.TestBundle || sb == repo.BuiltFrom || (repo.Artifacts ?? new()).Contains(sb)))
                p.Add($"{name}: Sbom '{sb}' is not an artifact name of its own");
            // A LIVE INTAKE WRITES RECORDS (V-D), and a record needs builtFrom, Gate 1's pass, and for an image the SBOM (V-19).
            if (repo.Intake == "live")
            {
                // A site's records carry builtFrom.packages empty (V-24): a Hugo build consumes no first-party package.
                // A tests repository's record carries it empty too (V-E): a test bundle is published, not built from packages.
                if (repo.BuiltFrom is null && repo.Class is not ("site" or "tests")) p.Add($"{name}: Intake: live needs BuiltFrom, since its records carry builtFrom.packages");
                if (repo.Gate1 is null) p.Add($"{name}: Intake: live needs Gate1; nothing is recorded without a pass");
                if (repo.Class is "image" or "tooling" && repo.Sbom is null) p.Add($"{name}: Intake: live for an image needs Sbom (V-19)");
            }
        }
        return p;
    }

    /// <summary>
    /// A TESTS REPOSITORY (DecoupledCd.md V-E): its one artifact is its test bundle, checked by the collection check, and it builds
    /// nothing a deployer or a record of its own would carry - no Artifacts, no SBOM, no builtFrom - and no Verify, since nothing
    /// deploys it.
    /// </summary>
    private static IEnumerable<string> TestsProblems(string name, PipelineRepositoryConfig repo)
    {
        if (repo.Gate1 is not { Kind: "collection" }) yield return $"{name}: a tests repository's Gate 1 is the collection check (Gate1.Kind: collection)";
        if (repo.Artifacts is { Count: > 0 }) yield return $"{name}: a tests repository publishes its test bundle alone, so it takes no Artifacts";
        if (repo.Sbom is not null) yield return $"{name}: a tests repository takes no Sbom; its record names a test bundle, not an image";
        if (repo.BuiltFrom is not null) yield return $"{name}: a tests repository takes no BuiltFrom; its record carries builtFrom.packages empty";
        if (repo.Verify is not null) yield return $"{name}: a tests repository takes no Verify; no deployer deploys its records";
    }

    /// <summary>Gate 1's settings (V-C): all of them or none, and each in a form the build can check.</summary>
    private static IEnumerable<string> Gate1Problems(string name, PipelineRepositoryConfig repo)
    {
        if (repo.Gate1?.Kind is { } kind && kind is not ("tests" or "site" or "collection" or "vitest"))
        {
            yield return $"{name}: Gate1.Kind '{kind}' is not tests, site, collection or vitest";
            yield break;
        }
        // A NODE REPOSITORY'S TESTS (BuyerAppReactPunchlist BR-P3, 2026-10-29): lz runs them - npm ci, then vitest run - in the
        // test bundle's root, and judges them from the count it prints. A test bundle to run, and none of a .NET run's settings.
        if (repo.Gate1 is { Kind: "vitest" } v)
        {
            if (repo.Class is "tests" or "site") yield return $"{name}: Gate1.Kind vitest runs a repository's own tests; a {repo.Class} repository has its own check";
            if (string.IsNullOrWhiteSpace(repo.TestBundle)) yield return $"{name}: a vitest run runs a TestBundle, and none is named";
            if (v.BuildRoot is { } vroot && (!Regex.IsMatch(vroot, "^/[A-Za-z0-9._/-]+$") || vroot.Contains("..")))
                yield return $"{name}: Gate1.BuildRoot '{vroot}' is not an absolute path";
            foreach (var (field, set) in new[] { ("Run", v.Run is not null), ("Filter", v.Filter is not null), ("SdkVersion", v.SdkVersion is not null),
                         ("SdkSha512", v.SdkSha512 is not null), ("Images", v.Images is { Count: > 0 }), ("Environment", v.Environment is { Count: > 0 }) })
                if (set) yield return $"{name}: Gate1.Kind vitest runs the bundle's own tests with npm, so it takes no {field}";
            yield break;
        }
        // THE BROWSER TESTS' GATE 1 (DecoupledCd.md §15.4): their collection, which lz runs - every spec loads and none is
        // marked .only - and their @gate count. A test bundle to collect, and none of a test run's settings.
        if (repo.Gate1 is { Kind: "collection" } c)
        {
            if (string.IsNullOrWhiteSpace(repo.TestBundle)) yield return $"{name}: a collection check collects a TestBundle, and none is named";
            foreach (var (field, set) in new[] { ("Run", c.Run is not null), ("Filter", c.Filter is not null), ("SdkVersion", c.SdkVersion is not null),
                         ("SdkSha512", c.SdkSha512 is not null), ("Images", c.Images is { Count: > 0 }), ("Environment", c.Environment is { Count: > 0 }) })
                if (set) yield return $"{name}: Gate1.Kind collection is lz's own check of the specs, so it takes no {field}";
            yield break;
        }
        if (repo.Gate1?.Kind == "site")
        {
            foreach (var problem in SiteCheckProblems(name, repo)) yield return problem;
            yield break;
        }
        if (repo.Gate1?.Indexed is not null) yield return $"{name}: Gate1.Indexed is for a site's check only (Gate1.Kind: site)";
        if (repo.Gate1?.ThirdPartyHosts is not null) yield return $"{name}: Gate1.ThirdPartyHosts is for a site's check only (Gate1.Kind: site)";
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
    /// A site's Gate 1 (DecoupledCd.md V-24): lz's own check of the bundles, so none of a test bundle's settings, which would
    /// say how to run repository code it never runs; and the environments whose site may be indexed, by name.
    /// </summary>
    private static IEnumerable<string> SiteCheckProblems(string name, PipelineRepositoryConfig repo)
    {
        if (repo.Class != "site")
            yield return $"{name}: Gate1.Kind site is lz's check of a site's bundles; {repo.Repo} is class '{repo.Class}'";
        var g = repo.Gate1!;
        if (repo.TestBundle is not null) yield return $"{name}: Gate1.Kind site runs no repository code, so it takes no TestBundle";
        foreach (var (field, set) in new[]
                 {
                     ("Run", g.Run is not null), ("Filter", g.Filter is not null), ("BuildRoot", g.BuildRoot is not null),
                     ("SdkVersion", g.SdkVersion is not null), ("SdkSha512", g.SdkSha512 is not null),
                     ("Images", g.Images is not null), ("Environment", g.Environment is not null),
                 })
            if (set) yield return $"{name}: Gate1.Kind site runs no repository code, so it takes no {field}";
        foreach (var env in g.Indexed ?? new())
            if (!Regex.IsMatch(env ?? "", SiteArtifacts.EnvironmentPattern))
                yield return $"{name}: Gate1.Indexed '{env}' is not an environment's name";
        foreach (var host in g.ThirdPartyHosts ?? new())
            if (!Regex.IsMatch(host ?? "", @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
                yield return $"{name}: Gate1.ThirdPartyHosts '{host}' is not a host name";
    }

    /// <summary>
    /// A site repository (DecoupledCd.md V-24): one bundle per environment, <c>site-{environment}</c>, each environment in the
    /// chain where there is one; lz's site check as its Gate 1; and no BuiltFrom or SBOM, since a Hugo build has neither.
    /// </summary>
    private static IEnumerable<string> SiteProblems(string name, PipelineRepositoryConfig repo, List<PipelineChainEntry>? chain)
    {
        if (repo.Gate1 is { } g && g.Kind != "site") yield return $"{name}: a site's Gate 1 is lz's site check (Gate1.Kind: site)";
        if (repo.BuiltFrom is not null) yield return $"{name}: a site consumes no first-party package, so it uploads no BuiltFrom";
        if (repo.Sbom is not null) yield return $"{name}: a site uploads no Sbom";
        var artifacts = repo.Artifacts ?? new();
        if (artifacts.Count == 0) yield return $"{name}: a site names at least one artifact, site-{{environment}}";
        var environments = chain?.Select(c => c.Environment).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var artifact in artifacts.Distinct(StringComparer.Ordinal))
        {
            if (artifacts.Count(a => a == artifact) > 1) yield return $"{name}: names '{artifact}' twice";
            if (SiteArtifacts.EnvironmentOf(artifact) is not { } env)
                yield return $"{name}: a site's artifact is site-{{environment}}, not '{artifact}'";
            else if (environments is { Count: > 0 } && !environments.Contains(env))
                yield return $"{name}: '{artifact}' names environment '{env}', which is not in the Chain";
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
    /// The repositories whose class no environment in the chain accepts (P5 stage A): each would be taken in by the intake
    /// and deployed nowhere. Given each environment's <c>Classes</c> as its config names them; an environment whose config is
    /// not beside this file is left out, and a build-side class, which no environment deploys, is never refused.
    /// </summary>
    public IReadOnlyList<string> UnacceptedRepositories(IReadOnlyDictionary<string, IReadOnlyList<string>?> classesByEnvironment)
        => (Repositories ?? new())
            .Where(r => r.Class is { } c && !PipelineConfig.BuildSideClasses.Contains(c)
                && classesByEnvironment.Count > 0
                && !classesByEnvironment.Values.Any(classes => classes is not { Count: > 0 } || classes.Contains(c)))
            .Select(r => $"{r.Repo} builds class '{r.Class}', which no environment in the Chain accepts (" +
                         string.Join("; ", classesByEnvironment.Select(e => $"{e.Key}: {string.Join(", ", e.Value ?? new List<string>())}")) + ")")
            .ToList();

    /// <summary>
    /// THE SITE AND THE ENVIRONMENTS THAT TAKE IT (DecoupledCd.md V-K step 5): an environment accepts class <c>site</c> exactly
    /// when a site repository builds its bundle, <c>site-{environment}</c>. A bundle for an environment that takes no site has
    /// its predecessor write site markers it refuses on every run; an environment that takes the site with no bundle built
    /// waits for one that never comes. An environment whose config is not beside this file is left out.
    /// </summary>
    public IReadOnlyList<string> SiteArtifactMismatches(IReadOnlyDictionary<string, IReadOnlyList<string>?> classesByEnvironment)
    {
        var problems = new List<string>();
        foreach (var repo in (Repositories ?? new()).Where(r => r.Class == "site"))
        {
            var built = (repo.Artifacts ?? new()).Select(SiteArtifacts.EnvironmentOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (env, classes) in classesByEnvironment)
            {
                if (classes is not { Count: > 0 }) continue;
                var takes = classes.Contains("site");
                if (takes && !built.Contains(env))
                    problems.Add($"{env} accepts class 'site', and {repo.Repo} builds no {SiteArtifacts.ArtifactFor(env)} for it");
                else if (!takes && built.Contains(env))
                    problems.Add($"{repo.Repo} builds {SiteArtifacts.ArtifactFor(env)}, and {env} does not accept class 'site'");
            }
        }
        return problems;
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
        pipeline.BuildSuffix = BuildSuffix;

        var entry = Chain?.FirstOrDefault(c => string.Equals(c.Environment, environment, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && pipeline.TargetAccountId is { } target && !string.Equals(entry.AccountId, target, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the promotion chain places '{environment}' in account {entry.AccountId}, but its systemconfig deploys " +
                $"into {target} (Pipeline.TargetAccountId). One of the two files is wrong.");

        pipeline.ChainEvidenceStore = entry?.EvidenceStore;
        pipeline.Outcomes = entry?.Outcomes ?? false;
        // THE INTAKE'S OUTCOMES LIST is the chain's first environment's, whichever environment's config planned the intake:
        // a build it refuses would have deployed there.
        pipeline.IntakeOutcomesTo = Chain is { Count: > 0 } && Chain[0].Outcomes
            ? new PipelineNeighbour(Chain[0].Environment!, Chain[0].AccountId!, Chain[0].EvidenceStore)
            : null;

        // THE NEIGHBOURS (P5): the environment before this one, whose markers it admits, and the one after, which admits its.
        var at = entry is null ? -1 : Chain!.IndexOf(entry);
        PipelineNeighbour? Neighbour(int i) => at < 0 || i < 0 || i >= Chain!.Count ? null
            : new PipelineNeighbour(Chain[i].Environment!, Chain[i].AccountId!, Chain[i].EvidenceStore);
        pipeline.Predecessor = Neighbour(at - 1);
        pipeline.Successor = Neighbour(at + 1);

        pipeline.DeployOnBuildRecord = Chain is { Count: > 0 }
            && string.Equals(Chain[0].Environment, environment, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One environment in the promotion chain.</summary>
public class PipelineChainEntry
{
    public string? Environment { get; set; }
    public string? AccountId { get; set; }

    /// <summary>
    /// The environment's evidence store, where its pass markers are written and the next environment reads them (P5 stage
    /// D). Required in a chain of more than one; the environment's deployer refuses to create any other.
    /// </summary>
    public string? EvidenceStore { get; set; }

    /// <summary>
    /// Tell this environment's outcomes list how every deploy ended (default <c>false</c>): a topic,
    /// <c>{systemkey}-{environment}-deploy-outcomes</c>, sent one message for every execution of its deployer that ends,
    /// deployed or not, by a function the execution's end invokes. In the chain's first environment the intake also sends it
    /// every build it refuses, or cannot decide, since such a build reaches no deployer.
    ///
    /// <para>NOBODY IS SUBSCRIBED BY lz, as for the alerts topic: a subscription is a person or a list, and confirming it is
    /// theirs. <c>lz bootstrapdeployer</c> reports how many the topic has and how to add one. Here, beside the chain, so the
    /// deployer that creates the topic and the intake that sends to it read one switch.</para>
    /// </summary>
    public bool Outcomes { get; set; }
}

/// <summary>A site repository's artifacts (DecoupledCd.md V-24): one bundle per environment, named <c>site-{environment}</c>.</summary>
public static class SiteArtifacts
{
    public const string Prefix = "site-";

    /// <summary>An environment's name as the site's artifacts and records spell it: lowercase, as lz's config file names do.</summary>
    public const string EnvironmentPattern = "^[a-z0-9][a-z0-9-]{0,31}$";

    /// <summary>The environment an artifact's name says it was built for; null when it is not <c>site-{environment}</c>.</summary>
    public static string? EnvironmentOf(string artifact)
        => artifact.StartsWith(Prefix, StringComparison.Ordinal) && Regex.IsMatch(artifact[Prefix.Length..], EnvironmentPattern)
            ? artifact[Prefix.Length..]
            : null;

    public static string ArtifactFor(string environment) => Prefix + environment;
}
