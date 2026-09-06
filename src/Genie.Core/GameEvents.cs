namespace Genie.Core.Events;

// ── Base ─────────────────────────────────────────────────────────────────────

/// <summary>Base for all events emitted by <see cref="Parser.DrXmlParser"/>.</summary>
public abstract record GameEvent;

// ── Text output ──────────────────────────────────────────────────────────────

/// <summary>Bare text line — room descriptions, combat results, dialogue, system messages.</summary>
/// <param name="Stream">e.g. "main", "logons", "thoughts", "death".</param>
/// <param name="Text">The visible line content. Leading whitespace preserved for server-side layout.</param>
/// <param name="Links">
/// Optional click-targets carried by <c>&lt;d cmd="..."&gt;</c> elements in the
/// XML. Each span is (Start, Length, Command) — Start/Length are character
/// offsets into <see cref="Text"/>, Command is what gets sent on click (the
/// <c>cmd</c> attribute when present, or the link's visible text when not).
/// Null when the line had no link markup; never empty.
/// </param>
/// <param name="BoldSpans">
/// Optional bold-text regions carried by <c>&lt;pushBold/&gt;</c> ...
/// <c>&lt;popBold/&gt;</c> markers in the XML. DR uses bold for emphasis
/// — most visibly to flag unread items in the <c>news</c> listing. Null
/// when the line had no bold markup; never empty.
/// </param>
/// <param name="PresetSpans">
/// Optional preset-styled regions carried by <c>&lt;preset id="..."&gt;</c> …
/// <c>&lt;/preset&gt;</c> markers (roomDesc, whisper, speech, roomName, …). The
/// parser records each region's id + offsets so the renderer can apply that
/// preset's configured colour. Null when the line had no preset markup.
/// </param>
/// <param name="Mono">
/// True when the line arrived inside a <c>&lt;output class="mono"/&gt;</c> …
/// <c>&lt;output class=""/&gt;</c> bracket — DR marks maps, stat tables, and
/// other column-aligned blocks this way. The display renders these in the
/// monospace font while normal prose uses the configured game font (public
/// #178), so a proportional normal font doesn't break table alignment.
/// </param>
/// <param name="DuplicateEcho">
/// True when this <c>main</c>-stream line is DR's bare re-send of a line it
/// already sent wrapped in a social stream — e.g. a talk/whispers line
/// arrives once inside <c>&lt;pushStream id="talk"&gt;…&lt;popStream/&gt;</c>,
/// then immediately again as plain <c>main</c> text right after the pop
/// (confirmed via a live capture; presumably for clients that don't
/// understand streams). The event is still emitted in full — Core consumers
/// (triggers under <c>ParseGameOnly</c>, scripts, plugins) need it exactly
/// as DR sent it — this flag only tells a <i>display</i> sink it would be
/// showing the same content a second time if it renders this line too.
/// Always false for the stream-tagged original and for ordinary main text.
/// </param>
public sealed record TextEvent(
    string Stream,
    string Text,
    IReadOnlyList<LinkSpan>? Links       = null,
    IReadOnlyList<BoldSpan>? BoldSpans   = null,
    IReadOnlyList<PresetSpan>? PresetSpans = null,
    bool Mono = false,
    bool DuplicateEcho = false
) : GameEvent;

/// <summary>
/// A clickable region inside a <see cref="TextEvent"/>. DR's XML wraps these
/// in <c>&lt;d cmd="..."&gt;visible text&lt;/d&gt;</c>; the parser strips the
/// tags from <see cref="TextEvent.Text"/> and records the span here so the
/// renderer can draw the visible text underlined and dispatch the command
/// when the user clicks it.
/// </summary>
/// <summary>
/// Inline clickable span within a <see cref="TextEvent"/>.
/// <para>
/// When <see cref="IsUrl"/> is false (default), this came from a
/// <c>&lt;d cmd&gt;</c> link and <see cref="Command"/> is a game command
/// that should be sent through the normal input pipeline on click.
/// </para>
/// <para>
/// When <see cref="IsUrl"/> is true, this came from an <c>&lt;a href&gt;</c>
/// hyperlink and <see cref="Command"/> is a URL that should be opened in the
/// user's OS-default browser. DR emits these in news/login resources blocks
/// (Simucoin Store, Elanthipedia, Olwydd's, Ranik Maps, etc.).
/// </para>
/// </summary>
public sealed record LinkSpan(int Start, int Length, string Command, bool IsUrl = false);

