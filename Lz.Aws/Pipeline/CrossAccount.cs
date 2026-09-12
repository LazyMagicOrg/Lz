using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.ECR;
using Amazon.ECR.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// What crosses between the build account and a target account (DecoupledCd.md §6, M5): images, by
/// ECR replication, and the Verify function's read of build records, by a bucket policy.
///
/// <para>PURE AND CONFIG-FREE on purpose. Every function takes the names and account ids it needs as
/// values, so none of them reads <c>SystemConfig.Pipeline</c> and the byte-identical guard's allowlist
/// does not grow; the planners that already hold the config pass them in.</para>
///
/// <para>EVERYTHING HERE IS WRITTEN INTO SOMETHING SHARED. A registry has one replication
/// configuration and one registry policy, and a bucket has one policy — all replaced whole by their
/// Put calls. One command run per environment therefore MERGES, keyed by what it owns, and must leave
/// everything else exactly as it found it: the Route 53 lesson, where an UPSERT meant to add one value
/// silently deleted the one already there.</para>
/// </summary>
public static class CrossAccount
{
    private static readonly JsonSerializerOptions PolicyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Merge <paramref name="owned"/> statements into an existing policy document, replacing any
    /// statement with the same <c>Sid</c> and preserving every other statement untouched.
    ///
    /// <para>A statement WITHOUT a Sid in the existing document is preserved — it cannot be ours, since
    /// everything written here carries one — and an owned statement without a Sid is refused, since it
    /// could never be found again to be replaced.</para>
    /// </summary>
    public static string MergeBySid(string? existingPolicy, IReadOnlyList<JsonObject> owned)
    {
        var sids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in owned)
        {
            var sid = statement["Sid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sid))
                throw new InvalidOperationException(
                    "an owned statement has no Sid, so a later run could not find it to replace it; " +
                    "it would accumulate a copy per run instead.");
            if (!sids.Add(sid))
                throw new InvalidOperationException($"two owned statements share the Sid '{sid}'.");
        }

        JsonObject document;
        if (string.IsNullOrWhiteSpace(existingPolicy))
        {
            document = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray() };
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(existingPolicy);
            }
            catch (JsonException)
            {
                // What the first real apply hit: an S3 error document handed over as the policy text.
                var start = existingPolicy.TrimStart();
                throw new InvalidOperationException(
                    $"the existing policy is not JSON (it begins '{start[..Math.Min(start.Length, 40)]}'); refusing to write a " +
                    "policy over something that could not be read.");
            }

