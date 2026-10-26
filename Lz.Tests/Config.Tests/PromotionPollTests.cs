using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Test's promotion poller, its decision (DecoupledCd.md §12 P5 stage I, §5.3): every minute, of the predecessor's set markers,
/// the newest that is neither decided nor started here - the newer of two sets being the one none of whose members is older
/// than the other's, by each record's builtAt and then its run attempt - unless a set is in flight here, or the hold is on or
/// missing. Two sets neither of which dominates start nothing and alarm; a marker whose execution closed without a decision
/// is not restarted and alarms; markers older than a set deployed here are decided superseded.
/// </summary>
public class PromotionPollTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T02:00:00Z");

    private static PassMarkerMember Member(string cls, string repo, string stem) => new(
        cls, repo, new PassMarkerRecordReference("scu-build-records-4df6-b9c6", $"{cls}/{repo.ToLowerInvariant()}/{stem}.json", "rv"),
        cls == "image" ? new BuildRecordIdentity("image", Digest: "sha256:" + stem.GetHashCode().ToString("x8").PadLeft(64, '0'))
                       : new BuildRecordIdentity("bundle", Bucket: "a", Key: $"{cls}/{repo.ToLowerInvariant()}/{stem}.zip", VersionId: "bv-" + stem, Sha256: new string('c', 64)),
        "1.0.0", new BuildRecordBuiltFrom(repo, new string('1', 40), "published", new Dictionary<string, string>()),
        new PassMarkerReference($"deploys/{stem}.json", "dv"));

    /// <summary>A set of the Service at <paramref name="service"/> and SellerApp at <paramref name="seller"/>, each a record stem.</summary>
    private static SeenMarker Set(string service, string seller, int minutesAgo = 1)
    {
        var members = new[] { Member("image", "scutara/scutaraservice", service), Member("client", "scutara/scutarasellerapp", seller) };
        var id = PassMarkerFormat.MarkerIdFor(members);
        var marker = new PassMarker(1, id, "dev", members, new PassMarkerEvidence(new PassMarkerReference("tests/x.json", "tv")), "req-x", "2026-10-26T00:00:00Z");
        return new SeenMarker(PassMarkerFormat.KeyFor(id), "mv-" + id[5..11], marker, Now.AddMinutes(-minutesAgo));
    }

    /// <summary>A site marker (V-K step 5) for the Website run at <paramref name="stem"/>.</summary>
    private static SeenMarker Site(string stem, int minutesAgo = 1)
    {
        var members = new[] { Member("site", "scutara/scutarawebsite", stem) };
        var id = PassMarkerFormat.MarkerIdFor(members);
        var marker = new PassMarker(1, id, "dev", members, new PassMarkerEvidence(null), "req-s", "2026-10-26T00:00:00Z");
        return new SeenMarker(PassMarkerFormat.KeyFor(id), "mv-" + id[5..11], marker, Now.AddMinutes(-minutesAgo));
    }

    private static readonly SeenMarker OldSite = Site("20261025T100000Z-7-1");
    private static readonly SeenMarker NewSite = Site("20261026T010000Z-8-1");

    private static readonly SeenMarker Old = Set("20261025T100000Z-1-1", "20261024T100000Z-9-1");
    private static readonly SeenMarker NewService = Set("20261025T200000Z-2-1", "20261024T100000Z-9-1");
    private static readonly SeenMarker NewSeller = Set("20261025T100000Z-1-1", "20261025T210000Z-8-1");
    private static readonly SeenMarker Both = Set("20261025T200000Z-2-1", "20261025T210000Z-8-1");

    private static PollState State(bool held = false, bool inFlight = false, IEnumerable<SeenMarker>? started = null,
        IDictionary<SeenMarker, string>? decided = null, DateTimeOffset? lastClosed = null) => new(
        held, inFlight,
        (started ?? Array.Empty<SeenMarker>()).Select(m => m.Marker.MarkerId).ToHashSet(),
        (decided ?? new Dictionary<SeenMarker, string>()).ToDictionary(d => d.Key.Marker.MarkerId, d => d.Value),
        lastClosed);

    // ---- ordering ----

    [Fact]
    public void AMembersAge_IsItsRecordsBuiltAt_ThenItsRunAttempt_AndAPrePushPathKeyIsAttemptOne()
    {
        Assert.True(PromotionPoll.AgeOf("image/scutara/scutaraservice/20261025T100000Z-5-2.json").CompareTo(PromotionPoll.AgeOf("image/scutara/scutaraservice/20261025T100000Z-5-1.json")) > 0);
        Assert.True(PromotionPoll.AgeOf("image/scutara/scutaraservice/20261025T100001Z-1-1.json").CompareTo(PromotionPoll.AgeOf("image/scutara/scutaraservice/20261025T100000Z-9-9.json")) > 0);
        Assert.Equal(PromotionPoll.AgeOf("image/scutara/scutaraservice/20260912T171147Z-34707373278-1.json"), PromotionPoll.AgeOf("image/scutara/scutaraservice/20260912T171147Z-34707373278.json"));
    }

    [Fact]
    public void ASetDominatesAnother_WhenNoneOfItsMembersIsOlder()
    {
        Assert.True(PromotionPoll.Dominates(NewService.Marker, Old.Marker));
        Assert.True(PromotionPoll.Dominates(Both.Marker, NewService.Marker));
        Assert.False(PromotionPoll.Dominates(Old.Marker, NewService.Marker));
        Assert.False(PromotionPoll.Dominates(NewService.Marker, NewSeller.Marker));
        Assert.False(PromotionPoll.Dominates(NewSeller.Marker, NewService.Marker));
    }

    // ---- what it starts ----

    [Fact]
    public void OfTwoUndecidedMarkers_OnlyTheNewerIsStarted()
    {
        var d = PromotionPoll.Decide(new[] { Old, NewService }, State(), Now);
        Assert.Equal(NewService, d.Start);
        Assert.Empty(d.Alerts);
    }

    [Fact]
    public void ADecidedMarker_IsNeverStarted()
    {
        var d = PromotionPoll.Decide(new[] { Old, NewService }, State(decided: new Dictionary<SeenMarker, string> { [NewService] = "refused" }), Now);
        Assert.Equal(Old, d.Start);
    }

    [Fact]
    public void TwoMarkersNeitherOfWhichDominates_StartNothing_AndAlarm()
    {
        var d = PromotionPoll.Decide(new[] { NewService, NewSeller }, State(), Now);
        Assert.Null(d.Start);
        Assert.Contains(d.Alerts, a => a.Contains("neither"));
    }

    [Fact]
    public void NothingStarts_WhileASetIsInFlight_AndNothingAlarmsForWaiting()
    {
        var d = PromotionPoll.Decide(new[] { NewService }, State(inFlight: true), Now);
        Assert.Null(d.Start);
        Assert.Empty(d.Alerts);
    }

    [Fact]
    public void UnderAHeldOrMissingHold_NothingIsStartedOrDecided()
    {
        var d = PromotionPoll.Decide(new[] { Old, NewService }, State(held: true, decided: new Dictionary<SeenMarker, string> { [Both] = "deployed" }), Now);
        Assert.Null(d.Start);
        Assert.Empty(d.Supersede);
        Assert.Empty(d.Alerts);
    }

    [Fact]
    public void AMarkerWhoseExecutionClosedWithoutADecision_IsNotRestarted_AndAlarms()
    {
        var d = PromotionPoll.Decide(new[] { NewService }, State(started: new[] { NewService }), Now);
        Assert.Null(d.Start);
        Assert.Contains(d.Alerts, a => a.Contains(NewService.Marker.MarkerId) && a.Contains("without a decision"));
    }

    [Fact]
    public void AStartedMarkerStillRunning_IsNeitherRestartedNorSuperseded()
    {
        // Both is deployed here and dominates Old, so Old would be superseded - were its execution not still running.
        var d = PromotionPoll.Decide(new[] { Old, NewService, Both }, State(inFlight: true, started: new[] { Old }, decided: new Dictionary<SeenMarker, string> { [Both] = "deployed" }), Now);
        Assert.DoesNotContain(Old, d.Supersede);
        Assert.Contains(NewService, d.Supersede);
        Assert.Null(d.Start);
    }

    // ---- superseded ----

    [Fact]
    public void MarkersOlderThanASetDeployedHere_AreDecidedSuperseded_AndNotStarted()
    {
        var d = PromotionPoll.Decide(new[] { Old, NewService, Both }, State(decided: new Dictionary<SeenMarker, string> { [Both] = "deployed" }), Now);
        Assert.Equal(new[] { Old, NewService }.ToHashSet(), d.Supersede.ToHashSet());
        Assert.Null(d.Start);
    }

    [Fact]
    public void AMarkerNoDeployedSetDominates_IsNotSuperseded()
    {
        var d = PromotionPoll.Decide(new[] { NewSeller }, State(decided: new Dictionary<SeenMarker, string> { [NewService] = "deployed" }), Now);
        Assert.Empty(d.Supersede);
        Assert.Equal(NewSeller, d.Start);
    }

    [Fact]
    public void ARefusedOrFailedSet_SupersedesNothing()
    {
        var d = PromotionPoll.Decide(new[] { Old }, State(decided: new Dictionary<SeenMarker, string> { [Both] = "failed" }), Now);
        Assert.Empty(d.Supersede);
        Assert.Equal(Old, d.Start);
    }

    // ---- the late alarm (§5.3 rule 4) ----

    [Fact]
    public void ANewestMarkerStartableForMoreThanTenMinutes_AlarmsAsItIsStarted()
    {
        var late = Set("20261025T230000Z-3-1", "20261025T230000Z-7-1", minutesAgo: 11);
        var d = PromotionPoll.Decide(new[] { late }, State(), Now);
        Assert.Equal(late, d.Start);
        Assert.Contains(d.Alerts, a => a.Contains("ten minutes"));
    }

    [Fact]
    public void TheTenMinutesCountFromTheLastExecutionsClose_WhenThatIsLater()
    {
        var waited = Set("20261025T230000Z-3-1", "20261025T230000Z-7-1", minutesAgo: 30);
        Assert.Empty(PromotionPoll.Decide(new[] { waited }, State(lastClosed: Now.AddMinutes(-2)), Now).Alerts);
    }

    // ---- the execution it starts ----

    // ---- the site, beside the sets (V-K step 5) ----

    [Fact]
    public void ASiteMarker_IsStarted_AsASiteExecution()
    {
        var d = PromotionPoll.Decide(new[] { NewSite }, State(), Now);
        Assert.Same(NewSite, d.Start);
        Assert.StartsWith("ps-", PromotionPoll.ExecutionNameFor(NewSite.Marker.MarkerId));
        Assert.Equal(PromotionPoll.ExecutionNameFor(NewSite.Marker.MarkerId), PromotionPoll.ExecutionNameFor(NewSite.Marker.MarkerId));
    }

    [Fact]
    public void ASetAndASite_NeverJudgeEachOther()
    {
        // They share no member, so each "dominates" the other vacuously: neither may start nothing for that, nor supersede.
        var d = PromotionPoll.Decide(new[] { Both, NewSite }, State(), Now);
        Assert.NotNull(d.Start);
        Assert.Empty(d.Supersede);
        Assert.Empty(d.Alerts);

        var deployedSet = PromotionPoll.Decide(new[] { Both, NewSite }, State(decided: new Dictionary<SeenMarker, string> { [Both] = "deployed" }), Now);
        Assert.Same(NewSite, deployedSet.Start);
        Assert.Empty(deployedSet.Supersede);
    }

    [Fact]
    public void OfASetAndASite_TheOneWrittenFirst_StartsFirst_AndTheOtherWaits()
    {
        var site = Site("20261026T010000Z-8-1", minutesAgo: 5);
        var set = Set("20261025T200000Z-2-1", "20261025T210000Z-8-1", minutesAgo: 2);
        var d = PromotionPoll.Decide(new[] { set, site }, State(), Now);
        Assert.Same(site, d.Start);
        Assert.Empty(d.Supersede);

        Assert.Same(set, PromotionPoll.Decide(new[] { Site("20261026T010000Z-8-1", minutesAgo: 1), set }, State(), Now).Start);
    }

    [Fact]
    public void OneExecutionAtATime_AcrossSetsAndSites()
        => Assert.Null(PromotionPoll.Decide(new[] { NewSite }, State(inFlight: true), Now).Start);

    [Fact]
    public void AnOlderSite_IsSupersededByANewerSiteDeployedHere_AndOnlyByASite()
    {
        var d = PromotionPoll.Decide(new[] { OldSite, NewSite }, State(decided: new Dictionary<SeenMarker, string> { [NewSite] = "deployed" }), Now);
        Assert.Equal(new[] { OldSite }, d.Supersede);
        Assert.Null(d.Start);

        Assert.Same(NewSite, PromotionPoll.Decide(new[] { OldSite, NewSite }, State(), Now).Start);
    }

    [Fact]
    public void TheExecutionsName_IsDerivedFromTheMarker()
    {
        Assert.Matches("^pm-[0-9a-f]{20}-1$", PromotionPoll.ExecutionNameFor(NewService.Marker.MarkerId));
        Assert.NotEqual(PromotionPoll.ExecutionNameFor(NewService.Marker.MarkerId), PromotionPoll.ExecutionNameFor(Old.Marker.MarkerId));
    }
}