/// <summary>
/// A bold-styled region inside a <see cref="TextEvent"/>. Carried by
/// <c>&lt;pushBold/&gt;</c> ... <c>&lt;popBold/&gt;</c> markers. The
/// parser strips the tags from <see cref="TextEvent.Text"/> and records
/// the byte offsets so the renderer can apply <c>FontWeight.Bold</c>.
/// </summary>
public sealed record BoldSpan(int Start, int Length);

/// <summary>
/// A preset-styled region inside a <see cref="TextEvent"/>. Carried by
/// <c>&lt;preset id="..."&gt;</c> … <c>&lt;/preset&gt;</c> markers. The parser
/// records the region's offsets and the preset <see cref="PresetId"/> (e.g.
/// "roomDesc", "whisper", "speech") so the renderer can colour it from the
/// configured preset palette (<c>Genie.Core.Presets.PresetEngine</c>).
/// </summary>
public sealed record PresetSpan(int Start, int Length, string PresetId);

// ── Vitals ───────────────────────────────────────────────────────────────────

/// <summary>
/// &lt;progressBar id="health" value="87" text="87"/&gt;
/// IDs seen in DR: health, mana, spirit, stamina, encumbrance, concentration,
/// plus <c>health2</c> — the same health figure, reported by the bar inside the
/// <c>injuries</c> dialog rather than <c>minivitals</c>. The parser reports the
/// server’s id verbatim (the dialog renderer needs it); consumers that map bars
/// onto vitals go through <see cref="VitalBars.Normalize"/>.
/// </summary>
public sealed record ProgressBarEvent(
    string BarId,    // "health" | "health2" | "mana" | "spirit" | "stamina" | "encumbrance" | "concentration"
    int    Value,    // 0–100 (percentage) for health/mana/spirit/stamina
    string Text      // display label (may be "87" or "87/100" etc.)
) : GameEvent;

/// <summary>
/// Bar-id aliasing for <see cref="ProgressBarEvent"/> consumers that map bars onto
/// vitals (game state, script globals, the vitals panel).
/// </summary>
/// <remarks>
/// DR reports health twice under different ids: <c>health</c> from
/// <c>minivitals</c>, and <c>health2</c> from the bar inside the <c>injuries</c>
/// dialog. They agree — across a 45-minute recorded session (2026-08-30) the two
/// never disagreed once both were flowing — but <c>health2</c> starts arriving
/// FIRST: 13 of its samples landed before the first <c>minivitals</c> health bar,
/// so a consumer that ignores it shows its default value through session opening.
/// The parser deliberately keeps the raw id (the injuries dialog renders that
/// control), so the alias is applied here, once, on the way in.
/// </remarks>
public static class VitalBars
{
    /// <summary>Lower-case a raw <c>progressBar</c> id and fold known aliases onto
    /// the canonical vitals id. Unknown ids pass through lower-cased.</summary>
    public static string Normalize(string? barId)
    {
        var id = barId?.ToLowerInvariant() ?? "";
        return id == "health2" ? "health" : id;
    }
}

/// <summary>
/// &lt;resource id="mana" value="412"/&gt; — absolute value bar (seen in some DR variants).
/// </summary>
public sealed record ResourceEvent(string ResourceId, int Value) : GameEvent;

/// <summary>
/// &lt;resource picture="3025"/&gt; — DR room/scene art id. DR sends this alongside
/// room output for locations that have artwork; <c>"0"</c> means "no image"
/// (clear). Core only surfaces the id — fetching <c>DR-art/{id}.jpg</c> from
/// play.net and displaying it is an App-layer concern, gated by
/// <c>showimages</c>. The Scene panel dedups and clears on <c>"0"</c>.
/// </summary>
public sealed record RoomImageEvent(string PictureId) : GameEvent;

