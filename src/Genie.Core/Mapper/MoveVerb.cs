namespace Genie.Core.Mapper;

/// <summary>
/// Normalizes a map arc's move command into the actual game command to send.
///
/// <para>Genie 4 map data encodes non-game <b>pacing prefixes</b> on some arcs —
/// <c>move="rt north"</c>, <c>move="slow south"</c> — directives the old
/// <c>.automapper</c> movement script consumed (wait-for-roundtime, pause), NOT
/// DragonRealms verbs. Sent verbatim they return <i>"Please rephrase that
/// command."</i> and stall travel (public #123). We strip the known pacing
/// prefix and send the bare movement; Genie 5's walker already paces
/// one-move-per-room and gates on roundtime, so the directive's intent is
/// preserved. Real DR verbs (<c>go</c>, <c>climb</c>, <c>swim</c>, <c>dive</c>,
/// <c>search</c>) are left untouched.</para>
/// </summary>
public static class MoveVerb
{
    /// <summary>Leading tokens that are pacing directives, not DR verbs. Confirmed
    /// against live play: <c>rt</c> (wait-for-roundtime) and <c>slow</c> (#123) both
    /// return "Please rephrase that command." if sent. <c>room</c> is confirmed
    /// against the G4 automapper script itself (automapper.cmd MOVE.ROOM): it just
    /// waits for pending movement to settle before sending — which Genie 5's
    /// one-move-per-room pacing already guarantees — so stripping preserves the
    /// intent (used on the Riverhaven/Shard/Ratha thief-door arcs, e.g.
    /// <c>move="room sear;-3knock concealed door;…"</c>). The G4 script recognised
    /// a larger family still (<c>wait/web/muck/ice/…</c>); those need confirmation
    /// before we strip them. Add to this list as more are verified.</summary>
    private static readonly string[] PacingPrefixes = { "rt", "slow", "room" };

