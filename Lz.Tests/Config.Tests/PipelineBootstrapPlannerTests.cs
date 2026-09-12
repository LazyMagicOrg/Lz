using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// What <c>lz bootstrappipeline</c> would create (DecoupledCd.md punchlist P1).
///
/// <para>Everything the planner produces is a NAME or a POLICY DOCUMENT, and both fail in ways an
/// applier test would not catch — a trust policy that matches nothing fails at the first GitHub
/// run, weeks later. So the plan is asserted here exactly, before any AWS call exists to make.</para>
/// </summary>
public class PipelineBootstrapPlannerTests
{
    private static SystemConfig Base() => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "abcd-1234",
        Rollback = new RollbackConfig { PinImageDigest = true },
    };

    private static PipelineConfig Enabled() => new()
    {
        Enabled = true,
        Classes = new List<string> { "image", "client", "config" },
        Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
        Repositories = new List<PipelineRepositoryConfig>
        {
            new() { Repo = "Scutara/ScutaraService",   Class = "image", Artifacts = new List<string> { "aiphost" } },
            new() { Repo = "Scutara/ScutaraSellerApp", Class = "client" },
            new() { Repo = "Scutara/ScutaraAdminApp",  Class = "client" },
        },
    };

    private static SystemConfig WithPipeline(PipelineConfig p)
    {
        var c = Base();
        c.Pipeline = p;
        return c;
    }

    // ---------------------------------------------------------------------------------------
    //  The gate
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ItRefusesASystemWithNoPipelineBlock()
    {
        // Bootstrapping would create roles GitHub can assume against an account whose owner never
        // opted in. DecoupledCd.md §8 item 6 asks for this refusal by name.
        var ex = Assert.Throws<InvalidOperationException>(() => PipelineBootstrapPlanner.Plan(Base()));
        Assert.Contains("Pipeline:", ex.Message);
    }

    [Fact]
    public void ItRefusesAPresentButDisabledBlock()
    {
        var p = Enabled();
        p.Enabled = false;

        Assert.Throws<InvalidOperationException>(() => PipelineBootstrapPlanner.Plan(WithPipeline(p)));
    }

    [Fact]
    public void ItRefusesAnEmptyRepositoryList()
    {
        var p = Enabled();
        p.Repositories = new List<PipelineRepositoryConfig>();

        var ex = Assert.Throws<InvalidOperationException>(() => PipelineBootstrapPlanner.Plan(WithPipeline(p)));
        Assert.Contains("trust boundary", ex.Message);
    }

    [Fact]
    public void ItRefusesARepositoryThatIsNotOwnerSlashName()
    {
        var p = Enabled();
        p.Repositories = new List<PipelineRepositoryConfig> { new() { Repo = "Service", Class = "image", Artifacts = new List<string> { "aiphost" } } };

        var ex = Assert.Throws<InvalidOperationException>(() => PipelineBootstrapPlanner.Plan(WithPipeline(p)));
        Assert.Contains("owner/name", ex.Message);
    }

    [Fact]
    public void ItRefusesAnUnknownClass()
    {
        var p = Enabled();
        p.Repositories = new List<PipelineRepositoryConfig>
        {
            new() { Repo = "Scutara/ScutaraService", Class = "container", Artifacts = new List<string> { "aiphost" } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PipelineBootstrapPlanner.Plan(WithPipeline(p)));
        Assert.Contains("'container'", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  Roles — the trust boundary
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void OneRolePerRepository_NamedByKindAndRepo()
    {
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.Equal(3, plan.Roles.Count);
        Assert.Equal(
            new[] { "scu-build-ci-scutaraservice", "scu-bundle-ci-scutarasellerapp", "scu-bundle-ci-scutaraadminapp" },
            plan.Roles.Select(r => r.Name));
    }

    [Fact]
    public void OnlyImageRolesGetASigningProfile()
    {
        // A bundle role with a signing profile would imply a signature that never exists: bundles
        // get no registry signature, their integrity comes from the build record (§4.2).
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.NotNull(plan.Roles.Single(r => r.Class == "image").SigningProfile);
        Assert.All(plan.Roles.Where(r => r.Class != "image"), r => Assert.Null(r.SigningProfile));
    }

    [Fact]
    public void TwoRepositoriesOfTheSameClass_GetDistinctRolesAndPrefixes()
    {
        // SellerApp and AdminApp are both `client`. Keying anything on the class rather than the
        // repository would collapse them, and one build repo could then overwrite the other's
        // artifacts — the exact thing prefix scoping exists to prevent.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        var clients = plan.Roles.Where(r => r.Class == "client").Select(r => r.Name).ToList();
        Assert.Equal(2, clients.Distinct().Count());

        var artifacts = plan.Stores.Single(s => s.Name.Contains("artifacts"));
        Assert.Equal(3, artifacts.WriterPrefixes.Distinct().Count());
    }

    // ---------------------------------------------------------------------------------------
    //  The trust policy — the measured one
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheTrustPolicyMatchesBothSubForms_IncludingTheIdHardenedOne()
    {
        // MEASURED, not defensive: GitHub issues repo:{org}@{orgId}/{repo}@{repoId}:ref:… and a
        // policy matching only the classic form fails AssumeRoleWithWebIdentity. It cost this
        // system its first website publish (reference/operations/website.md).
        var json = PipelineBootstrapPlanner.TrustPolicyFor("Scutara/ScutaraService");
        using var doc = JsonDocument.Parse(json);

        var subs = doc.RootElement
            .GetProperty("Statement")[0]
            .GetProperty("Condition")
            .GetProperty("StringLike")
            .GetProperty("token.actions.githubusercontent.com:sub")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("repo:Scutara/ScutaraService:ref:refs/heads/*", subs);
        Assert.Contains("repo:Scutara@*/ScutaraService@*:ref:refs/heads/*", subs);
    }

    [Fact]
    public void TheWildcardsSitOnlyInTheNumericIdSlots()
    {
        // `@` is illegal in GitHub org and repo names, which is what keeps `Scutara@*/X@*` from
        // over-matching another repository. A bare `repo:*` would trust every repo on GitHub.
        var json = PipelineBootstrapPlanner.TrustPolicyFor("Scutara/ScutaraService");

        Assert.DoesNotContain("repo:*", json);
        Assert.DoesNotContain("/*:ref", json);
        Assert.Contains("Scutara@*/ScutaraService@*", json);
    }

    [Fact]
    public void TheTrustPolicyIsScopedToBranches_NotToAnyRef()
    {
        // `*` would let a pull_request context — which anyone who can open a PR controls — assume
        // a role that pushes artifacts.
        var json = PipelineBootstrapPlanner.TrustPolicyFor("Scutara/ScutaraService");

        Assert.Contains("ref:refs/heads/*", json);
        Assert.DoesNotContain("\"repo:Scutara/ScutaraService:*\"", json);
    }

    [Fact]
    public void TheAudienceIsPinned()
    {
        var json = PipelineBootstrapPlanner.TrustPolicyFor("Scutara/ScutaraService");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("sts.amazonaws.com", doc.RootElement
            .GetProperty("Statement")[0].GetProperty("Condition")
            .GetProperty("StringEquals")
            .GetProperty("token.actions.githubusercontent.com:aud").GetString());
    }

    // ---------------------------------------------------------------------------------------
    //  Stores
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ThreeStores_NoneCarryingTheEnvironment()
    {
        // One build account holds artifacts PROMOTED between environments. A bucket named `-dev-`
        // could not hold the same artifact in prod — same reasoning as the registry's neutral name.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.Equal(3, plan.Stores.Count);
        Assert.All(plan.Stores, s => Assert.DoesNotContain("dev", s.Name));
        Assert.All(plan.Stores, s => Assert.StartsWith("scu-", s.Name));
    }

    [Fact]
    public void TheRequestStoreHasNoGitHubWritablePrefix()
    {
        // "GitHub writes build records; the deployer writes deploy requests. That separation is the
        // whole trust boundary" (§4.3). A writable prefix here would hand GitHub admission control
        // over prod — precisely what this design exists to prevent.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        var requests = plan.Stores.Single(s => s.Name.Contains("deploy-requests"));
        Assert.Empty(requests.WriterPrefixes);

        // ... while the two GitHub-written stores do have them.
        Assert.NotEmpty(plan.Stores.Single(s => s.Name.Contains("artifacts")).WriterPrefixes);
        Assert.NotEmpty(plan.Stores.Single(s => s.Name.Contains("build-records")).WriterPrefixes);
    }

    [Fact]
    public void PrefixesAreClassThenRepository_SoTheyCannotCollide()
    {
        Assert.Equal("image/scutara/scutaraservice/",
            PipelineBootstrapPlanner.PrefixFor("image", "Scutara/ScutaraService"));

        Assert.NotEqual(
            PipelineBootstrapPlanner.PrefixFor("client", "Scutara/ScutaraSellerApp"),
            PipelineBootstrapPlanner.PrefixFor("client", "Scutara/ScutaraAdminApp"));
    }

    [Fact]
    public void TheGrantedPrefixIsTheSameOneRecordsAreWrittenTo()
    {
        // Two definitions that agreed today would drift, and the failure would be an AccessDenied
        // at the first build rather than anything naming the cause.
        Assert.Equal(
            Lz.Aws.Pipeline.BuildRecordFormat.PrefixFor("image", "Scutara/ScutaraService"),
            PipelineBootstrapPlanner.PrefixFor("image", "Scutara/ScutaraService"));
    }

    // ---------------------------------------------------------------------------------------
    //  Self-rewrite
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheDenyListCoversEveryActionThatCouldRewriteTheDeployer()
    {
        // §5.5: a config bundle can change what the deployer deploys; it cannot change the deployer.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.Equal(
            new[] { "states:UpdateStateMachine", "events:PutRule", "events:PutTargets", "scheduler:*", "iam:*Policy*" },
            plan.DeniedActions);
    }

    // ---------------------------------------------------------------------------------------
    //  Permission policies — what GitHub can actually do once it has assumed a role
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void NoRoleCanDeleteAnything()
    {
        // Push-only BY CONSTRUCTION. This is what makes the stores append-only to GitHub — not
        // Object Lock, which does not stop a writer putting a new current version at an existing
        // key either.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");

        foreach (var role in plan.Roles)
        {
            Assert.DoesNotContain("Delete", role.PermissionPolicy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("s3:*", role.PermissionPolicy);
            Assert.DoesNotContain("ecr:*", role.PermissionPolicy);
        }
    }

    [Fact]
    public void NoRoleCanTouchTheRequestStore()
    {
        // The deployer writes deploy requests. GitHub holding admission control over prod is the
        // thing this whole design exists to prevent (§4.3).
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");
        var requests = plan.Stores.Single(s => s.Name.Contains("deploy-requests")).Name;

        Assert.All(plan.Roles, r => Assert.DoesNotContain(requests, r.PermissionPolicy));
    }

    [Fact]
    public void EveryRoleCanWriteItsOwnBuildRecord_IncludingImageRoles()
    {
        // §3's trust-boundary table lists only ECR and signer actions for {sk}-build-ci, which
        // would leave an image build unable to write the record §4.1 requires of EVERY build. The
        // grant is here for both shapes; the table is noted as having the gap.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");
        var records = plan.Stores.Single(s => s.Name.Contains("build-records")).Name;

        Assert.All(plan.Roles, r =>
        {
            Assert.Contains($"arn:aws:s3:::{records}/", r.PermissionPolicy);
            Assert.Contains("s3:PutObject", r.PermissionPolicy);
        });
    }

    [Fact]
    public void EachRoleIsScopedToItsOwnPrefix_AndNoOthers()
    {
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");

        var seller = plan.Roles.Single(r => r.Repo.EndsWith("SellerApp"));
        var adminPrefix = PipelineBootstrapPlanner.PrefixFor("client", "Scutara/ScutaraAdminApp");

        Assert.Contains(PipelineBootstrapPlanner.PrefixFor("client", "Scutara/ScutaraSellerApp"),
            seller.PermissionPolicy);
        Assert.DoesNotContain(adminPrefix, seller.PermissionPolicy);
    }

    [Fact]
    public void OnlyImageRolesGetEcrAndSignerActions()
    {
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");

        var image = plan.Roles.Single(r => r.Class == "image");
        Assert.Contains("ecr:PutImage", image.PermissionPolicy);
        Assert.Contains("signer:SignPayload", image.PermissionPolicy);

        // A bundle role holding signer actions would imply a signature that never exists (§4.2).
        Assert.All(plan.Roles.Where(r => r.Class != "image"), r =>
        {
            Assert.DoesNotContain("ecr:", r.PermissionPolicy);
            Assert.DoesNotContain("signer:", r.PermissionPolicy);
        });
    }

    [Fact]
    public void OnlyGetAuthorizationTokenIsGrantedOnStar()
    {
        // That action takes no resource, so it cannot be narrowed. Everything else must be scoped,
        // and a review of this policy should be able to see at a glance that only one wildcard
        // resource exists and which action it belongs to.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");
        var image = plan.Roles.Single(r => r.Class == "image");

        using var doc = JsonDocument.Parse(image.PermissionPolicy);
        var starred = doc.RootElement.GetProperty("Statement").EnumerateArray()
            .Where(s => s.GetProperty("Resource").ValueKind == JsonValueKind.String
                     && s.GetProperty("Resource").GetString() == "*")
            .SelectMany(s => s.GetProperty("Action").EnumerateArray().Select(a => a.GetString()))
            .ToList();

        Assert.Equal(new[] { "ecr:GetAuthorizationToken" }, starred);
    }

    [Fact]
    public void WithNoAccountId_ThePolicyCarriesAVisiblePlaceholder()
    {
        // A plan can be printed for an account nobody has logged into. The placeholder must be
        // obviously not-an-account-id, so a policy pasted from a dry run cannot silently apply.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));
        var image = plan.Roles.Single(r => r.Class == "image");

        Assert.Contains("<build-account-id>", image.PermissionPolicy);
    }

    [Fact]
    public void TheEcrGrantNamesExactRepositories_NotAWildcard()
    {
        // `{sk}-*` would let one build repository push to every repository the system will ever
        // have. Naming the artifacts is what makes the grant match what the repo produces.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");
        var image = plan.Roles.Single(r => r.Class == "image");

        Assert.Contains("repository/scu-abcd-1234-aiphost", image.PermissionPolicy);
        Assert.DoesNotContain("repository/scu-*", image.PermissionPolicy);
    }

    [Fact]
    public void AnImageRepositoryWithNoArtifacts_IsRefused()
    {
        var p = Enabled();
        p.Repositories = new List<PipelineRepositoryConfig>
        {
            new() { Repo = "Scutara/ScutaraService", Class = "image" }, // no Artifacts
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PipelineBootstrapPlanner.Plan(WithPipeline(p), "147440642635"));
        Assert.Contains("Artifacts", ex.Message);
    }

    [Fact]
    public void OnlyImageRolesCarryEcrRepositories()
    {
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()), "147440642635");

        Assert.Equal(new[] { "scu-abcd-1234-aiphost" }, plan.EcrRepositories);
        Assert.All(plan.Roles.Where(r => r.Class != "image"), r => Assert.Empty(r.EcrRepositories));
    }

    [Fact]
    public void ThePlanCarriesTheArtifactAccountWhenOneIsNamed()
    {
        var p = Enabled();
        p.ArtifactAccountId = "147440642635"; // scu-cicd

        Assert.Equal("147440642635", PipelineBootstrapPlanner.Plan(WithPipeline(p)).ArtifactAccountId);
    }
}