// ── Action timing ────────────────────────────────────────────────────────────

/// <summary>&lt;roundTime value="unix_timestamp"/&gt; — when the next action will be free.</summary>
public sealed record RoundTimeEvent(DateTimeOffset ExpiresAt) : GameEvent;

/// <summary>&lt;castTime value="unix_timestamp"/&gt; — spell casting lock expiry.</summary>
public sealed record CastTimeEvent(DateTimeOffset ExpiresAt) : GameEvent;

/// <summary>&lt;spelltime value="unix_timestamp"/&gt; — server-authoritative epoch of
/// when the currently prepared spell's prep began (Genie 4 Core/Game.cs:2131 sets
/// $spellstarttime from it). Not observed in normal prep sequences — the usual
/// source of prep-start is the &lt;spell&gt; tag arrival — but honored when sent
/// (e.g. logging in with a spell already held).</summary>
public sealed record SpellTimeEvent(DateTimeOffset StartsAt) : GameEvent;

// ── Named components ─────────────────────────────────────────────────────────

/// <summary>
/// &lt;component id='room title'&gt;…&lt;/component&gt;
///
/// Known DR component IDs:
///   room title     — current room name
///   room desc      — room description prose
///   room objs      — visible objects in room
///   room players   — other players present
///   room exits     — available exit directions
///   room extra     — extra room details
///   PC Health      — player health text string
///   PC Mana        — player mana text string
///   PC Stamina     — player stamina text string
///   PC Spirit      — player spirit text string
///   PC Stance      — offensive/defensive stance label
///   PC Encumbrance — encumbrance description
///   PC Status      — status effects (bleeding, stunned, etc.)
/// </summary>
/// <param name="BoldNames">For components that bolded names (DR bolds creatures
/// in <c>room objs</c>), the bold phrases incl. their trailing descriptor —
/// e.g. "a brown lynx that is sleeping". Null when nothing was bold. Backs the
/// monster count.</param>
public sealed record ComponentEvent(
    string ComponentId,
    string Content,
    IReadOnlyList<string>? BoldNames = null,
    IReadOnlyList<BoldSpan>? BoldSpans = null) : GameEvent;

// ── Status indicators ────────────────────────────────────────────────────────

/// <summary>
/// &lt;indicator id="IconKNEELING" visible="y"/&gt;
///
/// Known DR indicator IDs: IconKNEELING, IconPRONE, IconSITTING, IconSTANDING,
/// IconSTUNNED, IconHIDDEN, IconINVISIBLE, IconDEAD, IconWEBBED, IconBLEEDING,
/// IconPOISONED, IconDISEASED.
/// </summary>
public sealed record IndicatorEvent(string IndicatorId, bool Visible) : GameEvent;

/// <summary>
/// &lt;crtrStatus exist="91586721" hostile="0" disengaged="1" flying="1"/&gt;
///
/// Per-creature combat status keyed by the creature's <c>exist</c> object id.
/// <paramref name="Hostile"/> — the creature is hostile; <paramref name="Disengaged"/>
/// — you are disengaged from it (0 = engaged); <paramref name="Flying"/> — it is
/// airborne. Emitted per creature; the engine keeps the latest reading per
/// <paramref name="ExistId"/> in <c>CombatState.CreatureStatuses</c>, cleared
/// on room change since engagement is room-local (public #202).
/// </summary>
public sealed record CreatureStatusEvent(
    string ExistId, bool Hostile, bool Disengaged, bool Flying) : GameEvent;

// ── Injuries ─────────────────────────────────────────────────────────────────

