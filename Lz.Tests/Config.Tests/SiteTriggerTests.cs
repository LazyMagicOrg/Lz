using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The site's way into dev (DecoupledCd.md V-24, §15.7 V-K step 1): a site record's forwarded event starts one execution, into the
/// website bucket, named as a client's is; a route, a rule and the sweep take a site ENVIRONMENT's prefix and nothing wider; and
/// the bucket and domain come from the one tenant that routes its root to the website.
/// </summary>
public class SiteTriggerTests
{
    private const string BuildAccount = "147440642635";
    private const string Store = "scu-build-records-4df6-b9c6";
    private const string ArtifactStore = "scu-artifacts-4df6-b9c6";
    private const string Machine = "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer";
    private const string SiteBucket = "scu-mp--webapp-website-4df6-b9c6";
    private const string DevPrefix = "site/scutara/scutarawebsite/dev/";
    private const string DevKey = DevPrefix + "20261023T090000Z-39900000001-1.json";

    private static readonly TriggerRoute Service = new("image/scutara/scutaraservice/", new[]
    {
        new TriggerTarget("mp", new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")),
    });

    private static TriggerSettings Settings() => new(
        BuildAccount, Store, Machine, new[] { Service }, DeployerTrigger.DefaultRefs,
        new[] { new ClientTriggerRoute(DevPrefix, "site", SiteBucket) }, ArtifactStore);

    private static JsonObject Event(string key = DevKey) => new()
    {
        ["detail-type"] = "Object Created",
        ["source"] = "aws.s3",
        ["account"] = BuildAccount,
        ["detail"] = new JsonObject
        {
            ["bucket"] = new JsonObject { ["name"] = Store },
            ["object"] = new JsonObject { ["key"] = key },
            ["reason"] = "PutObject",
        },
    };

    [Fact]
    public void ASiteRecordsEvent_StartsOneExecution_IntoTheWebsiteBucket()
    {
        var only = Assert.Single(DeployerTrigger.ExecutionsFor(Event().ToJsonString(), Settings()));
        Assert.Equal("req-20261023T090000Z-39900000001-1-site-1", only.Name);
        Assert.Equal(DeployerTrigger.ClientInputFor(new RecordLocation(Store, DevKey), SiteBucket), only.Input);
    }

    // ---- the hold by kind (PerKindHold.md) ----

    // Shaped as an intake-written site record (the config record's shape in ConfigTriggerTests, a site's class and key).
    private const string SiteRecord = $$"""
        {
          "schema": 1, "class": "site",
          "builtFrom": { "repo": "Scutara/ScutaraWebsite", "commit": "5440a12cb99bbc2cd916ff4246a15c88592d2630", "lane": "published",
                         "packages": { }, "ref": "refs/heads/main" },
          "identity": { "kind": "bundle", "bucket": "{{ArtifactStore}}", "key": "site/scutara/scutarawebsite/dev/20261023T090000Z-39900000001-1.zip",
                        "versionId": "rEKPhw4vGJdF1RL3AWjDlbiXQOnlwYk6", "sha256": "160f48b074e84c2e0a3640a94cd8c8e0054562bf21b5bf9060ab661f7b249baf" },
          "builtAt": "2026-10-23T09:00:00Z", "builtBy": "a-person", "workflowRunId": "39900000001",
          "intake": { "runId": "39900000001", "runAttempt": 1, "environment": "dev", "jobId": "127960463356",
                      "workflowRef": "Scutara/ScutaraWebsite/.github/workflows/build-site.yml@refs/heads/main",
                      "githubDigest": "sha256:21b3ba60062cb3d41bf990af457773032f46f8410745566b6209519b61234ada",
                      "gate1": { "result": "passed", "evidence": "gate1/1233186125-39900000001-1.json" } }
        }
        """;

    private sealed class Records(string body) : IRecordStore
    {
        public Task<StoredRecord?> ReadAsync(string bucket, string key) => Task.FromResult<StoredRecord?>(new StoredRecord(body, "rv"));
    }

    private sealed class Executions(string hold) : IExecutions
    {
        public List<string> Starts { get; } = new();
        public Task<string?> HoldAsync(string parameter) => Task.FromResult<string?>(hold);
        public Task<bool> StartAsync(string stateMachineArn, string name, string input) { Starts.Add(name); return Task.FromResult(true); }
        public Task<string?> InputOfAsync(string executionArn) => Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData("sets", false)]
    [InlineData("off", false)]
    [InlineData("sites", true)]
    [InlineData("on", true)]
    public async Task ASiteRecord_IsHeldBySites_NotBySets(string hold, bool held)
    {
        var executions = new Executions(hold);
        var run = () => StartStep.RunAsync(Event().ToJsonString(), Settings() with { HoldParameter = "/scu/dev/pipeline/hold" }, new Records(SiteRecord), executions);
        if (held) Assert.Contains("holds sites", (await Assert.ThrowsAsync<DeployHeld>(run)).Message); else await run();
        Assert.Equal(held ? 0 : 1, executions.Starts.Count);
    }

    [Fact]
    public void AnotherEnvironmentsSiteRecord_HasNoRouteHere()
        => Assert.Throws<TriggerRefused>(() => DeployerTrigger.ExecutionsFor(
            Event("site/scutara/scutarawebsite/test/20261023T090000Z-39900000001-1.json").ToJsonString(), Settings()));

    [Theory]
    [InlineData("site/scutara/scutarawebsite/")]          // the repository, not one environment
    [InlineData("site/scutara/scutarawebsite/Dev/")]
    [InlineData("site/scutara/")]
    public void ARouteWiderOrOtherThanOneEnvironment_IsRefused(string prefix)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EncodeClientRoutes(new[] { new ClientTriggerRoute(prefix, "site", SiteBucket) }));
        Assert.Throws<InvalidOperationException>(() => DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }, new[] { prefix }));
        Assert.Throws<InvalidOperationException>(() => DeployerAlerts.EncodeBundleSources(new[] { prefix }));
    }

    [Fact]
    public void ASiteEnvironmentsPrefix_IsMatchedExactly_AfterTheClasses()
    {
        var pattern = DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image", "client" }, new[] { DevPrefix });
        Assert.Equal(new[] { "image/", "client/", DevPrefix }, DeployerTrigger.RecordPrefixesOf(pattern));
        // Without one, the pattern every environment already has is unchanged.
        Assert.Equal(DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }),
            DeployerTrigger.EventPattern(BuildAccount, Store, new[] { "image" }, Array.Empty<string>()));
    }

    [Fact]
    public void TheSweep_TakesASiteEnvironmentsPrefix()
        => Assert.Equal(new[] { DevPrefix }, DeployerAlerts.DecodeBundleSources(DeployerAlerts.EncodeBundleSources(new[] { DevPrefix })));

    // ---- the site's bucket and domain, from the tenant config ----

    private static SystemConfig System() => new() { SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6" };

    private static (string, TenantConfig) Tenant(string key, string domain, string? suffix = null, params (string Path, string App)[] sites) => (key, new TenantConfig
    {
        RootDomain = domain,
        TenantSuffix = suffix ?? "",
        Behaviors = new BehaviorsConfig { StaticSites = sites.Select(s => new StaticSiteBehavior { Path = s.Path, AppName = s.App }).ToList() },
    });

    [Fact]
    public void TheTenantThatRoutesItsRootToTheWebsite_GivesTheBucketAndDomain()
    {
        var inputs = DeployerBootstrapper.SiteInputsFor(System(), new[]
        {
            Tenant("mp", "aiproxydev.click", null, ("/", "website"), ("/explore/", "explore")),
            Tenant("other", "other.click", null, ("/docs/", "website")),
        });
        Assert.Equal(new DeployerSiteInputs("scu-mp--webapp-website-4df6-b9c6", "aiproxydev.click"), inputs);
    }

    [Fact]
    public void ATenantSuffix_NamesTheBucket()
        => Assert.Equal("scu-mp--webapp-website-ab12", DeployerBootstrapper.SiteInputsFor(System(), new[] { Tenant("mp", "x.click", "ab12", ("/", "website")) }).Bucket);

    [Fact]
    public void NoTenant_OrTwo_ServingTheSite_IsRefused()
    {
        Assert.Contains("0 do", Assert.Throws<InvalidOperationException>(() => DeployerBootstrapper.SiteInputsFor(System(), new[] { Tenant("mp", "x.click", null, ("/explore/", "explore")) })).Message);
        Assert.Contains("mp, b", Assert.Throws<InvalidOperationException>(() => DeployerBootstrapper.SiteInputsFor(System(), new[]
        {
            Tenant("mp", "x.click", null, ("/", "website")), Tenant("b", "y.click", null, ("/", "website")),
        })).Message);
    }
}