            document = parsed as JsonObject
                ?? throw new InvalidOperationException("the existing policy is not a JSON object; refusing to overwrite it.");
        }

        // A single statement may legally be an object rather than an array; normalise so it survives.
        var existing = document["Statement"] switch
        {
            JsonArray array => array.Select(s => s?.DeepClone()).ToList(),
            JsonObject single => new List<JsonNode?> { single.DeepClone() },
            null => new List<JsonNode?>(),
            _ => throw new InvalidOperationException("the existing policy's Statement is neither an object nor an array; refusing to overwrite it."),
        };

        var kept = existing
            .Where(s => s is not JsonObject o || o["Sid"]?.GetValue<string>() is not { } sid || !sids.Contains(sid))
            .ToList();

        document["Statement"] = new JsonArray(kept.Concat(owned.Select(o => (JsonNode?)o.DeepClone())).ToArray());
        return document.ToJsonString(PolicyJson);
    }

    /// <summary>
    /// What a <c>GetBucketPolicy</c> response says about the policy that exists: its text, or null for
    /// "there is none".
    ///
    /// <para>MEASURED, AND NOT WHAT THE CODE FIRST ASSUMED. With AWSSDK.S3 4.0.103.2, a bucket with no
    /// policy does NOT raise <c>NoSuchBucketPolicy</c>: the call returns normally with status 404 and
    /// the S3 error DOCUMENT in <c>Policy</c> — <c>&lt;?xml …&gt;&lt;Error&gt;&lt;Code&gt;NoSuchBucketPolicy…</c>.
    /// The first apply caught the exception that never came, handed the XML to the merge as a policy,
    /// and died parsing it — before writing anything, which is the only reason that was harmless.</para>
    ///
    /// <para>ONLY THAT ONE ABSENCE MEANS NULL. A 404 is also what a missing bucket returns, and a 403 is
    /// an access denial; treating either as "no policy yet" would write a policy over a document nobody
    /// could read. Anything but 200, or a 404 naming <c>NoSuchBucketPolicy</c>, is refused.</para>
    /// </summary>
    public static string? ExistingBucketPolicy(System.Net.HttpStatusCode status, string? body)
    {
        if (status == System.Net.HttpStatusCode.OK)
            return body;

        if (status == System.Net.HttpStatusCode.NotFound
            && body != null && body.Contains("<Code>NoSuchBucketPolicy</Code>", StringComparison.Ordinal))
            return null;

        throw new InvalidOperationException(
            $"reading the bucket policy returned {(int)status} {status}" +
            (body is { Length: > 0 } ? $" ({body.TrimStart()[..Math.Min(body.TrimStart().Length, 80)]})" : "") +
            "; refusing to write a policy over one that could not be read.");
    }

    /// <summary>
    /// The build-record store's grant to ONE target environment's Verify function: read image records,
    /// and list under <c>image/</c> so that a missing record is a 404 rather than a misleading 403.
    ///
    /// <para>THE PRINCIPAL IS THE ACCOUNT, NARROWED BY <c>aws:PrincipalArn</c>, not the role's ARN.
    /// A resource policy that names a role directly stores that role's unique id, so deleting and
    /// recreating the role — which re-running a bootstrap after a teardown does — would leave the grant
    /// pointing at a role that no longer exists, and Verify would fail with an access denial nothing
    /// explains. The condition matches by name and survives that.</para>
    /// </summary>
    public static IReadOnlyList<JsonObject> BuildRecordReadGrant(
        string buildRecordStore, string environment, string targetAccountId, string verifyRoleName)
    {
        RequireAccount(targetAccountId, nameof(targetAccountId));

        JsonObject Principal() => new() { ["AWS"] = $"arn:aws:iam::{targetAccountId}:root" };
        JsonObject OnlyVerify() => new()
        {
            ["ArnEquals"] = new JsonObject
            {
                ["aws:PrincipalArn"] = $"arn:aws:iam::{targetAccountId}:role/{verifyRoleName}",
            },
        };

        return new[]
        {
            new JsonObject
            {
                ["Sid"] = BuildRecordReadSid(environment),
                ["Effect"] = "Allow",
                ["Principal"] = Principal(),
                ["Action"] = "s3:GetObject",
                ["Resource"] = $"arn:aws:s3:::{buildRecordStore}/image/*",
                ["Condition"] = OnlyVerify(),
            },
            new JsonObject
            {
                ["Sid"] = BuildRecordListSid(environment),
                ["Effect"] = "Allow",
                ["Principal"] = Principal(),
                ["Action"] = "s3:ListBucket",
                ["Resource"] = $"arn:aws:s3:::{buildRecordStore}",
                ["Condition"] = new JsonObject
                {
                    ["ArnEquals"] = OnlyVerify()["ArnEquals"]!.DeepClone(),
                    ["StringLike"] = new JsonObject { ["s3:prefix"] = "image/*" },
                },
            },
        };
    }

    public static string BuildRecordReadSid(string environment) => $"DeployerReadsImageRecords{Suffix(environment)}";

    public static string BuildRecordListSid(string environment) => $"DeployerListsImageRecords{Suffix(environment)}";

    /// <summary>
    /// The TARGET registry's permission for the build account to replicate into it.
    ///
    /// <para><c>ecr:ReplicateImage</c> ONLY — never <c>ecr:CreateRepository</c>. AWS documents both,
    /// and says that without the second "you need to create repositories with the same name within your
    /// account". That is exactly the point: a repository replication creates for itself has none of this
    /// account's settings — no immutable tags, since "repository settings aren't replicated" — and DecoupledCd
    /// P6 calls a mutable replication target a defect. Without the grant, an image bound for a repository
    /// the target has not hardened is refused instead of landing somewhere unhardened.</para>
    ///
    /// <para>SCOPED TO THE NAMED REPOSITORIES, as AWS's own <c>repository/prod-*</c> example scopes it.
    /// With <c>repository/*</c>, anyone who can write the build account's replication rules could
    /// replicate into ANY same-named repository here — including the ones <c>deploycontainer</c> pushes
    /// to today, whose tags are mutable.</para>
    /// </summary>
    public static JsonObject ReplicationPermission(
        string artifactAccountId, string region, string targetAccountId, IReadOnlyList<string> repositories)
    {
        RequireAccount(artifactAccountId, nameof(artifactAccountId));
        RequireAccount(targetAccountId, nameof(targetAccountId));
        if (repositories is not { Count: > 0 })
            throw new InvalidOperationException("replication permission needs at least one repository to scope to.");

        return new JsonObject
        {
            ["Sid"] = ReplicationSid,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{artifactAccountId}:root" },
            ["Action"] = "ecr:ReplicateImage",
            ["Resource"] = new JsonArray(repositories
                .Select(r => (JsonNode?)$"arn:aws:ecr:{region}:{targetAccountId}:repository/{r}")
                .ToArray()),
        };
    }

    public const string ReplicationSid = "LzPipelineReplicatesFromTheBuildAccount";

    /// <summary>
    /// The build registry's replication rule for ONE target account: the named repositories, to that
    /// account's registry in the same region.
    ///
    /// <para>ONE PREFIX FILTER PER REPOSITORY NAME rather than the system prefix, because PREFIX_MATCH
    /// is ECR's only filter type: this is narrower than the system prefix but still selects any
    /// repository whose name EXTENDS this one (<c>scu-4df6-b9c6-aiphost-worker</c> would match). What
    /// keeps replication exact is the TARGET: its registry policy names exact repository ARNs and
    /// withholds ecr:CreateRepository, so an over-matched repository is refused there.</para>
    /// </summary>
    public static ReplicationRule ReplicationRule(string region, string targetAccountId, IReadOnlyList<string> repositories)
    {
        RequireAccount(targetAccountId, nameof(targetAccountId));
        if (repositories is not { Count: > 0 })
            throw new InvalidOperationException("a replication rule needs at least one repository.");

        return new ReplicationRule
        {
            Destinations = new List<ReplicationDestination> { new() { Region = region, RegistryId = targetAccountId } },
            RepositoryFilters = repositories
                .Distinct(StringComparer.Ordinal)
                .Select(r => new RepositoryFilter { Filter = r, FilterType = RepositoryFilterType.PREFIX_MATCH })
                .ToList(),
        };
    }

    /// <summary>
    /// The registry's replication configuration with <paramref name="ours"/> in place of whatever
    /// previously replicated to the same destination — and every OTHER destination left exactly as it
    /// was, including one that shares a rule with ours, which is split out rather than dropped.
    /// </summary>
    public static ReplicationConfiguration MergeReplication(ReplicationConfiguration? existing, ReplicationRule ours)
    {
        var destination = ours.Destinations.Single();
        bool IsOurs(ReplicationDestination d) =>
            string.Equals(d.RegistryId, destination.RegistryId, StringComparison.Ordinal)
            && string.Equals(d.Region, destination.Region, StringComparison.Ordinal);

        var rules = new List<ReplicationRule>();
        foreach (var rule in existing?.Rules ?? new List<ReplicationRule>())
        {
            // SDK v4: a collection with no members is null.
            var others = (rule.Destinations ?? new List<ReplicationDestination>()).Where(d => !IsOurs(d)).ToList();
            if (others.Count == 0) continue;

            rules.Add(new ReplicationRule { Destinations = others, RepositoryFilters = rule.RepositoryFilters });
        }

        rules.Add(ours);

        // AWS's limits: 25 rules, 25 unique destinations, 100 filters per rule.
        if (rules.Count > 25)
            throw new InvalidOperationException($"the merged replication configuration has {rules.Count} rules; ECR allows 25.");
        if (rules.SelectMany(r => r.Destinations).Select(d => (d.RegistryId, d.Region)).Distinct().Count() > 25)
            throw new InvalidOperationException("the merged replication configuration exceeds ECR's 25 unique destinations.");
        if (ours.RepositoryFilters.Count > 100)
            throw new InvalidOperationException("the replication rule has more than ECR's 100 filters.");

        return new ReplicationConfiguration { Rules = rules };
    }

    /// <summary>
    /// A command that runs in a TARGET account refuses a profile that resolves to any other account.
    /// Stronger than refusing only the build account: it also catches prod's profile pointed at dev's
    /// config, which would put dev's deployer — and dev's replication grant — into prod.
    /// </summary>
    public static void RequireTargetAccount(string? configuredTargetAccountId, string resolvedAccountId, string command)
    {
        if (string.IsNullOrWhiteSpace(configuredTargetAccountId))
            throw new InvalidOperationException(
                $"lz {command} refuses: Pipeline.TargetAccountId is not set, so there is nothing to check this " +
                "profile's account against — and this command writes a role that can roll services and a " +
                "registry policy that admits the build account. Name the account this environment deploys into.");

        if (!string.Equals(configuredTargetAccountId, resolvedAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"lz {command} refuses: this profile resolves to {resolvedAccountId}, but Pipeline.TargetAccountId " +
                $"is {configuredTargetAccountId}. Pass this environment's own profile.");
    }

    private static string Suffix(string environment)
    {
        if (string.IsNullOrWhiteSpace(environment) || !environment.All(char.IsLetterOrDigit))
            throw new InvalidOperationException(
                $"environment '{environment}' cannot be part of a policy statement id; Sids are alphanumeric.");
        return char.ToUpperInvariant(environment[0]) + environment[1..];
    }

    private static void RequireAccount(string accountId, string name)
    {
        if (accountId is null || accountId.Length != 12 || !accountId.All(char.IsAsciiDigit))
            throw new InvalidOperationException($"{name} '{accountId}' is not a 12-digit AWS account id.");
    }
}

