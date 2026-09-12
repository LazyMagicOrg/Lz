using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.Signer;
using Amazon.Signer.Model;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Creates what <see cref="PipelineBootstrapPlanner"/> decided, in the build account.
///
/// <para>IDEMPOTENT, in the pattern <see cref="AwsStateBootstrapper"/> established: read first,
/// skip what exists, report either way. Re-running is how you adopt a new build repository — add
/// it to <c>Pipeline.Repositories</c> and run again; everything already there is left alone.</para>
///
/// <para>IT PRINTS THE PLAN AND STOPS unless told to apply. The default is a dry run for the same
/// reason the publish workflows default to one: every step here is reviewable except the ones that
/// create an IAM role GitHub can assume, and those are worth reading before they exist rather than
/// after. The plan is a pure function, so the dry run is the real plan and not an approximation
/// of it.</para>
///
/// <para>A NOTE ON AWS SDK v4 COLLECTIONS, learned the expensive way. A list response property is
/// NULL, not empty, when the account has none of that resource — and a bootstrap runs against
/// exactly that account. The first real apply against the greenfield build account died on
/// <c>OpenIDConnectProviderList.Any()</c>. Treat every collection off a response as nullable here;
/// <see cref="AlreadyHasProvider"/> is the one that bit, and it is a named, tested function for
/// that reason rather than because the comparison is interesting.</para>
/// </summary>
public static class PipelineBootstrapper
{
    public static async Task BootstrapAsync(SystemConfig config, bool apply)
    {
        var profile = config.Profile;
        var region = config.Region;

        // The account is read, never taken from config: a plan applied to the wrong account is the
        // one mistake here that cannot be undone by re-running.
        var accountId = await ResolveAccountAsync(profile, region);

        var plan = PipelineBootstrapPlanner.Plan(config, accountId);

        Print(plan, accountId, profile, apply);
        if (!apply)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("DRY RUN — nothing was created. Re-run with --apply to create it.");
            Console.ResetColor();
            return;
        }

        if (plan.ArtifactAccountId is { } expected && expected != accountId)
            throw new InvalidOperationException(
                $"Pipeline.ArtifactAccountId is {expected} but this profile resolves to {accountId}. " +
                "Refusing: bootstrapping the pipeline into the wrong account creates roles GitHub " +
                "can assume somewhere nobody intended. Check --profile.");

        var creds = AwsCredentialsFactory.Resolve(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        using var s3 = creds != null ? new AmazonS3Client(creds, endpoint) : new AmazonS3Client(endpoint);
        using var iam = creds != null
            ? new AmazonIdentityManagementServiceClient(creds, endpoint)
            : new AmazonIdentityManagementServiceClient(endpoint);
        using var signer = creds != null
            ? new AmazonSignerClient(creds, endpoint) : new AmazonSignerClient(endpoint);

        var providerArn = await EnsureOidcProviderAsync(iam, accountId);

        foreach (var store in plan.Stores)
            await EnsureStoreAsync(s3, store, region);

        using var ecr = creds != null
            ? new Amazon.ECR.AmazonECRClient(creds, endpoint)
            : new Amazon.ECR.AmazonECRClient(endpoint);

        var signingRules = new List<Amazon.ECR.Model.SigningRule>();

        foreach (var role in plan.Roles)
        {
            string? profileArn = null;
            if (role.SigningProfile is { } sp)
                profileArn = await EnsureSigningProfileAsync(signer, sp);

            await EnsureRoleAsync(iam, role, providerArn);

            foreach (var repo in role.EcrRepositories)
                await EnsureRepositoryAsync(ecr, repo, config);

            // ONE RULE PER BUILD ROLE, filtered to that role's repositories (§4.2). Verification
            // trusts PROFILE ARNs, so provenance is expressed through profiles: a valid signature
            // under this profile means "signed at push under the profile only this repository's
            // workflow can use", and because no human permission set holds signer:SignPayload on it,
            // it also means "built by that workflow".
            if (profileArn != null && role.EcrRepositories.Count > 0)
                signingRules.Add(BuildSigningRule(profileArn, role.EcrRepositories));
        }

        await ApplySigningConfigurationAsync(ecr, signingRules);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Pipeline bootstrap complete.");
        Console.ResetColor();
    }

