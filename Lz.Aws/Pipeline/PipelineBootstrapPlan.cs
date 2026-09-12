using System.Text.Encodings.Web;
using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>A versioned S3 store the pipeline owns. See DecoupledCd.md §4.3 and §6.</summary>
/// <param name="Name">Globally-unique bucket name.</param>
/// <param name="Purpose">What it holds, for the plan report.</param>
/// <param name="WriterPrefixes">
/// The per-repository prefixes a writer role may PutObject under. Nothing may write outside these.
/// </param>
public sealed record PipelineStore(string Name, string Purpose, IReadOnlyList<string> WriterPrefixes);

/// <summary>A GitHub-assumable role. See the trust-boundary table in DecoupledCd.md §3.</summary>
/// <param name="Name">IAM role name.</param>
/// <param name="Repo">The GitHub repository allowed to assume it, <c>owner/name</c>.</param>
/// <param name="Class">The artifact class it builds.</param>
/// <param name="TrustPolicy">The assume-role policy document, JSON.</param>
/// <param name="SigningProfile">
/// For image roles only: the signing profile that role's pushes are signed under. Null for bundle
/// roles, because bundles get no registry signature (§4.2).
/// </param>
/// <param name="PermissionPolicy">
/// What the role may DO once assumed, JSON. Push-only by construction: no delete action of any
/// kind appears in either shape, which is what makes the stores append-only to GitHub.
/// </param>
/// <param name="EcrRepositories">
/// For image roles: the ECR repositories this role may push to, already named under the
/// configured mode. Empty for bundle roles, which have no registry.
/// </param>
public sealed record PipelineRole(
    string Name, string Repo, string Class, string TrustPolicy, string? SigningProfile,
    string PermissionPolicy, IReadOnlyList<string> EcrRepositories);

/// <summary>Everything <c>lz bootstrappipeline</c> would create, decided before anything is called.</summary>
public sealed record PipelineBootstrapPlan(
    string SystemKey,
    string Region,
    string? ArtifactAccountId,
    IReadOnlyList<PipelineStore> Stores,
    IReadOnlyList<PipelineRole> Roles,
    IReadOnlyList<string> EcrRepositories,
    string OidcProviderUrl,
    IReadOnlyList<string> DeniedActions);

/// <summary>
/// Decides what <c>lz bootstrappipeline</c> creates, as a pure function of config.
///
/// <para>WHY A PLANNER AND NOT JUST A COMMAND. Everything here is a NAME or a POLICY DOCUMENT, and
/// both are the kind of thing that is wrong in a way no test of the applier would catch — a trust
/// policy that matches nothing fails at the first GitHub run, weeks later, as
/// <c>Not authorized to perform sts:AssumeRoleWithWebIdentity</c>. Separating the decision from the
/// AWS calls means the decision can be asserted exactly, and printed for review before a single
/// resource exists. It follows the shape this library already uses for decisions worth pinning —
/// <c>ImagePinPolicy</c>, <c>BucketDurabilityPolicy</c>, <c>DeploymentAlarmPolicy</c>.</para>
///
/// <para>NOTHING HERE CALLS AWS. Planning a bootstrap for an account that does not exist yet is
/// valid and useful.</para>
/// </summary>
public static class PipelineBootstrapPlanner
{
    /// <summary>GitHub's OIDC issuer. One provider per account, shared by every role below.</summary>
    public const string OidcProvider = "token.actions.githubusercontent.com";

    /// <summary>
    /// How policy documents are written. RELAXED ESCAPING is deliberate: the default encoder is
    /// HTML-safe and escapes angle brackets to their unicode form, which matters twice here. A dry
    /// run PRINTS these documents for a human to read before they exist, and escaped placeholders
    /// are unreadable; and it already caused a real defect — the applier substituted the OIDC
    /// provider ARN by replacing a bracketed placeholder that the serializer had escaped, so the
    /// match silently never happened and the role would have been created trusting a literal
    /// placeholder. These are IAM documents, never HTML.
    /// </summary>
    private static readonly JsonSerializerOptions PolicyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The actions deployer roles are explicitly DENIED, so a config bundle can change what the
    /// deployer deploys but never the deployer itself (DecoupledCd.md §5.5).
    /// </summary>
    public static readonly string[] SelfRewriteDenied =
    {
        "states:UpdateStateMachine",
        "events:PutRule",
        "events:PutTargets",
        "scheduler:*",
        "iam:*Policy*",
    };

