using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The mapper browse-hold predicate (<see
/// cref="MapperViewModel.ShouldEnterBrowseHold"/>). Engaging the hold freezes
/// auto-follow AND the character-scoped script globals ($roomid / $zoneid /
/// $zonename / $roomnote, frozen in GenieCore.SyncMapperGlobals), so a wrong
/// answer here is not cosmetic — it desynchronises what scripts read from what
/// the map and the #66 status bar display.
///
/// <para>Shroom's 2026-08-14 report during the #226 hand-off verify: "$roomid
/// 0 when the map knows where I'm at and it shows the Zone and Room at the
/// bottom". Cause was the missing home-zone guard — with no
/// lastMatchedZoneFile yet, every first pick of a session read as browsing,
/// and because the character WAS in the picked zone none of the release paths
/// could ever fire.</para>
/// </summary>
public class MapperBrowseHoldTests
{
    // ── The regression: no home zone recorded yet ──────────────────────────

    [Fact]
    public void First_user_load_of_a_session_is_not_browsing()
    {
        // Shroom's case: user picks their own map before the mapper has
        // matched anything. Previously true → freeze latched on for the whole
        // session and $roomid read 0 forever.
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: true, filename: "Map127_Boar_Clan", lastMatchedZoneFile: null));
    }

    [Fact]
    public void Empty_home_zone_is_treated_the_same_as_null()
    {
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: true, filename: "Map127_Boar_Clan", lastMatchedZoneFile: ""));
    }

    // ── The behaviour the guard must NOT break (2026-08-04 / 2026-08-06) ───

    [Fact]
    public void User_load_of_a_different_zone_is_browsing()
    {
        // The ferry case: home is known, the user clicked elsewhere. The hold
        // must engage or travel tracking yanks the view back in the same beat.
        Assert.True(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: true, filename: "Map998_Transports", lastMatchedZoneFile: "Map1_Crossing"));
    }

    [Fact]
    public void User_load_of_the_characters_own_zone_is_not_browsing()
    {
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: true, filename: "Map1_Crossing", lastMatchedZoneFile: "Map1_Crossing"));
    }

    [Fact]
    public void Zone_identity_comparison_ignores_case()
    {
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: true, filename: "map1_crossing", lastMatchedZoneFile: "Map1_Crossing"));
    }

    [Fact]
    public void Engine_driven_switches_are_never_browsing()
    {
        // Boundary-note follow / room-search auto-load: the engine is
        // FOLLOWING the character across a zone edge, not browsing away.
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: false, filename: "Map998_Transports", lastMatchedZoneFile: "Map1_Crossing"));
    }

    [Fact]
    public void Preconnect_loads_are_never_browsing()
    {
        // No server room id yet → the caller passes userLoad: false. Nothing
        // to browse away from because the character isn't anywhere yet.
        Assert.False(MapperViewModel.ShouldEnterBrowseHold(
            userLoad: false, filename: "Map1_Crossing", lastMatchedZoneFile: null));
    }
}