    private static async Task<string> ResolveAccountAsync(string? profile, string region)
    {
        var creds = AwsCredentialsFactory.Resolve(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        using var sts = creds != null
            ? new AmazonSecurityTokenServiceClient(creds, endpoint)
            : new AmazonSecurityTokenServiceClient(endpoint);

        var who = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        return who.Account;
    }

    private static void Print(PipelineBootstrapPlan plan, string accountId, string? profile, bool apply)
    {
        Console.WriteLine($"=== Pipeline bootstrap: {plan.SystemKey} ===");
        Console.WriteLine($"  account:  {accountId}{(profile is null or "" ? " (ambient credentials)" : $" (profile {profile})")}");
        Console.WriteLine($"  region:   {plan.Region}");
        Console.WriteLine($"  mode:     {(apply ? "APPLY" : "dry run")}");
        Console.WriteLine();

        Console.WriteLine("  stores:");
        foreach (var s in plan.Stores)
        {
            Console.WriteLine($"    {s.Name}");
            Console.WriteLine($"      {s.Purpose}");
            Console.WriteLine(s.WriterPrefixes.Count == 0
                ? "      writable by GitHub: NOTHING (the deployer writes these)"
                : $"      writable by GitHub under: {string.Join(", ", s.WriterPrefixes)}");
        }

        Console.WriteLine();
        Console.WriteLine("  roles GitHub may assume:");
        foreach (var r in plan.Roles)
        {
            Console.WriteLine($"    {r.Name}");
            Console.WriteLine($"      for {r.Repo} building '{r.Class}'");
            Console.WriteLine($"      signing profile: {r.SigningProfile ?? "none (bundles carry no registry signature)"}");
            if (r.EcrRepositories.Count > 0)
                Console.WriteLine($"      may push to: {string.Join(", ", r.EcrRepositories)}");
        }

        if (plan.EcrRepositories.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ECR repositories (immutable tags, AES256, scan-on-push, signed at push):");
            foreach (var name in plan.EcrRepositories)
                Console.WriteLine($"    {name}");
        }
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// One signing rule: this profile signs pushes to these repositories.
    ///
    /// <para>EXTRACTED SO THE ENUM IS ASSERTED RATHER THAN TRUSTED. The filter type was written as
    /// the literal <c>"WILDCARD"</c>, which ECR rejected on the first apply — the value is
    /// <c>WILDCARD_MATCH</c>. Two neighbouring literals (<c>AES256</c>, <c>IMMUTABLE</c>) happened
    /// to be right, which is the worse half of the lesson: guessing a string that the SDK publishes
    /// as a constant is a coin flip that costs an apply. Every one of them is now the SDK's own
    /// constant, so a wrong value is a compile error, and this function exists so a test can pin it
    /// without AWS.</para>
    /// </summary>
    internal static Amazon.ECR.Model.SigningRule BuildSigningRule(
        string profileArn, IReadOnlyList<string> repositories)
        => new()
        {
            SigningProfileArn = profileArn,
            RepositoryFilters = repositories
                .Select(n => new Amazon.ECR.Model.SigningRepositoryFilter
                {
                    Filter = n,
                    FilterType = Amazon.ECR.SigningRepositoryFilterType.WILDCARD_MATCH,
                })
                .ToList(),
        };

    /// <summary>
    /// Is this OIDC provider already registered?
    /// </summary>
    /// <param name="existingArns">
    /// The ARNs the account already has — <b>nullable, and that is the whole reason this is a named
    /// function rather than an inline <c>.Any()</c></b>. The AWS SDK v4 returns NULL rather than an
    /// empty list for a collection with no members, so a greenfield account — exactly the account a
    /// bootstrap runs against — made the obvious code throw <c>ArgumentNullException</c> on the
    /// first apply, 2026-09-12. <c>AwsLiveVerifier</c> already guarded the same hazard with
    /// <c>?? new List&lt;&gt;()</c>; this is that lesson, made testable.
    /// </param>
    internal static bool AlreadyHasProvider(IEnumerable<string>? existingArns, string arn)
        => existingArns?.Any(a => string.Equals(a, arn, StringComparison.Ordinal)) ?? false;

    private static async Task<string> EnsureOidcProviderAsync(
        IAmazonIdentityManagementService iam, string accountId)
    {
        var arn = $"arn:aws:iam::{accountId}:oidc-provider/{PipelineBootstrapPlanner.OidcProvider}";

        var existing = await iam.ListOpenIDConnectProvidersAsync(new ListOpenIDConnectProvidersRequest());
        if (AlreadyHasProvider(existing.OpenIDConnectProviderList?.Select(p => p.Arn), arn))
        {
            Console.WriteLine($"  OIDC provider already exists. Skipping. ({arn})");
            return arn;
        }

        // No thumbprint is supplied. AWS stopped requiring one for the well-known GitHub issuer —
        // it validates against the issuer's certificate chain — and a pinned thumbprint is a
        // scheduled outage on the day GitHub rotates its CA.
        await iam.CreateOpenIDConnectProviderAsync(new CreateOpenIDConnectProviderRequest
        {
            Url = $"https://{PipelineBootstrapPlanner.OidcProvider}",
            ClientIDList = new List<string> { "sts.amazonaws.com" },
        });

        Console.WriteLine($"  OIDC provider created. ({arn})");
        return arn;
    }

    private static async Task EnsureStoreAsync(IAmazonS3 s3, PipelineStore store, string region)
    {
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = store.Name });
            Console.WriteLine($"  store '{store.Name}' already exists. Skipping creation.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await s3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = store.Name,
                BucketRegionName = region,
            });
            Console.WriteLine($"  store '{store.Name}' created.");
        }

        // VERSIONING IS THE DURABILITY HALF and is applied every run, not only on create: a store
        // that lost it would silently stop being able to name an artifact by object version, which
        // is the identity every bundle deploy rests on.
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = store.Name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = store.Name,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                BlockPublicPolicy = true,
                IgnorePublicAcls = true,
                RestrictPublicBuckets = true,
            },
        });

        Console.WriteLine("      versioning on, public access blocked.");
    }

    /// <summary>
    /// The ECR repository for one artifact, hardened. Registry hardening is DecoupledCd.md §8.5.
    /// </summary>
    private static async Task EnsureRepositoryAsync(
        Amazon.ECR.IAmazonECR ecr, string name, SystemConfig config)
    {
        try
        {
            await ecr.DescribeRepositoriesAsync(new Amazon.ECR.Model.DescribeRepositoriesRequest
            {
                RepositoryNames = new List<string> { name },
            });
            Console.WriteLine($"  repository '{name}' already exists.");
        }
        catch (Amazon.ECR.Model.RepositoryNotFoundException)
        {
            await ecr.CreateRepositoryAsync(new Amazon.ECR.Model.CreateRepositoryRequest
            {
                RepositoryName = name,
                // AES256 rather than a customer-managed key: ECR encrypts at rest either way, and a
                // CMK adds a key to rotate, grant across accounts and pay for. Named explicitly
                // rather than left to the default so the choice is visible in the plan.
                EncryptionConfiguration = new Amazon.ECR.Model.EncryptionConfiguration
                {
                    EncryptionType = Amazon.ECR.EncryptionType.AES256,
                },
                ImageScanningConfiguration = new Amazon.ECR.Model.ImageScanningConfiguration
                {
                    ScanOnPush = true,
                },
            });
            Console.WriteLine($"  repository '{name}' created.");
        }

        // IMMUTABLE TAGS, applied every run rather than only on create. This is what makes "nothing
        // deploys a tag" enforceable rather than a convention: a mutable tag is a pointer anyone
        // with push rights can move to different content, and the whole design rests on an identity
        // that cannot be repointed.
        await ecr.PutImageTagMutabilityAsync(new Amazon.ECR.Model.PutImageTagMutabilityRequest
        {
            RepositoryName = name,
            ImageTagMutability = Amazon.ECR.ImageTagMutability.IMMUTABLE,
        });

        // THE LIFECYCLE POLICY IS DELIBERATELY NARROW. §8.4 wants "keep every identity named in any
        // request of the last N deploys", and ECR lifecycle rules cannot express that — they select
        // by age and count, and know nothing about the request store. So this expires only UNTAGGED
        // images, which under immutable tags means genuinely orphaned layers, and leaves every
        // tagged image alone. Erring toward keeping too much: a deleted digest breaks a rollback
        // target, and storage is cheap next to that.
        var untaggedDays = config.Hygiene?.EcrUntaggedImageRetentionDays ?? 14;
        var policy = System.Text.Json.JsonSerializer.Serialize(new
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

        await ecr.PutLifecyclePolicyAsync(new Amazon.ECR.Model.PutLifecyclePolicyRequest
        {
            RepositoryName = name,
            LifecyclePolicyText = policy,
        });

        Console.WriteLine($"      immutable tags, AES256, scan-on-push, untagged expire {untaggedDays}d.");
    }

    /// <summary>
    /// ECR managed signing: images are signed AS THEY ARE PUSHED, under the profile the pushing
    /// role holds <c>signer:SignPayload</c> on. Registry-wide configuration, so every rule is
    /// written in one call — which is why the rules are collected first rather than applied per role.
    /// </summary>
    private static async Task ApplySigningConfigurationAsync(
        Amazon.ECR.IAmazonECR ecr, List<Amazon.ECR.Model.SigningRule> rules)
    {
        if (rules.Count == 0)
        {
            Console.WriteLine("  no image roles — no signing configuration written.");
            return;
        }

        await ecr.PutSigningConfigurationAsync(new Amazon.ECR.Model.PutSigningConfigurationRequest
        {
            SigningConfiguration = new Amazon.ECR.Model.SigningConfiguration { Rules = rules },
        });

        Console.WriteLine($"  signing configuration written: {rules.Count} rule(s).");
    }

    private static async Task<string> EnsureSigningProfileAsync(IAmazonSigner signer, string profileName)
    {
        try
        {
            var existing = await signer.GetSigningProfileAsync(
                new GetSigningProfileRequest { ProfileName = profileName });
            Console.WriteLine($"  signing profile '{profileName}' already exists. Skipping.");
            return existing.Arn;
        }
        catch (Amazon.Signer.Model.ResourceNotFoundException)
        {
            // fall through
        }

        // NO signatureValidityPeriod OVERRIDE — settled 2026-09-06 (DecoupledCd.md §11.2). It is
        // optional, the platform default is 135 months, and the 365 days this design once carried
        // was an ~11x tightening nothing justified: an expired signature fails a deploy of an
        // artifact that never changed.
        var created = await signer.PutSigningProfileAsync(new PutSigningProfileRequest
        {
            ProfileName = profileName,
            PlatformId = "Notation-OCI-SHA384-ECDSA",
        });

        Console.WriteLine($"  signing profile '{profileName}' created.");
        return created.Arn;
    }

    private static async Task EnsureRoleAsync(
        IAmazonIdentityManagementService iam, PipelineRole role, string providerArn)
    {
        // REGENERATED with the real provider ARN rather than string-substituted into the planned
        // document. The substitution version was a live defect: the serializer HTML-escapes angle
        // brackets, so the placeholder in the JSON never matched the one being searched for, and
        // the role would have been created trusting a literal `<oidc-provider-arn:…>`.
        // Asking the planner for the document it would produce given the ARN has no such failure
        // mode, and the planner is pure so this is the same document either way.
        var trust = PipelineBootstrapPlanner.TrustPolicyFor(role.Repo, providerArn);

        try
        {
            await iam.GetRoleAsync(new GetRoleRequest { RoleName = role.Name });
            Console.WriteLine($"  role '{role.Name}' already exists — updating its policies.");

            // Updated rather than skipped, deliberately: the trust policy is the trust boundary, so
            // a role that drifted from the plan is the one thing a re-run must correct.
            await iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest
            {
                RoleName = role.Name,
                PolicyDocument = trust,
            });
        }
        catch (NoSuchEntityException)
        {
            await iam.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = role.Name,
                AssumeRolePolicyDocument = trust,
                Description = $"lz pipeline: {role.Repo} builds '{role.Class}'. Push-only.",
                MaxSessionDuration = 3600,
            });
            Console.WriteLine($"  role '{role.Name}' created.");
        }

        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = role.Name,
            PolicyName = $"{role.Name}-push-only",
            PolicyDocument = role.PermissionPolicy,
        });
    }
}