    /// <summary>
    /// Build the plan, or throw with a message naming what is missing.
    /// </summary>
    /// <param name="accountId">
    /// The build account, for the ARNs in permission policies. Optional: when null the ARNs carry a
    /// visible placeholder, so a plan can be printed and reviewed for an account nobody has logged
    /// into. The applier passes the real id from <c>sts:GetCallerIdentity</c>.
    /// </param>
    public static PipelineBootstrapPlan Plan(SystemConfig config, string? accountId = null)
    {
        var p = config.Pipeline;

        // GATED, and this is the refusal DecoupledCd.md §8 item 6 asks for by name. Bootstrapping a
        // pipeline for a system that has not opted in would create roles GitHub can assume against
        // an account whose owner never asked for them.
        if (p is not { Enabled: true })
            throw new InvalidOperationException(
                "lz bootstrappipeline refuses: this config has no `Pipeline:` block with " +
                "`Enabled: true`. The command creates IAM roles that GitHub can assume, S3 stores " +
                "and signing profiles; it will not do that for a system that has not opted in. Add " +
                "the block (see PipelineConfig) and re-run.");

        if (p.Repositories is not { Count: > 0 })
            throw new InvalidOperationException(
                "lz bootstrappipeline refuses: Pipeline.Repositories is empty. Every entry becomes " +
                "a role GitHub can assume, so the list IS the trust boundary — an empty one would " +
                "bootstrap stores nothing can write to. Name the building repositories and the " +
                "class each produces.");

        var sk = RequireNonEmpty(config.SystemKey, nameof(config.SystemKey));
        var region = RequireNonEmpty(config.Region, nameof(config.Region));
        var suffix = RequireNonEmpty(config.SystemSuffix, nameof(config.SystemSuffix));

        var repos = p.Repositories!;

        // Store names first: the roles' permission policies name the stores, so they cannot be
        // built until the names exist.
        var artifacts = $"{sk}-artifacts-{suffix}";
        var buildRecords = $"{sk}-build-records-{suffix}";
        var requests = $"{sk}-deploy-requests-{suffix}";

        var acct = accountId ?? "<build-account-id>";
        var roles = repos
            .Select(r => BuildRole(config, sk, region, acct, artifacts, buildRecords, r))
            .ToList();

        var ecrRepositories = roles.SelectMany(r => r.EcrRepositories).Distinct().ToList();

        // PREFIXES ARE PER REPOSITORY, which is what makes "push-only" mean something: a role holds
        // PutObject on its own prefix and nothing else, so one compromised build repository cannot
        // overwrite another's artifacts. Derived from the repository NAME rather than the class,
        // because two repositories can produce the same class (SellerApp and AdminApp are both
        // `client`) and must not share a prefix.
        var prefixes = repos
            .Select(r => BuildRecordFormat.PrefixFor(
                RequireNonEmpty(r.Class, "Pipeline.Repositories[].Class"),
                RequireNonEmpty(r.Repo, "Pipeline.Repositories[].Repo")))
            .ToList();

        // NO ENVIRONMENT IN THESE NAMES, deliberately. One build account holds artifacts that are
        // PROMOTED between environments; an artifact that carried `-dev-` in the name of the bucket
        // it lives in could not be the same artifact in prod. Same reasoning as the registry's
        // `neutral` naming (§8.5).
        var stores = new List<PipelineStore>
        {
            new(artifacts,
                "bundles, written once and named by object version id", prefixes),
            new(buildRecords,
                "one build record per artifact — the uniform trigger (§4.3)", prefixes),
            new(requests,
                "deploy requests: what may enter an environment (§6)",
                // EMPTY ON PURPOSE. The request store is written by the DEPLOYER, never by GitHub —
                // "GitHub writes build records; the deployer writes deploy requests. That separation
                // is the whole trust boundary" (§4.3). A GitHub-assumable prefix here would hand
                // GitHub admission control over prod, which is what this design exists to prevent.
                Array.Empty<string>()),
        };

        return new PipelineBootstrapPlan(
            sk, region, p.ArtifactAccountId, stores, roles, ecrRepositories, OidcProvider,
            SelfRewriteDenied);
    }