/// <summary>What the injuries dialog reports for a body region. Severity runs
/// 1–3 for every kind (verified against Lich 5's parser, which packs each
/// region into 2 GSL bits and maps nerve text to at most 3).</summary>
public enum InjuryKind
{
    /// <summary>Region is healthy (the image name equals the region id, or a
    /// kind prefix with digit 0 — e.g. <c>Nsys0</c>).</summary>
    None,
    /// <summary>Fresh wound — image name <c>Injury1</c>…<c>Injury3</c>.</summary>
    Wound,
    /// <summary>Healed scar — image name <c>Scar1</c>…<c>Scar3</c>.</summary>
    Scar,
    /// <summary>Nerve damage — image name <c>Nsys1</c>…<c>Nsys3</c>. The nsys
    /// region NEVER uses the Injury/Scar names, and the dialog image alone
    /// cannot say whether the damage is a wound or a scar. It surfaces as this
    /// indeterminate kind until a <c>health</c> response resolves it: the
    /// parser always scans main-stream text for the six nerve lines (so a
    /// user-typed <c>health</c> refines it for free), and the user can opt in
    /// to a silent poll cadence (<c>#config injuriespoll N</c> / the Injuries
    /// panel's Auto-refresh picker; off by default) that re-emits a
    /// Wound/Scar-kinded event for the nsys region.</summary>
    Damage,
}

/// <summary>
/// One body-region update from the server's injuries dialog:
/// <c>&lt;dialogData id="injuries"&gt;&lt;image id="rightLeg" name="Injury1"/&gt;…</c>.
///
/// <para>
/// <see cref="Area"/> is the raw region id — one of DR's 16 hit-test regions:
/// head, neck, chest, abdomen, back, nsys (nervous system), plus left/right
/// eye, arm, hand, leg, foot. A healthy region arrives with
/// <c>name == id</c> and emits <see cref="InjuryKind.None"/> with severity 0.
/// </para>
///
/// <para>
/// The dialog reflects the player's selected display mode (the E/I
/// Wound/Scar/Both radios, <c>_injury N</c>), so a region that carries both a
/// wound and a scar reports whichever the current mode shows — the event is a
/// display snapshot, not a full medical chart.
/// </para>
/// </summary>
public sealed record InjuryEvent(string Area, InjuryKind Kind, int Severity) : GameEvent;

// ── Inventory ────────────────────────────────────────────────────────────────

public enum Hand { Left, Right }

/// <summary>
/// &lt;left exist="12345" noun="scimitar"&gt;razor-edged scimitar&lt;/left&gt; (or
/// &lt;right …&gt;). <paramref name="Noun"/>/<paramref name="ExistId"/> come from the
/// attributes; <paramref name="Display"/> is the element's body text — the full
/// display name ("razor-edged scimitar", or "Empty" for an empty hand), which is
/// what Genie 4 exposes as <c>$lefthand</c>/<c>$righthand</c> (public #172).
/// </summary>
public sealed record HeldItemEvent(Hand Hand, string Noun, string ExistId, string Display) : GameEvent;

// ── Spell ────────────────────────────────────────────────────────────────────

/// <summary>&lt;spell&gt;Fire Ball&lt;/spell&gt; — prepared or active spell name. Empty = none.</summary>
public sealed record SpellEvent(string SpellName) : GameEvent;

// ── Navigation ───────────────────────────────────────────────────────────────

/// <summary>Raw compass tag content — exits available from current room.</summary>
public sealed record CompassEvent(string RawXml) : GameEvent;

// ── Stream routing ───────────────────────────────────────────────────────────

public sealed record StreamPushEvent(string StreamId) : GameEvent;
public sealed record StreamPopEvent(string LeavingStreamId, string ReturningStreamId) : GameEvent;
public sealed record WindowEvent(string WindowId, string Title) : GameEvent;
public sealed record ClearStreamEvent(string StreamId) : GameEvent;

/// <summary>
/// Text routed into a named streamBox — <c>&lt;dynaStream id='spells'&gt;…&lt;/dynaStream&gt;</c>
/// (public #324). The setter companion to <see cref="ClearStreamEvent"/>, and
/// how a server dialog's streamBox gets its content (#156). Markup inside the
/// body is stripped to plain text.
/// </summary>
public sealed record DynaStreamEvent(string StreamId, string Text) : GameEvent;

// ── Prompt ───────────────────────────────────────────────────────────────────