/// <summary>
/// One repository's hardening, shared by both registries — the build account's, where images are
/// pushed, and each target's, where they replicate to. "Repository settings aren't replicated", so a
/// target that did not apply these itself would hold the same images with none of the same guarantees.
/// </summary>
public static class EcrRepositoryHardening
{
    /// <summary>
    /// The lifecycle policy: expire UNTAGGED images after <paramref name="untaggedDays"/> days, and never
    /// select a tagged one.
    ///
    /// <para>SIGNATURES ARE UNTAGGED MANIFESTS, which made this rule look dangerous — would it expire the
    /// signature of an image still in use, two weeks after push, and turn every rollback target into an
    /// unsigned image the hook refuses? AWS documents that it does not: "reference artifacts that refer to
    /// an active image are protected from deletion by LCP rules until their subject image is deleted"
    /// (AWS Open Source Blog, OCI 1.1 support in ECR). Documented, not measured.</para>
    /// </summary>
    public static string LifecyclePolicy(int untaggedDays) => JsonSerializer.Serialize(new
    {
        rules = new[]
        {
            new
            {
                rulePriority = 1,
                description =
                    $"Expire untagged images after {untaggedDays} days. Tagged images are never " +
                    "selected: under immutable tags they are the deployable identities, and a " +
                    "deploy request may still name any of them.",
                selection = new
                {
                    tagStatus = "untagged",
                    countType = "sinceImagePushed",
                    countUnit = "days",
                    countNumber = untaggedDays,
                },
                action = new { type = "expire" },
            },
        },
    });

