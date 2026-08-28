using System.Globalization;
using Genie.Core.Events;
using Genie.Core.Models;

namespace Genie.Core.Scripting;

/// <summary>
/// Mirrors live game state into <see cref="ScriptEngine.Globals"/> so
/// community scripts can read <c>$righthand</c>, <c>$stamina</c>, <c>$hidden</c>,
/// <c>$gameroomid</c>, the per-exit booleans (<c>$north</c>, <c>$up</c> etc.)
/// and the rest of Genie 4's reserved-variable vocabulary.
///
/// Implementation notes
/// --------------------
/// <para>
/// Event-typed dispatch. v1 (since reverted) refreshed all ~50 variables
/// on every event regardless of type — wasteful at high event rates. v2
/// subscribes once and routes per event type so each callback only touches
/// the 1-12 variables relevant to that event (hand state, one vital, one
/// status flag, etc.). Net work per game line is single-digit microseconds.
/// </para>
/// <para>
/// Thread-safe writes. <see cref="ScriptEngine.Globals"/> is now a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>,
/// so the parser-thread refresh and the script-thread reads (via
/// <c>SubstituteVars</c>) can interleave without locking the caller.
/// </para>
/// <para>
/// Genie 4 parity. Variable names + value formats verified against the
/// canonical Genie 4 source in <c>Core/Game.cs</c>. Status flags use
/// <c>"1"</c>/<c>"0"</c>; per-exit booleans use the same convention. Hand
/// state surfaces the noun (which is the closest thing our parser
/// currently captures from the <c>&lt;left&gt;</c>/<c>&lt;right&gt;</c>
/// element's attributes — full display names would require parser changes
/// to keep the body text, a separate enhancement).
/// </para>
/// </summary>
public sealed class ScriptGlobalsSync : IDisposable
{
    private readonly Models.GameState            _state;
    private readonly IDictionary<string, string> _globals;
    private readonly IDisposable                 _subscription;
    private readonly string                      _gameCode;
    private readonly string                      _characterName;
    private readonly string                      _accountName;
    private readonly string                      _clientName;
    private readonly string                      _clientVersion;

    /// <param name="gameCode">e.g. "DR" for DragonRealms. Surfaced as <c>$game</c> and <c>$gamename</c>.</param>
    /// <param name="characterName">Seeds <c>$charactername</c> until the server's component event arrives.</param>
    /// <param name="accountName">SGE account, surfaced as <c>$account</c>.</param>
    /// <param name="clientName">Product name, surfaced as <c>$client</c> (Genie 4 used "Genie Client 4").</param>
    /// <param name="clientVersion">App version string, surfaced as <c>$version</c>.</param>
    public ScriptGlobalsSync(
        Models.GameState            state,
        IDictionary<string, string> globals,
        IObservable<GameEvent>      events,
        string                      gameCode      = "DR",
        string                      characterName = "",
        string                      accountName   = "",
        string                      clientName    = "Genie Client 5",
        string                      clientVersion = "")
    {
        _state         = state;
        _globals       = globals;
        _gameCode      = gameCode;
        _characterName = characterName;
        _accountName   = accountName;
        _clientName    = clientName;
        _clientVersion = clientVersion;

        SeedInitial();
        _subscription  = events.Subscribe(OnEvent);
    }

    public void Dispose() => _subscription.Dispose();

