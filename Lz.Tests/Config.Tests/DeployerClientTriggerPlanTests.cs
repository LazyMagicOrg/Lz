using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The trigger and the sweep for client bundles, as planned (DecoupledCd.md P4 stage D): both rules match <c>client/</c>, the
/// start function routes each client repository to its app's bucket and may read client records, the sweep checks each client
/// repository's bundles, and the build account admits both roles to what they read — and none of it moves an environment
/// without a client target.
/// </summary>
public class DeployerClientTriggerPlanTests
{
    private const string TargetAccount = DeployerClientPlanTests.TargetAccount;
    private const string BuildAccount = "147440642635";
    private const string RecordStore = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";

    private static readonly DeployerTriggerInputs DevInputs = new("scu-dev-cluster", new[] { "mp" });

    /// <summary>Dev as configured today: the trigger and the alerts on, both client repositories naming their apps.</summary>
    private static SystemConfig Dev(bool clients = true, bool trigger = true, bool alerts = true)
    {
        var c = DeployerClientPlanTests.Config();
        c.Pipeline!.DeployOnBuildRecord = trigger;
        c.Pipeline.Alerts = alerts;
        if (!clients)
            foreach (var r in c.Pipeline.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;
        return c;
    }

    private static PipelineDeployer Plan(SystemConfig config)
        => DeployerPlanner.Plan(config, TargetAccount, config.Pipeline!.DeployOnBuildRecord ? DevInputs : null,
            DeployerPlanner.ClientApps(config).Count > 0 ? DeployerClientPlanTests.Distributions : null);

    private static DeployerFunction Function(PipelineDeployer plan, string handler) => plan.Functions.Single(f => f.Handler == handler);

    private static IEnumerable<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };

    private static (string Actions, string Resource, string? Prefix)[] Grants(DeployerFunction function)
        => Statements(function.Policy)
            .Where(s => !s.GetProperty("Sid").GetString()!.Contains("Log"))
            .Select(s => (
                string.Join(",", Strings(s.GetProperty("Action"))),
                string.Join(",", Strings(s.GetProperty("Resource"))),
                s.TryGetProperty("Condition", out var c) && c.TryGetProperty("StringLike", out var like) ? like.GetProperty("s3:prefix").GetString() : null))
            .ToArray();

    // ---------------------------------------------------------------------------------------
    //  The rules
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BothRules_MatchClientRecords_WithOnePattern()
    {
        var config = Dev();
        var start = Plan(config).Trigger!.EventPattern;
        var forward = PipelineBootstrapPlanner.Plan(config, BuildAccount).RecordForwarding!.EventPattern;

        Assert.Equal(forward, start);
        Assert.Equal(new[] { "image", "client" }, DeployerPlanner.TriggerRecordClasses(config));
        Assert.Equal(DeployerTrigger.EventPattern(BuildAccount, RecordStore, new[] { "image", "client" }), start);

        // Read out of the planned text itself, not compared with the function that wrote it — that comparison holds for any
        // pattern the function writes, including one that forgot the client prefix.
        using var pattern = JsonDocument.Parse(start);
        Assert.Equal(new[] { "image/", "client/" },
            pattern.RootElement.GetProperty("detail").GetProperty("object").GetProperty("key").EnumerateArray()
                .Select(m => m.GetProperty("prefix").GetString()));
    }

    [Fact]
    public void WithoutClientApps_BothRulesKeepTheImagePattern()
    {
        var config = Dev(clients: false);

        Assert.Equal(new[] { "image" }, DeployerPlanner.TriggerRecordClasses(config));
        Assert.Equal(DeployerTrigger.EventPattern(BuildAccount, RecordStore, new[] { "image" }), Plan(config).Trigger!.EventPattern);
        Assert.Equal(DeployerTrigger.EventPattern(BuildAccount, RecordStore, new[] { "image" }),
            PipelineBootstrapPlanner.Plan(config, BuildAccount).RecordForwarding!.EventPattern);
    }