    /// <summary>Create the repository if it is absent, and (re)apply every setting that can be changed.</summary>
    public static async Task EnsureAsync(IAmazonECR ecr, string name, int untaggedDays)
    {
        try
        {
            var found = await ecr.DescribeRepositoriesAsync(new DescribeRepositoriesRequest
            {
                RepositoryNames = new List<string> { name },
            });

            // ENCRYPTION CANNOT BE CHANGED AFTER CREATION. A repository that exists with something else
            // is reported rather than silently accepted, since every other setting below would be
            // re-applied and make it look hardened.
            var encryption = found.Repositories?.FirstOrDefault()?.EncryptionConfiguration?.EncryptionType;
            if (encryption != null && encryption != EncryptionType.AES256)
                throw new InvalidOperationException(
                    $"repository '{name}' already exists with {encryption.Value} encryption, which cannot be " +
                    "changed in place. Refusing to adopt it as a pipeline repository.");

            Console.WriteLine($"  repository '{name}' already exists.");
        }
        catch (RepositoryNotFoundException)
        {
            await ecr.CreateRepositoryAsync(new CreateRepositoryRequest
            {
                RepositoryName = name,
                // AES256 rather than a customer-managed key: ECR encrypts at rest either way, and a
                // CMK adds a key to rotate, grant across accounts and pay for. Named explicitly
                // rather than left to the default so the choice is visible in the plan.
                EncryptionConfiguration = new EncryptionConfiguration { EncryptionType = EncryptionType.AES256 },
                ImageScanningConfiguration = new ImageScanningConfiguration { ScanOnPush = true },
                ImageTagMutability = ImageTagMutability.IMMUTABLE,
            });
            Console.WriteLine($"  repository '{name}' created.");
        }

        // IMMUTABLE TAGS, applied every run rather than only on create. This is what makes "nothing
        // deploys a tag" enforceable rather than a convention: a mutable tag is a pointer anyone
        // with push rights can move to different content, and the whole design rests on an identity
        // that cannot be repointed.
        await ecr.PutImageTagMutabilityAsync(new PutImageTagMutabilityRequest
        {
            RepositoryName = name,
            ImageTagMutability = ImageTagMutability.IMMUTABLE,
        });

        // SCAN-ON-PUSH, THE DEPRECATED WAY, AND KEPT ON PURPOSE. AWS marks PutImageScanningConfiguration
        // deprecated "in favor of specifying the image scanning configuration at the registry level" — but
        // this is the setting MEASURED to work: on 2026-09-12, with the registry-level rules empty, it
        // scanned every push to the build registry and every replica arriving in dev. The registry rule
        // (EcrRegistryScanning) is applied beside it, and this goes once that rule is seen scanning a
        // repository on its own.
        await ecr.PutImageScanningConfigurationAsync(new PutImageScanningConfigurationRequest
        {
            RepositoryName = name,
            ImageScanningConfiguration = new ImageScanningConfiguration { ScanOnPush = true },
        });

        // THE LIFECYCLE POLICY IS DELIBERATELY NARROW. §8.4 wants "keep every identity named in any
        // request of the last N deploys", and ECR lifecycle rules cannot express that — they select
        // by age and count, and know nothing about the request store. So this expires only UNTAGGED
        // images and leaves every tagged image alone. Erring toward keeping too much: a deleted digest
        // breaks a rollback target, and storage is cheap next to that.
        await ecr.PutLifecyclePolicyAsync(new PutLifecyclePolicyRequest
        {
            RepositoryName = name,
            LifecyclePolicyText = LifecyclePolicy(untaggedDays),
        });

        Console.WriteLine($"      immutable tags, AES256, scan-on-push, untagged expire {untaggedDays}d.");
    }
}