    // ── Initial seed ──────────────────────────────────────────────────────
    /// <summary>
    /// Populate the dictionary with sensible defaults at construction time so
    /// a script launched the instant after connect (before any events have
    /// fired) sees usable values rather than empty strings.
    /// </summary>
    private void SeedInitial()
    {
        // Character identity (immutable for this session)
        Set("charactername", string.IsNullOrEmpty(_state.CharacterName) ? _characterName : _state.CharacterName);
        Set("gamename",      _gameCode);
        Set("game",          _gameCode);    // common alias used by some scripts
        Set("connected",     "1");

        // Session statics — known at construction, never change for this session.
        Set("account",       _accountName);
        Set("client",        _clientName);
        Set("version",       _clientVersion);

        // Vitals — match GameState defaults (100%).
        Set("health",        _state.Vitals.Health.ToString(CultureInfo.InvariantCulture));
        Set("mana",          _state.Vitals.Mana.ToString(CultureInfo.InvariantCulture));
        Set("spirit",        _state.Vitals.Spirit.ToString(CultureInfo.InvariantCulture));
        Set("stamina",       _state.Vitals.StaminaFatigue.ToString(CultureInfo.InvariantCulture));
        Set("concentration", _state.Vitals.Concentration.ToString(CultureInfo.InvariantCulture));
        Set("encumbrance",   _state.Vitals.Encumbrance.ToString(CultureInfo.InvariantCulture));
        Set("fatigue",       _state.Vitals.StaminaFatigue.ToString(CultureInfo.InvariantCulture));   // alias

        // Combat / casting. $casttime is the RAW epoch from <castTime value=…>
        // (Genie 4 Game.cs:2122 — scripts compose it: $casttime − $spellstarttime),
        // seeded "0" like G4 (Globals.cs:899). $casttimeremaining is NOT stored
        // here — it's a live-computed reserved var in ScriptEngine.TryResolveVar
        // (G4 recomputes @casttimeremaining@ at every substitution; a stored
        // snapshot froze at the full prep length, public #224 follow-up).
        Set("roundtime",          "0");
        Set("roundtimeremaining", "0");
        Set("casttime",           "0");
        Set("spellpreptime",      "0");
        Set("preparedspell", _state.Combat.PreparedSpell);
        Set("stance",        _state.Combat.Stance.ToString().ToLowerInvariant());

        // Hands — "Empty" matches Genie 4 convention.
        SetHand(Hand.Left,  _state.Inventory.LeftHand,  _state.Inventory.LeftHandNoun,  _state.Inventory.LeftExistId);
        SetHand(Hand.Right, _state.Inventory.RightHand, _state.Inventory.RightHandNoun, _state.Inventory.RightExistId);

        // Status flags — all 0 initially; indicator events flip them.
        foreach (var flag in StatusFlagNames) Set(flag, "0");
        // Standing is the implicit default — Genie 4 also sends an IconSTANDING
        // indicator on first prompt, but until then we want $standing=1 so
        // scripts that read it before the indicator arrives don't think the
        // player is sitting.
        Set("standing", "1");

        // Per-exit booleans — all 0 until compass event arrives.
        foreach (var dir in CompassDirectionNames) Set(dir, "0");

        // Room — empty until component events arrive.
        Set("roomname",    _state.Room.Title);
        Set("roomdesc",    _state.Room.Description);
        Set("roomexits",   _state.Room.Exits);
        Set("roomobjs",    _state.Room.Objects);
        Set("roomplayers", _state.Room.Players);
        Set("gameroomid",  _state.Room.RoomId > 0 ? _state.Room.RoomId.ToString(CultureInfo.InvariantCulture) : string.Empty);

        // Misc
        Set("prompt",        "");
        Set("gametime",      "0");
        Set("monstercount",  "0");
        Set("monsterlist",   "");
        // Resolved game endpoint — empty/0 until the GameConnection reports
        // Connected, at which point GenieCore overwrites these from the live
        // connection ($gamehost / $gameport, Genie 4 parity).
        Set("gamehost",      "");
        Set("gameport",      "0");
    }