    // ---------------------------------------------------------------------------------------
    //  The start function
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheStartFunction_RoutesEachClientRepository_ToItsAppsBucket_AndReadsWhatThePlanWrote()
    {
        var plan = Plan(Dev());
        var expected = new[]
        {
            new ClientTriggerRoute("client/scutara/scutarasellerapp/", "sellerapp", "scu---webapp-sellerapp-4df6-b9c6"),
            new ClientTriggerRoute("client/scutara/scutaraadminapp/", "adminapp", "scu---webapp-adminapp-4df6-b9c6"),
        };
        Assert.Equal(expected, plan.Trigger!.ClientRoutes);

        // Each route's bucket is the client target Verify checks the input against, so a triggered input passes that check.
        foreach (var route in expected)
            Assert.Equal(route.Bucket, plan.ClientTargets!.Single(t => BuildRecordFormat.PrefixFor("client", t.Repo) == route.RecordPrefix).Bucket);

        var env = Function(plan, DeployerHandlers.Start).Environment;
        var settings = TriggerSettings.Read(k => env.GetValueOrDefault(k));
        Assert.Equal(expected, settings.ClientRoutes);
        Assert.Equal(ArtifactStore, settings.ArtifactStore);
        Assert.Equal("image/scutara/scutaraservice/", settings.Routes.Single().RecordPrefix);
        Assert.Equal(7, env.Count);
    }