/// <summary>
/// The registry's scan-on-push rule for the pipeline's repositories (DecoupledCd.md §14.1).
///
/// <para>AT THE REGISTRY, which is where AWS now puts it: the repository-level API is deprecated "in favor
/// of specifying the image scanning configuration at the registry level", and under basic scanning "any
/// repositories that don't match a scan on push filter are set to the manual scan frequency". Both
/// registries carried <c>{scanType: BASIC, rules: []}</c> — no filter at all — and scanned every image
/// anyway, through the deprecated repository setting, which <see cref="EcrRepositoryHardening"/> still
/// applies. This rule is the documented successor, written beside it rather than instead of it.</para>
///
/// <para>MERGED, NEVER OVERWRITTEN. A registry has one scanning configuration of at most two rules, and a
/// rule another system or a person added is not this command's to drop.</para>
///
/// <para>REPLICATION COUNTS AS A PUSH, measured rather than documented: on 2026-09-12 the replica of
/// <c>sha256:1303a913…</c> arrived in dev at 15:13:24 and its scan completed at 15:14:47, under the
/// repository setting.</para>
/// </summary>
public static class EcrRegistryScanning
{
    /// <summary>ECR's limits: a registry scanning configuration holds at most 2 rules, a rule at most 100 filters.</summary>
    public const int MaxRules = 2;
    public const int MaxFiltersPerRule = 100;

