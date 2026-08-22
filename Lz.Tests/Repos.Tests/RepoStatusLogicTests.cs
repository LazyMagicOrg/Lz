using Lz.Core.Repos;

namespace Lz.Tests.Repos.Tests;

/// <summary>
/// Pins the repo-report decision rules. Two of these encode bugs that were found the hard way and
/// would silently regress in a rewrite: the upstream preference order and the annotated-tag
/// dereference. Everything here is pure — no git, no filesystem.
/// </summary>
public class RepoStatusLogicTests
{
    // ---------- SelectUpstream ----------

    [Fact]
    public void SelectUpstream_PrefersTrackedBranch_OverOriginHead()
    {
        // THE bug this prevents: origin/HEAD is written at clone time and never updated by a
        // fetch, so after a master->main rename it can name a pruned ref and produce phantom
        // ahead-counts. The branch's own tracking config is authoritative.
        var chosen = RepoStatusLogic.SelectUpstream(
            trackedUpstream: "origin/main",
            originHead: "origin/master",
            hasOriginMain: true,
            hasOriginMaster: true);

        Assert.Equal("origin/main", chosen);
    }

    [Fact]
    public void SelectUpstream_FallsBackThroughOriginHead_ThenMain_ThenMaster()
    {
        Assert.Equal("origin/dev",
            RepoStatusLogic.SelectUpstream(null, "origin/dev", true, true));
        Assert.Equal("origin/main",
            RepoStatusLogic.SelectUpstream(null, null, true, true));
        Assert.Equal("origin/master",
            RepoStatusLogic.SelectUpstream(null, null, false, true));
        Assert.Null(RepoStatusLogic.SelectUpstream(null, null, false, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectUpstream_TreatsBlankAsAbsent(string blank)
    {
        // `git rev-parse @{u}` on a branch with no upstream exits non-zero; a defensive caller may
        // still hand us an empty string rather than null.
        Assert.Equal("origin/main", RepoStatusLogic.SelectUpstream(blank, blank, true, false));
    }

    // ---------- ParseLsRemoteTags ----------

    [Fact]
    public void ParseLsRemoteTags_PrefersDereferencedCommit_ForAnnotatedTags()
    {
        // An annotated tag emits BOTH lines. Taking the tag-object sha would compare against
        // something outside the commit graph, and every ancestry test would report "diverged".
        string[] lines =
        [
            "1111111111111111111111111111111111111111\trefs/tags/prod",
            "2222222222222222222222222222222222222222\trefs/tags/prod^{}",
        ];

        var map = RepoStatusLogic.ParseLsRemoteTags(lines, ["prod"]);

        Assert.Equal("2222222222222222222222222222222222222222", map["prod"]);
    }

    [Fact]
    public void ParseLsRemoteTags_DereferencedWins_RegardlessOfLineOrder()
    {
        string[] lines =
        [
            "2222222222222222222222222222222222222222\trefs/tags/prod^{}",
            "1111111111111111111111111111111111111111\trefs/tags/prod",
        ];

        Assert.Equal("2222222222222222222222222222222222222222",
            RepoStatusLogic.ParseLsRemoteTags(lines, ["prod"])["prod"]);
    }

    [Fact]
    public void ParseLsRemoteTags_KeepsLightweightTags_AndIgnoresUnrequested()
    {
        string[] lines =
        [
            "3333333333333333333333333333333333333333\trefs/tags/test",
            "4444444444444444444444444444444444444444\trefs/tags/v1.2.3",
            "5555555555555555555555555555555555555555\trefs/heads/main",
        ];

        var map = RepoStatusLogic.ParseLsRemoteTags(lines, ["prod", "test"]);

        Assert.Equal("3333333333333333333333333333333333333333", map["test"]);
        Assert.False(map.ContainsKey("v1.2.3"));
        Assert.False(map.ContainsKey("prod"));
    }

    [Fact]
    public void ParseLsRemoteTags_ToleratesGarbage()
    {
        string[] lines = ["", "   ", "no-tab-here", "\t"];
        Assert.Empty(RepoStatusLogic.ParseLsRemoteTags(lines, ["prod"]));
    }

    // ---------- ClassifyTag ----------

    [Fact]
    public void ClassifyTag_AtHead_EvenWhenNotPresentLocally()
    {
        // Sha equality is decisive; the presence probe cannot contradict it.
        var p = RepoStatusLogic.ClassifyTag("abc", "abc", presentLocally: false,
            isAncestorOfHead: false, headIsAncestorOfTag: false, distance: 0);

        Assert.Equal(TagRelation.AtHead, p.Relation);
        Assert.Equal("@HEAD", RepoStatusLogic.FormatTagPosition(p));
    }

    [Fact]
    public void ClassifyTag_NotFetched_ShortCircuitsAncestry()
    {
        var p = RepoStatusLogic.ClassifyTag("abcdef1234", "head", presentLocally: false,
            isAncestorOfHead: true, headIsAncestorOfTag: true, distance: 9);

        Assert.Equal(TagRelation.NotFetched, p.Relation);
        Assert.Equal("→abcdef1(not-fetched)", RepoStatusLogic.FormatTagPosition(p));
    }

    [Fact]
    public void ClassifyTag_AheadBehindAndDiverged()
    {
        Assert.Equal("+38", RepoStatusLogic.FormatTagPosition(
            RepoStatusLogic.ClassifyTag("t", "h", true, true, false, 38)));

        Assert.Equal("-2", RepoStatusLogic.FormatTagPosition(
            RepoStatusLogic.ClassifyTag("t", "h", true, false, true, 2)));

        Assert.Equal("<>abc1234", RepoStatusLogic.FormatTagPosition(
            RepoStatusLogic.ClassifyTag("abc1234def", "h", true, false, false, 0)));
    }

    // ---------- FormatTags ----------

    [Fact]
    public void FormatTags_CollapsesMarkersThatAgree()
    {
        var at = new TagPosition(TagRelation.AtHead, 0, "abc");
        Assert.Equal("prod, test @HEAD",
            RepoStatusLogic.FormatTags([("prod", at), ("test", at)]));
    }

    [Fact]
    public void FormatTags_ListsMarkersSeparatelyWhenTheyDiverge()
    {
        Assert.Equal("prod @HEAD, test +2", RepoStatusLogic.FormatTags(
        [
            ("prod", new TagPosition(TagRelation.AtHead, 0, "abc")),
            ("test", new TagPosition(TagRelation.Ahead, 2, "def")),
        ]));
    }

    [Fact]
    public void FormatTags_OmitsAbsentMarkers_AndReportsNoneWhenEmpty()
    {
        Assert.Equal("prod +1", RepoStatusLogic.FormatTags(
            [("prod", new TagPosition(TagRelation.Ahead, 1, "abc")), ("test", null)]));

        Assert.Equal("(none)", RepoStatusLogic.FormatTags([("prod", null), ("test", null)]));
        Assert.Equal("(none)", RepoStatusLogic.FormatTags([]));
    }

    [Fact]
    public void FormatTags_SingleMarkerIsNotCollapsed()
    {
        // Guard against an over-eager "all agree" rule turning one marker into a bare position.
        Assert.Equal("prod @HEAD", RepoStatusLogic.FormatTags(
            [("prod", new TagPosition(TagRelation.AtHead, 0, "abc"))]));
    }

    // ---------- TryParseLeftRight ----------

    [Fact]
    public void TryParseLeftRight_LeftIsBehind_RightIsAhead()
    {
        Assert.True(RepoStatusLogic.TryParseLeftRight("3\t7", out var behind, out var ahead));
        Assert.Equal(3, behind);
        Assert.Equal(7, ahead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("5")]
    public void TryParseLeftRight_RejectsUnusableOutput(string? output)
    {
        Assert.False(RepoStatusLogic.TryParseLeftRight(output, out _, out _));
    }

    // ---------- FormatSync ----------

    [Fact]
    public void FormatSync_InSyncAndDivergent()
    {
        Assert.Equal("in sync vs origin/main",
            RepoStatusLogic.FormatSync("origin/main", 0, 0, fetchRequested: true, fetchSucceeded: true));

        Assert.Equal("2 ahead / 1 behind vs origin/main",
            RepoStatusLogic.FormatSync("origin/main", 1, 2, fetchRequested: true, fetchSucceeded: true));
    }

    [Fact]
    public void FormatSync_AnnotatesOnlyWhenARequestedFetchFailed()
    {
        // Requested and failed -> the counts are degraded, say so.
        Assert.EndsWith("(no fetch)",
            RepoStatusLogic.FormatSync("origin/main", 0, 0, fetchRequested: true, fetchSucceeded: false));

        // --no-fetch -> stale counts are the caller's explicit choice, not a degraded result.
        Assert.Equal("in sync vs origin/main",
            RepoStatusLogic.FormatSync("origin/main", 0, 0, fetchRequested: false, fetchSucceeded: false));
    }

    [Fact]
    public void FormatSync_NoUpstream()
    {
        Assert.Equal("n/a", RepoStatusLogic.FormatSync(null, 0, 0, false, false));
        Assert.Equal("n/a (no fetch)", RepoStatusLogic.FormatSync(null, 0, 0, true, false));
    }

    // ---------- FormatTree / TruncateCommit ----------

    [Fact]
    public void FormatTree_CleanAndDirty()
    {
        Assert.Equal("clean", RepoStatusLogic.FormatTree(0));
        Assert.Equal("1 changed", RepoStatusLogic.FormatTree(1));
        Assert.Equal("12 changed", RepoStatusLogic.FormatTree(12));
    }

    [Fact]
    public void TruncateCommit_OnlyTruncatesWhenTooLong()
    {
        Assert.Equal("abc1234 short", RepoStatusLogic.TruncateCommit("abc1234 short"));
        Assert.Equal(string.Empty, RepoStatusLogic.TruncateCommit(null));

        var truncated = RepoStatusLogic.TruncateCommit(new string('x', 60), 48);
        Assert.Equal(49, truncated.Length);          // 48 + the ellipsis
        Assert.EndsWith("…", truncated);
    }
}