    // ── Event dispatch ────────────────────────────────────────────────────
    private void OnEvent(GameEvent evt)
    {
        switch (evt)
        {
            case ProgressBarEvent bar: OnProgressBar(bar); break;
            case IndicatorEvent ind:   OnIndicator(ind);   break;
            case HeldItemEvent held:   OnHeldItem(held);   break;
            case ComponentEvent comp:  OnComponent(comp);  break;
            case CompassEvent comp:    OnCompass(comp);    break;
            case NavEvent nav:         Set("gameroomid", nav.RoomId ?? string.Empty); break;
            // Trigger-only: the corrected end lives in GameState — the engine
            // subscribes first and has already converted the server instant
            // through the prompt-learned clock offset (#261). Reading the raw
            // event here would republish the PC-clock skew.
            case RoundTimeEvent:       { var s = SecondsRemaining(_state.Combat.RoundTimeEnd); Set("roundtime", s); Set("roundtimeremaining", s); break; }
            // $casttime = the tag's raw epoch (G4 Game.cs:2122), not a countdown —
            // the live countdown is $casttimeremaining (computed in ScriptEngine).
            case CastTimeEvent ct:     Set("casttime", ct.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)); break;
            case SpellEvent sp:        Set("preparedspell", sp.SpellName ?? string.Empty); break;
            case PromptEvent p:        OnPrompt(p);                                           break;
            case AppEvent app:         OnApp(app);                                            break;
        }
    }

    /// <summary>
    /// Session identity from the server's <c>&lt;app char=… game=…/&gt;</c> tag.
    /// This corrects the construction-time seed — critical for Lich sessions,
    /// where <c>cfg.GameCode</c> is only the connect dialog's guess but scripts
    /// branch on <c>$game</c> (Platinum portals, Fallen shortcuts). Genie 4
    /// (Core/Game.cs:1922) refreshes only <c>$gamename</c> here because its
    /// <c>$game</c> comes from the launcher file, which Lich supplies; we have
    /// no launcher file, so the server's word corrects <c>$game</c> too.
    /// </summary>
    private void OnApp(AppEvent app)
    {
        if (!string.IsNullOrWhiteSpace(app.Character))
            Set("charactername", app.Character.Trim());
        if (!string.IsNullOrWhiteSpace(app.Game))
        {
            // Genie 4 normalization: strip ':' and spaces from the attr value.
            var code = app.Game.Replace(":", "").Replace(" ", "");
            Set("gamename", code);
            Set("game",     code);
        }
    }

    private void OnPrompt(PromptEvent p)
    {
        Set("prompt", p.Indicator);

        // $roundtime / $roundtimeremaining — re-mirror from live state on every
        // prompt, which is the only thing that ever walks them back DOWN to 0.
        // Genie 4 does exactly this (Game.cs:2285-2304: `rt = roundTime −
        // gametime`, store rt when > 0 else store "0"); we previously wrote
        // these only on RoundTimeEvent, which fires when RT *starts*, so the
        // mirror froze at the last RT length forever.
        //
        // Scripts and typed commands don't depend on this refresh — both read
        // through ScriptEngine.TryResolveVar, which computes the value live and
        // outranks the mirror. This keeps the globals dictionary itself honest
        // for the variables panel and GetGlobalVariables() readers.
        // CombatState.RoundTimeRemaining is already clamped at 0, so a lapsed
        // RT lands on "0" without G4's explicit else-branch.
        var rtText = ((int)Math.Ceiling(_state.Combat.RoundTimeRemaining))
                     .ToString(CultureInfo.InvariantCulture);
        Set("roundtime",          rtText);
        Set("roundtimeremaining", rtText);

        // Genie 4 reserved $gametime — the server's clock (Unix seconds) carried
        // on each <prompt time='...'> tag. The parser leaves ServerTime at its
        // default when a prompt arrives without a time attribute; guard against
        // that so we never publish a bogus negative epoch.
        if (p.ServerTime > DateTimeOffset.UnixEpoch)
            Set("gametime", p.ServerTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        // $spellpreptime — the spell's FULL prep length in seconds. Genie 4
        // (Globals.cs ParseSpecialVariables): @spellpreptime@ = casttime −
        // spellstarttime, i.e. the epoch when the cast will be fully prepared
        // minus the epoch prep began — a constant per spell, NOT the elapsed
        // count-up (that's $spelltime). 0 when nothing is being prepared or
        // the server sent no castTime.
        //
        // Integer-epoch arithmetic, deliberately: CastTimeEnd is a whole-second
        // server epoch but SpellTimeStart is a local UtcNow stamp with fractional
        // ms, so a TimeSpan difference is almost always prepLen−0.x and a plain
        // (int) cast truncated it down — 19 for a 20s prep (public #224
        // follow-up). G4 floors the START epoch then subtracts whole integers
        // (FormMain.cs:6392 ToUnixTimeSeconds → Globals.cs:213); matching that
        // truncation direction restores the exact figure.
        var prepLen = _state.Combat.SpellTimeStart is { } prepStart
                      && _state.Combat.CastTimeEnd > prepStart
            ? Math.Max(0L, _state.Combat.CastTimeEnd.ToUnixTimeSeconds() - prepStart.ToUnixTimeSeconds())
            : 0L;
        Set("spellpreptime", prepLen.ToString(CultureInfo.InvariantCulture));
    }

    private void OnProgressBar(ProgressBarEvent bar)
    {
        // BarId is one of "health" | "mana" | "spirit" | "stamina" |
        // "encumbrance" | "concentration" per the parser docs. Update both
        // the numeric value and the bar's display text.
        var id = bar.BarId?.ToLowerInvariant() ?? "";
        if (id.Length == 0) return;
        Set(id, bar.Value.ToString(CultureInfo.InvariantCulture));
        Set($"{id}BarText", bar.Text ?? "");

        // "stamina" is also surfaced as "fatigue" — common alias in scripts.
        if (id == "stamina") Set("fatigue", bar.Value.ToString(CultureInfo.InvariantCulture));
    }

    private void OnIndicator(IndicatorEvent ind)
    {
        // Map server icon ids to Genie 4 variable names.
        var key = ind.IndicatorId switch
        {
            "IconKNEELING"  => "kneeling",
            "IconPRONE"     => "prone",
            "IconSITTING"   => "sitting",
            "IconSTANDING"  => "standing",
            "IconSTUNNED"   => "stunned",
            "IconHIDDEN"    => "hidden",
            "IconINVISIBLE" => "invisible",
            "IconDEAD"      => "dead",
            "IconWEBBED"    => "webbed",
            "IconJOINED"    => "joined",
            "IconBLEEDING"  => "bleeding",
            "IconPOISONED"  => "poisoned",
            "IconDISEASED"  => "diseased",
            _               => null,
        };
        if (key is null) return;
        Set(key, ind.Visible ? "1" : "0");
    }

    private void OnHeldItem(HeldItemEvent held)
        => SetHand(held.Hand, held.Display, held.Noun, held.ExistId);

    /// <summary>
    /// Re-mirror <c>$monstercount</c>/<c>$monsterlist</c> from the (just
    /// recomputed) Room state. Called by GenieCore when the monster-count
    /// ignore list changes between room updates, so scripts see the same
    /// filtered list as the Mobs panel without waiting for the next
    /// <c>room objs</c> event.
    /// </summary>
    public void RefreshMonsterVars()
    {
        Set("monstercount", _state.Room.MonsterCount.ToString(CultureInfo.InvariantCulture));
        Set("monsterlist",  string.Join(", ", _state.Room.Creatures));
    }

    private void OnComponent(ComponentEvent comp)
    {
        switch (comp.ComponentId?.ToLowerInvariant())
        {
            case "room title":   Set("roomname",    comp.Content ?? ""); break;
            case "room desc":    Set("roomdesc",    comp.Content ?? ""); break;
            case "room exits":   Set("roomexits",   comp.Content ?? ""); break;
            case "room objs":
                Set("roomobjs", comp.Content ?? "");
                // Monster count (Genie 4): GameStateEngine processed this same
                // event first (it subscribes earlier), so Room.Creatures is
                // already filtered through the ignore list here.
                Set("monstercount", _state.Room.MonsterCount.ToString(CultureInfo.InvariantCulture));
                Set("monsterlist",  string.Join(", ", _state.Room.Creatures));
                break;
            case "room players": Set("roomplayers", comp.Content ?? ""); break;
            // SeedInitial wrote $stance once at construction; without this
            // case it never updated on stance changes, so scripts that read
            // $stance after a `stance off` would see the stale initial value.
            // Genie 4 stores the lowercase text — keep that convention.
            case "pc stance":    Set("stance",      (comp.Content ?? "").Trim().ToLowerInvariant()); break;
        }
    }

    private void OnCompass(CompassEvent comp)
    {
        // CompassEvent.RawXml is the space-separated direction tokens from
        // <compass><dir value="nw"/>...</compass>. Surface as $roomexits (a
        // compass-only synonym) AND set each per-exit boolean ($north etc.)
        // so scripts can do `if ($north) then put north` cleanly.
        var raw = comp.RawXml ?? "";

        // First clear all direction flags so previously-set ones from the
        // last room go back to 0. (Without this, walking from a room with
        // $north=1 into a room without a north exit would leave the flag
        // stuck.)
        foreach (var dir in CompassDirectionNames) Set(dir, "0");

        // Now set the ones present. Tokens are short-form ("nw", "ne", etc.)
        // per the parser; map them to the full-form Genie 4 names.
        foreach (var token in raw.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = ShortToFullDirection(token);
            if (full is not null) Set(full, "1");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private void SetHand(Hand hand, string display, string noun, string existId)
    {
        // Genie 4 parity (#172): $Xhand is the FULL display name from the
        // <left>/<right> body text ("whiskey jug", or "Empty"); $Xhandnoun is
        // the bare noun attribute ("jug"). Fall back to the noun for streams
        // that only carried attributes, then to "Empty".
        var name = !string.IsNullOrEmpty(display) ? display
                 : !string.IsNullOrEmpty(noun)    ? noun
                 : "Empty";
        var prefix = hand == Hand.Left ? "left" : "right";
        Set($"{prefix}hand",     name);
        Set($"{prefix}handnoun", string.IsNullOrEmpty(noun) ? "" : noun);
        Set($"{prefix}handid",   existId ?? "");
    }

    /// <summary>
    /// Whole seconds left until <paramref name="expires"/>, floored at 0.
    /// Rounds UP, matching <c>ScriptEngine.RoundTimeRemainingSeconds</c>
    /// (GenieCore wires it as <c>Math.Ceiling</c>) and the RT wakeup
    /// scheduler — a plain <c>(int)</c> cast truncated a 7s roundtime read a
    /// millisecond later to "6", so the same RT reported 6 through this
    /// mirror and 7 through the live resolver, and a script that paused on
    /// the floored value woke a beat early and hit roundtime anyway.
    /// </summary>
    private static string SecondsRemaining(DateTimeOffset expires)
    {
        var sec = (int)Math.Ceiling(Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalSeconds));
        return sec.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ShortToFullDirection(string token) => token.ToLowerInvariant() switch
    {
        "n"  => "north",     "north"     => "north",
        "ne" => "northeast", "northeast" => "northeast",
        "e"  => "east",      "east"      => "east",
        "se" => "southeast", "southeast" => "southeast",
        "s"  => "south",     "south"     => "south",
        "sw" => "southwest", "southwest" => "southwest",
        "w"  => "west",      "west"      => "west",
        "nw" => "northwest", "northwest" => "northwest",
        "u"  => "up",        "up"        => "up",
        "d"  => "down",      "down"      => "down",
        "out"=> "out",
        _ => null,
    };

    private void Set(string key, string value) => _globals[key] = value ?? string.Empty;

    private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\r', '\n' };

    private static readonly string[] StatusFlagNames =
    [
        "kneeling", "prone", "sitting", "standing", "stunned",
        "hidden",   "invisible", "dead", "webbed", "joined",
        "bleeding", "poisoned", "diseased",
    ];

    private static readonly string[] CompassDirectionNames =
    [
        "north", "northeast", "east", "southeast",
        "south", "southwest", "west", "northwest",
        "up", "down", "out",
    ];
}
