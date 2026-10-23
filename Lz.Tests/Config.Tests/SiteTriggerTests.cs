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
