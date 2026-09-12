using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            document = JsonNode.Parse(existingPolicy) as JsonObject
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
    /// <para>ONE PREFIX FILTER PER REPOSITORY NAME rather than the system prefix: only repositories the
    /// pipeline created replicate, and a repository someone adds later under the same prefix does not
    /// start flowing into every target account unannounced.</para>
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
