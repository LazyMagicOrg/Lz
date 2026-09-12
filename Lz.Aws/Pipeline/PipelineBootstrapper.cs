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
/// <para>WHAT IT DOES NOT DO YET, stated so nobody assumes otherwise: registry hardening — the
/// environment-neutral repository name, tag immutability and the lifecycle policy (DecoupledCd.md
/// §8.5) — and the ECR managed-signing RULE that binds a profile to a repository filter. Both need
/// the repository names, which come from service definitions rather than from the Pipeline block,
/// so they are a separate step. The signing PROFILES are created here, because the roles reference
/// them.</para>
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

        foreach (var role in plan.Roles)
        {
            if (role.SigningProfile is { } sp)
                await EnsureSigningProfileAsync(signer, sp);

            await EnsureRoleAsync(iam, role, providerArn);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Pipeline bootstrap complete.");
        Console.ResetColor();
        Console.WriteLine(
            "NOT done here and still required before a build can push: registry hardening " +
            "(neutral repository name, tag immutability, lifecycle) and the ECR managed-signing " +
            "rule per profile. See DecoupledCd.md §8.5.");
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
        }
        Console.WriteLine();
    }

    // -------------------------------------------------------------------------------------------

    private static async Task<string> EnsureOidcProviderAsync(
        IAmazonIdentityManagementService iam, string accountId)
    {
        var arn = $"arn:aws:iam::{accountId}:oidc-provider/{PipelineBootstrapPlanner.OidcProvider}";

        var existing = await iam.ListOpenIDConnectProvidersAsync(new ListOpenIDConnectProvidersRequest());
        if (existing.OpenIDConnectProviderList.Any(p => p.Arn == arn))
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

    private static async Task EnsureSigningProfileAsync(IAmazonSigner signer, string profileName)
    {
        try
        {
            await signer.GetSigningProfileAsync(new GetSigningProfileRequest { ProfileName = profileName });
            Console.WriteLine($"  signing profile '{profileName}' already exists. Skipping.");
            return;
        }
        catch (Amazon.Signer.Model.ResourceNotFoundException)
        {
            // fall through
        }

        // NO signatureValidityPeriod OVERRIDE — settled 2026-09-06 (DecoupledCd.md §11.2). It is
        // optional, the platform default is 135 months, and the 365 days this design once carried
        // was an ~11x tightening nothing justified: an expired signature fails a deploy of an
        // artifact that never changed.
        await signer.PutSigningProfileAsync(new PutSigningProfileRequest
        {
            ProfileName = profileName,
            PlatformId = "Notation-OCI-SHA384-ECDSA",
        });

        Console.WriteLine($"  signing profile '{profileName}' created.");
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