    [Fact]
    public void TheStartFunction_MayReadClientRecords_AndStillListsNothing()
    {
        var grants = Grants(Function(Plan(Dev()), DeployerHandlers.Start));

        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{RecordStore}/client/*", null), grants);
        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{RecordStore}/image/*", null), grants);
        Assert.DoesNotContain(grants, g => g.Actions.Contains("s3:List"));
        // It reads records only: nothing of the artifact store, and nothing of an app's bucket.
        Assert.DoesNotContain(grants, g => g.Resource.Contains(ArtifactStore) || g.Resource.Contains("---webapp-"));
    }

    [Fact]
    public void WithoutClientApps_TheStartFunctionIsWhatItWas()
    {
        var plan = Plan(Dev(clients: false));
        var start = Function(plan, DeployerHandlers.Start);

        Assert.Empty(plan.Trigger!.ClientRoutes ?? Array.Empty<ClientTriggerRoute>());
        Assert.Equal(5, start.Environment.Count);
        Assert.False(start.Environment.ContainsKey(DeployerEnvironment.TriggerClientRoutes));
        Assert.False(start.Environment.ContainsKey(DeployerEnvironment.ArtifactStore));
        Assert.DoesNotContain("client/", start.Policy);
    }

    [Fact]
    public void RoutesTooLargeTogether_ForOneFunctionsEnvironment_AreRefused()
    {
        // Lambda holds four kilobytes of variables per function, and the image routes may already take most of the planner's
        // share: the client routes count against the same limit, not beside it.
        var tenants = Enumerable.Range(0, 22).Select(i => $"t{i}").ToArray();
        var ex = Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(Dev(), TargetAccount,
            new DeployerTriggerInputs("scu-dev-cluster", tenants), DeployerClientPlanTests.Distributions));

        Assert.Contains("environment", ex.Message);

        // The same tenants without the client routes fit.
        DeployerPlanner.Plan(Dev(clients: false), TargetAccount, new DeployerTriggerInputs("scu-dev-cluster", tenants));
    }

    // ---------------------------------------------------------------------------------------
    //  The sweep
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheSweep_ChecksEachClientRepositorysBundles_AndReadsWhatThePlanWrote()
    {
        var plan = Plan(Dev());

        Assert.Equal(new[] { "client/scutara/scutarasellerapp/", "client/scutara/scutaraadminapp/" }, plan.Alerts!.BundlePrefixes);

        var env = Function(plan, DeployerHandlers.Corroborate).Environment;
        var settings = CorroborateSettings.Read(k => env.GetValueOrDefault(k));
        Assert.Equal(plan.Alerts.BundlePrefixes, settings.BundlePrefixes);
        Assert.Equal(ArtifactStore, settings.ArtifactStore);
        Assert.Equal("image/scutara/scutaraservice/", settings.Sources.Single().RecordPrefix);
    }

    [Fact]
    public void TheSweep_MayListBundleVersionsAndReadClientRecords_AndWritesNothingButAnomalies()
    {
        var grants = Grants(Function(Plan(Dev()), DeployerHandlers.Corroborate));

        Assert.Contains(("s3:ListBucketVersions", $"arn:aws:s3:::{ArtifactStore}", "client/*"), grants);
        Assert.Contains(("s3:GetObject", $"arn:aws:s3:::{RecordStore}/client/*", null), grants);
        // A missing record must read as absent — an anomaly — not as a denial that fails the whole sweep.
        Assert.Contains(("s3:ListBucket", $"arn:aws:s3:::{RecordStore}", "client/*"), grants);

        // Never a bundle's bytes: a version is judged by its record, not downloaded.
        Assert.DoesNotContain(grants, g => g.Resource.StartsWith($"arn:aws:s3:::{ArtifactStore}/", StringComparison.Ordinal));
        Assert.All(grants.Where(g => g.Actions.Contains("Put")), g => Assert.EndsWith("/anomalies/*", g.Resource));
    }

    [Fact]
    public void WithoutClientApps_TheSweepIsWhatItWas()
    {
        var plan = Plan(Dev(clients: false));
        var corroborate = Function(plan, DeployerHandlers.Corroborate);

        Assert.Empty(plan.Alerts!.BundlePrefixes ?? Array.Empty<string>());
        Assert.Equal(4, corroborate.Environment.Count);
        Assert.DoesNotContain("client/", corroborate.Policy);
        Assert.DoesNotContain(ArtifactStore, corroborate.Policy);
    }

    // ---------------------------------------------------------------------------------------
    //  The build account's half
    // ---------------------------------------------------------------------------------------

    private static string[] Principals(System.Text.Json.Nodes.JsonObject statement)
    {
        var node = statement["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!;
        return node is System.Text.Json.Nodes.JsonArray a ? a.Select(n => n!.GetValue<string>()).ToArray() : new[] { node.GetValue<string>() };
    }

    private static string Role(string name) => $"arn:aws:iam::{TargetAccount}:role/{name}";

    [Fact]
    public void TheBuildAccount_LetsTheStartAndSweepRolesReadClientRecords_AndTheSweepListThem()
    {
        var config = Dev();
        var grant = PipelineBootstrapPlanner.Plan(config, BuildAccount).BuildRecordReadGrant!;

        var read = grant.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsClientRecordsDev");
        Assert.Equal(new[]
        {
            Role(DeployerPlanner.VerifyRoleName(config)), Role(DeployerPlanner.StartFunctionRoleName(config)),
            Role(DeployerPlanner.CorroborateFunctionRoleName(config)),
        }, Principals(read));

        var list = grant.Single(s => s["Sid"]!.GetValue<string>() == "DeployerListsClientRecordsDev");
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role(DeployerPlanner.CorroborateFunctionRoleName(config)) }, Principals(list));
        Assert.Equal("client/*", list["Condition"]!["StringLike"]!["s3:prefix"]!.GetValue<string>());
    }

    [Fact]
    public void TheBuildAccount_LetsTheSweepListBundleVersions_ButReadNone()
    {
        var config = Dev();
        var grant = PipelineBootstrapPlanner.Plan(config, BuildAccount).ArtifactReadGrant!;

        var list = grant.Single(s => s["Sid"]!.GetValue<string>() == "DeployerListsClientBundlesDev");
        Assert.Equal(new[] { Role(DeployerPlanner.VerifyRoleName(config)), Role(DeployerPlanner.CorroborateFunctionRoleName(config)) }, Principals(list));

        var read = grant.Single(s => s["Sid"]!.GetValue<string>() == "DeployerReadsClientBundlesDev");
        Assert.DoesNotContain(Role(DeployerPlanner.CorroborateFunctionRoleName(config)), Principals(read));
        Assert.DoesNotContain(Role(DeployerPlanner.StartFunctionRoleName(config)), Principals(read));
    }

    [Fact]
    public void WithoutTheTriggerOrTheAlerts_TheClientGrantsNameVerifyAlone_AsStageCWroteThem()
    {
        // The statements applied on 2026-09-14 name Verify as a string. Neither later role, neither string becomes an array.
        var config = Dev(trigger: false, alerts: false);
        var build = PipelineBootstrapPlanner.Plan(config, BuildAccount);

        foreach (var sid in new[] { "DeployerReadsClientRecordsDev", "DeployerListsClientRecordsDev" })
        {
            var statement = build.BuildRecordReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == sid);
            Assert.Equal(Role(DeployerPlanner.VerifyRoleName(config)), statement["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
        }

        var bundleList = build.ArtifactReadGrant!.Single(s => s["Sid"]!.GetValue<string>() == "DeployerListsClientBundlesDev");
        Assert.Equal(Role(DeployerPlanner.VerifyRoleName(config)), bundleList["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
        Assert.Null(build.RecordForwarding);
    }
}
