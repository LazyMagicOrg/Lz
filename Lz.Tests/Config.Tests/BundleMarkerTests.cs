using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The app's deploy marker (DecoupledCd.md P4 stage C): the build time kept beside the app, so an older bundle is refused,
/// and the lease that keeps two deploys of one app from interleaving.
/// </summary>
public class BundleMarkerTests
{
    private const string Ours = "2026-09-14T17:32:58Z";
    private const string Older = "2026-09-14T10:00:00Z";
    private const string Newer = "2026-09-15T09:00:00Z";
    private const string Execution = "req-20260914T173258Z-34875215076-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T18:00:00Z");

    private static BundleMarker.DeployedBuild Deployed(string builtAt, string execution = "req-earlier-1")
        => new(builtAt, execution, "2026-09-14T12:00:00Z", "client/x/y/z.zip", "v1", new string('a', 64), new string('b', 64));

    /// <summary>A claim exactly as the deployer writes one, expiring at <paramref name="expires"/>.</summary>
    private static BundleMarker.LeaseClaim Claim(string builtAt, string execution, DateTimeOffset expires)
        => BundleMarker.Claimed(null, builtAt, execution, expires - BundleMarker.Lease).Claim!;

    [Fact]
    public void NoMarker_Proceeds_WithoutComparing()
    {
        var (verdict, reason) = BundleMarker.Decide(null, Ours, Execution, Now, allowOlderBuild: false);

        Assert.Equal(BundleMarker.Verdict.Proceed, verdict);
        Assert.Contains("not compared", reason);
    }

    [Theory]
    [InlineData(Older, BundleMarker.Verdict.Proceed)]
    [InlineData(Ours, BundleMarker.Verdict.Proceed)]    // the same build again: a re-run, or a retry after the release
    [InlineData(Newer, BundleMarker.Verdict.Superseded)]
    public void TheDeployedBuild_RefusesOnlyAnOlderOne(string serving, BundleMarker.Verdict expected)
    {
        var marker = new BundleMarker.MarkerState(Deployed(serving), null);

        Assert.Equal(expected, BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false).Verdict);
    }

    [Fact]
    public void AnOlderBuild_IsDeployedOnPurpose_OnlyWithAllowOlderBuild()
    {
        var marker = new BundleMarker.MarkerState(Deployed(Newer), null);

        var (verdict, reason) = BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: true);

        Assert.Equal(BundleMarker.Verdict.Proceed, verdict);
        Assert.Contains("allowOlderBuild", reason);
        Assert.Contains("Nothing was written", BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false).Reason);
    }

    [Fact]
    public void AnotherExecutionsLiveLease_IsWaitedOn_WhenItsBuildIsNotNewer()
    {
        var marker = new BundleMarker.MarkerState(Deployed(Older), Claim(Older, "req-other-1", Now.AddMinutes(5)));

        var (verdict, reason) = BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false);

        Assert.Equal(BundleMarker.Verdict.Wait, verdict);
        Assert.Contains("req-other-1", reason);
    }

    [Fact]
    public void AnotherExecutionsLiveLease_ForANewerBuild_Supersedes_RatherThanWaitingToPutTheOlderBack()
    {
        var marker = new BundleMarker.MarkerState(Deployed(Older), Claim(Newer, "req-other-1", Now.AddMinutes(5)));

        Assert.Equal(BundleMarker.Verdict.Superseded, BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false).Verdict);
        // Asked on purpose, it waits its turn instead.
        Assert.Equal(BundleMarker.Verdict.Wait, BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: true).Verdict);
    }

    [Fact]
    public void AnExpiredLease_BelongsToADeployThatDied_AndIsTakenOver()
    {
        var marker = new BundleMarker.MarkerState(Deployed(Older), Claim(Newer, "req-dead-1", Now.AddSeconds(-1)));

        Assert.Equal(BundleMarker.Verdict.Proceed, BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false).Verdict);
    }

    [Fact]
    public void ThisExecutionsOwnLease_IsReentered()
    {
        // A retried DeployBundle meets the claim its first attempt wrote.
        var marker = new BundleMarker.MarkerState(Deployed(Older), Claim(Ours, Execution, Now.AddMinutes(10)));

        Assert.Equal(BundleMarker.Verdict.Proceed, BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false).Verdict);
    }

    [Fact]
    public void WhatAClaimWrites_TheNextExecutionCanRead()
    {
        // Written by one execution's Claimed, judged by the next one's Decide: the times must survive the trip.
        var written = BundleMarker.Parse(BundleMarker.Serialize(BundleMarker.Claimed(null, Older, "req-other-1", Now)));

        Assert.Equal("2026-09-14T18:15:00Z", written.Claim!.ExpiresAt);
        Assert.Equal(BundleMarker.Verdict.Wait, BundleMarker.Decide(written, Ours, Execution, Now, allowOlderBuild: false).Verdict);
        Assert.Equal(BundleMarker.Verdict.Proceed, BundleMarker.Decide(written, Ours, Execution, Now.AddMinutes(16), allowOlderBuild: false).Verdict);
    }

    [Fact]
    public void AClaim_KeepsTheDeployedBuild_AndLastsTheLease_AndARelease_ClearsIt()
    {
        var marker = new BundleMarker.MarkerState(Deployed(Older), null);

        var claimed = BundleMarker.Claimed(marker, Ours, Execution, Now);
        Assert.Equal(marker.Deployed, claimed.Deployed);
        Assert.Equal(Execution, claimed.Claim!.Execution);
        Assert.Equal(Now + BundleMarker.Lease, DateTimeOffset.Parse(claimed.Claim.ExpiresAt));

        var released = BundleMarker.Released(Deployed(Ours, Execution));
        Assert.Null(released.Claim);
        Assert.Equal(released, BundleMarker.Parse(BundleMarker.Serialize(released)));
        Assert.Equal(claimed, BundleMarker.Parse(BundleMarker.Serialize(claimed)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schema\":2}")]
    [InlineData("{\"schema\":1,\"deployed\":{\"builtAt\":\"2026-09-14T17:32:58Z\"}}")]
    public void AMarkerTheDeployerDidNotWrite_IsRefused_NotGuessedAt(string json)
    {
        var ex = Assert.Throws<DeployRefused>(() => BundleMarker.Parse(json));

        Assert.Equal("marker", ex.Refusals.Single().Check);
    }

    [Fact]
    public void ABuildTimeThatIsNotUtc_IsRefused()
    {
        var marker = new BundleMarker.MarkerState(Deployed("2026-09-14T17:32:58"), null);

        Assert.Throws<DeployRefused>(() => BundleMarker.Decide(marker, Ours, Execution, Now, allowOlderBuild: false));
        Assert.Throws<DeployRefused>(() => BundleMarker.Decide(null, "yesterday", Execution, Now, allowOlderBuild: false));
    }

    [Fact]
    public void TheLease_OutlastsADeployBundleInvocation()
        => Assert.True(BundleMarker.Lease > TimeSpan.FromSeconds(
            DeployerPlanner.Plan(DeployerClientPlanTests.Config(), DeployerClientPlanTests.TargetAccount, clientInputs: DeployerClientPlanTests.Distributions)
                .Functions.Single(f => f.Handler == DeployerHandlers.DeployBundle).TimeoutSeconds));

    // ---------------------------------------------------------------------------------------
    //  Client targets as the environment and the state carry them
    // ---------------------------------------------------------------------------------------

    private static readonly ClientTarget Seller = new(
        "Scutara/ScutaraSellerApp", "sellerapp", "scu---webapp-sellerapp-4df6-b9c6", "seller/", new[] { "E31GJ01SW2CEEA" });

    [Fact]
    public void AClientTarget_RoundTrips()
    {
        var decoded = Assert.Single(ClientTargets.Decode(ClientTargets.Encode(new[] { Seller })));

        Assert.Equal(Seller.Repo, decoded.Repo);
        Assert.Equal(Seller.Bucket, decoded.Bucket);
        Assert.Equal(Seller.Distributions, decoded.Distributions);
        Assert.Equal("wwwroot/seller/", decoded.KeyPrefix);
        Assert.Equal("/seller*", decoded.InvalidationPath);
    }

    [Theory]
    [InlineData("keyPrefix", "wwwroot/")]
    [InlineData("invalidationPath", "/*")]
    public void ATargetWhosePrefixItsBasePathDoesNotGive_IsRefused(string field, string value)
    {
        // A hand-edited target could otherwise aim the mirror's deletes at the whole bucket, or clear every app's cache.
        var json = ClientTargets.ToJson(Seller);
        json[field] = value;

        Assert.Throws<InvalidOperationException>(() => ClientTargets.FromJson(json));
    }

    [Fact]
    public void ATargetWithNoDistribution_IsRefused()
    {
        var json = ClientTargets.ToJson(Seller);
        json["distributions"] = new JsonArray();

        Assert.Throws<InvalidOperationException>(() => ClientTargets.FromJson(json));
    }
}