    // ECR's documented pattern for a scanning filter.
    private static readonly Regex FilterPattern = new(@"^[a-z0-9*](?:[._\-/a-z0-9*]?[a-z0-9*]+)*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Does an ECR scanning filter select a repository? As AWS documents it, which is NOT prefix or exact
    /// matching: "a filter with no wildcard will match all repository names that contain the filter", and
    /// "a filter with a wildcard (*) matches on any repository name where the wildcard replaces zero or
    /// more characters". So a repository's own name, used as its filter, also selects every repository
    /// whose name contains it — harmless for scanning, which only adds scans, and unavoidable: there is
    /// no exact filter.
    /// </summary>
    public static bool FilterSelects(string filter, string repository)
    {
        if (!filter.Contains('*'))
            return repository.Contains(filter, StringComparison.Ordinal);

        var pattern = "^" + string.Join(".*", filter.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(repository, pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>The configuration to write — null when nothing changes — and what the merge found.</summary>
    public sealed record Merge(
        RegistryScanningConfiguration? ToWrite, IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyCovered, int RulesPreserved);

    /// <summary>
    /// The registry's scanning configuration with every one of <paramref name="repositories"/> selected by
    /// a scan-on-push filter, and every existing rule and filter kept.
    ///
    /// <para>ENHANCED SCANNING IS REFUSED, not converted. Verify reads basic scan results, and AWS warns that
    /// "switching between Enhanced scanning and Basic scanning will cause previously established scans to
    /// no longer be available" — a change to every repository in the registry that this command has no
    /// business making.</para>
    /// </summary>
    public static Merge MergeScanOnPush(RegistryScanningConfiguration? existing, IReadOnlyList<string> repositories)
    {
        if (repositories is not { Count: > 0 })
            throw new InvalidOperationException("a scan-on-push rule needs at least one repository.");

        var scanType = existing?.ScanType?.Value ?? ScanType.BASIC.Value;
        if (scanType != ScanType.BASIC.Value)
            throw new InvalidOperationException(
                $"this registry uses {scanType} scanning. The pipeline's Verify reads basic scan results, and " +
                "switching a registry's scan type makes the scans it already has unavailable, for every " +
                "repository in it. Refusing to change it; the pipeline is not built for enhanced scanning.");

        // COPIES, so the caller's configuration is never mutated. SDK v4: a collection with no members is null.
        var rules = (existing?.Rules ?? new List<RegistryScanningRule>())
            .Select(r => new RegistryScanningRule
            {
                ScanFrequency = r.ScanFrequency,
                RepositoryFilters = (r.RepositoryFilters ?? new List<ScanningRepositoryFilter>())
                    .Select(f => new ScanningRepositoryFilter { Filter = f.Filter, FilterType = f.FilterType })
                    .ToList(),
            })
            .ToList();
        var preserved = rules.Count;

        var onPush = rules.FirstOrDefault(r => r.ScanFrequency?.Value == ScanFrequency.SCAN_ON_PUSH.Value);
        var wanted = repositories.Distinct(StringComparer.Ordinal).ToList();
        var covered = wanted
            .Where(repo => onPush?.RepositoryFilters.Any(f => f.Filter != null && FilterSelects(f.Filter, repo)) == true)
            .ToList();
        var added = wanted.Except(covered, StringComparer.Ordinal).ToList();

        if (added.Count == 0)
            return new Merge(null, added, covered, preserved);

        foreach (var repo in added.Where(r => r.Length > 255 || !FilterPattern.IsMatch(r)))
            throw new InvalidOperationException(
                $"'{repo}' cannot be written as an ECR scanning filter (1-255 characters matching {FilterPattern}).");

        if (onPush is null)
        {
            if (rules.Count >= MaxRules)
                throw new InvalidOperationException(
                    $"the registry already has {rules.Count} scanning rules, none of them scan-on-push, and ECR allows " +
                    $"{MaxRules}. Refusing to replace one; add the pipeline's repositories to a rule by hand.");

            onPush = new RegistryScanningRule { ScanFrequency = ScanFrequency.SCAN_ON_PUSH, RepositoryFilters = new List<ScanningRepositoryFilter>() };
            rules.Add(onPush);
        }

        onPush.RepositoryFilters.AddRange(added.Select(repo =>
            new ScanningRepositoryFilter { Filter = repo, FilterType = ScanningRepositoryFilterType.WILDCARD }));

        if (onPush.RepositoryFilters.Count > MaxFiltersPerRule)
            throw new InvalidOperationException(
                $"the scan-on-push rule would hold {onPush.RepositoryFilters.Count} filters; ECR allows {MaxFiltersPerRule}.");

        return new Merge(new RegistryScanningConfiguration { ScanType = ScanType.BASIC, Rules = rules }, added, covered, preserved);
    }

    /// <summary>Read the registry's scanning configuration, merge the pipeline's repositories in, write it back if it changed.</summary>
    public static async Task ApplyAsync(IAmazonECR ecr, IReadOnlyList<string> repositories)
    {
        var current = await ecr.GetRegistryScanningConfigurationAsync(new GetRegistryScanningConfigurationRequest());
        if (current.HttpStatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"reading the registry scanning configuration returned {(int)current.HttpStatusCode}; refusing to write over it.");

        var merge = MergeScanOnPush(current.ScanningConfiguration, repositories);
        if (merge.ToWrite is null)
        {
            Console.WriteLine($"  registry scanning: scan-on-push already selects {string.Join(", ", merge.AlreadyCovered)}.");
            return;
        }

        await ecr.PutRegistryScanningConfigurationAsync(new PutRegistryScanningConfigurationRequest
        {
            ScanType = merge.ToWrite.ScanType,
            Rules = merge.ToWrite.Rules,
        });
        Console.WriteLine(
            $"  registry scanning: scan-on-push added for {string.Join(", ", merge.Added)}; " +
            $"{merge.RulesPreserved} existing rule(s) kept.");
    }
}