    /// <summary>
    /// The S3 key prefix a repository's writer role owns, <c>{class}/{repo}/</c>. Delegates to
    /// <see cref="BuildRecordFormat.PrefixFor"/> so the prefix a role is GRANTED and the prefix a
    /// record is WRITTEN to are one definition — two that agreed today would drift, and the failure
    /// would be an AccessDenied at the first build rather than anything naming the cause.
    /// </summary>
    public static string PrefixFor(string cls, string repo) => BuildRecordFormat.PrefixFor(cls, repo);

    private static PipelineRole BuildRole(
        SystemConfig config, string sk, string region, string accountId, string artifacts,
        string buildRecords, PipelineRepositoryConfig r)
    {
        var repo = RequireNonEmpty(r.Repo, "Pipeline.Repositories[].Repo");
        var cls = RequireNonEmpty(r.Class, "Pipeline.Repositories[].Class");

        if (!PipelineConfig.KnownClasses.Contains(cls))
            throw new InvalidOperationException(
                $"Pipeline.Repositories names class '{cls}' for {repo}, which is not an artifact " +
                "class. Valid: " + string.Join(", ", PipelineConfig.KnownClasses) + ".");

        if (!repo.Contains('/'))
            throw new InvalidOperationException(
                $"Pipeline.Repositories entry '{repo}' is not owner/name. The OIDC trust policy " +
                "matches on what GitHub puts in the token, so it needs GitHub's own spelling — " +
                "note the repository name may differ from the workspace folder.");

        // An IMAGE role pushes to ECR and its pushes are signed; every other class writes a bundle
        // to S3 and gets no signing profile, because bundles carry no registry signature (§4.2).
        var kind = cls == "image" ? "build" : "bundle";
        var slug = SlugFor(repo);
        var profile = cls == "image" ? $"{sk}_build_ci_{slug.Replace('-', '_')}" : null;
        var prefix = BuildRecordFormat.PrefixFor(cls, repo);

        // Naming the artifacts is what lets the ECR grant be exact instead of `{sk}-*`.
        if (cls == "image" && r.Artifacts is not { Count: > 0 })
            throw new InvalidOperationException(
                $"Pipeline.Repositories entry '{repo}' builds class 'image' but names no "+
                "Artifacts. One ECR repository is created per artifact, and naming them is what "+
                "scopes this role's push permission to the repositories it actually produces "+
                "rather than to every repository the system will ever have. Add e.g. "+
                "`Artifacts: [aiphost]`.");

        var ecrRepos = cls == "image"
            ? r.Artifacts!.Select(a => EcrRepositoryNaming.For(config, a)).ToList()
            : new List<string>();

        return new PipelineRole(
            Name: $"{sk}-{kind}-ci-{slug}",
            Repo: repo,
            Class: cls,
            TrustPolicy: TrustPolicyFor(repo),
            SigningProfile: profile,
            PermissionPolicy: PermissionPolicyFor(
                region, accountId, artifacts, buildRecords, prefix, ecrRepos, profile),
            EcrRepositories: ecrRepos);
    }

