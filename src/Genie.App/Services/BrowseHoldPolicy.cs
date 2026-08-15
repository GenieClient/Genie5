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
    ///
    /// <para><paramref name="userLoad"/> is true only for a deliberate user
    /// gesture (dropdown pick, cross-zone click, <c>#mapper zone</c>). It is
    /// false for engine-driven switches (boundary-note follow, room-search
    /// auto-load) and for pre-connect loads where no server room id exists
    /// yet — neither is ever browsing.</para>
    ///
    /// <para><b>Decide by zone IDENTITY, never by match-probing</b> ("did the
    /// character resolve in the loaded zone?"). Probing is unsound and this is
    /// the invariant most likely to be "simplified" away by a later edit:
    /// cross-zone BOUNDARY STUBS duplicate the character's room into
    /// neighbouring maps by design, so browsing Map998 MATCHED its "Alfren's
    /// Ferry" stub, a probing check concluded "not browsing", lifted the
    /// freeze, followed the stub's note back toward Map1, mis-rematched, and
    /// the fingerprint auto-load landed on Map50 with $zoneid=50/$roomid=0
    /// (2026-08-06 video, frames 90→118). Identity is decidable; probing is
    /// not. <see cref="ShouldReleaseOnMatch"/> is the one place a match may
    /// speak, and only when the room differs from the hold anchor.</para>
    ///
    /// <para>Identity needs a KNOWN home to compare against — which is why an
    /// empty <paramref name="lastMatchedZoneFile"/> means "not browsing"
    /// rather than "browsing", and why the guard is load-bearing rather than
    /// defensive. A bare <c>!string.Equals(filename, null)</c> is true for
    /// EVERY pick, so the first zone load of a session counted as browsing
    /// even when the user picked their own character's map. That LATCHED, and
    /// every escape route was closed at once: the freeze stayed on (globals
    /// stuck at the seed "0") while LoadZone→Recalculate still resolved
    /// CurrentNode, so the canvas and the #66 status bar showed the real
    /// Zone/Room (Refresh has no browse guard — which is precisely why the two
    /// disagreed); the CurrentNodeChanged handler's <c>if (BrowsingZone)
    /// return</c> ran before it could record the home zone, so the hold could
    /// never self-correct; and RoomNotFoundInZone — the only release path that
    /// existed then — never fires while the room IS in the loaded zone.</para>
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
