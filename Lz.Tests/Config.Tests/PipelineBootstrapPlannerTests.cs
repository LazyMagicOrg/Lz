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
    public void EveryStore_IsWriteOnce_ByItsOwnBucketPolicy()
    {
        // The request store too, though nothing writes it yet: §6 writes requests "under conditional writes", and a
        // store made write-once only once its first writer exists would be write-once too late (DecoupledCd §14.3).
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.All(plan.Stores, s =>
        {
            var statement = Assert.Single(s.PolicyStatements);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(WriteOnceStore.Deny(s.Name), statement),
                $"{s.Name} does not carry the write-once statement for its own bucket: {statement.ToJsonString()}");
        });
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
            new[]
            {
                "states:UpdateStateMachine", "events:PutRule", "events:PutTargets",
                "events:RemoveTargets", "events:DeleteRule", "events:DisableRule", "events:PutPermission", "events:RemovePermission",
                "scheduler:*", "iam:*Policy*",
                "lambda:UpdateFunctionCode", "lambda:UpdateFunctionConfiguration", "lambda:AddPermission", "lambda:RemovePermission",
                "lambda:PutFunctionEventInvokeConfig", "lambda:UpdateFunctionEventInvokeConfig",
                "sns:SetTopicAttributes", "sns:AddPermission", "sns:RemovePermission", "sns:DeleteTopic", "sns:Unsubscribe",
                "cloudwatch:PutMetricAlarm", "cloudwatch:DeleteAlarms", "cloudwatch:DisableAlarmActions",
                "logs:DeleteLogGroup", "logs:PutRetentionPolicy", "logs:DeleteRetentionPolicy",
            },
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
    public void AToolingRepository_GetsAnImageRole()
    {
        // CLASS 8 IS AN IMAGE. The record format names a tooling build by digest, so a bundle role — S3 writes, no
        // registry, no signing profile — could never produce the artifact its own record must name (P4 stage A).
        var p = Enabled();
        p.Repositories!.Add(new() { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });

        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(p), "147440642635");
        var tooling = plan.Roles.Single(r => r.Class == "tooling");

        Assert.Equal("scu-build-ci-scutara", tooling.Name);
        Assert.Equal("scu_build_ci_scutara", tooling.SigningProfile);
        Assert.Equal(new[] { "scu-abcd-1234-tooling" }, tooling.EcrRepositories);
        Assert.Contains("scu-abcd-1234-tooling", plan.EcrRepositories);
        Assert.Contains("repository/scu-abcd-1234-tooling", tooling.PermissionPolicy);
        Assert.Contains("tooling/scutara/scutara/", tooling.PermissionPolicy);
    }

    [Fact]
    public void AToolingRepositoryWithNoArtifacts_IsRefused()
    {
        var p = Enabled();
        p.Repositories!.Add(new() { Repo = "Scutara/Scutara", Class = "tooling" }); // no Artifacts

        var ex = Assert.Throws<InvalidOperationException>(
            () => PipelineBootstrapPlanner.Plan(WithPipeline(p), "147440642635"));
        Assert.Contains("'tooling'", ex.Message);
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

    // ---------------------------------------------------------------------------------------
    //  What crosses to the environment this run was given (MigrationPlan M5)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WithATargetAccount_ItReplicatesTheImageRepositoriesThere()
    {
        var p = Enabled();
        p.ArtifactAccountId = "147440642635";
        p.TargetAccountId = "503947800380";

        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(p), "147440642635");

        var destination = Assert.Single(plan.Replication!.Destinations);
        Assert.Equal(("503947800380", "us-west-2"), (destination.RegistryId, destination.Region));
        // Image repositories only: the two client builders have no registry at all.
        Assert.Equal(plan.Roles.Where(r => r.Class == "image").SelectMany(r => r.EcrRepositories),
            plan.Replication.RepositoryFilters.Select(f => f.Filter));
    }

    [Fact]
    public void TheReadGrant_NamesTheRoleTheDeployerPlannerCreates()
    {
        // TWO ACCOUNTS, ONE NAME. The build account's bucket policy names a role the target account's
        // planner creates; if the two ever spelled it differently, Verify could not read a single record
        // and the denial would name neither side.
        var p = Enabled();
        p.ArtifactAccountId = "147440642635";
        p.TargetAccountId = "503947800380";
        var config = WithPipeline(p);

        var plan = PipelineBootstrapPlanner.Plan(config, "147440642635");
        var verify = DeployerPlanner.Plan(config, "503947800380").Functions.Single(f => f.Handler == DeployerHandlers.Verify);

        Assert.All(plan.BuildRecordReadGrant!, s => Assert.Equal(
            $"arn:aws:iam::503947800380:role/{verify.RoleName}",
            s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>()));
        Assert.Equal(plan.BuildRecordStore, verify.Environment[DeployerEnvironment.BuildRecordStore]);
    }

    [Fact]
    public void WithoutATargetAccount_NothingCrosses()
    {
        // A guessed account would replicate artifacts somewhere nobody chose. The rest of the plan is
        // unchanged, so a build-only environment still bootstraps.
        var plan = PipelineBootstrapPlanner.Plan(WithPipeline(Enabled()));

        Assert.Null(plan.Replication);
        Assert.Null(plan.BuildRecordReadGrant);
        Assert.NotEmpty(plan.Roles);
        Assert.Null(plan.RecordForwarding);
        Assert.Null(plan.ForwardRuleToRemove);
    }

    // ---------------------------------------------------------------------------------------
    //  The trigger's build half (P2 stage D)
    // ---------------------------------------------------------------------------------------

    private static SystemConfig Forwarding(bool on)
    {
        var p = Enabled();
        p.ArtifactAccountId = "147440642635";
        p.TargetAccountId = "503947800380";
        p.DeployOnBuildRecord = on;
        return WithPipeline(p);
    }

    [Fact]
    public void WithAlerts_TheForwardingQueueAlarm_NotifiesTheEnvironmentsTopic_AndOtherwiseNobody()
    {
        // An alarm update replaces its actions, so the empty list is how turning alerts off reaches the alarm.
        Assert.Empty(PipelineBootstrapPlanner.Plan(Forwarding(true), "147440642635").RecordForwarding!.AlarmActions);

        var alerting = Forwarding(true);
        alerting.Pipeline!.Alerts = true;
        Assert.Equal(
            new[] { "arn:aws:sns:us-west-2:503947800380:scu-dev-pipeline-alerts" },
            PipelineBootstrapPlanner.Plan(alerting, "147440642635").RecordForwarding!.AlarmActions);
    }

    [Fact]
    public void WithAlerts_TheSweepMayListAndReadImageRecords_AndWithoutThemItMayNot()
    {
        const string sweep = "arn:aws:iam::503947800380:role/scu-dev-deployer-corroborate-fn";

        var alerting = Forwarding(false);
        alerting.Pipeline!.Alerts = true;
        var grant = PipelineBootstrapPlanner.Plan(alerting, "147440642635").BuildRecordReadGrant!;
        Assert.Equal(2, grant.Count);
        Assert.All(grant, s => Assert.Contains(sweep, s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.ToJsonString()));

        var without = PipelineBootstrapPlanner.Plan(Forwarding(false), "147440642635").BuildRecordReadGrant!;
        Assert.All(without, s => Assert.DoesNotContain("corroborate", s.ToJsonString()));
    }

    [Fact]
    public void WithDeployOnBuildRecord_ImageRecordsAreForwardedToTheEnvironmentsTriggerBus()
    {
        var plan = PipelineBootstrapPlanner.Plan(Forwarding(on: true), "147440642635");
        var f = plan.RecordForwarding!;

        Assert.Equal("scu-dev-forward-build-records", f.RuleName);
        // On the default bus, so no bus in the ARN.
        Assert.Equal("arn:aws:events:us-west-2:147440642635:rule/scu-dev-forward-build-records", f.RuleArn);
        Assert.Equal("arn:aws:events:us-west-2:503947800380:event-bus/scu-dev-deployer-trigger", f.TargetBusArn);
        Assert.Equal(DeployerTrigger.EventPattern("147440642635", "scu-build-records-abcd-1234"), f.EventPattern);
        Assert.Null(plan.ForwardRuleToRemove);
    }

    [Fact]
    public void TheForwardingRole_IsAssumableByEventBridgeForThisRuleOnly_AndMayOnlyPutEventsOnTheBus()
    {
        var f = PipelineBootstrapPlanner.Plan(Forwarding(on: true), "147440642635").RecordForwarding!;

        using var trust = JsonDocument.Parse(f.RoleTrustPolicy);
        var t = trust.RootElement.GetProperty("Statement").EnumerateArray().Single();
        Assert.Equal("events.amazonaws.com", t.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal("sts:AssumeRole", t.GetProperty("Action").GetString());
        Assert.Equal("147440642635", t.GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:SourceAccount").GetString());
        Assert.Equal(f.RuleArn, t.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").GetString());

        using var permission = JsonDocument.Parse(f.RolePermissionPolicy);
        var p = permission.RootElement.GetProperty("Statement").EnumerateArray().Single();
        Assert.Equal("events:PutEvents", p.GetProperty("Action").GetString());
        Assert.Equal(f.TargetBusArn, p.GetProperty("Resource").GetString());
    }

    [Fact]
    public void TheForwardingQueue_AdmitsEventBridgeForThisRuleOnly()
    {
        var f = PipelineBootstrapPlanner.Plan(Forwarding(on: true), "147440642635").RecordForwarding!;

        using var policy = JsonDocument.Parse(f.DeadLetterQueuePolicy);
        var s = policy.RootElement.GetProperty("Statement").EnumerateArray().Single();
        Assert.Equal("arn:aws:sqs:us-west-2:147440642635:scu-dev-forward-build-records-dlq", f.DeadLetterQueueArn);
        Assert.Equal(f.DeadLetterQueueArn, s.GetProperty("Resource").GetString());
        Assert.Equal("sqs:SendMessage", s.GetProperty("Action").GetString());
        Assert.Equal(f.RuleArn, s.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").GetString());
        Assert.Equal("scu-dev-forward-build-records-dlq-not-empty", f.AlarmName);
    }

    [Fact]
    public void WithTheTrigger_TheStartFunctionMayReadImageRecords_ButNotList()
    {
        // D2: it reads a record for its branch. The event names the key, so it gets GetObject and nothing else.
        var config = Forwarding(on: true);
        var grant = PipelineBootstrapPlanner.Plan(config, "147440642635").BuildRecordReadGrant!;

        var read = grant.Single(s => s["Action"]!.GetValue<string>() == "s3:GetObject");
        Assert.Equal(
            new[]
            {
                $"arn:aws:iam::503947800380:role/{DeployerPlanner.VerifyRoleName(config)}",
                $"arn:aws:iam::503947800380:role/{DeployerPlanner.StartFunctionRoleName(config)}",
            },
            read["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.AsArray().Select(n => n!.GetValue<string>()));

        var list = grant.Single(s => s["Action"]!.GetValue<string>() == "s3:ListBucket");
        Assert.Equal($"arn:aws:iam::503947800380:role/{DeployerPlanner.VerifyRoleName(config)}",
            list["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());

        // And the role it names is the one the target account's planner creates.
        var start = DeployerPlanner.Plan(config, "503947800380", new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }))
            .Functions.Single(f => f.Handler == DeployerHandlers.Start);
        Assert.Equal(DeployerPlanner.StartFunctionRoleName(config), start.RoleName);
    }

    [Fact]
    public void WithTheTriggerOff_TheReadGrantIsWhatItWasBeforeTheTrigger()
    {
        var grant = PipelineBootstrapPlanner.Plan(Forwarding(on: false), "147440642635").BuildRecordReadGrant!;

        Assert.All(grant, s => Assert.Equal(
            $"arn:aws:iam::503947800380:role/{DeployerPlanner.VerifyRoleName(Forwarding(on: false))}",
            s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>()));
    }

    [Fact]
    public void WithTheTriggerOff_TheForwardingRuleIsPlannedForRemoval()
    {
        // A flag that could only create would keep forwarding records to an environment that turned the trigger off.
        var plan = PipelineBootstrapPlanner.Plan(Forwarding(on: false), "147440642635");

        Assert.Null(plan.RecordForwarding);
        Assert.Equal("scu-dev-forward-build-records", plan.ForwardRuleToRemove);
    }
}