/// <summary>
/// Server-side prompt — marks end of a server output batch.
///
/// <para>
/// <b>ServerTime</b> is the Unix-epoch timestamp from the &lt;prompt time='…'/&gt;
/// open tag. For bare-text prompts in Wizard mode (no XML) this is
/// <see cref="DateTimeOffset.MinValue"/>.
/// </para>
///
/// <para>
/// <b>Indicator</b> is the raw prompt string the server sent — typically one of:
/// <list type="bullet">
///   <item><c>&gt;</c> — ready, no special state</item>
///   <item><c>R&gt;</c> — in roundtime</item>
///   <item><c>H&gt;</c> — hidden / stalking</item>
///   <item><c>HR&gt;</c> — hidden + roundtime</item>
///   <item><c>S&gt;</c> — stunned</item>
///   <item><c>D&gt;</c> — dead</item>
/// </list>
/// In Wizard plain-text mode this is the only authoritative source of these
/// status flags — the separate &lt;indicator&gt; / &lt;roundTime&gt; tags only
/// appear in StormFront/Wrayth XML. Genie 4 scripts read this as the
/// <c>$prompt</c> variable.
/// </para>
/// </summary>
public sealed record PromptEvent(DateTimeOffset ServerTime, string Indicator = "") : GameEvent;

// ── Output styling ───────────────────────────────────────────────────────────

public sealed record OutputClassEvent(string ClassName) : GameEvent;

// ── Session lifecycle ────────────────────────────────────────────────────────

/// <summary>&lt;endSetup/&gt; — server init block complete, gameplay stream begins.</summary>
public sealed record EndSetupEvent() : GameEvent;

/// <summary>
/// &lt;nav rm="12345"/&gt; — room navigation occurred.
/// RoomId is the server-assigned numeric room ID — the mapper's primary lookup key.
/// Empty string if the server omits the rm attribute (rare).
/// </summary>
public sealed record NavEvent(string RoomId) : GameEvent;

/// <summary>
/// Session identity from the login stream's <c>&lt;app char="Renucci" game="DR"
/// title="[DR: Renucci] Wrayth"/&gt;</c> tag — the server's authoritative word on
/// which character and game instance (<c>DR</c>/<c>DRX</c>/<c>DRF</c>/<c>DRT</c>)
/// this connection serves. Matters most for Lich sessions, where the connect
/// dialog can't know which instance Lich logged into but community scripts
/// branch on <c>$game</c> (Platinum portals, Fallen shortcuts). Genie 4 parity:
/// Core/Game.cs:1903 reads the same attributes. The settings-dump form
/// <c>&lt;app maximized='t'/&gt;</c> (no <c>char</c>) never emits this.
/// </summary>
public sealed record AppEvent(string Character, string Game, string Title) : GameEvent;

/// <summary>
/// Character's guild, parsed from the <c>info</c> verb output line
/// (<c>Name: … Race: … Guild: X</c>). DR doesn't push guild in a structured
/// tag, so this only fires when the player runs <c>info</c>. <see cref="Guild"/>
/// is the raw display name (e.g. "Barbarian", "Moon Mage", "Commoner").
/// </summary>
public sealed record GuildEvent(string Guild) : GameEvent;

/// <summary>
/// Bare character name learned mid-session. DirectSGE knows the name from the
/// login handshake, but a Lich-proxy attach doesn't — Lich performed the login,
/// so the client never sees <c>&lt;app char=…/&gt;</c>. Fired by the parser when
/// the Lich ident reply arrives (public issue #127). The <c>info</c> Name field
/// can't be used instead: it embeds optional pre-titles and surname
/// ("Legendary Moon Mage Renucci Wepatocmaite") with no way to isolate the name.
/// </summary>
public sealed record CharacterNameEvent(string Name) : GameEvent;

/// <summary>
/// &lt;settingsInfo .../&gt; — server init block done and ready for commands.
/// GenieCore auto-sends "look" when this fires to populate room/vitals state.
/// </summary>
public sealed record SettingsInfoEvent() : GameEvent;

