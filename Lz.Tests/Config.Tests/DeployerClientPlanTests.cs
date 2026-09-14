using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The deployer's class-2 plan (DecoupledCd.md P4 stage C): the web apps a client repository deploys as, the two bundle
/// functions and what their roles may touch, the branch on the class, and the build account's grants on <c>client/</c>.
/// </summary>
public class DeployerClientPlanTests
{
    internal const string TargetAccount = "503947800380";
    private const string BuildAccount = "147440642635";
    private const string SellerBucket = "scu---webapp-sellerapp-4df6-b9c6";
    private const string AdminBucket = "scu---webapp-adminapp-4df6-b9c6";

    internal static readonly DeployerClientInputs Distributions = new(new[] { "E31GJ01SW2CEEA" });

    /// <summary>Dev's shape: the image repository, both client repositories naming their web apps, and the WebApps they name.</summary>
    internal static SystemConfig Config(bool approval = false, List<string>? sellerArtifacts = null, List<string>? adminArtifacts = null,
        List<string>? classes = null, string topology = "ecs-fargate-cognito-dynamodb") => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6", Topology = topology,
        Durability = new DurabilityConfig { BucketVersioning = true },
        Hygiene = new HygieneConfig { S3NoncurrentVersionExpirationDays = 30 },
        Behaviors = new BehaviorsConfig
        {
            WebApps = new List<WebAppBehavior>
            {
                new() { Path = "/admin/,/admin,", AppName = "adminapp", AuthConfig = "systemauth" },
                new() { Path = "/seller/,/seller", AppName = "sellerapp", AuthConfig = "tenantauth" },
            },
        },
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            ArtifactAccountId = BuildAccount,
            TargetAccountId = TargetAccount,
            Classes = classes ?? new List<string> { "image", "client" },
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Scutara/ScutaraService", Class = "image", Artifacts = new List<string> { "aiphost" } },
                new() { Repo = "Scutara/ScutaraSellerApp", Class = "client", Artifacts = sellerArtifacts ?? new List<string> { "sellerapp" } },
                new() { Repo = "Scutara/ScutaraAdminApp", Class = "client", Artifacts = adminArtifacts ?? new List<string> { "adminapp" } },
            },
            Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
            Scan = new PipelineScanConfig { BlockOn = new List<string> { "CRITICAL" } },
            Approval = approval
                ? new PipelineApprovalConfig { Required = true, NotifyTopicArn = "arn:aws:sns:us-west-2:1:deploys" }
                : new PipelineApprovalConfig { Required = false },
        },
    };

    private static PipelineDeployer Plan(SystemConfig? config = null) => DeployerPlanner.Plan(config ?? Config(), TargetAccount, clientInputs: Distributions);

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);

    private static IEnumerable<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };

    private static IEnumerable<string> Actions(string policy) => Statements(policy).SelectMany(s => Strings(s.GetProperty("Action")));

    // ---------------------------------------------------------------------------------------
    //  The apps
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EachClientRepository_DeploysAsTheWebAppItNames_AtThatAppsPath()
    {
        var apps = DeployerPlanner.ClientApps(Config());

        Assert.Equal(new[]
        {
            new ClientApp("Scutara/ScutaraSellerApp", "sellerapp", SellerBucket, "seller/"),
            new ClientApp("Scutara/ScutaraAdminApp", "adminapp", AdminBucket, "admin/"),
        }, apps);
    }

    [Fact]
    public void NoArtifacts_OrNoClientClass_MeansNoClientApps()
    {
        var unnamed = Config();
        foreach (var r in unnamed.Pipeline!.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;

        Assert.Empty(DeployerPlanner.ClientApps(unnamed));
        Assert.Empty(DeployerPlanner.ClientApps(Config(classes: new List<string> { "image" })));
    }

    [Fact]
    public void ARepositoryNamingTwoApps_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployerPlanner.ClientApps(Config(sellerArtifacts: new List<string> { "sellerapp", "adminapp" })));

        Assert.Contains("names 2 web apps", ex.Message);
    }

    [Fact]
    public void AnAppWebAppsDoesNotName_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployerPlanner.ClientApps(Config(sellerArtifacts: new List<string> { "storeapp" })));

        Assert.Contains("Behaviors.WebApps does not name", ex.Message);
    }

    [Fact]
    public void TwoRepositoriesDeployingAsOneApp_AreRefused()
    {
        // Each mirror deletes what its bundle lacks under the app's prefix — the other's files.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployerPlanner.ClientApps(Config(adminArtifacts: new List<string> { "sellerapp" })));

        Assert.Contains("both deploy as web app 'sellerapp'", ex.Message);
    }

    [Fact]
    public void ACentralAuthTopology_WithItsPerTenantWebApps_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.ClientApps(Config(topology: "ecs-fargate-keycloak")));
    }

    [Fact]
    public void ClientApps_WithoutTheirDistributions_AreRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(Config(), TargetAccount));

        Assert.Contains("CloudFront distributions", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The functions and their roles
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBundleFunctions_ArePlanned_AndVerifyKnowsTheStoreAndTheTargets()
    {
        var plan = Plan();

        var deploy = Function(plan, DeployerHandlers.DeployBundle);
        var verifyBundle = Function(plan, DeployerHandlers.VerifyBundle);
        Assert.Equal("scu-dev-deployer-deploy-bundle", deploy.Name);
        Assert.Equal(DeployerPlanner.DeployBundleRoleName(Config()), deploy.RoleName);
        Assert.True(deploy.InvokedByStateMachine);
        Assert.True(verifyBundle.InvokedByStateMachine);

        var verify = Function(plan, DeployerHandlers.Verify);
        Assert.Equal("scu-artifacts-4df6-b9c6", verify.Environment[DeployerEnvironment.ArtifactStore]);
        var targets = ClientTargets.Decode(verify.Environment[DeployerEnvironment.ClientTargets]);
        Assert.Equal(new[] { SellerBucket, AdminBucket }, targets.Select(t => t.Bucket));
        Assert.All(targets, t => Assert.Equal(new[] { "E31GJ01SW2CEEA" }, t.Distributions));

        // What the settings reader reads back is what the plan wrote.
        var settings = VerifySettings.Read(name => verify.Environment.TryGetValue(name, out var v) ? v : null);
        Assert.Equal("scu-artifacts-4df6-b9c6", settings.ArtifactStore);
        Assert.Equal(2, settings.ClientTargets!.Count);

        var deploySettings = DeployBundleSettings.Read(name => name == DeployerEnvironment.Region
            ? "us-west-2"
            : deploy.Environment.TryGetValue(name, out var v) ? v : null);
        Assert.Equal(TargetAccount, deploySettings.TargetAccountId);
        Assert.True(deploySettings.BucketVersioning);
        Assert.Equal(30, deploySettings.NoncurrentExpirationDays);
    }

    [Fact]
    public void TheBundleDeployRole_ReachesTheConfiguredBucketsAndDistributionsByName_AndNothingElse()
    {
        var policy = Function(Plan(), DeployerHandlers.DeployBundle).Policy;
        var grants = Statements(policy).Where(s => !s.GetProperty("Sid").GetString()!.Contains("Log")).ToList();

        var resources = grants.SelectMany(s => Strings(s.GetProperty("Resource"))).ToList();
        Assert.All(resources, r => Assert.DoesNotContain("---webapp-*", r));
        Assert.Contains($"arn:aws:s3:::{SellerBucket}", resources);
        Assert.Contains($"arn:aws:s3:::{AdminBucket}/*", resources);
        Assert.Contains("arn:aws:s3:::scu-artifacts-4df6-b9c6/client/*", resources);
        Assert.Contains("arn:aws:cloudfront::503947800380:distribution/E31GJ01SW2CEEA", resources);
        Assert.DoesNotContain(resources, r => r.Contains("distribution/*"));

        // The mirror deletes (§4.6) and the deploy creates and hardens its bucket (P-7) — and it touches nothing of ECS,
        // IAM, Pulumi's state or any other bucket (§3).
        var actions = Actions(policy).ToList();
        Assert.Contains("s3:DeleteObject", actions);
        Assert.Contains("s3:PutBucketPolicy", actions);
        Assert.Contains("s3:CreateBucket", actions);
        Assert.DoesNotContain(actions, a => a.StartsWith("ecs:") || a.StartsWith("iam:") || a.StartsWith("states:") || a == "*" || a == "s3:*");
        Assert.DoesNotContain(resources, r => r == "*" || r.Contains("pulumi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDurabilityGrants_FollowTheDecision()
    {
        var off = Config();
        off.Durability = null;

        var actions = Actions(Function(Plan(off), DeployerHandlers.DeployBundle).Policy).ToList();

        Assert.DoesNotContain("s3:PutBucketVersioning", actions);
        Assert.DoesNotContain("s3:PutLifecycleConfiguration", actions);
        Assert.Equal("false", Function(Plan(off), DeployerHandlers.DeployBundle).Environment[DeployerEnvironment.BucketVersioning]);
    }

    [Fact]
    public void VerifyBundle_OnlyReads()
    {
        var actions = Actions(Function(Plan(), DeployerHandlers.VerifyBundle).Policy).ToList();

        Assert.Equal(new[] { "cloudfront:GetInvalidation", "logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents", "s3:GetObject", "s3:ListBucket" },
            actions.Distinct().OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void Verify_HeadsBundlesByVersion_AndReadsClientRecords_ButDownloadsNothingUnversioned()
    {
        var statements = Statements(Function(Plan(), DeployerHandlers.Verify).Policy).ToList();

        var head = statements.Single(s => s.GetProperty("Sid").GetString() == "HeadClientBundleVersions");
        Assert.Equal(new[] { "s3:GetObjectVersion" }, Strings(head.GetProperty("Action")));
        Assert.Equal("arn:aws:s3:::scu-artifacts-4df6-b9c6/client/*", head.GetProperty("Resource").GetString());

        // No s3:GetObject on the artifact store: a read that names no version is not one Verify makes.
        Assert.DoesNotContain(statements, s => Strings(s.GetProperty("Action")).Contains("s3:GetObject")
                                               && Strings(s.GetProperty("Resource")).Any(r => r.StartsWith("arn:aws:s3:::scu-artifacts")));
    }

    // ---------------------------------------------------------------------------------------
    //  The machine
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "Verify")]
    [InlineData(true, "Approve")]
    public void TheMachine_BranchesOnTheVerifiedClass_AfterVerifyAndAnyApproval(bool approval, string before)
    {
        var plan = Plan(Config(approval));
        var states = JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");

        Assert.Equal("ByClass", states.GetProperty(before).GetProperty("Next").GetString());
        var choice = states.GetProperty("ByClass");
        Assert.Equal("Choice", choice.GetProperty("Type").GetString());
        var branches = choice.GetProperty("Choices").EnumerateArray()
            .ToDictionary(c => c.GetProperty("StringEquals").GetString()!, c => c.GetProperty("Next").GetString());
        Assert.All(choice.GetProperty("Choices").EnumerateArray(), c => Assert.Equal("$.verified.class", c.GetProperty("Variable").GetString()));
        Assert.Equal("Prepare", branches["image"]);
        Assert.Equal("DeployBundle", branches["client"]);
        Assert.Equal("NoBranchForClass", choice.GetProperty("Default").GetString());

        Assert.Equal("VerifyBundle", states.GetProperty("DeployBundle").GetProperty("Next").GetString());
        Assert.Equal("Record", states.GetProperty("VerifyBundle").GetProperty("Next").GetString());
        Assert.Equal("$.rollout", states.GetProperty("VerifyBundle").GetProperty("ResultPath").GetString());
        Assert.Equal("RecordFailure", states.GetProperty("NoBranchForClass").GetProperty("Next").GetString());

        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    [Fact]
    public void WaitingForALease_OutlastsTheLease_AndIsRetriedByItsName()
    {
        var states = JsonDocument.Parse(Plan().Definition).RootElement.GetProperty("States");
        var retry = states.GetProperty("DeployBundle").GetProperty("Retry").EnumerateArray()
            .Single(r => Strings(r.GetProperty("ErrorEquals")).Contains(nameof(BundleDeployInProgress)));

        var waited = TimeSpan.FromSeconds(retry.GetProperty("IntervalSeconds").GetInt32() * retry.GetProperty("MaxAttempts").GetInt32());
        Assert.True(waited > BundleMarker.Lease, $"waits {waited}, lease {BundleMarker.Lease}");

        var verifyRetries = states.GetProperty("VerifyBundle").GetProperty("Retry").EnumerateArray()
            .SelectMany(r => Strings(r.GetProperty("ErrorEquals"))).ToList();
        Assert.Contains(nameof(BundleInvalidationInProgress), verifyRetries);
        // What cannot change by waiting is not retried.
        Assert.DoesNotContain(nameof(BundleNotDeployed), verifyRetries);
        Assert.DoesNotContain(nameof(BundleDeployRaced), states.GetProperty("DeployBundle").GetProperty("Retry").EnumerateArray()
            .SelectMany(r => Strings(r.GetProperty("ErrorEquals"))));
    }

    [Fact]
    public void WithoutClientApps_TheMachineIsTheClassOneMachine()
    {
        var unnamed = Config();
        foreach (var r in unnamed.Pipeline!.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;

        var plan = DeployerPlanner.Plan(unnamed, TargetAccount);
        var states = JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States");

        Assert.Equal("Prepare", states.GetProperty("Verify").GetProperty("Next").GetString());
        Assert.False(states.TryGetProperty("ByClass", out _));
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.DeployBundle || f.Handler == DeployerHandlers.VerifyBundle);
        var verify = Function(plan, DeployerHandlers.Verify);
        Assert.False(verify.Environment.ContainsKey(DeployerEnvironment.ArtifactStore));
        Assert.DoesNotContain("client/", verify.Policy);
        Assert.Empty(plan.ClientTargets!);
    }

    // ---------------------------------------------------------------------------------------
    //  The contract checker learns the branch
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AChoiceOnAFieldNothingWrites_IsAGap()
    {
        var plan = Plan();
        var broken = plan.Definition.Replace("\"$.verified.class\"", "\"$.verdict.class\"");

        var gaps = DeployerPlanner.ContractGaps(broken, plan.RolePolicy);

        Assert.Contains(gaps, g => g.Contains("ByClass branches on $.verdict.class"));
    }

    [Fact]
    public void ATransitionToAStateThatDoesNotExist_IsAGap()
    {
        var plan = Plan();
        var broken = plan.Definition.Replace("\"Next\": \"DeployBundle\"", "\"Next\": \"DeployBundel\"");

        var gaps = DeployerPlanner.ContractGaps(broken, plan.RolePolicy);

        Assert.Contains(gaps, g => g.Contains("ByClass goes to DeployBundel"));
    }

    [Fact]
    public void TheRole_MayInvokeTheBundleFunctions()
    {
        var plan = Plan();
        var withoutThem = plan.RolePolicy
            .Replace("\"arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-deploy-bundle\",", "");

        Assert.Contains(DeployerPlanner.ContractGaps(plan.Definition, withoutThem), g => g.Contains("DeployBundle invokes"));
    }

    // ---------------------------------------------------------------------------------------
    //  The build account's half
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBuildAccount_LetsVerifyReadClientRecordsAndHeadBundles_AndDeployBundleReadThem()
    {
        var build = PipelineBootstrapPlanner.Plan(Config(), BuildAccount);

        var recordSids = build.BuildRecordReadGrant!.Select(s => s["Sid"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "DeployerReadsImageRecordsDev", "DeployerListsImageRecordsDev", "DeployerReadsClientRecordsDev", "DeployerListsClientRecordsDev" }, recordSids);
        var clientRecords = build.BuildRecordReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsClientRecordsDev");
        Assert.Equal("arn:aws:s3:::scu-build-records-4df6-b9c6/client/*", clientRecords["Resource"]!.GetValue<string>());
        Assert.Equal("arn:aws:iam::503947800380:role/scu-dev-deployer-verify-fn",
            clientRecords["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());

        Assert.Equal("scu-artifacts-4df6-b9c6", build.ArtifactStore);
        var read = build.ArtifactReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsClientBundlesDev");
        Assert.Equal("s3:GetObjectVersion", read["Action"]!.GetValue<string>());
        Assert.Equal("arn:aws:s3:::scu-artifacts-4df6-b9c6/client/*", read["Resource"]!.GetValue<string>());
        Assert.Equal(new[] { "arn:aws:iam::503947800380:role/scu-dev-deployer-verify-fn", "arn:aws:iam::503947800380:role/scu-dev-deployer-deploy-bundle-fn" },
            read["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.AsArray().Select(n => n!.GetValue<string>()));

        // The build account's names for the environment's roles are the environment's own.
        var plan = Plan();
        Assert.Equal(DeployerPlanner.DeployBundleRoleName(Config()), Function(plan, DeployerHandlers.DeployBundle).RoleName);
        Assert.Equal(DeployerPlanner.VerifyRoleName(Config()), Function(plan, DeployerHandlers.Verify).RoleName);
    }

    [Fact]
    public void WithoutClientApps_TheBuildAccountGrantsWhatItDidBefore()
    {
        var unnamed = Config();
        foreach (var r in unnamed.Pipeline!.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;

        var build = PipelineBootstrapPlanner.Plan(unnamed, BuildAccount);

        Assert.Null(build.ArtifactReadGrant);
        Assert.DoesNotContain(build.BuildRecordReadGrant!, s => s.ToJsonString().Contains("client/"));
    }
}