    /// <summary>Strip a known pacing prefix from <paramref name="verb"/> (e.g.
    /// "slow south" → "south", "rt north" → "north"); return it unchanged if it
    /// carries no such prefix. Only strips when the prefix is followed by a space
    /// and a non-empty remainder, so "slower" or a bare "rt" are left alone.</summary>
    public static string Normalize(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb)) return verb ?? string.Empty;
        var v = verb.TrimStart();
        foreach (var p in PacingPrefixes)
            if (v.Length > p.Length + 1 &&
                v[p.Length] == ' ' &&
                v.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return v[(p.Length + 1)..].TrimStart();
        return v;
    }

    /// <summary>Movement verbs that can follow a <c>search</c> directive in
    /// community map data. Only these mark the leading "search" as a directive —
    /// an arc like "search bushes" is a literal game command and stays whole.</summary>
    private static readonly string[] SearchInnerVerbs = { "go", "climb" };

    /// <summary>
    /// DR verbs whose whole purpose is to leave the room. Used by
    /// <see cref="IsMovementCommand"/>; deliberately does NOT include
    /// <c>search</c> (a bare search stays put — only the map-data
    /// <c>"search go …"</c> directive form moves, and that is recognised by its
    /// inner verb).
    /// </summary>
    private static readonly string[] MovementVerbs =
    {
        "go", "climb", "swim", "dive", "wade", "ford", "crawl", "squeeze",
        "enter", "exit", "board", "disembark", "jump", "leap", "slide",
        "ascend", "descend", "walk", "run", "follow",
    };

    /// <summary>
    /// True when <paramref name="command"/> is a command that attempts to change
    /// rooms — a compass/vertical primitive ("n", "northwest", "up", "out"), or a
    /// phrase led by a movement verb ("swim north", "go shore", "climb bank").
    ///
    /// <para>This is the mapper's "did the player just try to move?" test, and it
    /// has to be verb-based rather than arc-based. The obvious alternative —
    /// "does this command match an authored arc on the current node?" — silently
    /// fails wherever it is needed most: a community map that authors the
    /// Segoltha arcs as <c>move="north"</c> while the player's script sends
    /// <c>swim north</c> would not match, and neither would anything at all when
    /// the mapper hasn't placed the player yet (CurrentNode null). Both cases are
    /// exactly the identical-room corridors where this signal is the only
    /// evidence a move happened.</para>
    ///
    /// <para>Pacing prefixes are stripped first, so <c>"rt north"</c> and
    /// <c>"slow south"</c> classify as movement.</para>
    /// </summary>
    /// <summary>
    /// Recover the compass direction from a two-token movement phrase —
    /// <c>"swim north"</c> → <see cref="Direction.North"/>, <c>"go se"</c> →
    /// <see cref="Direction.SouthEast"/>. False for anything else.
    ///
    /// <para>Strictly two tokens by design. <c>"go north gate"</c> is a move
    /// through a portal named "north gate", NOT a move northward, and treating
    /// it as compass would walk the wrong arc. Three-token phrases keep falling
    /// back to whole-string MoveCommand matching, which is what they need.</para>
    ///
    /// <para>The direction this returns is for MATCHING ONLY — it must not be
    /// used to author an arc, or recording a Segoltha swim would write
    /// <c>move="north"</c> onto a river arc that actually requires
    /// <c>swim north</c>.</para>
    /// </summary>
    public static bool TryGetCompassTarget(string? command, out Direction direction)
    {
        direction = Direction.None;
        if (string.IsNullOrWhiteSpace(command)) return false;

        var v = Normalize(command.Split(';')[0].Trim());
        var space = v.IndexOf(' ');
        if (space < 0) return false;

        var verb = v[..space];
        if (!MovementVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) return false;

        var rest = v[(space + 1)..].Trim();
        if (rest.Contains(' ')) return false;   // "go north gate" is not a compass move

        direction = DirectionHelper.Parse(rest);
        return direction != Direction.None;
    }

    public static bool IsMovementCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // Take the first ';' segment — the pipeline dispatches those separately.
        var v = Normalize(command.Split(';')[0].Trim());
        if (v.Length == 0) return false;

        if (DirectionHelper.Parse(v) != Direction.None) return true;

        var space = v.IndexOf(' ');
        var verb  = space < 0 ? v : v[..space];

        // "search go trampled path" — the directive form moves; "search bushes"
        // does not. Same for "objsearch outcropping climb handholds".
        if (TryParseSearchDirective(v, out _)) return true;
        if (TryParseObjSearchDirective(v, out _, out _)) return true;

        return MovementVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recognize the Genie 4 <b>search directive</b> arc form: <c>move="search go
    /// trampled path"</c> (also <c>search climb …</c>) marks a <b>hidden exit</b> —
    /// the destination only opens after a successful in-room <c>search</c>. The
    /// Genie 4 <c>.automapper</c> script (MOVE.SEARCH) consumed the prefix: try the
    /// inner move first (the path may already be open), and only on "I could not
    /// find what you were referring to." fall back to search-and-retry. 35+ arcs
    /// across the community map set use this idiom, so the walker must honour it.
    /// <para>Returns true with the inner move ("go trampled path") when
    /// <paramref name="verb"/> is a search directive; false for plain moves and
    /// for literal search commands ("search bushes") — the directive form is only
    /// recognized when the remainder starts with a movement verb.</para>
    /// </summary>
    public static bool TryParseSearchDirective(string? verb, out string innerMove)
    {
        innerMove = string.Empty;
        if (string.IsNullOrWhiteSpace(verb)) return false;
        var v = verb.Trim();

        const string prefix = "search ";
        if (v.Length <= prefix.Length ||
            !v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = v[prefix.Length..].TrimStart();
        foreach (var inner in SearchInnerVerbs)
            if (rest.Length > inner.Length + 1 &&
                rest[inner.Length] == ' ' &&
                rest.StartsWith(inner, StringComparison.OrdinalIgnoreCase))
            {
                innerMove = rest;
                return true;
            }
        return false;
    }

    /// <summary>
    /// Recognize the Genie 4 <b>objsearch directive</b> arc form:
    /// <c>move="objsearch outcropping climb handholds"</c> — a hidden exit that
    /// only opens after searching a NAMED in-room object. Unlike the bare
    /// search directive (<see cref="TryParseSearchDirective"/>, go-first with
    /// search-on-bounce), Genie 4's <c>.automapper</c> MOVE.OBJSEARCH always
    /// searches FIRST — <c>put search %searchObj</c>, wait out the roundtime,
    /// then send the inner move — and live testing (Mistwood outcropping,
    /// 2026-08-30) shows why: a bare <c>search</c> never reveals these exits,
    /// only the targeted object search does (and re-searching an already
    /// revealed object is a harmless "You notice …" line). G4's parse is
    /// <c>^(objsearch) (\S+) (.+)</c> — object is a single token (kept verbatim,
    /// dots included: <c>back.wall</c> is sent exactly as authored, matching
    /// G4), remainder is the inner move.
    /// <para>Returns true with the object token and the inner move when
    /// <paramref name="verb"/> is an objsearch directive. <c>objsearch</c> is
    /// not a DR verb (it bounces with "Please rephrase that command."), so
    /// there is no literal-command ambiguity to guard against.</para>
    /// </summary>
    public static bool TryParseObjSearchDirective(string? verb, out string searchObj, out string innerMove)
    {
        searchObj = string.Empty;
        innerMove = string.Empty;
        if (string.IsNullOrWhiteSpace(verb)) return false;
        var v = verb.Trim();

        const string prefix = "objsearch ";
        if (v.Length <= prefix.Length ||
            !v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = v[prefix.Length..].TrimStart();
        var space = rest.IndexOf(' ');
        if (space <= 0) return false;                      // need "<obj> <move…>"

        var obj  = rest[..space];
        var move = rest[(space + 1)..].TrimStart();
        if (move.Length == 0) return false;

        searchObj = obj;
        innerMove = move;
        return true;
    }

    /// <summary>
    /// Expand Genie 4 <b>quick-send</b> chain segments into their <c>#send</c>
    /// form. G4's command parser rewrites any ';'-segment starting with
    /// QuickSendChar (<c>-</c>) to <c>#send</c> + remainder (Genie4
    /// Core/Command.cs:254), and G4's #send then peels a leading run of
    /// digits/'.' — no space required — as a wait-before-send, queuing the rest
    /// roundtime-gated. Community maps lean on this for timed door rituals:
    /// <c>move="pull sconce;-1 go door"</c> (Boar Clan), <c>move="room
    /// sear;-3knock concealed door;-whisp door $haven.pw"</c> (Riverhaven —
    /// note the glued <c>-3knock</c>). ProcessInput now performs this same
    /// rewrite itself (public #278, see <see cref="Commanding.QuickSend"/>);
    /// the expansion here predates it and is kept so the walker dispatches a
    /// deterministic, already-normalized form that the arc matcher
    /// (<see cref="TryStripQuickSend"/>) can correlate.
    /// <para>Each dash segment becomes <c>{commandChar}send {delay} {cmd}</c>
    /// (delay omitted when absent — a bare <c>-whisp …</c> is a 0-delay
    /// roundtime-gated send in G4's quick-send path, NOT the script-send eager
    /// marker: the dash is consumed by the rewrite before #send ever sees it).
    /// A space is always emitted after the delay because Genie 5's
    /// ParseSendDelay — unlike G4's character scanner — requires the boundary.
    /// Non-dash segments (including mid-word hyphens like "halfling-sized
    /// burrow") and plain moves pass through unchanged; the queued sends ride
    /// the FIFO CommandQueue, so chain order survives the mixed
    /// immediate/queued dispatch.</para>
    /// </summary>
    public static string ExpandQuickSends(string? verb, char commandChar = '#')
    {
        if (string.IsNullOrWhiteSpace(verb)) return verb ?? string.Empty;
        if (!verb.Contains('-')) return verb;

        var segments = verb.Split(';');
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            if (TryParseQuickSend(seg, out var delay, out var cmd))
            {
                segments[i] = delay.Length > 0
                    ? $"{commandChar}send {delay} {cmd}"
                    : $"{commandChar}send {cmd}";
                changed = true;
            }
            else
            {
                segments[i] = seg;
            }
        }
        return changed ? string.Join(";", segments) : verb;
    }

    /// <summary>Matcher-side inverse of <see cref="ExpandQuickSends"/>: recover
    /// the bare command a quick-send segment ultimately puts on the wire
    /// ("-3knock concealed door" → "knock concealed door", "-pray" → "pray") so
    /// arc matching can correlate it. False when the segment isn't a quick-send
    /// form.</summary>
    public static bool TryStripQuickSend(string? segment, out string cmd)
    {
        cmd = string.Empty;
        return !string.IsNullOrEmpty(segment)
            && TryParseQuickSend(segment.Trim(), out _, out cmd);
    }

    /// <summary>Split a quick-send segment into delay + command. The parser
    /// itself lives in <see cref="Commanding.QuickSend"/> (shared with
    /// ProcessInput and the script engine since public #278); this shim keeps
    /// the mapper-local call sites unchanged.</summary>
    private static bool TryParseQuickSend(string segment, out string delay, out string cmd)
        => Commanding.QuickSend.TryParse(segment, out delay, out cmd);
}