/// <summary>
/// Parsed result of a <c>flags</c> verb probe (issue #29): flag name — as DR
/// spells it (e.g. "RoomBrief") — mapped to true (ON) / false (OFF). Emitted
/// ONLY for the connect-time silent probe, whose response is suppressed from
/// display; a user-typed <c>flags</c> renders normally and emits no event.
/// GenieCore compares the stream-affecting flags against a verified baseline
/// and warns on any deviation (untested parser input state).
/// </summary>
public sealed record FlagsReportEvent(IReadOnlyDictionary<string, bool> Flags) : GameEvent;

// ── Inventory containers ─────────────────────────────────────────────────────

/// <summary>
/// &lt;container id='stow' title="My Backpack" target='#37666728' location='right'/&gt;
/// DR pushes one ContainerEvent per equipped container at session start
/// (and re-emits when containers change hands).
/// <para>
/// <see cref="TargetId"/> is the <c>#NNNN</c>-form server-side item ID — the
/// same form that appears inside <c>&lt;d cmd="get #NNNN in #MMMM"&gt;</c>
/// link commands. Mapping <c>TargetId → Title</c> lets the UI translate
/// link-click echoes like <c>get a tapered cutlass in #37666728</c> into
/// the human form <c>get a tapered cutlass in My Backpack</c>.
/// </para>
/// </summary>
public sealed record ContainerEvent(string LogicalId, string Title, string TargetId) : GameEvent;

// ── Server-driven dialogs (public #156) ──────────────────────────────────────

/// <summary>The Wrayth dialog-control vocabulary (see
/// docs/internal/SERVER_DIALOGS_DESIGN.md). <see cref="Unknown"/> keeps a
/// forward-compatible bag-only control instead of dropping new tags.</summary>
public enum DialogControlType
{
    Label, CmdButton, CloseButton, CheckBox, Radio, StreamBox, DropDownBox,
    EditBox, UpDownEditBox, ProgressBar, ClearContainer, Skin, Image, Link,
    Unknown,
}

/// <summary>
/// One control from a <c>&lt;dialogData&gt;</c> block. The common attributes
/// surface as strong fields; EVERYTHING the tag carried (including those) is
/// in <see cref="Attributes"/> so renderers and the capture journal lose
/// nothing. Geometry stays string-typed — DR mixes pixel and percent forms
/// (<c>left='20%'</c>, <c>top='160'</c>).
/// </summary>
public sealed record DialogControl(
    DialogControlType Type,
    string Id,
    string? Value,
    string? Text,
    string? Cmd,
    string? Left,
    string? Top,
    string? Width,
    string? Height,
    string? Align,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// One <c>&lt;dialogData id='…'&gt;</c> block — a DELTA against the dialog's
/// current state (controls merge by id; see the minivitals captures), not a
/// redraw. <see cref="Clear"/> maps the <c>clear='t'</c> attribute form —
/// reset the dialog before applying <see cref="Controls"/> (which may be
/// empty). <see cref="RawXml"/> is the verbatim block for the dialog capture
/// journal and offline schema work.
/// </summary>
public sealed record DialogDataEvent(
    string DialogId,
    IReadOnlyList<DialogControl> Controls,
    bool Clear,
    string RawXml) : GameEvent;

/// <summary>DR opens a dialog window — sent in the login block for the
/// stored Wrayth profile's windows, and situationally mid-session.
/// <c>location='detach'</c> means float; Width/Height are pixel strings.</summary>
public sealed record OpenDialogEvent(
    string Id, string Title, string Location, string? Width, string? Height,
    bool Resident, string DialogType, string RawXml) : GameEvent;

/// <summary>DR closes the dialog window with this id.</summary>
public sealed record CloseDialogEvent(string Id) : GameEvent;

/// <summary>DR brings the dialog window with this id to the front.</summary>
public sealed record ExposeDialogEvent(string Id) : GameEvent;

// ── Unknown ──────────────────────────────────────────────────────────────────

/// <summary>Tag the parser doesn't recognise — forwarded raw to the AI for analysis.</summary>
public sealed record UnknownTagEvent(string TagName, string RawXml) : GameEvent;
