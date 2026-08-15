using Genie.App.Services;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The mapper browse-hold truth table (Shroom's #226 live-verify "$roomid
/// reads 0" bug). The hold's original release paths all assumed the browsed
/// zone does NOT contain the character; when it did — picking your own zone
/// at session start, or as a lost-tracking recovery — rooms kept matching,
/// no release ever fired, and the hold-gated globals sync froze $roomid at 0
/// for the whole session. Two policy decisions fix it: no latch when no zone
/// has ever matched, and release-on-new-room-match while held.
/// </summary>
public class BrowseHoldPolicyTests
{
    // ── ShouldLatch ─────────────────────────────────────────────────────────

    [Fact]
    public void No_latch_before_any_zone_has_matched()
    {
        // The session-start freeze: user picks a zone (maybe their own) before
        // the mapper has ever placed them — a starting guess, not browsing.
        Assert.False(BrowseHoldPolicy.ShouldLatch(userLoad: true, lastMatchedZoneFile: null, filename: "Map1a_Crossing"));
        Assert.False(BrowseHoldPolicy.ShouldLatch(userLoad: true, lastMatchedZoneFile: "",   filename: "Map1a_Crossing"));
    }

    [Fact]
    public void Latches_a_pick_away_from_the_matched_home_zone()
    {
        Assert.True(BrowseHoldPolicy.ShouldLatch(true, "Map1a_Crossing", "Map7_NTF"));
    }

    [Fact]
    public void Repicking_the_home_zone_is_not_browsing()
    {
        Assert.False(BrowseHoldPolicy.ShouldLatch(true, "Map1a_Crossing", "Map1a_Crossing"));
        Assert.False(BrowseHoldPolicy.ShouldLatch(true, "Map1a_Crossing", "MAP1A_CROSSING")); // case-insensitive
    }

    [Fact]
    public void Engine_driven_loads_never_latch()
    {
        Assert.False(BrowseHoldPolicy.ShouldLatch(userLoad: false, lastMatchedZoneFile: "Map1a_Crossing", filename: "Map7_NTF"));
    }

    [Fact]
    public void Preconnect_loads_never_latch()
    {
        // No server room id yet, so the caller passes userLoad: false. The
        // character isn't anywhere yet — nothing to browse away from.
        Assert.False(BrowseHoldPolicy.ShouldLatch(userLoad: false, lastMatchedZoneFile: null, filename: "Map1a_Crossing"));
    }

    // ── ShouldReleaseOnMatch ────────────────────────────────────────────────

    [Fact]
    public void Anchor_room_matching_is_a_boundary_stub_not_evidence()
    {
        // Stationary browse: the room the character occupied when browsing
        // began matches (a stub duplicated into the browsed map). Stay held.
        Assert.False(BrowseHoldPolicy.ShouldReleaseOnMatch(matched: true, liveServerRoomId: "10015", holdAnchorRoomId: "10015"));
    }

    [Fact]
    public void New_room_matching_in_the_browsed_zone_releases()
    {
        // The lost-tracking recovery: user picked the zone they're really in,
        // then moved — the new room resolves here, so this IS their zone.
        Assert.True(BrowseHoldPolicy.ShouldReleaseOnMatch(true, "10020", "10015"));
    }

    [Fact]
    public void No_match_or_no_live_room_keeps_the_hold()
    {
        Assert.False(BrowseHoldPolicy.ShouldReleaseOnMatch(matched: false, liveServerRoomId: "10020", holdAnchorRoomId: "10015"));
        Assert.False(BrowseHoldPolicy.ShouldReleaseOnMatch(matched: true,  liveServerRoomId: null,    holdAnchorRoomId: "10015"));
        Assert.False(BrowseHoldPolicy.ShouldReleaseOnMatch(matched: true,  liveServerRoomId: "",      holdAnchorRoomId: "10015"));
    }
}
