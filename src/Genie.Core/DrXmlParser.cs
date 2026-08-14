using System.Xml;
using System.Reactive.Subjects;
using Genie.Core.Events;
using Genie.Core.Models;
using Microsoft.Extensions.Logging;

namespace Genie.Core.Parser;

/// <summary>
/// Parses the DragonRealms XML stream into strongly-typed <see cref="GameEvent"/> objects.
///
/// DR's stream is not a well-formed XML document — it is a continuous flow of XML
/// fragments interleaved with bare text.  This parser maintains a stateful buffer
/// and recognises the following tag vocabulary (from observed Genie 4 / Lich 5 behaviour):
///
///   &lt;streamWindow/&gt;          — output window routing
///   &lt;component id='…'&gt;       — named UI component update (vitals, room, etc.)
///   &lt;roundTime value='…'/&gt;   — action roundtime expiry timestamp
///   &lt;castTime value='…'/&gt;    — spell casting roundtime
///   &lt;indicator id='…' visible='…'/&gt; — status indicators (kneeling, prone, etc.)
///   &lt;left/&gt; &lt;right/&gt;         — held items
///   &lt;spell/&gt;                 — active/prepared spell
///   &lt;compass&gt;               — available exits
///   &lt;resource id='…'/&gt;      — mana / spirit / stamina bars
///   &lt;pushStream id='…'/&gt;    — stream routing push
///   &lt;popStream/&gt;             — stream routing pop
///   &lt;prompt time='…'/&gt;      — server-side timestamp / prompt marker
///   &lt;output class='…'/&gt;     — text styling class
///   bare text lines            — room descriptions, combat text, dialogue
/// </summary>
public sealed class DrXmlParser : IDisposable
{
    private readonly ILogger<DrXmlParser> _log;

    // ── Outbound event stream ────────────────────────────────────────────────
    private readonly Subject<GameEvent> _events = new();
    public IObservable<GameEvent> GameEvents => _events;

    // ── Stream routing state ─────────────────────────────────────────────────
    private string _activeStream = "main";
    private readonly Stack<string> _streamStack = new();

    // Duplicate-echo tracking: DR sends a talk/whispers line twice — once
    // inside <pushStream id="...">…<popStream/>, then immediately again as
    // bare `main` text right after the pop, presumably for clients that
    // don't understand streams (confirmed via a live capture). See
    // EmitLine's DuplicateEcho check. Scoped to the confirmed double-send
    // set (talk/whispers) rather than "any non-main stream" — combat,
    // atmospherics, etc. haven't shown this pattern, and a wider net risks
    // flagging a coincidentally-identical unrelated main line.
    private static readonly HashSet<string> DuplicateEchoStreams =
        new(StringComparer.OrdinalIgnoreCase) { "talk", "whispers" };
    private string? _lastEmittedStream;
    private string  _lastEmittedText = "";

    // ── Multi-chunk accumulation ─────────────────────────────────────────────
    // DR sends <component id='...'>inner text or child tags</component> and
    // <spell>Name</spell> and <compass><dir.../></compass> across separate chunks.
    private string? _pendingComponentId;
    private readonly System.Text.StringBuilder _componentBuffer = new();
    private bool _inComponent = false;
    private bool _inSpell     = false;
    private bool _inHand      = false;   // <left>…</left> / <right>…</right> body — display name, emitted on close
    private string _handNoun  = "";      // <left>/<right> attrs stashed at the open tag; the
    private string _handExist = "";      // HeldItemEvent fires at the close tag with the body text (#172)
    private bool _inCompass   = false;
    private bool _inPrompt    = false;   // <prompt>…</prompt> body — route to _promptBuffer, fire on close
    // #178: text between <output class="mono"/> and <output class=""/> is a
    // column-aligned block (maps, stat tables) the display renders in the
    // monospace font. Set on the mono bracket, cleared on the empty bracket.
    private bool _inMono      = false;
    // #176: a genuine blank line (server sent `\n\n` in the TEXT stream) is
    // preserved, but the empty produced by a `\n` right after a tag close
    // (component/prompt/style/etc.) is not — those aren't real output. This is
    // true only while a real text line has been emitted since the last tag, so
    // an empty line emits a blank iff it directly follows real text.
    private bool _emittedTextLine = false;
    private string _currentPresetId = "";
    private readonly System.Text.StringBuilder _compassBuffer = new();

    // ── Prompt indicator capture ────────────────────────────────────────────
    // The text BETWEEN <prompt time='…'> and </prompt> is the indicator chars
    // (">", "R>", "HR>", etc.). We collect it into a side buffer so it doesn't
    // pollute _textLineBuffer, and fire the PromptEvent on close — that way
    // we have both the server timestamp from the open and the indicator
    // string from the body in a single emission.
    private readonly System.Text.StringBuilder _promptBuffer = new();
    private DateTimeOffset _pendingPromptTime = DateTimeOffset.MinValue;

    // ── Text line accumulation ───────────────────────────────────────────────
    // EmitChunks (GameConnection) splits at every '>' boundary, so inline
    // formatting tags like <pushBold/>, <d>text</d> break a single text line
    // into many fragments.  We accumulate raw fragments here and only emit when
    // we see a '\n' or an explicit flush call (e.g. on <prompt> or stream change).
    private readonly System.Text.StringBuilder _textLineBuffer = new();

    // ── Clickable link tracking ─────────────────────────────────────────────
    // Each open <d cmd="..."> records its position in _textLineBuffer plus
    // the command string. When </d> closes we compute the span length and
    // commit a LinkSpan to _pendingLinks. The list is consumed by EmitLine
    // and attached to the resulting TextEvent so the renderer can draw the
    // visible text as clickable.
    //
    // Nesting: DR's XML doesn't nest <d> tags in practice, but we track a
    // small stack just to stay safe — depth == 0 means we're outside any
    // link and incoming text doesn't belong to one.
    private readonly List<LinkSpan> _pendingLinks = new();
    private readonly Stack<(int Start, string? Cmd)> _linkStack = new();

    // Parallel stack for <a href='URL'> tags — DR's news/login resources
    // block uses these for external hyperlinks (Simucoin Store,
    // Elanthipedia, etc.). Kept separate from _linkStack so an interleaved
    // <a>/<d> pair (rare but possible) doesn't pop the wrong entry on
    // close; the parser uses tag name to route each close to the right
    // stack and emits the resulting LinkSpan with IsUrl=true.
    private readonly Stack<(int Start, string? Href)> _urlStack = new();

    // ── Bold tracking ──────────────────────────────────────────────────────
    // <pushBold/> marks "bold from here"; <popBold/> closes it. Both are
    // self-closing markers (NOT paired open/close), so neither has children
    // in the XML sense. We track the open position and close it on popBold
    // by recording a BoldSpan into _pendingBoldSpans, which EmitLine then
    // attaches to the TextEvent. DR doesn't nest bold in practice, but we
    // use a stack defensively for parity with the link-span design.
    private readonly List<BoldSpan> _pendingBoldSpans = new();
    private readonly Stack<int>     _boldStack        = new();

    // <preset id='X'>…</preset> spans, tracked exactly like bold: push the
    // buffer position (and id) on the open tag, record a PresetSpan over the
    // accumulated text on the close tag. EmitLine attaches them to the line.
    private readonly List<PresetSpan>          _pendingPresetSpans = new();
    private readonly Stack<(int Pos, string Id)> _presetStack      = new();

    // <style id='X'/> … <style id=''/> toggle spans (DR wraps the room-title
    // line this way). Unlike <preset>, the open and close arrive as two
    // SELF-CLOSING tags, so we track a single open position rather than a
    // stack: a non-empty id starts a span, an empty id closes it into
    // _pendingPresetSpans. -1 = no style currently open.
    private int    _styleStart = -1;
    private string _styleId    = "";

    // Bold tracking for COMPONENT content (a separate buffer from the display
    // line buffer). DR bolds creature names inside the `room objs` component;
    // capturing the bold ranges here is what lets the monster-count feature
    // tell a creature (bold) from an item (not bold) — the line-level bold
    // tracking above can't, because component text never touches _textLineBuffer.
    private readonly List<BoldSpan> _componentBoldSpans = new();
    private readonly Stack<int>     _componentBoldStack = new();

    // ── Injuries dialog context (public issue #18) ───────────────────────────
    // Set while inside <dialogData id="injuries"> … </dialogData>. The <image>
    // children carry one body-region reading each (name "Injury2"/"Scar1", or
    // name == region id when healthy); outside this context <image> is layout
    // noise and stays dropped.
    private bool _inInjuriesDialog;

    // ── Silent `health` window (injuries auto-refresh) ───────────────────────
    // The dialog's Nsys image can't say wound vs scar; only the `health` verb's
    // text can. When the core issues an auto-refresh poll it arms this window
    // first: the response — bracketed by <output class="mono"/> … <output
    // class=""/> exactly like Lich's nerve tracker observes — is consumed for
    // its nerve line but never emitted as TextEvents, so the poll stays
    // invisible. The window disarms on the closing bracket, on the deadline
    // (mono never arrived / response never closed), or on a runaway line count
    // — the three valves guarantee suppression can't outlive one response.
    // User-TYPED health output is untouched (window is only armed by the poll);
    // its nerve line still refines nsys via the always-on scan in EmitLine.
    private DateTimeOffset _silentHealthDeadline = DateTimeOffset.MinValue;
    private bool _silentHealthActive;
    private int  _silentHealthLines;
    private const int SilentHealthMaxLines = 60;

    /// <summary>Arm suppression for the next <c>health</c> response (injuries
    /// auto-refresh poll). Call immediately before sending the command.</summary>
    public void BeginSilentHealthWindow(TimeSpan? timeout = null) =>
        _silentHealthDeadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

    // ── Silent `flags` window (connect-time flag-state probe, issue #29) ──────
    // The `flags` verb prints a plain-text mono table (no dedicated XML element):
    //   Usage / FLAG {flag_name} {on|off} / Example …           ← preamble
    //   Flag            Status  Behavior for this setting        ← header
    //   RoomBrief          OFF  Display the full text …          ← one row per flag
    //   …
    //   For other setting options, see AVOID, SET, and TOGGLE.   ← footer
    // When the core probes at connect it arms this window first: the response is
    // parsed into a FlagsReportEvent (name → ON/OFF) and NOT emitted as text, so
    // the login stays quiet. Only lines that are recognisable flags-report lines
    // (a known flag name + ON/OFF, or one of the fixed boilerplate/header/footer
    // strings) are suppressed — an interleaved game line can never be eaten.
    // Valves: the footer completes it; a deadline and a runaway line count are
    // backstops. User-TYPED `flags` is untouched (the window is only armed by the
    // probe) and displays normally.
    private DateTimeOffset _flagsCaptureDeadline = DateTimeOffset.MinValue;
    private bool _flagsCaptureActive;
    private int  _flagsCaptureLines;
    private Dictionary<string, bool>? _flagsCaptured;
    private const int FlagsCaptureMaxLines = 80;

