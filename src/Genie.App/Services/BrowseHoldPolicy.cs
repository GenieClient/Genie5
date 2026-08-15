using System;

namespace Genie.App.Services;

/// <summary>
/// The mapper browse-hold's two decision points, extracted pure so the truth
/// table is unit-testable (the surrounding <c>MapperViewModel</c> machinery
/// needs a live engine + UI thread).
///
/// <para>The browse-hold (2026-08-04) freezes auto-follow while the user
/// deliberately looks at a different map. Its original release paths all
/// assumed the browsed zone does NOT contain the character — so when it DID
/// (the user picked their <b>own</b> zone: at session start before any match,
/// or as a manual recovery when tracking was lost), every room kept matching,
/// <c>RoomNotFoundInZone</c> never fired, and the hold latched for the rest of
/// the session. <c>SyncMapperGlobals</c> is gated on the hold, so
/// <c>$roomid</c>/<c>$zoneid</c> froze at 0 while the status line (whose
/// refresh is deliberately not gated) kept tracking — Shroom's live #226
/// verify: "$roomid reads 0 while the status bar shows Zone + Room", which
/// sends travel.cmd's <c>$roomid=0</c> branch into MOVERANDOM.</para>
/// </summary>
public static class BrowseHoldPolicy
{
    /// <summary>
    /// Should a USER-initiated zone load latch the browse-hold?
    /// Only when there is a home zone to protect: before any zone has ever
    /// matched (<paramref name="lastMatchedZoneFile"/> null), "browsing" is
    /// meaningless — the pick is a starting guess, and if it's wrong the very
    /// first room event misses and ordinary auto-follow corrects it. Latching
    /// instead froze <c>$roomid</c> for the whole session when the guess was
    /// RIGHT (matches never release the hold).
    /// </summary>
    public static bool ShouldLatch(bool userLoad, string? lastMatchedZoneFile, string filename) =>
        userLoad &&
        !string.IsNullOrEmpty(lastMatchedZoneFile) &&   // "" = unset, same as the ⌖ Return check
        !string.Equals(filename, lastMatchedZoneFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// While held, should a NODE MATCH in the browsed zone release the hold?
    /// A match alone means nothing — boundary stubs duplicate the character's
    /// room into neighbouring maps, so the anchor room matching is not
    /// evidence (stay held). But a match on a <b>different</b> server room
    /// than the hold anchor means the character is MOVING and the browsed
    /// zone is resolving their rooms — it is their zone; adopt it. (The
    /// mismatch-while-moving case — ferry rides past a browsed map — releases
    /// via RoomNotFoundInZone, unchanged.)
    /// </summary>
    public static bool ShouldReleaseOnMatch(bool matched, string? liveServerRoomId, string? holdAnchorRoomId) =>
        matched &&
        !string.IsNullOrEmpty(liveServerRoomId) &&
        !string.Equals(liveServerRoomId, holdAnchorRoomId, StringComparison.OrdinalIgnoreCase);
}