    /// <summary>
    /// What one role may do once assumed.
    ///
    /// <para>PUSH-ONLY BY CONSTRUCTION: no delete action of any kind appears in either shape. That
    /// is what makes the stores append-only to GitHub — not Object Lock, which does not stop a
    /// writer putting a NEW current version at an existing key either. Immutability of a written
    /// record comes from the conditional write; durability from versioning plus replication into an
    /// account GitHub cannot reach.</para>
    ///
    /// <para>EVERY ROLE WRITES A BUILD RECORD, including image roles. §3's trust-boundary table
    /// lists only ECR and signer actions for <c>{sk}-build-ci</c>, which would leave an image build
    /// unable to write the record §4.1 requires of every build — so the build-record grant is here
    /// for both shapes. Noted as a gap in that table rather than silently diverging from it.</para>
    ///
    /// <para>Both S3 grants are scoped to this repository's own <c>{class}/{repo}/</c> prefix, so a
    /// compromised build repository cannot write another's artifacts or records. Nothing grants any
    /// access to the REQUEST store: the deployer writes those, and GitHub holding admission control
    /// over prod is the thing this design exists to prevent.</para>
    /// </summary>
    private static string PermissionPolicyFor(
        string region, string accountId, string artifacts, string buildRecords, string prefix,
        IReadOnlyList<string> ecrRepositories, string? signingProfile)
    {
        var statements = new List<object>
        {
            new
            {
                Sid = "WriteItsOwnBuildRecords",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{buildRecords}/{prefix}*",
            },
        };

        if (signingProfile is null)
        {
            statements.Add(new
            {
                Sid = "WriteItsOwnBundles",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{artifacts}/{prefix}*",
            });
        }
        else
        {
            // ECR's documented single-repository push policy: the layer-upload actions and PutImage
            // on the repository, plus GetAuthorizationToken on "*" — that action takes no resource,
            // so it cannot be narrowed and is granted alone rather than bundled with the writes.
            statements.Add(new
            {
                Sid = "PushToItsOwnRepositories",
                Effect = "Allow",
                Action = new[]
                {
                    "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload",
                    "ecr:BatchCheckLayerAvailability", "ecr:PutImage",
                },
                // EXACTLY the repositories this repo produces — not `{sk}-*`, which would let one
                // build repository push to every repository the system will ever have.
                Resource = ecrRepositories
                    .Select(n => $"arn:aws:ecr:{region}:{accountId}:repository/{n}").ToArray(),
            });
            statements.Add(new
            {
                Sid = "EcrAuthTokenTakesNoResource",
                Effect = "Allow",
                Action = new[] { "ecr:GetAuthorizationToken" },
                Resource = "*",
            });
            statements.Add(new
            {
                Sid = "SignItsOwnPushes",
                Effect = "Allow",
                Action = new[] { "signer:SignPayload" },
                Resource = $"arn:aws:signer:{region}:{accountId}:/signing-profiles/{signingProfile}",
            });
        }

        return JsonSerializer.Serialize(
            new { Version = "2012-10-17", Statement = statements },
            PolicyJson);
    }

    /// <summary>
    /// The assume-role policy for one repository.
    ///
    /// <para>BOTH SUB FORMS ARE TRUSTED, and that is not belt-and-braces — it is a measured
    /// requirement. GitHub issues ID-HARDENED subs, <c>repo:{org}@{orgId}/{repo}@{repoId}:ref:…</c>,
    /// so a policy matching only the classic <c>repo:{org}/{repo}:ref:…</c> fails with
    /// <c>Not authorized to perform sts:AssumeRoleWithWebIdentity</c>. That cost this system its
    /// first website publish; reference/operations/website.md records it, and
    /// <c>bootstrapwebsiteci</c> / <c>bootstrape2eci</c> already write exactly this shape.</para>
    ///
    /// <para>The wildcards sit ONLY in the numeric id slots. <c>@</c> is illegal in GitHub org and
    /// repository names, so <c>Scutara@*/ScutaraService@*</c> cannot over-match another
    /// repository.</para>
    ///
    /// <para>Scoped to <c>refs/heads/*</c>: a build runs from a branch. Leaving it at <c>*</c> would
    /// let a <c>pull_request</c> context — which anyone who can open a PR controls — assume a role
    /// that pushes artifacts.</para>
    /// </summary>
    public static string TrustPolicyFor(string repo, string? providerArn = null)
    {
        var parts = repo.Split('/', 2);
        var idHardened = parts.Length == 2 ? $"{parts[0]}@*/{parts[1]}@*" : repo;

        return JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new object[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Federated = providerArn ?? $"<oidc-provider-arn:{OidcProvider}>" },
                    Action = "sts:AssumeRoleWithWebIdentity",
                    Condition = new Dictionary<string, object>
                    {
                        ["StringEquals"] = new Dictionary<string, object>
                        {
                            [$"{OidcProvider}:aud"] = "sts.amazonaws.com",
                        },
                        ["StringLike"] = new Dictionary<string, object>
                        {
                            [$"{OidcProvider}:sub"] = new[]
                            {
                                $"repo:{repo}:ref:refs/heads/*",
                                $"repo:{idHardened}:ref:refs/heads/*",
                            },
                        },
                    },
                },
            },
        }, PolicyJson);
    }

    /// <summary>A repository name reduced to an IAM-safe slug: <c>Scutara/ScutaraService</c> → <c>scutaraservice</c>.</summary>
    public static string SlugFor(string repo)
    {
        var name = repo.Contains('/') ? repo[(repo.LastIndexOf('/') + 1)..] : repo;
        return new string(name.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
    }

    private static string RequireNonEmpty(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"lz bootstrappipeline refuses: {name} is required.")
            : value;
}
