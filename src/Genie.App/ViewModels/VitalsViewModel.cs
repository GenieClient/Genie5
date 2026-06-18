using System.Reactive.Linq;
using Avalonia.Threading;
using Genie.Core;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

public class VitalsViewModel : ReactiveObject
{
    [Reactive] public int Health        { get; private set; } = 100;
    [Reactive] public int Mana          { get; private set; } = 100;
    [Reactive] public int Spirit        { get; private set; } = 100;
    [Reactive] public int Stamina       { get; private set; } = 100;
    [Reactive] public int Concentration { get; private set; } = 100;

    [Reactive] public double RoundTimeSeconds { get; private set; }
    [Reactive] public bool   InRoundTime      { get; private set; }

    /// <summary>Commands currently typed ahead of the game (sent, awaiting a
    /// prompt). Drives the filled pips in the command-bar type-ahead counter.</summary>
    [Reactive] public int    TypeAheadInFlight { get; private set; }

    /// <summary>The type-ahead cap (1 free / 2 premium / 3 +LTB, or the
    /// server-calibrated value). Number of pips drawn by the counter.</summary>
    [Reactive] public int    TypeAheadLimit    { get; private set; } = 2;

    /// <summary>Bare prepared-spell name from <c>&lt;spell&gt;X&lt;/spell&gt;</c>.
    /// Empty or "None" means no spell held. This is what scripts see as
    /// <c>$preparedspell</c>; the UI binds to <see cref="PreparedSpellLabel"/>.</summary>
    [Reactive] public string PreparedSpell      { get; private set; } = "";

    /// <summary>Display string for the hands strip — "(N) Name" while a spell
    /// is held (N = seconds since prep started), or empty when nothing is
    /// prepared. Matches Genie 4's <c>LabelSpellC</c> formatting.</summary>
    [Reactive] public string PreparedSpellLabel { get; private set; } = "";

    /// <summary>Whether the prepared-spell timer display is shown — gated by the
    /// Genie 4 <c>ShowSpellTimer</c> setting (<c>#config spelltimer</c>). Read
    /// from config on <see cref="Attach"/>; default on.</summary>
    [Reactive] public bool   IsSpellTimerVisible { get; private set; } = true;

    /// <summary>Whole seconds remaining until the magenta prep bar hits zero —
    /// derived from <c>castTime − promptTime</c>, decremented locally between
    /// server pushes. Zero whenever prep is complete or no spell is held.</summary>
    [Reactive] public int    CastBarSecondsRemaining { get; private set; }

    /// <summary>The remaining-seconds value captured the moment
    /// <see cref="CastTimeEvent"/> arrived. Used as the bar's <c>Maximum</c>
    /// so the fill ratio is stable even though Remaining ticks down. Reseeded
    /// on every new CastTimeEvent (mirrors Genie 4's <c>StartRT</c>).</summary>
    [Reactive] public int    CastBarSecondsTotal     { get; private set; }

    /// <summary>True while the bar is actively counting down — drives the bar's
    /// visibility so it disappears once prep is complete.</summary>
    [Reactive] public bool   IsPrepping              { get; private set; }

    /// <summary>Whole seconds since <see cref="PreparedSpell"/> became
    /// non-empty. Ticks up while a spell is held; resets when cleared. Drives
    /// the "(N)" prefix in <see cref="PreparedSpellLabel"/>.</summary>
    [Reactive] public int    SpellElapsedSeconds     { get; private set; }

    [Reactive] public string LeftHand      { get; private set; } = "Empty";
    [Reactive] public string RightHand     { get; private set; } = "Empty";