    /// <summary>The flag names DR's <c>flags</c> verb reports, as it spells them
    /// (case-insensitive match). A line is only treated as a flag row when its
    /// first token is one of these — the safety guard that stops the capture
    /// window from ever suppressing ordinary game text. Verified against a live
    /// capture 2026-07-09 (35 flags; the wiki's stale 32-list was wrong).</summary>
    internal static readonly IReadOnlySet<string> KnownFlagNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LogOn", "LogOff", "Disconnect", "ShowDeaths", "RoomNames",
            "Description", "RoomBrief", "BattleBrief", "CombatBrief",
            "MonsterBold", "Inactivity", "Portrait", "StatusPrompt",
            "AvoidJoiners", "AvoidHolders", "AvoidDancers", "AvoidWhispers",
            "AvoidDraggers", "AvoidTeachers", "AvoidSinging", "NoHarnessShare",
            "HarnessWarning", "HarnessVerbose", "AutoSneak", "ConciseThoughts",
            "HideLogin", "DeathLocation", "HidePreStrings", "HidePostStrings",
            "HideMyCusLogin", "HideOtCusLogin", "HideTrivia", "SkinKills",
            "LootKills", "ShowRoomID",
        };

    private static readonly System.Text.RegularExpressions.Regex _flagRowRe =
        new(@"^\s*(?<name>[A-Za-z]{3,20})\s+(?<state>ON|OFF)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled
          | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Arm silent capture for the next <c>flags</c> response (the
    /// connect-time probe). Call immediately before sending <c>flags</c>.</summary>
    public void BeginFlagsCaptureWindow(TimeSpan? timeout = null)
    {
        _flagsCaptureDeadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        _flagsCaptureActive   = false;
        _flagsCaptureLines    = 0;
        _flagsCaptured        = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Consume one line while a flags-probe window is armed. Returns
    /// true if the line belonged to the flags report (suppress it); false to let
    /// it display normally. Recognises the report by its known flag names and its
    /// fixed boilerplate, so interleaved game text is never swallowed. On the
    /// footer / deadline / line-cap it finalises and emits the FlagsReportEvent.</summary>
    private bool TryCaptureFlagsLine(string stripped)
    {
        if (DateTimeOffset.UtcNow >= _flagsCaptureDeadline
            || ++_flagsCaptureLines > FlagsCaptureMaxLines)
        {
            FinishFlagsCapture();
            return false;
        }

        var trimmed = stripped.TrimStart();

        // Footer — always the last line of the report. Complete and suppress.
        if (trimmed.StartsWith("For other setting options", StringComparison.OrdinalIgnoreCase))
        {
            FinishFlagsCapture();
            return true;
        }

        // A flag row: "<Name>   ON|OFF   <behavior>". Only when the first token
        // is a known flag name (guards against the "FLAG LOGON ON" example line,
        // whose first token is "FLAG", and against ordinary prose).
        var m = _flagRowRe.Match(trimmed);
        if (m.Success && KnownFlagNames.Contains(m.Groups["name"].Value))
        {
            _flagsCaptured![m.Groups["name"].Value] =
                m.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
            _flagsCaptureActive = true;
            return true;
        }

        // Boilerplate/header that frames the rows (Usage, the "FLAG …" syntax
        // lines, "Flag names may be abbreviated", "Example", the column header).
        // Suppress these too so the probe leaves no trace. Anchored to literals.
        if (trimmed.Equals("Usage", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("FLAG ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Flag names may be abbreviated", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Example", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("Flag", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Status", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Behavior", StringComparison.OrdinalIgnoreCase)))
        {
            _flagsCaptureActive = true;
            return true;
        }

        // Not a flags-report line. If we'd already started capturing, the report
        // ended without its footer (truncated / reordered) — finalise now and let
        // this genuine game line display.
        if (_flagsCaptureActive) FinishFlagsCapture();
        return false;
    }

    private void FinishFlagsCapture()
    {
        var captured = _flagsCaptured;
        _flagsCaptureDeadline = DateTimeOffset.MinValue;
        _flagsCaptureActive   = false;
        _flagsCaptured        = null;
        if (captured is { Count: > 0 })
            _events.OnNext(new FlagsReportEvent(captured));
    }

    // ── Lich-attach room seed (public issue #126) ─────────────────────────────
    // A Lich detachable attach happens AFTER Lich performed the login, so the
    // login block — the only unprompted source of <component id='room …'>
    // updates — is gone, and DR never re-sends components on `look` (verified
    // against all recorded sessions: room-component count == <nav/> count).
    // When armed, the next `look` response's display lines are additionally
    // folded into synthetic ComponentEvents so the Room panel / GameState /
    // mapper seed exactly as a direct-connect login block would have seeded
    // them. Lines still display normally — this is a tee, not a suppressor.
    // Valves: deadline, runaway line count, completion ("Obvious paths"), and
    // any REAL room component / <nav/> (movement beat us to it — the genuine
    // data must win).
    private DateTimeOffset _roomSeedDeadline = DateTimeOffset.MinValue;
    private bool _roomSeedArmed;
    private int  _roomSeedLines;
    private const int RoomSeedMaxLines = 200;

    /// <summary>Arm the one-shot room-component seed for the next <c>look</c>
    /// response (Lich attach). Call immediately before sending the command.</summary>
    public void BeginRoomSeedCapture(TimeSpan? timeout = null)
    {
        _roomSeedDeadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        _roomSeedArmed    = true;
        _roomSeedLines    = 0;
    }

    // ── Lich ident reply (public issue #127) ─────────────────────────────────
    // GenieCore asks Lich for the bare character name via `,eq` +
    // `respond "GENIE5-IDENT #{XMLData.name}"` on attach (nil-safe; waits briefly
    // if name is not populated yet). The reply is a mono-bracketed
    // marker line; while the window is armed we consume it (never displayed)
    // and emit CharacterNameEvent. One-shot: disarms on match or deadline.
    private DateTimeOffset _identDeadline = DateTimeOffset.MinValue;
    private static readonly System.Text.RegularExpressions.Regex _lichIdentRe =
        new(@"^GENIE5-IDENT\s+(\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Arm capture of the Lich ident reply (Lich attach). Call
    /// immediately before sending the ident query.</summary>
    public void BeginLichIdentWindow(TimeSpan? timeout = null) =>
        _identDeadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

    // Nerve lines from the `health` verb, verbatim from Lich 5's tracker
    // (lib/common/xmlparser.rb:652-669) — the only source that can split the
    // dialog's indeterminate Nsys reading into wound vs scar. Scanned on every
    // main-stream line starting with "You", so a user-typed `health` refines
    // the panel exactly like a poll does.
    private static readonly (string Phrase, InjuryKind Kind, int Severity)[] _nervePhrases =
    {
        ("a case of uncontrollable convulsions",      InjuryKind.Wound, 3),
        ("a case of sporadic convulsions",            InjuryKind.Wound, 2),
        ("a strange case of muscle twitching",        InjuryKind.Wound, 1),
        ("a very difficult time with muscle control", InjuryKind.Scar,  3),
        ("constant muscle spasms",                    InjuryKind.Scar,  2),
        ("developed slurred speech",                  InjuryKind.Scar,  1),
    };

    // ── News-listing auto-link state (public issue #30) ──────────────────────
    // DR sends the `news` listing as PLAIN TEXT — the numbered item lines carry
    // no <d>/<a> tags, so they're not clickable on their own. We track the
    // listing context (between the "ITEM # - HEADLINE" header and the trailing
    // "Type NEWS HELP" / "END NEWS ITEM" footer) plus the current category from
    // each "** Category N - … **" header, then synthesize a click link
    // (command "news <cat> <item>") over each numbered line in EmitLine.
    private bool _inNewsList;
    private int  _newsCategory;

    // ── Raw buffer ───────────────────────────────────────────────────────────
    private readonly System.Text.StringBuilder _rawBuffer = new(2048);

    public DrXmlParser(ILogger<DrXmlParser> log) => _log = log;

    /// <summary>
    /// Feed one raw XML chunk (as emitted by <see cref="GameConnection"/>).
    /// </summary>
    public void Feed(string chunk)
    {
        _rawBuffer.Append(chunk);
        ProcessBuffer();
    }

    // ── Core processing ──────────────────────────────────────────────────────

    private void ProcessBuffer()
    {
        while (_rawBuffer.Length > 0)
        {
            var raw = _rawBuffer.ToString();

            // Look for the next '<' to split bare text from tags
            int tagStart = raw.IndexOf('<');

            if (tagStart < 0)
            {
                // All bare text — accumulate and clear
                AccumulateText(raw);
                _rawBuffer.Clear();
                return;
            }

            if (tagStart > 0)
            {
                // Text before the tag
                AccumulateText(raw[..tagStart]);
                _rawBuffer.Remove(0, tagStart);
                continue;
            }

            // We're at a '<' — but a '<' only OPENS A TAG when what follows can
            // legally start one: a name character (letter/'_'), a closing tag
            // ("</x"), or an XML construct ("<!", "<?"). DR game text quotes
            // angle-bracket literals — "<1-20>", "I <3 you", "a < b" — and
            // consuming those to the next '>' silently deletes them (#238).
            // Deciding needs the char after '<' (and after "</"): wait for it.
            if (raw.Length < 2 || (raw[1] == '/' && raw.Length < 3))
                return; // '<' (or "</") at buffer end — need the next char

            char after = raw[1];
            bool opensTag =
                char.IsAsciiLetter(after) || after == '_' || after == '!' || after == '?' ||
                (after == '/' && (char.IsAsciiLetter(raw[2]) || raw[2] == '_'));
            if (!opensTag)
            {
                AccumulateText("<");
                _rawBuffer.Remove(0, 1);
                continue; // "1-20>" etc. re-enters as bare text
            }

            // Find the matching '>'
            int tagEnd = raw.IndexOf('>');
            if (tagEnd < 0)
                return; // incomplete tag — wait for more data

            var fullTag = raw[..(tagEnd + 1)];
            _rawBuffer.Remove(0, tagEnd + 1);

            ParseTag(fullTag);
        }
    }

    // The game sends plain-text prompts like ">", "H>", "HR>" (stance + round-time).
    private static readonly System.Text.RegularExpressions.Regex _promptRe =
        new(@"^[A-Z]*>$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The server room id DR appends to the room streamWindow subtitle:
    // " - [The Crossing, Hodierna Way] (10015)". Captures the digits inside the
    // trailing parens; "(**)" (unknown room) has no digits and won't match.
    private static readonly System.Text.RegularExpressions.Regex _roomUidRe =
        new(@"\((\d+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The `info` verb's first line: "Name: <name>   Race: <race>   Guild: <guild>".
    // Anchored to start at "Name:" and capture the trailing "Guild: X" so we
    // don't false-match on arbitrary game text mentioning a guild. Race can be
    // multi-word (e.g. "S'Kra Mur"), hence the lazy ".+?" between fields.
    private static readonly System.Text.RegularExpressions.Regex _guildRe =
        new(@"^\s*Name:\s.+?\bGuild:\s+([A-Za-z][A-Za-z' ]*?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── News-listing markers (public issue #30) ──────────────────────────────
    // Enter the listing context on either the "Listing all news items." preamble
    // or the "ITEM # - HEADLINE" column header; leave it on the "Type NEWS HELP"
    // footer or "END NEWS ITEM" (which precedes the body of a read article — we
    // must NOT synthesize links inside article text).
    private static readonly System.Text.RegularExpressions.Regex _newsEnterRe =
        new(@"^(Listing all news items\.|ITEM # - HEADLINE)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _newsExitRe =
        new(@"^(Type NEWS HELP|END NEWS ITEM)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    // "** Category 1 - GENERAL ANNOUNCEMENTS **" → captures the category number.
    private static readonly System.Text.RegularExpressions.Regex _newsCategoryRe =
        new(@"^\*\* Category (\d+) - .* \*\*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    // "     2 - COMMUNICATION WITH STAFF" → captures the item number (group 1).
    // Matched against the un-trimmed line so the capture index gives the real
    // offset of the digit within the display text.
    private static readonly System.Text.RegularExpressions.Regex _newsItemRe =
        new(@"^\s*(\d+) - .+$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Buffers raw text fragments across inline tag splits (e.g. <pushBold/>, <d>).
    // Flushed on '\n' or explicit FlushTextLine() call.
    private void AccumulateText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_inComponent) { _componentBuffer.Append(text); return; }
        if (_inSpell)     { _componentBuffer.Append(text); return; }
        if (_inHand)      { _componentBuffer.Append(text); return; } // content discarded on </left>/</right>
        if (_inCompass)   { _compassBuffer.Append(text);   return; }
        if (_inPrompt)    { _promptBuffer.Append(text);    return; } // indicator chars, emitted on </prompt>

        _textLineBuffer.Append(text);

        // Flush every complete '\n'-terminated line.
        var buf = _textLineBuffer.ToString();
        int nlPos;
        while ((nlPos = buf.IndexOf('\n')) >= 0)
        {
            var line = buf[..nlPos].TrimEnd('\r');
            SplitOpenStyleSpan(line.Length);
            EmitLine(line);
            buf = buf[(nlPos + 1)..];
        }
        _textLineBuffer.Clear();
        if (buf.Length > 0) _textLineBuffer.Append(buf);
    }

    // Flush any partial line still in the buffer (called at logical boundaries like <prompt>).
    private void FlushTextLine()
    {
        if (_textLineBuffer.Length == 0) return;
        SplitOpenStyleSpan(_textLineBuffer.Length);
        EmitLine(_textLineBuffer.ToString());
        _textLineBuffer.Clear();
    }

    // A <style> toggle open across a line boundary spans the emitted line's
    // tail — record it before the line flushes, the same treatment open
    // <preset> blocks get in HandleEndElement. DR closes the room-title
    // toggle on the line AFTER the title (<style id="roomName"/>[Title]\n
    // <style id=""/>…), so without this the title's TextEvent carried no
    // span and the close then computed zero length against the fresh buffer:
    // the roomname preset never painted anywhere (public #174). Re-anchoring
    // at 0 keeps the still-open toggle colouring follow-on lines until its
    // close arrives.
    private void SplitOpenStyleSpan(int lineLength)
    {
        if (_styleStart < 0) return;
        if (lineLength > _styleStart)
            _pendingPresetSpans.Add(new PresetSpan(
                _styleStart, lineLength - _styleStart, _styleId));
        _styleStart = 0;
    }

    // Find the index of the first lowercase→uppercase adjacency (e.g. the "Y"
    // in "ledgerYou"). DR item display names never butt an uppercase letter
    // directly against a lowercase one without a separator — a space ("Imperial
    // saber") or apostrophe ("Se'Karan") always breaks the adjacency — so this
    // boundary reliably marks where a server-merged response was concatenated
    // onto a hand body. Returns -1 when there is no such seam.
    private static int FindMergeSeam(string s)
    {
        for (var i = 1; i < s.Length; i++)
            if (char.IsLower(s[i - 1]) && char.IsUpper(s[i])) return i;
        return -1;
    }

    // Emit one complete, decoded text line as a game event.
    private void EmitLine(string rawLine)
    {
        // Decode HTML entities and strip embedded XML, then trim trailing
        // whitespace only. LEADING whitespace is significant — DR uses it for
        // visual layout (right-aligned stat labels in `info`, indented
        // inventory items, etc.). Stripping it (the old behavior) made
        // `info`/`exp` output flush-left and broke column alignment.
        //
        // NOTE: bold / preset / link span offsets were recorded against the RAW
        // _textLineBuffer. This transform collapses HTML entities (&lt; → <,
        // 4 chars → 1) and strips inline tags, so any entity or tag BEFORE a
        // span shifts every later offset. The spans are rebased into
        // decoded-text space just below (public #199) — combat lines are the
        // reliable trigger, since DR prefixes them with a literal `&lt;`.
        var stripped = System.Net.WebUtility.HtmlDecode(StripBasicXml(rawLine)).TrimEnd();

        // Snapshot and clear link + bold state regardless of whether we
        // emit — a line that's all whitespace shouldn't leak spans into
        // the next one.
        IReadOnlyList<LinkSpan>? links = _pendingLinks.Count > 0
            ? _pendingLinks.ToArray()
            : null;
        _pendingLinks.Clear();

        IReadOnlyList<BoldSpan>? boldSpans = _pendingBoldSpans.Count > 0
            ? _pendingBoldSpans.ToArray()
            : null;
        _pendingBoldSpans.Clear();

        IReadOnlyList<PresetSpan>? presetSpans = _pendingPresetSpans.Count > 0
            ? _pendingPresetSpans.ToArray()
            : null;
        _pendingPresetSpans.Clear();

        if (stripped.Length == 0)
        {
            // Preserve a real blank line (INFO/LOOK/HELP spacing, public #176)
            // — but only when it directly follows real text on this stream.
            // The empty produced by a tag-adjacent newline (buffer emptied by a
            // component/prompt close) has _emittedTextLine == false and is
            // dropped, so we never spam a blank after every tag.
            if (_emittedTextLine)
            {
                _emittedTextLine = false;   // one blank per run of empties
                _events.OnNext(new TextEvent(_activeStream, string.Empty));
            }
            return;
        }

        // DR's bare-echo duplicate: this main-stream line is byte-identical to
        // — and the immediate next emission after — a just-closed talk/
        // whispers line (e.g. talk's <pushStream>…<popStream/> followed at
        // once by the same "You say, ..." text as plain main). Scoped tight:
        // only the IMMEDIATE next emission qualifies (any other line in
        // between updates _lastEmittedStream and clears the match), so two
        // genuinely identical main-stream lines elsewhere are unaffected.
        //
        // Tag, don't drop: Core consumers (Triggers/Scripts/Plugins — see
        // GenieCore.ProcessGameTextEvent) need this event exactly as DR sent
        // it, most notably ParseGameOnly triggers, which under Genie 4
        // parity fire on the Main-targeted copy of a line — for talk, that
        // IS this bare duplicate. Only a display sink should skip it.
        var isDuplicateEcho = _activeStream == "main" && _lastEmittedStream is not null &&
            DuplicateEchoStreams.Contains(_lastEmittedStream) && stripped == _lastEmittedText;

        // Rebase every span offset from raw-buffer space into decoded-text
        // space (public #199). The offsets were taken against _textLineBuffer,
        // whose entities/tags `stripped` has since collapsed — so replay the
        // identical transform over the raw prefix to find where each offset
        // lands in `stripped`. Runs before any consumer of the spans below
        // (room-seed capture, the emitted TextEvent). The synthesized news
        // link added later is already in decoded space and is not rebased.
        if (boldSpans is not null || presetSpans is not null || links is not null)
        {
            int Rebase(int rawOffset)
            {
                if (rawOffset <= 0) return 0;
                if (rawOffset >= rawLine.Length) return stripped.Length;
                var dec = System.Net.WebUtility.HtmlDecode(StripBasicXml(rawLine[..rawOffset])).Length;
                return dec < stripped.Length ? dec : stripped.Length;
            }

            boldSpans = RebaseSpans(boldSpans, b => b.Start, b => b.Length,
                (_, s, l) => new BoldSpan(s, l), Rebase);
            presetSpans = RebaseSpans(presetSpans, p => p.Start, p => p.Length,
                (p, s, l) => new PresetSpan(s, l, p.PresetId), Rebase);
            links = RebaseSpans(links, x => x.Start, x => x.Length,
                (x, s, l) => new LinkSpan(s, l, x.Command, x.IsUrl), Rebase);
        }

        // Bare-text prompt line (">", "H>", "HR>"). Trim leading whitespace
        // for prompt detection only — the regex anchors on '^' so accidental
        // leading spaces from XML tag splits would miss the match.
        var promptCandidate = stripped.TrimStart();
        if (_promptRe.IsMatch(promptCandidate))
        {
            // Wizard plain-text mode: this line IS the indicator. StormFront
            // mode prompts arrive as <prompt>…</prompt> XML and never reach here.
            // As in the XML branch, emit only the PromptEvent — GameTextViewModel
            // owns whether/how the prompt is rendered (Config.Prompt + promptbreak).
            _events.OnNext(new PromptEvent(DateTimeOffset.MinValue, promptCandidate));
            return;
        }

        // Silent `flags` window (issue #29): while a connect-time probe is armed,
        // fold the flags-report lines into a FlagsReportEvent and swallow them so
        // the probe leaves no visible output. Only recognisable report lines are
        // consumed; anything else (and every line once the window is disarmed)
        // falls through to display normally.
        if (DateTimeOffset.UtcNow < _flagsCaptureDeadline && _activeStream == "main"
            && TryCaptureFlagsLine(stripped))
            return;

        // Guild detection from the `info` first line. Fire alongside the
        // TextEvent (the line still displays) so GameState + the title can
        // pick up the guild when the player runs `info`.
        var guildMatch = _guildRe.Match(stripped);
        if (guildMatch.Success)
            _events.OnNext(new GuildEvent(guildMatch.Groups[1].Value.Trim()));

        // Lich ident reply (issue #127): the marker line is plumbing, not game
        // text — emit the name event and swallow the line. Only an armed window
        // can match, so ordinary text can never be eaten.
        if (DateTimeOffset.UtcNow < _identDeadline)
        {
            var ident = _lichIdentRe.Match(stripped.TrimStart());
            if (ident.Success)
            {
                _identDeadline = DateTimeOffset.MinValue;
                _events.OnNext(new CharacterNameEvent(ident.Groups[1].Value));
                return;
            }
        }

        // Room seed capture (issue #126): tee the `look` response lines into
        // synthetic room ComponentEvents. Never suppresses the line itself.
        if (_roomSeedArmed)
            TryCaptureRoomSeed(stripped, boldSpans, presetSpans);

        // Nervous-system refinement (#18): the `health` verb's nerve line is the
        // only wound-vs-scar source for the nsys region (the dialog image can't
        // say). Always on — a user-typed `health` refines the panel exactly
        // like an auto-refresh poll. Runs BEFORE silent suppression below so a
        // polled response still yields its reading.
        if (_activeStream == "main" && stripped.StartsWith("You", StringComparison.Ordinal))
        {
            foreach (var (phrase, kind, severity) in _nervePhrases)
            {
                if (!stripped.Contains(phrase, StringComparison.Ordinal)) continue;
                _events.OnNext(new InjuryEvent("nsys", kind, severity));
                break;
            }
        }

        // Silent-health window: this line is part of a polled `health` response
        // — consume it without emitting. Deadline + line-count valves stop the
        // suppression from ever outliving one response.
        if (_silentHealthActive)
        {
            if (DateTimeOffset.UtcNow < _silentHealthDeadline
                && ++_silentHealthLines <= SilentHealthMaxLines)
                return;
            _silentHealthActive   = false;   // valve tripped — stop suppressing
            _silentHealthDeadline = DateTimeOffset.MinValue;
        }

        // News-listing auto-link (public issue #30). Updates the listing-context
        // state machine and, on a numbered item line, hands back a synthesized
        // click LinkSpan to merge with any tag-derived links (there are none on
        // these plain-text lines in practice, but we merge defensively).
        var newsLink = TrackNewsAndLink(stripped);
        if (newsLink is not null)
            links = links is null ? new[] { newsLink } : links.Append(newsLink).ToArray();

        _events.OnNext(new TextEvent(_activeStream, stripped, links, boldSpans, presetSpans,
            Mono: _inMono, DuplicateEcho: isDuplicateEcho));
        _emittedTextLine = true;   // a real blank line may now follow (#176)
        _lastEmittedStream = _activeStream;
        _lastEmittedText   = stripped;
    }

    // Map a span list from raw-buffer offsets into decoded-text offsets using
    // <paramref name="rebase"/> (public #199). A span that collapses to zero
    // length after rebasing (e.g. it covered only entity/tag characters) is
    // dropped; a null/empty result returns null so the TextEvent carries no
    // empty list. <paramref name="rebuild"/> reconstructs each record from its
    // new (start, length) so per-type fields (PresetId, link Command) survive.
    private static IReadOnlyList<T>? RebaseSpans<T>(
        IReadOnlyList<T>? spans,
        Func<T, int> getStart,
        Func<T, int> getLength,
        Func<T, int, int, T> rebuild,
        Func<int, int> rebase)
    {
        if (spans is null || spans.Count == 0) return null;

        List<T>? result = null;
        foreach (var span in spans)
        {
            var start = rebase(getStart(span));
            var end = rebase(getStart(span) + getLength(span));
            if (end > start)
                (result ??= new List<T>()).Add(rebuild(span, start, end - start));
        }
        return result;
    }

    // One armed `look` response → synthetic room ComponentEvents (issue #126).
    // The look output carries the same data the login-block components did,
    // just as display text:
    //   <style id="roomName"/>[Title]        → room title
    //   <preset id='roomDesc'>…</preset>     → room desc   (its own flush)
    //   "You also see …"                     → room objs   (bold = creatures)
    //   "Also here: …"                       → room players
    //   "Obvious paths|exits: …"             → room exits  (completes capture)
    // The roomName style toggle closes on the LINE AFTER the title; since the
    // #174 fix FlushTextLine records the open toggle's span at flush, so the
    // presetSpans check normally matches. The live-toggle check stays as a
    // guard for flush paths that emit before the span split runs.
    private void TryCaptureRoomSeed(string stripped,
                                    IReadOnlyList<BoldSpan>? boldSpans,
                                    IReadOnlyList<PresetSpan>? presetSpans)
    {
        if (DateTimeOffset.UtcNow >= _roomSeedDeadline || ++_roomSeedLines > RoomSeedMaxLines)
        {
            _roomSeedArmed = false;
            return;
        }
        if (_activeStream != "main") return;

        if ((_styleStart >= 0 && _styleId == "roomName")
            || (presetSpans is not null && presetSpans.Any(p => p.PresetId == "roomName")))
        {
            _events.OnNext(new ComponentEvent("room title", stripped.Trim()));
            return;
        }

        var descSpan = presetSpans?.FirstOrDefault(p => p.PresetId == "roomDesc");
        if (descSpan is not null)
        {
            // The parser flushes on </preset> for roomDesc, so the line IS the
            // description; the span slice is a guard against leading fragments.
            var desc = descSpan.Start >= 0 && descSpan.Start + descSpan.Length <= stripped.Length
                ? stripped.Substring(descSpan.Start, descSpan.Length).Trim()
                : stripped.Trim();
            _events.OnNext(new ComponentEvent("room desc", desc));
            return;
        }

        var t    = stripped.TrimStart();
        var lead = stripped.Length - t.Length;

        if (t.StartsWith("You also see", StringComparison.Ordinal))
        {
            // Rebase bold spans onto the trimmed text; bold phrases are the
            // creatures (same convention as the real room objs component).
            List<BoldSpan>? rebased = null;
            List<string>?   names   = null;
            if (boldSpans is not null)
            {
                rebased = new List<BoldSpan>();
                names   = new List<string>();
                foreach (var b in boldSpans)
                {
                    var start = b.Start - lead;
                    if (start < 0 || start + b.Length > t.Length) continue;
                    rebased.Add(new BoldSpan(start, b.Length));
                    names.Add(t.Substring(start, b.Length));
                }
            }
            _events.OnNext(new ComponentEvent("room objs", t, names, rebased));
            return;
        }

        if (t.StartsWith("Also here:", StringComparison.Ordinal))
        {
            _events.OnNext(new ComponentEvent("room players", t));
            return;
        }

        if (t.StartsWith("Obvious paths:", StringComparison.Ordinal)
            || t.StartsWith("Obvious exits:", StringComparison.Ordinal))
        {
            _events.OnNext(new ComponentEvent("room exits", t));
            _roomSeedArmed = false;   // room block complete
        }
    }

    // Drive the news-listing state machine for one display line and, when the
    // line is a numbered item inside a known category, return a click LinkSpan
    // whose command re-issues "news <category> <item>". Returns null otherwise.
    // See issue #30 — DR's news listing is plain text with no link tags.
    private LinkSpan? TrackNewsAndLink(string stripped)
    {
        var t = stripped.TrimStart();

        // Footer ends the listing context (and guards against linking inside the
        // body of a read article, which follows "END NEWS ITEM").
        if (_newsExitRe.IsMatch(t)) { _inNewsList = false; _newsCategory = 0; return null; }

        // Preamble / column header opens a listing.
        if (_newsEnterRe.IsMatch(t)) { _inNewsList = true; _newsCategory = 0; return null; }

        // Category header sets the active category (and implies listing mode).
        var cat = _newsCategoryRe.Match(t);
        if (cat.Success)
        {
            _inNewsList   = true;
            _newsCategory = int.Parse(cat.Groups[1].Value);
            return null;
        }

        // Numbered item line within a category → synthesize the click link.
        if (_inNewsList && _newsCategory > 0)
        {
            var m = _newsItemRe.Match(stripped);
            if (m.Success)
            {
                var num   = m.Groups[1];
                var start = num.Index;                 // offset of the digit in the display text
                var len   = stripped.Length - start;   // number through end of headline
                return new LinkSpan(start, len, $"news {_newsCategory} {num.Value}");
            }
        }

        return null;
    }

    private void ParseTag(string tag)
    {
        // XmlReader in Fragment mode can't handle standalone end tags like </component>,
        // so extract the name directly and dispatch without a reader.
        if (tag.StartsWith("</", StringComparison.Ordinal))
        {
            var nameEnd = tag.IndexOfAny([' ', '\t', '>'], 2);
            var name    = nameEnd >= 0 ? tag[2..nameEnd] : tag[2..^1];
            HandleEndElement(name.TrimEnd('>'));
            return;
        }

        try
        {
            using var reader = XmlReader.Create(
                new System.IO.StringReader(tag),
                new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });

            if (!reader.Read()) return;

            if (reader.NodeType == XmlNodeType.Element)
            {
                HandleElement(reader, tag);
                return;
            }
        }
        catch (XmlException)
        {
            // XmlReader at Fragment level rejects some perfectly-real DR tags
            // — most notably bare `<d>` (open with no attributes and no
            // self-close), which it sees as "Data at root level invalid".
            // Fall through to the manual parser below so these still
            // dispatch through HandleElement.
        }

        // Manual fallback: scrape the name and any name="value" attributes
        // out of the raw tag with a regex. Wrap in a fake XmlReader-like
        // shim (RawAttrReader below) so the existing HandleElement
        // attribute-by-name calls keep working.
        var open = tag.AsSpan();
        if (open.Length < 3 || open[0] != '<') return;
        int n = 1;
        while (n < open.Length && (char.IsLetterOrDigit(open[n]) || open[n] == '_' || open[n] == ':'))
            n++;
        if (n == 1) return;
        var elemName = new string(open[1..n]);
        var attrs    = ParseAttributesFallback(tag);
        HandleElement(new RawAttrReader(elemName, attrs), tag);
    }

    /// <summary>
    /// Pull <c>name="value"</c> and <c>name='value'</c> pairs out of a raw
    /// element tag string. Used by the fallback path when XmlReader refuses
    /// to parse a tag (e.g. bare <c>&lt;d&gt;</c>).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _attrRe =
        new(@"(\w+)\s*=\s*(?:""([^""]*)""|'([^']*)')",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static Dictionary<string, string> ParseAttributesFallback(string tag)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in _attrRe.Matches(tag))
        {
            var name  = m.Groups[1].Value;
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            dict[name] = value;
        }
        return dict;
    }

    /// <summary>
    /// Minimal <see cref="XmlReader"/> stand-in that only implements
    /// <c>Name</c> and the attribute indexer — the two members
    /// <see cref="HandleElement"/> uses. Avoids re-flowing the element
    /// handlers when XmlReader can't parse the tag.
    /// </summary>
    private sealed class RawAttrReader : XmlReader
    {
        private readonly string _name;
        private readonly Dictionary<string, string> _attrs;
        public RawAttrReader(string name, Dictionary<string, string> attrs)
        { _name = name; _attrs = attrs; }
        public override string Name => _name;
        public override string LocalName => _name;
        public override string? GetAttribute(string name)
            => _attrs.TryGetValue(name, out var v) ? v : null;
        public override string? this[string name] => GetAttribute(name);
        // Members below are required overrides but not used by HandleElement.
        public override int AttributeCount => _attrs.Count;
        public override string BaseURI => string.Empty;
        public override int Depth => 0;
        public override bool EOF => true;
        public override bool HasValue => false;
        public override bool IsEmptyElement => false;
        public override XmlNameTable NameTable => new NameTable();
        public override string NamespaceURI => string.Empty;
        public override XmlNodeType NodeType => XmlNodeType.Element;
        public override string Prefix => string.Empty;
        public override ReadState ReadState => ReadState.Interactive;
        public override string Value => string.Empty;
        public override string GetAttribute(int i) => _attrs.Values.ElementAt(i);
        public override string? GetAttribute(string name, string? ns) => GetAttribute(name);
        public override string LookupNamespace(string prefix) => string.Empty;
        public override bool MoveToAttribute(string name) => _attrs.ContainsKey(name);
        public override bool MoveToAttribute(string name, string? ns) => _attrs.ContainsKey(name);
        public override bool MoveToElement() => true;
        public override bool MoveToFirstAttribute() => _attrs.Count > 0;
        public override bool MoveToNextAttribute() => false;
        public override bool Read() => false;
        public override bool ReadAttributeValue() => false;
        public override void ResolveEntity() { }
    }

    // Tags to silently skip — either the initial Wrayth settings dump or
    // UI-layout-only tags that carry no game-semantic information.
    private static readonly HashSet<string> _settingsTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Initial settings dump ────────────────────────────────────────────
        "mode", "settings", "presets", "p", "macros", "keys", "k",
        "palette", "i", "stream", "w", "cmdline", "strings", "names",
        "ignores", "vars", "scripts", "dialog", "builtin", "panels",
        // "app" is NOT here — <app char=… game=…/> carries the session identity
        // (AppEvent → $game/$charactername); its settings-dump form
        // <app maximized='t'/> is filtered by the char-attr guard in HandleElement.
        "group", "toggles", "misc", "m", "display", "options", "o",
        "font", "s",
        // ── Session/connection info ─────────────────────────────────────────
        "playerid",
        // ── UI dialog layout ────────────────────────────────────────────────
        // "dialogdata" and "image" are NOT here — <dialogData id="injuries">
        // carries the per-region injury <image> updates (issue #18), so both
        // are handled explicitly; non-injuries dialog content is still
        // discarded there.
        "opendialog", "detach", "skin", "radio",
        // ── Wrayth dialog-control vocabulary (pre-seeded, public #216) ──────
        // Server-driven dialog controls (bank/store/spells/feats/profile-edit
        // popups — the DynamicWindows-style vocabulary, public #156) plus the
        // demeanor-menu <menuImage> observed live. Dropped today, but listed
        // in _droppedDataTags below so `#audit xmlhunting` classifies them as
        // DroppedData (a tracked coverage gap the server-dialog renderer will
        // consume), not Unknown (which would draft a gap-report issue per
        // tag). <progressBar>, <radio>, and <clearContainer> from the same
        // vocabulary are already handled/skipped above.
        "menuimage", "closedialog", "exposedialog",
        "label", "cmdbutton", "closebutton", "checkbox",
        "streambox", "dropdownbox", "editbox", "updowneditbox",
        // <switchQuickBar id='quick-simu'/> — Wrayth/StormFront quick-bar
        // selection, pure client UI chrome with no game data (public #188).
        "switchquickbar",
        // <link id='1' value='Game Info' cmd='url:/dr/info/'/> — server-defined
        // menu/nav link (populates the client's Game/Help menus with URLs).
        // Self-closing UI chrome, no game-state data; distinct from the in-text
        // clickable <a>/<d> links, which ARE handled (public #198).
        "link",
        // <menulink .../> — the in-menu sibling of <link>: server-driven menu
        // entries (demeanor/context menus). Chrome, no game-state text; observed
        // live in beta.4. Listed in _droppedDataTags so it classifies as a tracked
        // coverage gap (server-dialog renderer #156), not a per-tag Unknown report.
        "menulink",
        // <forging outfitting engineering alchemy enchanting custom={set}> —
        // Wrayth's crafting-UI config from the settings dump (value-less flag
        // attributes + a {set} preset placeholder), not live crafting state.
        // Discarded chrome; if the crafting panels are ever rendered they'd come
        // through the server-driven dialog path (#156), not this tag (public #208).
        "forging",
        // ── Text styling ────────────────────────────────────────────────────
        // "preset" is NOT here — its </preset> must reach HandleEndElement to
        // flush the room description before exits appear on the next line.
        // NOTE: "pushbold"/"popbold" used to be in this skip set. They're now
        // handled explicitly so each bold span emits a BoldSpan on the next
        // TextEvent (DR uses bold for unread news items, emphasis, etc.).
        // "style" is NOT here — DR wraps the room-title line in a
        // <style id="roomName"/> … <style id=""/> toggle; HandleElement now
        // records a PresetSpan so the title renders in the roomName colour.
        // ── Inventory container management ──────────────────────────────────
        // "inv" is NOT here — its </inv> must reach HandleEndElement to flush each item to its own line.
        // "container" is NOT here — its open tag emits a ContainerEvent so the
        // UI can build a `#NNNN → "My Backpack"` map (used by BuildLinkEcho to
        // render container-IDs as human names in click-echoes). DR's <container>
        // tags are self-closing in practice, so the close path is never hit.
        "exposecontainer", "clearcontainer",
        // ── Experience panel slot definitions ───────────────────────────────
        "compdef",
        // NOTE: "d" was previously in this skip set, which destroyed click
        // information. The tag is now handled explicitly in HandleElement /
        // HandleEndElement so each <d cmd="..."> emits a LinkSpan on the
        // resulting TextEvent.
    };

    // ── Tag-coverage classification (for `#audit xmlhunting`) ────────────────
    // Source of truth for "are we using 100% of the XML?". Three buckets:
    //   • Consumed       — produces a typed GameEvent (the HandleElement switch).
    //   • DroppedData     — IN _settingsTags but carries real game data we don't
    //                       yet consume (injuries <skin>, server dialogs,
    //                       exp <compDef>, …). The hunt targets.
    //   • DroppedSetting  — the Wrayth settings dump; correctly discarded.
    //   • Unknown         — neither handled nor skipped → emits UnknownTagEvent.
    // Keep <see cref="_handledTags"/> in sync with the HandleElement switch.
    private static readonly HashSet<string> _handledTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "app", "b", "casttime", "cleardynastream", "clearstream", "compass", "component", "container",
        "crtrstatus", "d", "dialogdata", "dir", "endsetup", "image", "indicator", "inv",
        "left", "nav", "openwindow", "output", "popbold", "popstream",
        "preset", "progressbar", "prompt", "pushbold", "pushstream",
        "resource", "right", "roundtime", "settingsinfo", "spell", "spelltime",
        "streamwindow", "style",
    };

    // Subset of _settingsTags that actually carries game data we drop today —
    // the coverage gaps "#audit xmlhunting" exists to surface. (Skip behaviour
    // is unchanged; this set only drives classification.)
    private static readonly HashSet<string> _droppedDataTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "skin", "compdef", "opendialog",
        "radio", "detach", "playerid", "exposecontainer", "clearcontainer",
        // Wrayth dialog-control vocabulary — pre-seeded as DroppedData so the
        // first bank/store/spells dialog opened in Wrayth mode doesn't fire a
        // gap-report issue per control (public #216; renderer is #156).
        "menuimage", "closedialog", "exposedialog", "menulink",
        "label", "cmdbutton", "closebutton", "checkbox",
        "streambox", "dropdownbox", "editbox", "updowneditbox",
    };

    /// <summary>How the parser treats a given element name — see the bucket
    /// notes above. Backs the <c>#audit xmlhunting</c> coverage diagnostic.</summary>
    public enum TagFate { Consumed, DroppedData, DroppedSetting, Unknown }

    /// <summary>Classify an element name by what the parser does with it.
    /// Case-insensitive. The single source of truth for XML coverage.</summary>
    public static TagFate ClassifyTag(string name)
    {
        if (_handledTags.Contains(name))     return TagFate.Consumed;
        if (_droppedDataTags.Contains(name)) return TagFate.DroppedData;
        if (_settingsTags.Contains(name))    return TagFate.DroppedSetting;
        return TagFate.Unknown;
    }

    private void HandleElement(XmlReader r, string rawTag)
    {
        var name = r.Name.ToLowerInvariant();

        // A tag boundary breaks the run of real text (#176): a newline that
        // arrives right after this tag with an empty buffer must NOT emit a
        // blank line. The next real text line re-arms it. Inline tags (<d>,
        // bold) are harmless — the line carrying their text still emits and
        // re-sets the flag before any following blank.
        _emittedTextLine = false;

        if (_settingsTags.Contains(name)) return;

        switch (name)
        {
            // ── Vitals bar ───────────────────────────────────────────────
            case "progressbar":
            {
                // <progressBar id="mana" value="87" text="87" left="425" top="290" width="102" height="13"/>
                // Skip the emit if id is missing OR value attr is absent /
                // unparseable. Previously we silently emitted value=0, which
                // could briefly display "0% health" when DR sent partial bar
                // markup. Log so we can spot it without sneaking through.
                var id     = r["id"];
                var valStr = r["value"];
                if (string.IsNullOrEmpty(id) ||
                    !int.TryParse(valStr, out var v))
                {
                    _log.LogDebug("progressBar dropped: id='{Id}' value='{Val}'", id, valStr);
                    break;
                }
                var text = r["text"] ?? v.ToString();
                _events.OnNext(new ProgressBarEvent(id, v, text));
                break;
            }

            // ── Named component update ───────────────────────────────────
            case "component":
            {
                _pendingComponentId = r["id"] ?? "";
                _componentBuffer.Clear();
                _inComponent = true;
                // A real room component arrived (movement) — the armed Lich
                // room seed (#126) must not overwrite genuine data.
                if (_roomSeedArmed && _pendingComponentId.StartsWith("room", StringComparison.OrdinalIgnoreCase))
                    _roomSeedArmed = false;
                // Content accumulates in EmitText until </component>
                break;
            }


            // ── Roundtime ────────────────────────────────────────────────
            case "roundtime":
            {
                var expiry = long.TryParse(r["value"], out var ts) ? ts : 0L;
                var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
                _events.OnNext(new RoundTimeEvent(expires));
                break;
            }

            case "casttime":
            {
                var expiry = long.TryParse(r["value"], out var ts) ? ts : 0L;
                var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
                _events.OnNext(new CastTimeEvent(expires));
                break;
            }

            // <spelltime value='epoch'/> — server-authoritative prep-start
            // (Genie 4 Game.cs:2131). Rare; see SpellTimeEvent.
            case "spelltime":
            {
                if (long.TryParse(r["value"], out var st) && st > 0)
                    _events.OnNext(new SpellTimeEvent(DateTimeOffset.FromUnixTimeSeconds(st)));
                break;
            }

            // ── Status indicator ─────────────────────────────────────────
            case "indicator":
            {
                var id      = r["id"]      ?? "";
                var visible = r["visible"] == "y";
                _events.OnNext(new IndicatorEvent(id, visible));
                break;
            }

            // ── Per-creature combat status ───────────────────────────────
            // <crtrStatus exist="91586721" hostile="0" disengaged="1" flying="1"/>
            // Keyed by exist id; the flags are "1"/"0". No exist = nothing to
            // key on, so drop it (public #202).
            case "crtrstatus":
            {
                var exist = r["exist"];
                if (string.IsNullOrEmpty(exist)) break;
                _events.OnNext(new CreatureStatusEvent(
                    exist,
                    r["hostile"]    == "1",
                    r["disengaged"] == "1",
                    r["flying"]     == "1"));
                break;
            }

            // ── Held items ───────────────────────────────────────────────
            // Stash the attributes; the HeldItemEvent fires at the CLOSE tag,
            // once the body text — the display name Genie 4 exposes as
            // $lefthand/$righthand ("razor-edged scimitar" vs noun "scimitar")
            // — has been buffered (public #172). A self-closing form has no
            // body, so it emits immediately with an empty display name.
            case "left":
            case "right":
            {
                _handNoun  = r["noun"]  ?? "";
                _handExist = r["exist"] ?? "";
                var hand = name == "left" ? Hand.Left : Hand.Right;
                if (rawTag.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                {
                    _events.OnNext(new HeldItemEvent(hand, _handNoun, _handExist, ""));
                    break;
                }
                _componentBuffer.Clear();
                _inHand = true;
                break;
            }

            // ── Prepared spell ───────────────────────────────────────────
            case "spell":
                _componentBuffer.Clear();
                _inSpell = true;
                break;

            // ── Stream routing ───────────────────────────────────────────
            case "pushstream":
            {
                FlushTextLine(); // text before a stream push belongs to the current stream
                var sid = r["id"] ?? "main";
                _streamStack.Push(_activeStream);
                _activeStream = sid;
                _events.OnNext(new StreamPushEvent(sid));
                break;
            }
            case "popstream":
            {
                FlushTextLine(); // flush stream content before popping back
                if (_streamStack.TryPop(out var prev))
                {
                    _events.OnNext(new StreamPopEvent(_activeStream, prev));
                    _activeStream = prev;
                }
                else if (_activeStream != "main")
                {
                    // Empty-stack pop = a stray/extra popStream, i.e. a push/pop
                    // desync. DR's stream model is FLAT (a pop always means "back
                    // to main"; that's why DR labels some pops with an id), so
                    // recover to main instead of silently leaving the active
                    // stream stranded on a side stream — otherwise every
                    // subsequent main line (room/NPC/emote text) emits with the
                    // stranded Stream id (e.g. "whispers"). Balanced sessions
                    // never reach this branch.
                    _activeStream = "main";
                }
                break;
            }

            // ── Prompt / timestamp ───────────────────────────────────────
            // Open tag — capture the server timestamp and start routing the
            // inner text (the indicator chars: ">", "R>", "HR>", …) into
            // _promptBuffer. We fire PromptEvent on the CLOSE tag so the
            // event carries both the timestamp and the indicator together.
            case "prompt":
            {
                FlushTextLine(); // flush any partial line before the prompt marker

                // Stream-routing backstop: a server prompt marks a message
                // boundary at which DR is always back on the main stream. If a
                // push/pop desync (a lost/dropped popStream on the wire) left
                // _activeStream stranded on a side stream, reset here so the
                // mis-routing is bounded to a single message instead of flooding
                // the side stream (e.g. "whispers") with all later main text.
                // Balanced sessions are already on "main" at this point, so this
                // is a no-op for them.
                if (_activeStream != "main") _activeStream = "main";
                _streamStack.Clear();

                var ts = long.TryParse(r["time"], out var t) ? t : 0L;
                _pendingPromptTime = DateTimeOffset.FromUnixTimeSeconds(ts);
                _promptBuffer.Clear();
                _inPrompt = true;
                break;
            }

            // ── Compass / exits ──────────────────────────────────────────
            case "compass":
                _compassBuffer.Clear();
                _inCompass = true;
                break;

            // ── Compass direction entry ──────────────────────────────────
            case "dir" when _inCompass:
            {
                var val = r["value"] ?? "";
                if (val.Length > 0)
                {
                    if (_compassBuffer.Length > 0) _compassBuffer.Append(' ');
                    _compassBuffer.Append(val);
                }
                break;
            }

            // ── Resource bar (mana, spirit, stamina absolute values) ─────
            case "resource":
            {
                // <resource picture="N"/> — DR room/scene art id ("0" = none).
                // Surface the raw id (incl. "0", so the Scene panel can clear);
                // display + showimages gating live in the App layer.
                if (r["picture"] is { } picture)
                {
                    _events.OnNext(new RoomImageEvent(picture));
                    break;
                }
                var id = r["id"] ?? "";
                if (id.Length == 0) break;
                var value = int.TryParse(r["value"], out var v) ? v : 0;
                _events.OnNext(new ResourceEvent(id, value));
                break;
            }

            // ── Session lifecycle ────────────────────────────────────────────
            case "endsetup":
                _events.OnNext(new EndSetupEvent());
                break;

            case "settingsinfo":
                _events.OnNext(new SettingsInfoEvent());
                break;

            // ── Clickable link ───────────────────────────────────────────────
            // <d cmd="bank debt">BANK DEBT</d> — when cmd is missing, the
            // inner text doubles as the command (e.g. <d>BANK DEBT</d>).
            // The text body flows through normal accumulation; we just
            // bookmark the start position and remember the cmd. On </d>
            // we'll know the span length.
            case "d":
            {
                // DR's protocol does not nest <d> tags in practice. If one
                // ever arrives, log it — the close handler still pops the
                // matching open, but downstream code may be confused by
                // overlapping spans. We don't refuse the nested push because
                // discarding it would unbalance the stack on the close.
                if (_linkStack.Count > 0)
                    _log.LogDebug("Nested <d> tag detected (depth={Depth}); link spans may overlap.", _linkStack.Count);
                var cmd = r["cmd"];
                _linkStack.Push((_textLineBuffer.Length, cmd));
                break;
            }

            // ── External hyperlink ─────────────────────────────────────────
            // <a href='URL'>label</a> — used in news/login resource blocks
            // to point at Simucoin Store, Elanthipedia, etc. Bookmark the
            // current buffer position + remember the href; on </a> we
            // commit a LinkSpan with IsUrl=true so the renderer's click
            // handler routes to the OS browser instead of the game socket.
            case "a":
            {
                if (_urlStack.Count > 0)
                    _log.LogDebug("Nested <a> tag detected (depth={Depth}); URL spans may overlap.", _urlStack.Count);
                var href = r["href"];
                _urlStack.Push((_textLineBuffer.Length, href));
                break;
            }

            // ── Bold styling ────────────────────────────────────────────────
            // Self-closing markers, NOT a paired open/close element. Each
            // pushBold records the current buffer position; popBold pops
            // and emits a BoldSpan into _pendingBoldSpans for EmitLine to
            // pick up. Used by DR to mark unread news items in bold and
            // to emphasize keywords elsewhere.
            case "pushbold":
                // Inside a component, text flows to _componentBuffer (not the
                // line buffer), so track bold against that buffer instead.
                if (_inComponent) _componentBoldStack.Push(_componentBuffer.Length);
                else              _boldStack.Push(_textLineBuffer.Length);
                break;
            case "popbold":
                if (_inComponent)
                {
                    if (_componentBoldStack.TryPop(out var cStart))
                    {
                        var cLen = _componentBuffer.Length - cStart;
                        if (cLen > 0) _componentBoldSpans.Add(new BoldSpan(cStart, cLen));
                    }
                }
                else if (_boldStack.TryPop(out var boldStart))
                {
                    var spanLen = _textLineBuffer.Length - boldStart;
                    if (spanLen > 0)
                        _pendingBoldSpans.Add(new BoldSpan(boldStart, spanLen));
                }
                else
                {
                    _log.LogDebug("Unmatched <popBold/> (no open bold on stack).");
                }
                break;

            // ── Paired HTML-style bold ──────────────────────────────────────
            // <b>…</b> is a real open/close element (DR uses it for header
            // emphasis in help text such as PROFILE HELP), NOT the self-closing
            // pushBold/popBold marker pair. Same span mechanism, opened here and
            // closed in HandleEndElement. A stray self-closing <b/> wraps no
            // text — ignore it so it can't leave a dangling entry on the stack.
            case "b":
                if (r.IsEmptyElement) break;
                if (_inComponent) _componentBoldStack.Push(_componentBuffer.Length);
                else              _boldStack.Push(_textLineBuffer.Length);
                break;

            // ── Inventory item line ──────────────────────────────────────────
            // Inline <inv id='stow'>...</inv> tags carry container contents
            // OUTSIDE a <pushStream id='inv'> wrapper — they arrive immediately
            // after the room burst when _activeStream has already popped back
            // to "main". To keep these items off the main game window, treat
            // the inv element as an implicit stream push/pop: flush any
            // pending main-stream text first, then switch to "inv" so the
            // body lines emit on the inventory stream. Close in HandleEndElement
            // flushes the buffered line and pops back.
            case "inv":
                FlushTextLine();
                _streamStack.Push(_activeStream);
                _activeStream = "inv";
                break;

            // ── Text styling spans ───────────────────────────────────────────
            case "preset":
                _currentPresetId = r["id"] ?? "";
                _presetStack.Push((_textLineBuffer.Length, _currentPresetId));
                break;

            // <style id="roomName"/> … text … <style id=""/>
            // DR emits <style> as a self-closing TOGGLE (not a wrapping pair
            // like <preset>): a non-empty id turns styling ON for the text that
            // follows; an empty id resets to default. Record a PresetSpan over
            // the spanned text so the renderer colours it via PresetEngine
            // (e.g. roomName → #FF8000). This is how the room-title line gets
            // its colour — there is no <preset> around it.
            case "style":
            {
                var styleId = r["id"] ?? "";
                if (styleId.Length > 0)
                {
                    _styleStart = _textLineBuffer.Length;
                    _styleId    = styleId;
                }
                else if (_styleStart >= 0)
                {
                    var len = _textLineBuffer.Length - _styleStart;
                    if (len > 0)
                        _pendingPresetSpans.Add(new PresetSpan(_styleStart, len, _styleId));
                    _styleStart = -1;
                    _styleId    = "";
                }
                break;
            }

            // ── Room navigation ──────────────────────────────────────────────
            case "nav":
                // Modern DR sends a BARE <nav/>; the server room id arrives via
                // the room streamWindow subtitle "(NNNNN)" (handled in the
                // streamwindow case). Only emit a NavEvent when rm is actually
                // present — an empty one would fire an early fingerprint
                // re-resolve against the not-yet-updated room title, transiently
                // landing on the wrong node before the subtitle uid corrects it.
                // Movement also cancels an armed Lich room seed (#126): real
                // room components are about to arrive.
                _roomSeedArmed = false;
                if (r["rm"] is { Length: > 0 } navRm)
                    _events.OnNext(new NavEvent(navRm));
                break;

            // ── Session identity ─────────────────────────────────────────────
            // <app char="Renucci" game="DR" title="[DR: Renucci] Wrayth"/> —
            // the server's authoritative character + game-instance code. The
            // char guard matches Genie 4 (Core/Game.cs:1907): the Wrayth
            // settings dump also contains bare <app maximized='t'/> forms,
            // which carry no identity and stay ignored.
            case "app":
                if (r["char"] is { Length: > 0 } appChar)
                    _events.OnNext(new AppEvent(appChar, r["game"] ?? "", r["title"] ?? ""));
                break;

            // ── Inventory container declaration ──────────────────────────────
            // <container id='stow' title="My Backpack" target='#37666728' location='right'/>
            // DR emits one of these per equipped container at session start
            // and re-emits when containers move. We forward TargetId+Title so
            // the UI can substitute "#NNNN" → "My Backpack" in click-echoes.
            // Skip if target is missing or empty — without an ID there's
            // nothing to map.
            case "container":
            {
                var target = r["target"];
                if (string.IsNullOrEmpty(target)) break;
                _events.OnNext(new ContainerEvent(
                    LogicalId: r["id"]    ?? "",
                    Title:     r["title"] ?? "",
                    TargetId:  target));
                break;
            }

            // ── Server dialog data (injuries) ────────────────────────────────
            // Only the injuries dialog carries game state we consume; every
            // other dialogData block (minivitals skin layout, radio defs, …)
            // is still dropped — its known children (<skin>, <radio>) stay in
            // the skip set, and <progressBar> children were always consumed
            // via the generic progressBar case. A self-closing
            // <dialogData … /> has no children, so it must not open the
            // context; any open tag for a DIFFERENT dialog closes it
            // defensively (DR never nests dialogData).
            case "dialogdata":
                _inInjuriesDialog =
                    string.Equals(r["id"], "injuries", StringComparison.OrdinalIgnoreCase)
                    && !rawTag.TrimEnd().EndsWith("/>", StringComparison.Ordinal);
                break;

            // ── Injury reading (one body region) ─────────────────────────────
            // <image id="rightLeg" name="Injury1"/> inside the injuries dialog.
            // name == region id → healthy; "Injury<N>" → wound; "Scar<N>" →
            // scar; "Nsys<N>" → nerve damage (the nsys region never uses the
            // Injury/Scar names — Lich 5 xmlparser.rb:618). Severity runs 1–3
            // for every kind; a 0 digit ("Nsys0") reads healthy. Note the
            // healthy nsys echo is lowercase "nsys" (= the region id), which
            // must hit the None branch, so the Nsys prefix check requires a
            // digit after it. Outside the injuries dialog, <image> is UI
            // layout only.
            case "image" when _inInjuriesDialog:
            {
                var area = r["id"] ?? "";
                if (area.Length == 0) break;
                var imageName = r["name"] ?? "";
                InjuryKind kind = InjuryKind.None;
                int severity = 0;
                if (imageName.StartsWith("Injury", StringComparison.OrdinalIgnoreCase))
                {
                    kind = InjuryKind.Wound;
                    int.TryParse(imageName.AsSpan("Injury".Length), out severity);
                }
                else if (imageName.StartsWith("Scar", StringComparison.OrdinalIgnoreCase))
                {
                    kind = InjuryKind.Scar;
                    int.TryParse(imageName.AsSpan("Scar".Length), out severity);
                }
                else if (imageName.Length > "Nsys".Length
                         && imageName.StartsWith("Nsys", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(imageName.AsSpan("Nsys".Length), out severity))
                {
                    kind = severity > 0 ? InjuryKind.Damage : InjuryKind.None;
                }
                // anything else (name echoes the region id) → healthy
                if (kind == InjuryKind.None) severity = 0;
                _events.OnNext(new InjuryEvent(area, kind, severity));
                break;
            }

            case "image":
                break;   // non-injuries <image> — dialog layout only, drop

            // ── Output class (bold, mono, etc.) ─────────────────────────
            case "output":
            {
                var cls = r["class"] ?? "";
                // Silent-health window: swallow the mono/'' brackets around a
                // polled `health` response (and everything between — see
                // EmitLine). Only an armed window can open; an open window
                // closes on the empty-class bracket.
                if (!_silentHealthActive && cls == "mono"
                    && DateTimeOffset.UtcNow < _silentHealthDeadline)
                {
                    FlushTextLine();   // anything buffered before the bracket is real text
                    _silentHealthActive = true;
                    _silentHealthLines  = 0;
                    break;
                }
                if (_silentHealthActive && cls.Length == 0)
                {
                    _silentHealthActive   = false;
                    _silentHealthDeadline = DateTimeOffset.MinValue;
                    _textLineBuffer.Clear();   // drop any partial suppressed line
                    break;
                }
                // #178: track the mono bracket so lines between mono and the
                // empty-class close render in the monospace font. "mono" opens;
                // "" (or any other class) closes.
                _inMono = string.Equals(cls, "mono", StringComparison.OrdinalIgnoreCase);
                _events.OnNext(new OutputClassEvent(cls));
                break;
            }

            // ── Window routing + room-title carrier ──────────────────────
            // DR emits the current room name as the subtitle attribute of
            //   <streamWindow id='room' title='Room' subtitle=' - [Garden Rooftop, Medical Pavilion]'/>
            // every time the player enters a new room. There is no
            // <component id='room title'> tag — that was a long-standing
            // assumption in this parser that left _state.Room.Title empty,
            // causing the mapper engine's OnStateChanged to bail on
            // string.IsNullOrWhiteSpace(title) and never fire matching or
            // RoomNotFoundInZone. Bridge the subtitle into a synthetic
            // ComponentEvent("room title", "[Title]") so GameStateEngine and
            // the mapper adapter pick it up via the existing pipeline.
            case "streamwindow":
            case "openwindow":
                // Routing hint — record but don't change active stream yet.
                _events.OnNext(new WindowEvent(r["id"] ?? "", r["title"] ?? ""));
                if (string.Equals(r["id"], "room", StringComparison.OrdinalIgnoreCase))
                {
                    // DR emits the current room name as the subtitle attribute:
                    //   subtitle=" - [Garden Rooftop, Medical Pavilion]"
                    // Rare edge case: nested brackets like " - [Garden [Inner]]"
                    // — pair the LAST '[' with the LAST ']' so we capture the
                    // innermost bracketed label (DR convention when nesting).
                    // Without this we'd grab "[Garden [Inner]" with an
                    // unbalanced count and the mapper would mis-fingerprint.
                    var subtitle = r["subtitle"] ?? "";
                    var lastOpen  = subtitle.LastIndexOf('[');
                    var lastClose = subtitle.LastIndexOf(']');
                    string title;
                    if (lastOpen >= 0 && lastClose > lastOpen)
                        title = subtitle.Substring(lastOpen, lastClose - lastOpen + 1);
                    else if (subtitle.Contains(" - "))
                        title = subtitle[(subtitle.IndexOf(" - ") + 3)..].Trim();
                    else
                        title = subtitle.TrimStart(' ', '-').Trim();
                    if (!string.IsNullOrEmpty(title))
                        _events.OnNext(new ComponentEvent("room title", title));

                    // Server room id: DR appends it to the subtitle as "(NNNNN)"
                    // after the title when room numbers are enabled. Modern
                    // StormFront sends a BARE <nav/>, so this subtitle is the
                    // ONLY carrier of the server room id — without it the mapper
                    // has no authoritative match and runs fingerprint-only
                    // (collides on same-description rooms; breaks scripts that
                    // read $roomid/$gameroomid, e.g. travel.cmd never confirming
                    // arrival). Emit it as a NavEvent so the id flows to
                    // GameState.Room.RoomId → the mapper adapter. "(**)" (unknown
                    // room) has no digits and is correctly ignored.
                    var tail = lastClose >= 0 && lastClose + 1 <= subtitle.Length
                        ? subtitle[(lastClose + 1)..] : subtitle;
                    var uid = _roomUidRe.Match(tail);
                    if (uid.Success)
                        _events.OnNext(new NavEvent(uid.Groups[1].Value));
                }
                break;

            // ── Clear window ─────────────────────────────────────────────
            // <clearDynaStream id='spellInfo'/> is the dynamic-stream variant
            // of <clearStream> (public #220) — same clear-this-window meaning.
            case "clearstream":
            case "cleardynastream":
                _events.OnNext(new ClearStreamEvent(r["id"] ?? ""));
                break;

            // ── Unrecognised (log at trace for AI analysis) ──────────────
            default:
                _log.LogTrace("Unknown DR tag: {Tag}", rawTag);
                _events.OnNext(new UnknownTagEvent(name, rawTag));
                break;
        }
    }

    private void HandleEndElement(string name)
    {
        // Tag boundary — same blank-line gating as HandleElement (#176). A
        // close handler that emits real text (hand/spell/inv merge-seam,
        // roomDesc flush) re-arms the flag itself via EmitLine.
        _emittedTextLine = false;

        if (_settingsTags.Contains(name)) return;

        switch (name.ToLowerInvariant())
        {
            case "component" when _inComponent:
            {
                _inComponent = false;
                var raw      = _componentBuffer.ToString();
                var content  = System.Net.WebUtility.HtmlDecode(StripBasicXml(raw)).Trim();
                var id       = _pendingComponentId ?? "";
                // Bold names (creatures) → each phrase runs from the bold start
                // to the next comma/period in the raw buffer (Genie 4 captures
                // the trailing descriptor, e.g. "a kobold that appears dead", so
                // the ignore list can match it). Decoded; positions index the
                // same raw buffer the spans were recorded against.
                IReadOnlyList<string>? boldNames = ExtractBoldPhrases(raw);
                IReadOnlyList<BoldSpan>? boldSpans = ExtractComponentBoldSpans(raw, content);
                _events.OnNext(new ComponentEvent(id, content, boldNames, boldSpans));
                _pendingComponentId = null;
                _componentBuffer.Clear();
                _componentBoldSpans.Clear();
                _componentBoldStack.Clear();
                break;
            }
            case "spell" when _inSpell:
            {
                _inSpell = false;
                var spellBody = _componentBuffer.ToString();
                _componentBuffer.Clear();
                // Merge-seam recovery (same invariant as hands): the server can
                // concatenate a response onto the prepared-spell name with no
                // separator (<spell>Fire ShardsYou feel…</spell>). Split at the
                // first lower→upper seam. Unlike hands we KEEP the prefix — it's
                // the real spell name — and re-emit the suffix as game text.
                // Spell names never carry a bare lower→upper adjacency (spaces /
                // apostrophes always break it, e.g. "Fire Shards", "Y'ntrel
                // Sechra"), so a clean spell never trips the seam.
                var spellSeam = FindMergeSeam(spellBody);
                var spellAppended = "";
                if (spellSeam > 0)
                {
                    spellAppended = spellBody[spellSeam..];
                    spellBody     = spellBody[..spellSeam];
                }
                var name2 = System.Net.WebUtility.HtmlDecode(StripBasicXml(spellBody)).Trim();
                _events.OnNext(new SpellEvent(name2));
                if (spellAppended.Length > 0) EmitLine(spellAppended);
                break;
            }
            case "left" when _inHand:
            case "right" when _inHand:
            {
                _inHand = false;
                var handBody = _componentBuffer.ToString();
                _componentBuffer.Clear();
                // The body is the display name ("razor-edged scimitar", or
                // "Empty") — Genie 4's $lefthand/$righthand (#172). But the
                // server sometimes merges a response INTO the hand body with
                // no separator:
                //   <right noun='ledger'>black ledgerYou unlock and open…</right>
                // Splitting on the first lower→upper seam separates them: the
                // prefix is the display name, the suffix is appended game text
                // (re-emitted so it isn't silently lost).
                var handSeam = FindMergeSeam(handBody);
                var handAppended = "";
                if (handSeam > 0)
                {
                    handAppended = handBody[handSeam..];
                    handBody     = handBody[..handSeam];
                }
                var display = System.Net.WebUtility.HtmlDecode(StripBasicXml(handBody)).Trim();
                _events.OnNext(new HeldItemEvent(
                    name.Equals("left", StringComparison.OrdinalIgnoreCase) ? Hand.Left : Hand.Right,
                    _handNoun, _handExist, display));
                if (handAppended.Length > 0) EmitLine(handAppended);
                break;
            }

            case "inv":
            {
                // Merge-seam recovery (same invariant as hands/spell): the
                // server can concatenate a response onto an inv item with no
                // separator (<inv> a leather bellowsYou put…</inv>). Split the
                // buffered item at the first lower→upper seam — the prefix is
                // the real item (flushed on the inv stream), the suffix is
                // appended game text (re-emitted on the stream the inv block
                // interrupted). Item descriptions never carry a bare lower→upper
                // adjacency (a space/apostrophe always precedes a capital, e.g.
                // "orange Moon Sphere", "ka'hurst carving knife"), so a clean
                // item never trips the seam.
                var invBody = _textLineBuffer.ToString();
                var invSeam = FindMergeSeam(invBody);
                string? invAppended = null;
                if (invSeam > 0)
                {
                    invAppended = invBody[invSeam..];
                    _textLineBuffer.Clear();
                    _textLineBuffer.Append(invBody[..invSeam]);
                }

                // Force each inventory item onto its own line, then restore
                // the previous stream context (matches the implicit push in
                // HandleElement). Without the pop, subsequent main-stream
                // text would continue to emit on the "inv" stream.
                FlushTextLine();
                if (_streamStack.TryPop(out var invPrev))
                    _activeStream = invPrev;

                // Appended response belongs to the interrupted stream, not inv.
                if (invAppended is { Length: > 0 })
                    EmitLine(invAppended);
                break;
            }

            case "d":
                // Close a clickable link. Compute the span length from the
                // bookmark we set on <d> open, and use the inner text as the
                // command when the cmd attribute was missing (DR convention:
                // <d>BANK DEBT</d> sends "BANK DEBT" on click).
                if (_linkStack.TryPop(out var link))
                {
                    var spanLen = _textLineBuffer.Length - link.Start;
                    if (spanLen > 0)
                    {
                        var inner = _textLineBuffer.ToString(link.Start, spanLen);
                        var cmd   = string.IsNullOrEmpty(link.Cmd) ? inner : link.Cmd;
                        _pendingLinks.Add(new LinkSpan(link.Start, spanLen, cmd!));
                    }
                }
                else
                {
                    // Unmatched </d> — stack was empty. Genie 4 never observed
                    // this in practice but log it so a future protocol change
                    // doesn't silently corrupt subsequent link spans.
                    _log.LogDebug("Unmatched </d> closing tag (no open link on stack).");
                }
                break;

            case "a":
                // Close an external hyperlink. Span text is the visible label
                // (e.g. "Elanthipedia"); the href on the open tag is what we
                // pass through as Command (the URL). IsUrl=true tells the
                // renderer to route this through the OS browser path instead
                // of the game-command pipeline. If href is missing or empty,
                // skip emission — we'd have nothing useful to open. The
                // visible text stays in the buffer either way.
                if (_urlStack.TryPop(out var urlEntry))
                {
                    var spanLen = _textLineBuffer.Length - urlEntry.Start;
                    if (spanLen > 0 && !string.IsNullOrEmpty(urlEntry.Href))
                    {
                        _pendingLinks.Add(new LinkSpan(
                            urlEntry.Start, spanLen, urlEntry.Href!, IsUrl: true));
                    }
                }
                else
                {
                    _log.LogDebug("Unmatched </a> closing tag (no open URL on stack).");
                }
                break;

            case "preset":
                // Record the preset span over the text accumulated since the
                // open tag BEFORE any flush, so a roomDesc flush emits the line
                // with its span attached.
                if (_presetStack.TryPop(out var openPreset))
                {
                    var len = _textLineBuffer.Length - openPreset.Pos;
                    if (len > 0)
                        _pendingPresetSpans.Add(new PresetSpan(openPreset.Pos, len, openPreset.Id));
                }
                // Only flush for presets where the following text is a new line
                // (roomDesc → exits follow; inv → next item follows).
                // For whisper/speech the quoted content is a continuation on the
                // same line, so flushing here would split "Renucci whispers," from
                // its message.
                if (_currentPresetId is "roomDesc" or "inv")
                    FlushTextLine();
                _currentPresetId = "";
                break;

            case "prompt":
                // Close tag — emit the PromptEvent with the timestamp captured
                // at open + the indicator string accumulated from the body.
                // The raw body may contain encoded entities (e.g. "&gt;" → ">");
                // _promptBuffer holds the post-decode text since AccumulateText
                // routes already-decoded chunks here.
                if (_inPrompt)
                {
                    // HTML-decode: DR sends ">" as the entity "&gt;" inside
                    // the <prompt> body. The normal text path runs every
                    // line through HtmlDecode in EmitLine; the prompt path
                    // bypasses EmitLine (we route to _promptBuffer instead
                    // of _textLineBuffer) so we must decode here ourselves.
                    // Without this the renderer shows literal "R&gt;" / "&gt;".
                    var indicator = System.Net.WebUtility
                        .HtmlDecode(_promptBuffer.ToString()).Trim();
                    _events.OnNext(new PromptEvent(_pendingPromptTime, indicator));
                    // Displaying the prompt in the game window is an App-layer
                    // concern: GameTextViewModel subscribes to PromptEvent and
                    // applies Config.Prompt + promptbreak dedup. Core stays
                    // UI-free and never decides when a prompt line is shown.
                    _promptBuffer.Clear();
                    _inPrompt = false;
                }
                // Defensive: a malformed stream missing the open could leave
                // partial text in _textLineBuffer; clearing here matches the
                // pre-change behavior so a corrupt prompt doesn't leak into
                // the next line.
                _textLineBuffer.Clear();
                break;

            case "compass" when _inCompass:
            {
                _inCompass = false;
                _events.OnNext(new CompassEvent(_compassBuffer.ToString()));
                _compassBuffer.Clear();
                break;
            }

            case "dialogdata":
                _inInjuriesDialog = false;
                break;

            case "b":
            {
                // Close the paired <b>…</b> bold span opened in HandleElement
                // (mirrors popBold). Record over the text accumulated since the
                // open so EmitLine attaches it to the resulting TextEvent.
                if (_inComponent)
                {
                    if (_componentBoldStack.TryPop(out var cbStart))
                    {
                        var cbLen = _componentBuffer.Length - cbStart;
                        if (cbLen > 0) _componentBoldSpans.Add(new BoldSpan(cbStart, cbLen));
                    }
                }
                else if (_boldStack.TryPop(out var bStart))
                {
                    var bLen = _textLineBuffer.Length - bStart;
                    if (bLen > 0) _pendingBoldSpans.Add(new BoldSpan(bStart, bLen));
                }
                else
                {
                    _log.LogDebug("Unmatched </b> (no open bold on stack).");
                }
                break;
            }
        }
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    // Matches ANSI escape sequences like ESC[1m, ESC[0m, ESC[31m, etc.
    private static readonly System.Text.RegularExpressions.Regex _ansiRe =
        new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Strips XML formatting tags and ANSI escape codes from text,
    /// leaving only human-readable content.
    /// </summary>
    /// <summary>
    /// Build the bold "creature" phrases for the just-closed component from the
    /// recorded <see cref="_componentBoldSpans"/> (positions into <paramref name="raw"/>,
    /// the un-stripped component buffer). Each phrase runs from the bold start to
    /// the next comma/period — capturing the trailing descriptor ("a kobold that
    /// appears dead") so a consumer's ignore list can match it. Returns null when
    /// the component had no bold (the common case), so non-creature components
    /// stay allocation-free.
    /// </summary>
    private IReadOnlyList<string>? ExtractBoldPhrases(string raw)
    {
        if (_componentBoldSpans.Count == 0) return null;
        var phrases = new List<string>(_componentBoldSpans.Count);
        foreach (var span in _componentBoldSpans)
        {
            var start = Math.Clamp(span.Start, 0, raw.Length);
            var from  = Math.Clamp(span.Start + span.Length, start, raw.Length);
            var end   = raw.IndexOfAny(new[] { ',', '.' }, from);
            if (end < 0) end = raw.Length;

            // Cap the phrase at the START of the next bold creature. DR separates
            // list items with a comma EXCEPT the final pair, joined by " and "
            // with no comma ("a viper and a viper.", and the only separator in a
            // two-mob list). Without this cap the comma/period scan runs past
            // " and " and swallows the following creature, so two same-type mobs
            // show as one entry "a viper and a viper" and the count is off
            // (#118). The trailing descriptor ("a kobold that appears dead")
            // still survives — it sits before the next bold span, not after it.
            foreach (var other in _componentBoldSpans)
                if (other.Start > span.Start && other.Start < end) end = other.Start;

            var phrase = System.Net.WebUtility.HtmlDecode(raw[start..end]).Trim();
            phrase = TrimTrailingConnector(phrase);
            if (phrase.Length > 0) phrases.Add(phrase);
        }
        return phrases.Count > 0 ? phrases : null;
    }

    /// <summary>
    /// Bold ranges for a component's DISPLAY text (#131 Room-panel MonsterBold),
    /// as character offsets into <paramref name="content"/> (the decoded/stripped
    /// text the ComponentEvent carries). Unlike <see cref="ExtractBoldPhrases"/>
    /// — which extends each phrase to the next comma/period for creature-counting
    /// and so over-reaches ("a ship's rat and a poster") — this returns the EXACT
    /// &lt;pushBold&gt; span. Each recorded bold range is a range in the raw
    /// buffer; we decode that slice to the text as it appears in Content and find
    /// it there (scanning forward so duplicate creatures map in order and any
    /// tag/entity/trim shifts are absorbed). Consumed by RoomViewModel to render
    /// creatures gold in the Room panel via the same Tokenize path as the streams.
    /// </summary>
    private IReadOnlyList<BoldSpan>? ExtractComponentBoldSpans(string raw, string content)
    {
        if (_componentBoldSpans.Count == 0 || content.Length == 0) return null;

        // Buffer order (ascending start) so the forward scan below can't rematch
        // an earlier duplicate; the recorded spans are flat for room objs but
        // sorting keeps this correct if a component ever nests bold.
        var ordered = _componentBoldSpans.OrderBy(s => s.Start);
        var spans = new List<BoldSpan>(_componentBoldSpans.Count);
        int searchFrom = 0;
        foreach (var span in ordered)
        {
            var start = Math.Clamp(span.Start, 0, raw.Length);
            var end   = Math.Clamp(span.Start + span.Length, start, raw.Length);
            if (end <= start) continue;
            var boldText = System.Net.WebUtility.HtmlDecode(raw[start..end]);
            if (boldText.Length == 0) continue;
            int at = content.IndexOf(boldText, Math.Min(searchFrom, content.Length),
                                     System.StringComparison.Ordinal);
            if (at < 0) continue;   // shouldn't happen — the bold text is a slice of the same source
            spans.Add(new BoldSpan(at, boldText.Length));
            searchFrom = at + boldText.Length;
        }
        return spans.Count > 0 ? spans : null;
    }

    /// <summary>
    /// Strip a trailing list connector left behind when a bold phrase is capped
    /// at the next creature mid-list — a dangling "and" and/or comma (e.g.
    /// "a sleazy lout and" → "a sleazy lout"). Loops because the connector can be
    /// ", and". Case-insensitive on the word "and".
    /// </summary>
    private static string TrimTrailingConnector(string s)
    {
        s = s.TrimEnd();
        while (true)
        {
            if (s.EndsWith(",", System.StringComparison.Ordinal))
                s = s[..^1].TrimEnd();
            else if (s.EndsWith(" and", System.StringComparison.OrdinalIgnoreCase))
                s = s[..^4].TrimEnd();
            else
                return s;
        }
    }

    private static string StripBasicXml(string input)
    {
        if (input.Contains('\x1B'))
            input = _ansiRe.Replace(input, string.Empty);
        if (!input.Contains('<')) return input;
        // Strip only spans that could actually BE tags — the same name-start
        // rule the ProcessBuffer lexer uses ("<x", "</x", "<!", "<?"). Angle-
        // bracket literals the lexer deliberately passed through as text
        // ("<1-20>", "<*special*>") must survive this residual cleanup too
        // (public #238); real leaked tags (a nested <d …> inside a component
        // buffer) still get removed.
        return System.Text.RegularExpressions.Regex.Replace(
            input, "<(?:/?[A-Za-z_]|[!?])[^>]*>", string.Empty);
    }

    public void Dispose() => _events.Dispose();
}