    // ── Compass exits ─────────────────────────────────────────────────────
    // Mirrors GameState.Room.CompassExits as a HashSet so the enhanced hands
    // strip's CompassView control can check Contains("n") / Contains("ne") /
    // etc. in O(1). Populated from CompassEvent — the parser already splits
    // the <compass><dir value="X"/></compass> payload into space-separated
    // direction tokens, we just lift them into a set. The classic hands
    // strip doesn't use this; only the enhanced one does.
    [Reactive] public HashSet<string> CompassExits { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Status indicators ─────────────────────────────────────────────────
    // Each flips on/off as the server emits <indicator id="IconX" visible="y|n"/>.
    // Standing is the default — no badge for it, the absence of any posture
    // bool implies upright. Bound by name to the status-badge sub-group in the
    // hands strip; ScriptGlobalsSync also mirrors these into $kneeling /
    // $prone / etc. for script compatibility (Genie 4 parity).
    [Reactive] public bool IsKneeling  { get; private set; }
    [Reactive] public bool IsProne     { get; private set; }
    [Reactive] public bool IsSitting   { get; private set; }
    [Reactive] public bool IsStunned   { get; private set; }
    [Reactive] public bool IsHidden    { get; private set; }
    [Reactive] public bool IsInvisible { get; private set; }
    [Reactive] public bool IsDead      { get; private set; }
    [Reactive] public bool IsWebbed    { get; private set; }
    [Reactive] public bool IsJoined    { get; private set; }
    [Reactive] public bool IsBleeding  { get; private set; }
    [Reactive] public bool IsPoisoned  { get; private set; }
    [Reactive] public bool IsDiseased  { get; private set; }

    // ── Stance ────────────────────────────────────────────────────────────
    // DR sends combat stance via <component id="pc stance">offensive</component>
    // which the parser surfaces as a ComponentEvent. Six mutually-exclusive
    // bools drive the hands-strip badge (one badge per stance with its own
    // color baked in); StanceLabel/StanceTooltip are convenience strings for
    // anything that wants the full name.
    [Reactive] public bool IsStanceOffensive { get; private set; }
    [Reactive] public bool IsStanceAdvance   { get; private set; }
    [Reactive] public bool IsStanceForward   { get; private set; }
    [Reactive] public bool IsStanceNeutral   { get; private set; }
    [Reactive] public bool IsStanceGuarded   { get; private set; }
    [Reactive] public bool IsStanceDefensive { get; private set; }
    [Reactive] public string StanceLabel   { get; private set; } = "";
    [Reactive] public string StanceTooltip { get; private set; } = "";

    /// <summary>Latest <c>ExpiresAt</c> from a <see cref="RoundTimeEvent"/>. The tick
    /// timer recomputes <see cref="RoundTimeSeconds"/> / <see cref="InRoundTime"/> from it.</summary>
    private DateTimeOffset _rtExpiresAt;

    /// <summary>Latest <c>ExpiresAt</c> from a <see cref="CastTimeEvent"/>. The tick
    /// timer recomputes <see cref="CastBarSecondsRemaining"/> + <see cref="IsPrepping"/>
    /// from it. <c>DateTimeOffset.MinValue</c> means "no prep in progress".</summary>
    private DateTimeOffset _castExpiresAt;

    /// <summary>Wall-clock instant when the current <see cref="PreparedSpell"/>
    /// first arrived (non-empty, non-"None"). Used to compute the (N) elapsed
    /// prefix. Null means no spell is held — Genie 4 mirror of
    /// <c>m_oGlobals.SpellTimeStart</c>.</summary>
    private DateTime?      _spellPreparedAtWallClock;

    private readonly DispatcherTimer _ticker;

    public VitalsViewModel()
    {
        // 100 ms tick — fast enough for "2.3s → 2.2s → 2.1s" RT display, and
        // a multi-purpose driver for the cast bar (1-second resolution, but the
        // visual bar interpolates) and the spell-elapsed (N) prefix counter.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ticker.Tick += (_, _) => Tick();
    }

    public void Attach(GenieCore core)
    {
        // ShowSpellTimer (Genie 4): gate the prepared-spell timer display.
        IsSpellTimerVisible = core.Config?.ShowSpellTimer ?? true;

        // ── Type-ahead counter ──────────────────────────────────────────────
        // Mirror the Core type-ahead state into reactive props. The Changed
        // event can fire off the UI thread (send happens on the script/UI
        // thread, the prompt decrement on the parser thread), so marshal.
        void SyncTypeAhead()
        {
            void apply()
            {
                TypeAheadInFlight = core.TypeAheadInFlight;
                TypeAheadLimit    = core.TypeAheadLimit;
            }
            if (Dispatcher.UIThread.CheckAccess()) apply();
            else Dispatcher.UIThread.Post(apply);
        }
        core.TypeAheadChanged += SyncTypeAhead;
        SyncTypeAhead(); // seed initial values

        core.GameEvents.OfType<ProgressBarEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                switch (e.BarId.ToLowerInvariant())
                {
                    case "health":        Health        = e.Value; break;
                    case "mana":          Mana          = e.Value; break;
                    case "spirit":        Spirit        = e.Value; break;
                    case "stamina":       Stamina       = e.Value; break;
                    case "concentration": Concentration = e.Value; break;
                }
            });

        core.GameEvents.OfType<RoundTimeEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                // RoundTimeOffset: extend the displayed RT by the configured
                // margin so the badge matches the script-gating end (which the
                // GameStateEngine also offsets). Read live; default 0 = no-op.
                var rtOffset = core.Config?.RoundTimeOffset ?? 0;
                _rtExpiresAt = rtOffset != 0 ? e.ExpiresAt.AddSeconds(rtOffset) : e.ExpiresAt;
                RecomputeRt();
                StartTickerIfNeeded();
            });

        // ── Spell preparation bar ─────────────────────────────────────────
        // Genie 4 fires the bar's reset from inside the next <prompt> after
        // a <castTime>. For simplicity we treat the CastTime arrival itself
        // as the bar-seeding moment — same net effect on the user since the
        // server emits both within ~1 ms of each other, and our parser's
        // PromptEvent doesn't carry the matched-pair semantics.
        core.GameEvents.OfType<CastTimeEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                _castExpiresAt = e.ExpiresAt;
                // Seed both Total and Remaining with the same value so the
                // bar starts at 100% full and ticks down from there. This
                // mirrors ComponentRoundtime.SetRT(secondsRemaining) which
                // assigns both RT and StartRT in one shot.
                RecomputeCastBar(seedTotal: true);
                StartTickerIfNeeded();
            });

        core.GameEvents.OfType<SpellEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var name = e.SpellName ?? "";
                PreparedSpell = name;

                // Genie 4 parity: anything other than literal "None" counts as
                // an active spell. Empty string is also treated as cleared in
                // case the parser ever emits one (the canonical clear payload
                // from the server is "None").
                var isCleared = string.IsNullOrEmpty(name) ||
                                name.Equals("None", StringComparison.OrdinalIgnoreCase);

                if (isCleared)
                {
                    _spellPreparedAtWallClock = null;
                    _castExpiresAt            = DateTimeOffset.MinValue;
                    SpellElapsedSeconds       = 0;
                    CastBarSecondsRemaining   = 0;
                    CastBarSecondsTotal       = 0;
                    IsPrepping                = false;
                    PreparedSpellLabel        = "";
                }
                else
                {
                    _spellPreparedAtWallClock = DateTime.UtcNow;
                    SpellElapsedSeconds       = 0;
                    UpdateSpellLabel();
                    StartTickerIfNeeded();
                }
            });

        core.GameEvents.OfType<HeldItemEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                if (e.Hand == Hand.Left)  LeftHand  = string.IsNullOrEmpty(e.Noun) ? "Empty" : e.Noun;
                if (e.Hand == Hand.Right) RightHand = string.IsNullOrEmpty(e.Noun) ? "Empty" : e.Noun;
            });

        // ── Compass exits ─────────────────────────────────────────────────
        // The parser emits CompassEvent.Exits as a single space-separated
        // string ("n s e nw" etc.). Split + drop into a fresh HashSet so the
        // CompassView control can do O(1) Contains checks. Allocating a new
        // set rather than mutating in place ensures the [Reactive] field
        // fires PropertyChanged — Avalonia + the control's subscription
        // depend on reference inequality to detect the change.
        core.GameEvents.OfType<CompassEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                // Field is named RawXml on the record but the parser already
                // collapses the <compass><dir value="…"/></compass> payload
                // into a single space-separated direction list ("n s e nw").
                // Split on whitespace and lift into a set for CompassView.
                var tokens = string.IsNullOrWhiteSpace(e.RawXml)
                    ? Array.Empty<string>()
                    : e.RawXml.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                CompassExits = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
            });

        // ── Status indicators ────────────────────────────────────────────
        // The server emits IndicatorEvent each time a status flips. Map the
        // canonical Genie 4 indicator ids to our reactive bools. ToUpperInvariant
        // because GameStateEngine.cs upper-cases before its switch — keep them
        // aligned so both layers agree on case handling.
        core.GameEvents.OfType<IndicatorEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                switch (e.IndicatorId.ToUpperInvariant())
                {
                    case "ICONKNEELING":  IsKneeling  = e.Visible; break;
                    case "ICONPRONE":     IsProne     = e.Visible; break;
                    case "ICONSITTING":   IsSitting   = e.Visible; break;
                    case "ICONSTUNNED":   IsStunned   = e.Visible; break;
                    case "ICONHIDDEN":    IsHidden    = e.Visible; break;
                    case "ICONINVISIBLE": IsInvisible = e.Visible; break;
                    case "ICONDEAD":      IsDead      = e.Visible; break;
                    case "ICONWEBBED":    IsWebbed    = e.Visible; break;
                    case "ICONJOINED":    IsJoined    = e.Visible; break;
                    case "ICONBLEEDING":  IsBleeding  = e.Visible; break;
                    case "ICONPOISONED":  IsPoisoned  = e.Visible; break;
                    case "ICONDISEASED":  IsDiseased  = e.Visible; break;
                    // "ICONSTANDING" is the implicit default — no bool to set;
                    // when it flips to visible all the other posture bools
                    // should already have flipped to false via their own events.
                }
            });

        // ── Stance ────────────────────────────────────────────────────────
        // DR pushes the textual stance via <component id="pc stance">...</component>;
        // ComponentEvent surfaces it with ComponentId="pc stance". GameStateEngine
        // already maps the text to the Stance enum; we mirror it to mutually-
        // exclusive bools here so the badge sub-group can flip without a
        // value converter. The pbarStance progressBar also exists (0-100
        // numeric) but the textual label is what players read.
        core.GameEvents.OfType<ComponentEvent>()
            .Where(e => string.Equals(e.ComponentId, "pc stance", StringComparison.OrdinalIgnoreCase))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var s = (e.Content ?? "").Trim().ToLowerInvariant();

                // Clear all six first so a stance change can never leave two
                // badges lit at once (e.g. transitioning Offensive → Neutral
                // would otherwise need explicit clears).
                IsStanceOffensive = IsStanceAdvance = IsStanceForward =
                IsStanceNeutral   = IsStanceGuarded = IsStanceDefensive = false;

                switch (s)
                {
                    case "offensive": IsStanceOffensive = true; StanceLabel = "OFF"; StanceTooltip = "Offensive"; break;
                    case "advance":   IsStanceAdvance   = true; StanceLabel = "ADV"; StanceTooltip = "Advance";   break;
                    case "forward":   IsStanceForward   = true; StanceLabel = "FWD"; StanceTooltip = "Forward";   break;
                    case "neutral":   IsStanceNeutral   = true; StanceLabel = "NEU"; StanceTooltip = "Neutral";   break;
                    case "guarded":   IsStanceGuarded   = true; StanceLabel = "GRD"; StanceTooltip = "Guarded";   break;
                    case "defensive": IsStanceDefensive = true; StanceLabel = "DEF"; StanceTooltip = "Defensive"; break;
                    default:          StanceLabel = ""; StanceTooltip = "";                                       break;
                }
            });
    }

    // ── Tick ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick of the shared 100 ms timer: drives the roundtime countdown,
    /// the spell-prep bar countdown, and the elapsed-since-prepped counter.
    /// Stops the timer when none of the three needs further updates so we
    /// don't burn CPU between actions.
    /// </summary>
    private void Tick()
    {
        RecomputeRt();
        RecomputeCastBar();
        RecomputeElapsed();
        StopTickerIfIdle();
    }

    private void RecomputeRt()
    {
        var remaining = (_rtExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (remaining <= 0)
        {
            RoundTimeSeconds = 0;
            InRoundTime      = false;
            return;
        }
        RoundTimeSeconds = remaining;
        InRoundTime      = true;
    }

    /// <summary>
    /// Recompute the prep-bar countdown from the stored <see cref="_castExpiresAt"/>.
    /// When called with <paramref name="seedTotal"/>=true (only from the
    /// <see cref="CastTimeEvent"/> handler), also captures the current
    /// remaining-seconds value as the bar's <c>Total</c> so the fill ratio is
    /// stable for the whole countdown.
    /// </summary>
    private void RecomputeCastBar(bool seedTotal = false)
    {
        if (_castExpiresAt == DateTimeOffset.MinValue)
        {
            CastBarSecondsRemaining = 0;
            IsPrepping              = false;
            return;
        }

        var rem = (int)Math.Max(0, (_castExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        CastBarSecondsRemaining = rem;
        IsPrepping              = rem > 0;

        if (seedTotal)
        {
            // Use Max(1, rem) so the bar always has a non-zero Maximum to
            // avoid divide-by-zero in any percentage-style rendering. A
            // CastTime that's already in the past (replay mode) leaves
            // Total=1 and Remaining=0, so the bar paints empty.
            CastBarSecondsTotal = Math.Max(1, rem);
        }

        if (!IsPrepping)
            _castExpiresAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Recompute the elapsed-since-prepped counter and the resulting
    /// <see cref="PreparedSpellLabel"/> ("(N) Name" while held).
    /// </summary>
    private void RecomputeElapsed()
    {
        if (_spellPreparedAtWallClock is null)
        {
            SpellElapsedSeconds = 0;
            PreparedSpellLabel  = "";
            return;
        }

        SpellElapsedSeconds =
            (int)(DateTime.UtcNow - _spellPreparedAtWallClock.Value).TotalSeconds;
        UpdateSpellLabel();
    }

    /// <summary>
    /// Refresh the display label from current <see cref="PreparedSpell"/> +
    /// <see cref="SpellElapsedSeconds"/>. Called both from the SpellEvent
    /// handler (so the label updates immediately on prep without waiting for
    /// the next tick) and from <see cref="RecomputeElapsed"/> each tick.
    /// </summary>
    private void UpdateSpellLabel()
    {
        if (string.IsNullOrEmpty(PreparedSpell) ||
            PreparedSpell.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            PreparedSpellLabel = "";
            return;
        }
        PreparedSpellLabel = $"({SpellElapsedSeconds}) {PreparedSpell}";
    }

    // ── Timer lifecycle ───────────────────────────────────────────────────

    /// <summary>Start the shared ticker if any of the three countdowns needs it.</summary>
    private void StartTickerIfNeeded()
    {
        if (_ticker.IsEnabled) return;
        var needsTick = InRoundTime || IsPrepping || _spellPreparedAtWallClock is not null;
        if (needsTick) _ticker.Start();
    }

    /// <summary>Stop the ticker when none of RT / cast-bar / elapsed needs further updates.</summary>
    private void StopTickerIfIdle()
    {
        if (!_ticker.IsEnabled) return;
        if (!InRoundTime && !IsPrepping && _spellPreparedAtWallClock is null)
            _ticker.Stop();
    }
}
