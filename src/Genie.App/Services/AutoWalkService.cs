using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Mapper;
using Genie.Core.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.Services;

/// <summary>
/// Drives step-by-step execution of an <see cref="AutoMapperEngine.FindPath"/>
/// result. The user clicks a room in the Mapper, the service produces an
/// <see cref="AutoWalkSession"/>, dispatches the first move via the
/// command pipeline, then advances the plan each time the engine reports
/// the player has entered the expected next room.
///
/// <para>
/// Cancellation surfaces (all of these terminate the session):
/// <list type="bullet">
///   <item>User presses Esc — wired in <c>MainWindow.KeyDown</c></item>
///   <item>User types any non-meta command — wired in <c>MainWindowViewModel.HandleCommandLine</c></item>
///   <item>App window has been unfocused past the configured window — only
///         when the user has opted in via
///         <c>GenieConfig.AutoWalkPauseOnUnfocus</c> (default OFF); the
///         service starts a timer on deactivate and fires <see cref="Pause"/>
///         when it expires</item>
///   <item>Connection drops — service subscribes to <c>core.StateStream</c>
///         and cancels on Disconnected</item>
///   <item>Player walks into an unexpected room (off-plan) — heuristic
///         cancel with diagnostic message</item>
/// </list>
/// </para>
///
/// <para>
/// Compliance posture: a click (or #goto) is direct user intent and the
/// walker is *responsive* to it (RT-gated stepping, not a command burst).
/// The unfocus auto-pause below is an OPTIONAL idle backstop, OFF by default
/// — DR policy asks for responsiveness, not window focus.
/// — The service refuses to send a step while paused.
/// — Never auto-resumes across disconnects: a fresh session must be
///   started from a fresh user click.
/// — The visible indicator + Esc cancel + typed-command-cancel are the
///   always-available user controls.
/// </para>
/// </summary>
public sealed class AutoWalkService : ReactiveObject
{
    private readonly GenieCore _core;
    private readonly AutoMapperEngine _mapEngine;

    /// <summary>
    /// Fallback unfocus-pause window (seconds) used only if config somehow
    /// reports a sub-minimum value. The live value comes from
    /// <see cref="GenieConfig.AutoWalkUnfocusSeconds"/>, and the whole
    /// behavior is gated OFF by default behind
    /// <see cref="GenieConfig.AutoWalkPauseOnUnfocus"/>.
    /// </summary>
    private const int DefaultUnfocusPauseSeconds = 60;

    /// <summary>
    /// The current session, or null when no walk is in progress. Bindable
    /// via Reactive — the Mapper status strip's IsVisible is gated on this
    /// being non-null.
    /// </summary>
    [Reactive] public AutoWalkSession? Current { get; private set; }

    /// <summary>Live status line for the active walk — a reactive mirror of
    /// <see cref="AutoWalkSession.StatusMessage"/>. The session's own
    /// StatusMessage is a plain (non-observable) field and <see cref="Current"/>
    /// is reassigned only at walk start/end, so binding the banner directly to
    /// <c>Current.StatusMessage</c> froze it at the initial "Walking to…" text and
    /// never surfaced the per-step gate reason (e.g. "waiting (sitting)"). The
    /// banner binds here instead; updated on every <see cref="SessionChanges"/> push.</summary>
    [Reactive] public string? CurrentStatus { get; private set; }

    /// <summary>
    /// True when at least one session has completed (Finished or Cancelled)
    /// in this app lifetime — used by the indicator to flash the final
    /// status briefly before clearing. Not used yet; reserved for v2.
    /// </summary>
    [Reactive] public string? LastStatusFlash { get; private set; }

    /// <summary>
    /// Convenience bool for XAML: `true` exactly when the current session
    /// is in <see cref="AutoWalkState.Paused"/>. Used to gate the Resume
    /// button's visibility on the indicator strip. Avoids needing an
    /// enum-equality value converter in AXAML.
    /// </summary>
    [Reactive] public bool IsCurrentPaused { get; private set; }

    // ── Cross-zone wait state ─────────────────────────────────────────
    // Surfaces as a progress-bar in the Mapper's auto-walk indicator
    // when the walker is mid-transit on a cross-zone hop with a known
    // wait window (boats, ferries, scheduled departures). The actual
    // arrival is driven by the destination-zone fingerprint match;
    // this countdown is just a UI hint so the user knows roughly how
    // long the wait should be — they can Esc to cancel any time.

    /// <summary>True while a cross-zone wait timer is active. Gates the
    /// progress bar's IsVisible in the Mapper indicator.</summary>
    [Reactive] public bool IsWaitingForCrossZone { get; private set; }

    /// <summary>Total wait window in seconds (average of WaitMin / Max).
    /// Used as ProgressBar.Maximum.</summary>
    [Reactive] public int CurrentWaitTotalSeconds { get; private set; }

    /// <summary>Seconds left in the expected wait window. Counts down
    /// from <see cref="CurrentWaitTotalSeconds"/> to 0; if 0 elapses
    /// before the destination zone arrives, the bar pegs at 0 but the
    /// walker keeps waiting for the room change.</summary>
    [Reactive] public int CurrentWaitSecondsLeft { get; private set; }

    /// <summary>Human-readable "4:23 left" — refreshed on every timer
    /// tick alongside <see cref="CurrentWaitSecondsLeft"/>. Made a
    /// concrete <c>[Reactive]</c> string rather than a computed getter
    /// so XAML bindings refresh without manual RaisePropertyChanged.</summary>
    [Reactive] public string CurrentWaitDisplay { get; private set; } = "";

    /// <summary>Short label describing what we're waiting for — e.g.
    /// "boarding boat". Reactive so the indicator text updates when
    /// the wait kicks off.</summary>
    [Reactive] public string CurrentWaitLabel { get; private set; } = "";

    /// <summary>The full "boarding boat · ~4:23 left" line next to the wait
    /// bar, composed here rather than from three inline &lt;Run&gt;s in AXAML:
    /// a TextBlock does not reliably re-render bound Run.Text changes, which
    /// left the bar showing just the hardcoded "·" separator. One reactive
    /// string on a plain TextBlock.Text always refreshes, and composing it
    /// here also drops the separator when the step has no description.</summary>
    [Reactive] public string CurrentWaitText { get; private set; } = "";

    private DispatcherTimer? _waitTimer;

    private readonly Subject<AutoWalkSession?> _sessionChanges = new();
    public IObservable<AutoWalkSession?> SessionChanges => _sessionChanges;

    private DispatcherTimer? _unfocusTimer;

    // Per-step watchdog: after a move is dispatched we expect a confirmed room
    // change within this window. If none arrives (DR rejected the verb with
    // "Please rephrase…", a closed gate, an obstacle, or the planned path
    // desynced past a zone boundary), the walk is stuck — emit MOVEMENT FAILED
    // so #goto-driven scripts recover instead of hanging in their matchwait,
    // and cancel. Generous enough to clear normal roundtime + lag; cross-zone
    // wait steps (ferries, minutes-long) are exempt — the wait countdown owns
    // those.
    private DispatcherTimer? _stepTimer;
    private const int StepTimeoutSeconds = 15;

    // Pre-send pacing (Issue #5): the next move is held until roundtime clears and
    // the character can actually move (not stunned/webbed/prone/…). This timer
    // re-checks and fires the deferred step. Distinct from the post-send watchdog.
    private DispatcherTimer? _pacingTimer;

    // Issue #5 auto-stand: a posture block (sitting/kneeling/prone) only clears
    // when the character stands. The walker sends `stand` itself — typing it
    // would trip SendCommand's "typed command cancels the walk" guard, killing
    // the very walk that's waiting to stand.
    //
    // A single `stand` isn't enough: wounds/imbalance can bounce it ("You are so
    // unbalanced you cannot manage to stand. Roundtime: 2 sec.") — balance
    // recovers over a few seconds, so we retry once per posture-block poll up to
    // MaxAutoStandAttempts, each after the failed stand's roundtime clears. Only
    // one `stand` is sent per poll (not per tick) so we don't spam the verb; the
    // counter re-arms whenever the movability gate next clears.
    private int _autoStandAttempts;
    private const int MaxAutoStandAttempts = 4;

    // Issue #130 auto-retreat: DR refuses a movement verb while the character
    // is engaged in melee/pole combat, replying "You are engaged to a X in
    // melee range!" (and variants). Genie 3/4 — and the community travel /
    // climbcross / disarm scripts — retreat and retry the move; without it the
    // walker just stalls until the 15s watchdog fires MOVEMENT FAILED. When a
    // dispatched move is bounced by an engagement message the walker sends
    // `retreat` itself via the core command path (not the typed path, so it
    // doesn't trip SendCommand's "typed command cancels the walk" guard) and
    // re-dispatches the SAME step. Retreat carries roundtime, which
    // DispatchNextStep already paces around. Counted per step and capped so a
    // persistent block can't spin an infinite retreat/move loop — mirrors the
    // community scripts' `if moveRetreat > 4` bailout. Reset on a confirmed
    // room change (the step landed) and at walk start / cancel.
    private int _retreatCount;
    private const int MaxRetreatsPerStep = 4;

    // Search-directive arcs (Genie 4 hidden-exit idiom): a map move of
    // "search go trampled path" means the exit only opens after a successful
    // in-room `search`. Genie 4's .automapper script (MOVE.SEARCH) tries the
    // inner go FIRST — the path may already be open from a recent search — and
    // only when DR bounces it ("I could not find what you were referring to.")
    // searches and retries. We mirror that: _hiddenExitVerb holds the inner
    // move ("go trampled path") while a search-directive step is in flight;
    // when the bounce text arrives the walker sends `search` via the core
    // command path, waits out the search's roundtime (DispatchNextStep's
    // movability gate), and re-sends the SAME step. Searching is a skill /
    // random check that regularly whiffs on the first try ("You find some
    // interesting signs something is here."), so the loop retries — capped so
    // an unfindable path can't search forever. Reset on a confirmed room
    // change and at walk start / cancel, like the retreat counter.
    private string? _hiddenExitVerb;
    private int _searchAttempts;
    private const int MaxSearchAttempts = 4;

    /// <summary>
    /// Server responses meaning "that exit isn't there (yet)" — the bounce a
    /// hidden-exit go/climb gets before a successful search reveals the path.
    /// Mirrors the relevant members of Genie 4 automapper.cmd's %move_FAIL.
    /// </summary>
    private static readonly Regex HiddenExitBlock = new(
        @"^(?:I could not find what you were referring to\.|What were you referring to\?|You can't go there\.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Server responses that mean "you can't move — you're engaged in combat".
    /// DR exposes engagement ONLY through this move-blocked text — there is no
    /// engagement &lt;indicator&gt; in the XML stream (verified against Lich's
    /// ICONMAP and both non-combat + community sources). Lich's own walk_to
    /// (dragonrealms/common-travel.rb) and the community scripts (climbcross.cmd,
    /// disarm.cmd) all key off these same lines to drive retreat-then-move, so a
    /// text match here is the parity-correct mechanism, not a fallback.
    /// </summary>
    private static readonly Regex EngagementBlock = new(
        @"^(?:You are engaged to .+!|You try to move, but you're engaged\.|While in combat\?\s+You'll have better luck if you first retreat\.|You can't do that while engaged!)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Id of the room we were standing in when the most recent move was
    /// dispatched — i.e. the room we expect this step to take us OUT of. The
    /// step pump only advances when <see cref="AutoMapperEngine.CurrentNode"/>
    /// becomes a different, non-null room than this.
    ///
    /// <para>Why this matters (#69): DR delivers a single room transition as
    /// several parser updates — title, then compass exits, then description —
    /// and the mapper engine fires <c>CurrentNodeChanged</c> on each one (plus
    /// a null fire on a zone-miss). Advancing the plan on every raw event meant
    /// one physical room arrival pumped 2-4 moves into the game's typeahead
    /// buffer, cascading into a flood that overran the parser and stalled the
    /// walk. Gating on a confirmed change of room identity makes each move wait
    /// for the previous one to actually land.</para>
    /// </summary>
    private int? _departureNodeId;

    /// <summary>
    /// The server room uid (<c>&lt;nav rm="…"/&gt;</c>) we were standing in when
    /// the most recent move was dispatched. Backstop pacing signal for
    /// same-description areas (Lava Field, marsh): when the map pins several
    /// identical rooms to one node, <see cref="_departureNodeId"/> never changes
    /// and the step pump would stall the full 15s watchdog on every room. The
    /// server uid changes on every physical room, so a uid change confirms the
    /// move even when the node id can't. Empty in WIZ mode / pre-nav, where we
    /// fall back to node-only pacing.
    /// </summary>
    private string? _departureServerRoomId;

    /// <summary>
    /// Cancel the current walk. Wired to the Cancel button in the indicator
    /// strip; also fires from Esc handler in MainWindow code-behind.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Resume from Paused. Wired to the Resume button (only visible when paused).
    /// </summary>
    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }

    public AutoWalkService(GenieCore core, AutoMapperEngine mapEngine)
    {
        _core      = core;
        _mapEngine = mapEngine;

        CancelCommand = ReactiveCommand.Create(() => Cancel("user clicked Cancel"));
        ResumeCommand = ReactiveCommand.Create(() => Resume());

        // Step pump: each room change advances the walk if the player
        // arrived at the next room on the planned path. Posted to UI
        // thread because the engine event can fire from any thread the
        // parser observable is hopping through.
        _mapEngine.CurrentNodeChanged += () => Dispatcher.UIThread.Post(OnRoomChanged);

        // Watch game text for "you're engaged, can't move" responses (#130) so a
        // #goto/.travel step bounced by combat retreats and retries instead of
        // stalling, and for hidden-exit bounces ("I could not find what you were
        // referring to.") so a search-directive arc searches and retries. Posted
        // to the UI thread like the node-change pump; both handlers only act
        // while a move is actually in flight (watchdog armed).
        _core.GameEvents
             .OfType<TextEvent>()
             .Subscribe(te => Dispatcher.UIThread.Post(() =>
             {
                 OnGameTextForRetreat(te.Text);
                 OnGameTextForHiddenExit(te.Text);
             }));

        // Mirror the active session's StatusMessage into the reactive CurrentStatus
        // so the walk banner reflects mid-walk changes (waiting/paused/arrived);
        // AutoWalkSession.StatusMessage isn't observable on its own. Every
        // StatusMessage write is already followed by _sessionChanges.OnNext(Current).
        _sessionChanges.Subscribe(s => CurrentStatus = s?.StatusMessage);
    }

    /// <summary>
    /// Emit a Genie 4 automapper protocol line into the script match pipeline.
    /// Scripts that drive movement with <c>#goto</c> (travel.cmd, hunt.cmd, …)
    /// fire the goto and then <c>matchwait</c> on these exact lines —
    /// <c>YOU HAVE ARRIVED!</c> on success, <c>AUTOMAPPER MOVEMENT FAILED</c> /
    /// <c>DESTINATION NOT FOUND</c> on failure. Without these the script hangs
    /// in its matchwait forever (the walk completes but nothing signals it).
    /// Fed through OnGameLine so <c>matchre</c>/<c>matchwait</c> see it exactly
    /// as if the server had emitted it.
    /// </summary>
    public void EmitAutomapperSignal(string line)
    {
        _core.Audit.Note("GOTO", line);   // visible in the Live Audit log
        _core.Scripts.OnGameLine(line);
    }

    /// <summary>
    /// Plan a route from origin to destination and begin walking. Returns
    /// false if there's already an active walk, no path exists, or the
    /// destination is the current room.
    /// </summary>
    public bool Start(MapNode origin, MapNode destination)
    {
        if (Current is { State: AutoWalkState.Active or AutoWalkState.Paused })
        {
            // Genie 4 parity (#96): a new #goto interrupts the in-flight walk
            // rather than being silently rejected. Rejecting emitted no signal,
            // so a script that `matchwait`s on the second #goto hung forever.
            // Cancel the old session (tears down its timers + departure
            // bookkeeping) and fall through to start the new one; the fresh
            // walk emits its own arrival/failure signal on completion.
            Cancel("superseded by a new #goto");
        }

        if (origin.Id == destination.Id)
        {
            FlashStatus("Already here.");
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.Arrived);   // #goto to the current room
            return false;
        }

        var moves = _mapEngine.FindPath(origin, destination);
        if (moves is null)
        {
            FlashStatus($"No path to '{destination.Title}'.");
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.DestinationNotFound);
            return false;
        }
        if (moves.Count == 0)
        {
            FlashStatus("Already here.");
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.Arrived);   // #goto to the current room
            return false;
        }

        return BeginPlan(origin, destination,
                         moves.Select(v => new Genie.Core.Mapper.WalkStep { Verb = v }).ToList(),
                         hasCrossZoneHop: false);
    }

    /// <summary>
    /// Start a <b>cross-zone</b> attended walk from the current room, using a plan
    /// the caller produced with <see cref="Genie.Core.Mapper.MultiZonePathfinder"/>
    /// (the ViewModel owns the maps dir / connections needed to build it). The
    /// walker already executes cross-zone <see cref="Genie.Core.Mapper.WalkStep"/>s —
    /// wait countdown + destination-zone fingerprint arrival — so this just feeds
    /// it the multi-zone plan. <paramref name="destinationLabel"/> is a display-only
    /// node (the resolved target's title/id); arrival is confirmed by zone
    /// fingerprint, not its id. Returns false if already walking or no route.
    /// </summary>
    public bool StartCrossZone(MapNode origin, MapNode destinationLabel,
                               Genie.Core.Mapper.MultiZonePath? plan)
    {
        if (Current is { State: AutoWalkState.Active or AutoWalkState.Paused })
            // #96: interrupt the in-flight walk instead of a silent reject (see Start).
            Cancel("superseded by a new #goto");
        if (plan is null || plan.Steps.Count == 0)
        {
            FlashStatus($"No path to '{destinationLabel.Title}'.");
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.DestinationNotFound);
            return false;
        }
        return BeginPlan(origin, destinationLabel, plan.Steps, plan.HasCrossZoneHop);
    }

    /// <summary>Shared session setup + first-step kick-off for both the single-zone
    /// (<see cref="Start"/>) and cross-zone (<see cref="StartCrossZone"/>) paths.</summary>
    private bool BeginPlan(MapNode origin, MapNode destination,
                           IReadOnlyList<Genie.Core.Mapper.WalkStep> steps, bool hasCrossZoneHop)
    {
        var session = new AutoWalkSession(origin, destination, steps, hasCrossZoneHop);
        session.StatusMessage = $"Walking to {destination.Title} — {session.ProgressText} · Esc to cancel";
        Current = session;
        _sessionChanges.OnNext(session);
        IsCurrentPaused = false;
        // Seed the departure room so the first dispatched move is gated
        // against the origin, not against a stale id from a prior walk (#69).
        _autoStandAttempts     = 0;
        _retreatCount          = 0;
        _searchAttempts        = 0;
        _hiddenExitVerb        = null;
        _departureNodeId       = origin.Id;
        _departureServerRoomId = _mapEngine.CurrentServerRoomId;

        // Kick off the first step. Subsequent steps fire from OnRoomChanged
        // when the player arrives at the expected next room.
        DispatchNextStep();
        return true;
    }

    /// <summary>
    /// Cancel the current walk with a reason. No-op if nothing's running.
    /// Always safe to call (e.g. from the disconnect handler).
    /// </summary>
    public void Cancel(string reason)
    {
        if (Current is null) return;
        Current.State         = AutoWalkState.Cancelled;
        Current.StatusMessage = $"Cancelled: {reason}";
        FlashStatus(Current.StatusMessage);
        _sessionChanges.OnNext(Current);
        Current = null;
        _autoStandAttempts = 0;
        _retreatCount = 0;
        _searchAttempts = 0;
        _hiddenExitVerb = null;
        _departureNodeId = null;
        _departureServerRoomId = null;
        IsCurrentPaused = false;
        StopUnfocusTimer();
        StopWaitCountdown();
        StopStepWatchdog();
        StopPacingTimer();
    }

    /// <summary>
    /// Pause — used by the unfocus timer. Distinct from Cancel because the
    /// user can Resume, picking up at the current step.
    /// </summary>
    public void Pause(string reason)
    {
        if (Current is null) return;
        if (Current.State != AutoWalkState.Active) return;
        Current.State         = AutoWalkState.Paused;
        Current.StatusMessage = $"Paused — {reason} (Resume / Cancel)";
        IsCurrentPaused = true;
        _sessionChanges.OnNext(Current);
        StopStepWatchdog();   // not expecting a move while paused; Resume re-arms
        StopPacingTimer();    // drop any held "waiting for RT/status" retry too
        // Don't kill the wait countdown on pause — the boat doesn't
        // stop because the user alt-tabbed. The bar keeps ticking;
        // when they Resume the walker is just waiting for the room
        // change to fire whenever it actually does.
    }

    /// <summary>
    /// Resume from Paused. No-op if not paused. Fires the next step
    /// immediately — the user has signalled they're back at the keyboard.
    /// </summary>
    public void Resume()
    {
        if (Current is null) return;
        if (Current.State != AutoWalkState.Paused) return;
        Current.State         = AutoWalkState.Active;
        Current.StatusMessage = $"Walking to {Current.Destination.Title} — {Current.ProgressText} · Esc to cancel";
        IsCurrentPaused = false;
        _sessionChanges.OnNext(Current);
        DispatchNextStep();
    }

    /// <summary>
    /// Called when MainWindow loses focus. Opt-in only: does nothing unless
    /// the user has enabled <see cref="GenieConfig.AutoWalkPauseOnUnfocus"/>.
    /// When on, starts the configured unfocus timer; if it expires while the
    /// walk is still active, we Pause (the user clicks Resume to continue).
    /// </summary>
    public void OnWindowDeactivated()
    {
        if (Current is null || Current.State != AutoWalkState.Active) return;

        // Default OFF. DR's Scripting Policy is about being responsive to the
        // game, not about keeping the window focused — so this safeguard only
        // arms when the user has explicitly opted in.
        if (!_core.Config.AutoWalkPauseOnUnfocus) return;

        var seconds = Math.Max(DefaultUnfocusPauseSeconds, _core.Config.AutoWalkUnfocusSeconds);
        StopUnfocusTimer();
        _unfocusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _unfocusTimer.Tick += (_, _) =>
        {
            StopUnfocusTimer();
            Pause($"window unfocused {seconds}s");
        };
        _unfocusTimer.Start();
    }

    /// <summary>
    /// Called when MainWindow regains focus. Just stops the unfocus timer;
    /// doesn't auto-resume — the user has to click Resume.
    /// </summary>
    public void OnWindowActivated() => StopUnfocusTimer();

    private void StopUnfocusTimer()
    {
        _unfocusTimer?.Stop();
        _unfocusTimer = null;
    }

    /// <summary>(Re)arm the per-step watchdog after dispatching a move. A real
    /// room change restarts it (next step); a timeout means the move stuck.
    /// Cross-zone wait steps are exempt — the wait countdown handles those.</summary>
    private void StartStepWatchdog()
    {
        StopStepWatchdog();
        if (IsWaitingForCrossZone) return;
        _stepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StepTimeoutSeconds) };
        _stepTimer.Tick += (_, _) =>
        {
            StopStepWatchdog();
            if (Current is null || Current.State != AutoWalkState.Active) return;
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.MovementFailed);
            Cancel("no room change after move (stuck)");
        };
        _stepTimer.Start();
    }

    private void StopStepWatchdog()
    {
        _stepTimer?.Stop();
        _stepTimer = null;
    }

    /// <summary>True when the character can take the next move RIGHT NOW: no
    /// roundtime and no movement-blocking status. When false, <paramref
    /// name="retryIn"/> is how long to wait before re-checking and <paramref
    /// name="reason"/> names the blocker (for the indicator).</summary>
    private bool CanMoveNow(out TimeSpan retryIn, out string reason)
    {
        var combat = _core.State.Combat;
        if (combat.InRoundTime)
        {
            // Wait out the remaining RT (+a hair), capped so a stale/huge value
            // can't park the walk; clamped up so we don't busy-spin.
            retryIn = TimeSpan.FromSeconds(Math.Clamp(combat.RoundTimeRemaining + 0.2, 0.25, 5.0));
            reason  = "roundtime";
            return false;
        }
        if (MovementBlocker(_core.State) is { } blocker)
        {
            retryIn = TimeSpan.FromMilliseconds(500);   // poll until it clears (user stands / stun fades)
            reason  = blocker;
            return false;
        }
        retryIn = TimeSpan.Zero;
        reason  = "";
        return true;
    }

    /// <summary>Name of the first movement-blocking status, or null if the
    /// character can move. (Posture states need a stand first; stun/web pin you.)</summary>
    private static string? MovementBlocker(GameState st)
    {
        if (st.ActiveStatuses.Contains(CharacterStatus.Stunned))  return "stunned";
        if (st.ActiveStatuses.Contains(CharacterStatus.Webbed))   return "webbed";
        if (st.ActiveStatuses.Contains(CharacterStatus.Prone))    return "prone";
        if (st.ActiveStatuses.Contains(CharacterStatus.Kneeling)) return "kneeling";
        if (st.ActiveStatuses.Contains(CharacterStatus.Sitting))  return "sitting";
        return null;
    }

    /// <summary>True for posture blocks that require a <c>stand</c> to clear
    /// (sitting / kneeling / prone), as opposed to passive blocks (roundtime /
    /// stunned / webbed) that clear on their own without a command.</summary>
    private static bool IsPostureBlock(string? reason) =>
        reason is "sitting" or "kneeling" or "prone";

    /// <summary>Re-attempt the held step after <paramref name="delay"/> (the
    /// remaining RT, or a short poll while a status blocks). DispatchNextStep
    /// re-checks <see cref="CanMoveNow"/> and either sends or re-defers.</summary>
    private void ScheduleMovabilityRetry(TimeSpan delay)
    {
        StopPacingTimer();
        var interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : delay;
        _pacingTimer = new DispatcherTimer { Interval = interval };
        _pacingTimer.Tick += (_, _) => { StopPacingTimer(); DispatchNextStep(); };
        _pacingTimer.Start();
    }

    private void StopPacingTimer()
    {
        _pacingTimer?.Stop();
        _pacingTimer = null;
    }

    private void OnRoomChanged()
    {
        if (Current is null || Current.State != AutoWalkState.Active) return;

        var node = _mapEngine.CurrentNode;

        // Gate on a CONFIRMED change of room identity (#69). The engine fires
        // CurrentNodeChanged several times for a single physical room — once
        // per parser update (title, exits, description) — and once with a null
        // CurrentNode on a zone-miss. Only a non-null room whose id differs
        // from the room we dispatched the last move from means we actually
        // moved; everything else is a repeat/partial fire and must NOT pump
        // another move (doing so floods DR's typeahead buffer and stalls).
        if (node is null) return;

        // Confirm a REAL move before advancing. Normally the mapper node id
        // changing is the signal. But in same-description areas (Lava Field,
        // marsh) the map pins many identical rooms to one node, so node.Id
        // stays put and the walk would stall the full 15s watchdog per room.
        // The live server room uid changes on every physical room the server
        // reports, so a uid change rescues pacing when the node id can't.
        // Either signal counts as "we moved"; if both are unchanged it's a
        // repeat/partial CurrentNodeChanged fire and we must not pump a move.
        var nodeUnchanged = _departureNodeId is { } fromId && node.Id == fromId;
        var srvUid        = _mapEngine.CurrentServerRoomId;
        var uidChanged    = !string.IsNullOrEmpty(srvUid)
                            && !string.IsNullOrEmpty(_departureServerRoomId)
                            && !string.Equals(srvUid, _departureServerRoomId, StringComparison.OrdinalIgnoreCase);
        if (nodeUnchanged && !uidChanged) return;

        // Confirmed move landed — clear the per-step retreat counter (#130) and
        // the hidden-exit search budget so the next step starts fresh.
        _retreatCount   = 0;
        _searchAttempts = 0;
        _hiddenExitVerb = null;

        // Any room-change implicitly clears a pending cross-zone wait —
        // either we arrived at the target zone, or we landed somewhere
        // intra-zone that progresses the plan. Either way the countdown
        // is no longer meaningful.
        if (IsWaitingForCrossZone) StopWaitCountdown();

        // Did we arrive at the destination?
        if (node.Id == Current.Destination.Id)
        {
            Current.StepsCompleted = Current.StepsTotal;
            Current.State          = AutoWalkState.Finished;
            Current.StatusMessage  = $"Arrived at {Current.Destination.Title}";
            FlashStatus(Current.StatusMessage);
            _sessionChanges.OnNext(Current);
            Current = null;
            _departureNodeId = null;
            StopUnfocusTimer();
            StopStepWatchdog();
            // Signal automapper-driven scripts that the #goto leg finished.
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.Arrived);
            return;
        }

        // Otherwise advance the step counter and pump the next move. The
        // simple "match-by-position" approach trusts FindPath's ordering;
        // if the player got bounced into a different room we'll send a
        // wrong next move and cancel on the off-path heuristic.
        Current.StepsCompleted++;
        Current.StatusMessage = $"Walking to {Current.Destination.Title} — {Current.ProgressText} · Esc to cancel";
        _sessionChanges.OnNext(Current);

        if (Current.StepsCompleted >= Current.Plan.Count)
        {
            // Plan exhausted but we didn't match destination — must have
            // wandered off. Cancel rather than send arbitrary commands.
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.MovementFailed);
            Cancel("walked past destination");
            return;
        }

        DispatchNextStep();
    }

    private void DispatchNextStep()
    {
        if (Current is null || Current.State != AutoWalkState.Active) return;
        if (Current.StepsCompleted >= Current.Plan.Count) return;

        // Pace to the game (Issue #5): hold the next move until roundtime has
        // cleared AND the character can move (not stunned / webbed / prone /
        // kneeling / sitting). Otherwise DR bounces the verb ("...wait N seconds")
        // or queues it unpredictably — the walker must be responsive, not spammy.
        // Re-check shortly (after the remaining RT, or every 0.5s while blocked).
        if (!CanMoveNow(out var retryIn, out var blockReason))
        {
            // Auto-stand (Issue #5): a posture block only clears when the
            // character stands, but a typed `stand` would trip SendCommand's
            // "typed command cancels the walk" guard. So the walker stands the
            // character itself, ONCE per block, via the same core command path
            // the moves use (not the typed-command path, so no self-cancel).
            // Passive blocks (roundtime / stunned / webbed) clear on their own —
            // just wait those out.
            if (IsPostureBlock(blockReason) && _autoStandAttempts < MaxAutoStandAttempts)
            {
                _autoStandAttempts++;
                var attemptNote = _autoStandAttempts > 1 ? $" (attempt {_autoStandAttempts})" : "";
                Current.StatusMessage =
                    $"Walking to {Current.Destination.Title} — standing up to continue{attemptNote} · Esc to cancel";
                _sessionChanges.OnNext(Current);
                _core.Commands.ProcessInput("stand");
                ScheduleMovabilityRetry(TimeSpan.FromMilliseconds(750));  // let the stand (+ its RT) resolve
                return;
            }

            Current.StatusMessage =
                $"Walking to {Current.Destination.Title} — waiting ({blockReason}) · Esc to cancel";
            _sessionChanges.OnNext(Current);
            ScheduleMovabilityRetry(retryIn);
            return;
        }
        _autoStandAttempts = 0;   // gate passed — re-arm auto-stand for any later posture block

        var step = Current.Plan[Current.StepsCompleted];

        // Cross-zone hop — surface the wait window to the user before
        // sending the transit verb. The engine doesn't enforce the wait
        // (we naturally wait for the destination zone to fingerprint-
        // match), but showing the expected window calibrates the user's
        // patience: a 5-10 minute boat wait shouldn't look like a hang.
        if (step.IsCrossZone && step.ExpectedWaitMinSeconds.HasValue)
        {
            var min = step.ExpectedWaitMinSeconds.Value;
            var max = step.ExpectedWaitMaxSeconds ?? min;
            var avg = (min + max) / 2;
            Current.StatusMessage =
                $"Walking to {Current.Destination.Title} — {Current.ProgressText} · "
                + $"{step.Description} (wait ~{min / 60}-{max / 60} min) · Esc to cancel";
            _sessionChanges.OnNext(Current);

            // Kick the countdown bar — visible in the Mapper indicator
            // strip until the destination zone fingerprints in. Pegs at
            // 0 if the wait expires before arrival (we don't know the
            // true ETA, the window is just a hint from the community
            // ZoneConnections.xml data).
            StartWaitCountdown(step.Description, avg);
        }

        // Remember the room we're leaving so the step pump (OnRoomChanged)
        // can tell a real arrival from the repeat/partial CurrentNodeChanged
        // fires DR emits for a single transition (#69). Captured BEFORE the
        // send: this is the room the next move takes us out of.
        _departureNodeId       = _mapEngine.CurrentNode?.Id;
        _departureServerRoomId = _mapEngine.CurrentServerRoomId;

        // Map arcs may carry Genie 4 automapper pacing prefixes
        // (e.g. move="rt north", move="slow south") — directives meaning
        // "wait for roundtime / go carefully, then move", NOT literal commands.
        // Sent verbatim they return "Please rephrase that command." (public #123).
        // MoveVerb.Normalize strips the known prefix: movement is already paced
        // one-move-per-room and gates on roundtime, so the real verb alone honours
        // the intent. Real DR verbs (go/climb/swim/dive) are left untouched.
        var verb = Genie.Core.Mapper.MoveVerb.Normalize(step.Verb);

        // Search-directive arc ("search go trampled path" — hidden exit): send
        // the inner move and remember it. G4 MOVE.SEARCH parity — the go goes
        // first because the path may already be open; if DR bounces it, the
        // hidden-exit handler searches and re-dispatches this same step.
        if (Genie.Core.Mapper.MoveVerb.TryParseSearchDirective(verb, out var hiddenMove))
        {
            _hiddenExitVerb = hiddenMove;
            verb = hiddenMove;
        }
        else
        {
            _hiddenExitVerb = null;
        }

        // Genie 4 quick-send chain segments — move="pull sconce;-1 go door":
        // a '-' segment is G4 shorthand for "#send [delay] cmd" (wait, then
        // send roundtime-gated). ProcessInput has no quick-send rewrite, so a
        // dash segment sent verbatim bounces off DR; expand it here. The
        // emitted #send segments ride the FIFO CommandQueue, so chain order
        // survives the mixed immediate/queued dispatch.
        verb = Genie.Core.Mapper.MoveVerb.ExpandQuickSends(verb, _core.Config.CommandChar);

        // Send through ProcessInput so the same alias / trigger / command
        // pipeline runs as if the user typed it. Movement is paced by the
        // confirmed room change above — one move per room — so it stays
        // responsive to the game rather than bursting the whole path.
        _core.Commands.ProcessInput(verb);

        // Arm the watchdog — a confirmed room change (OnRoomChanged) restarts it
        // for the next step; no change in time means this move stuck.
        StartStepWatchdog();
    }

    /// <summary>
    /// #130 auto-retreat: DR bounces a movement verb with "You are engaged to a
    /// X in melee range!" (and variants) while the character is locked in combat.
    /// Genie 3/4 and the community travel scripts retreat and retry; we do the
    /// same so #goto/.travel recover instead of stalling to the watchdog. Only
    /// acts while a step is in flight (<see cref="_stepTimer"/> armed) so a stray
    /// combat line outside a dispatched move can't trigger a spurious retreat.
    /// Sends <c>retreat</c> once per bounce (capped at <see cref="MaxRetreatsPerStep"/>
    /// — pole→melee→free can need two), then re-dispatches the held step; the
    /// retreat's roundtime is paced by <see cref="DispatchNextStep"/>.
    /// </summary>
    private void OnGameTextForRetreat(string line)
    {
        if (Current is null || Current.State != AutoWalkState.Active) return;
        if (_stepTimer is null) return;                 // no move in flight — ignore
        if (string.IsNullOrEmpty(line)) return;
        if (!EngagementBlock.IsMatch(line)) return;

        // Budget exhausted — something keeps us pinned (or it wasn't really an
        // engagement block). Stop retreating and let the step watchdog own the
        // eventual MOVEMENT FAILED / cancel.
        if (_retreatCount >= MaxRetreatsPerStep) return;

        StopStepWatchdog();     // we're handling this bounce; the retry re-arms it
        StopPacingTimer();
        _retreatCount++;
        Current.StatusMessage =
            $"Walking to {Current.Destination.Title} — retreating from combat to continue · Esc to cancel";
        _sessionChanges.OnNext(Current);

        // Retreat via the core command path (not the typed path) so it doesn't
        // trip the "typed command cancels the walk" guard, exactly like auto-stand.
        _core.Commands.ProcessInput("retreat");

        // Let the retreat (and its roundtime) resolve, then re-send the held move.
        // StepsCompleted was NOT advanced, so DispatchNextStep re-issues the same verb.
        ScheduleMovabilityRetry(TimeSpan.FromMilliseconds(750));
    }

    /// <summary>
    /// Hidden-exit search-and-retry (Genie 4 automapper MOVE.SEARCH parity). A
    /// search-directive arc's inner move ("go trampled path") bounces with
    /// "I could not find what you were referring to." until an in-room
    /// <c>search</c> reveals the path. When that bounce arrives for the step in
    /// flight, send <c>search</c> via the core command path (not the typed path —
    /// same guard-dodge as auto-stand/auto-retreat) and re-dispatch the SAME
    /// step; the search's roundtime is paced by <see cref="DispatchNextStep"/>'s
    /// movability gate. Searching is a skill/random check that can whiff, so the
    /// loop repeats — each failed go buys one more search — capped at
    /// <see cref="MaxSearchAttempts"/>, after which the walk fails cleanly
    /// instead of searching forever.
    /// </summary>
    private void OnGameTextForHiddenExit(string line)
    {
        if (Current is null || Current.State != AutoWalkState.Active) return;
        if (_stepTimer is null) return;                 // no move in flight — ignore
        if (_hiddenExitVerb is null) return;            // current step isn't a search-directive arc
        if (string.IsNullOrEmpty(line)) return;
        if (!HiddenExitBlock.IsMatch(line)) return;

        StopStepWatchdog();     // we're handling this bounce; the retry re-arms it
        StopPacingTimer();

        if (_searchAttempts >= MaxSearchAttempts)
        {
            EmitAutomapperSignal(Genie.Core.Mapper.AutomapperSignals.MovementFailed);
            Cancel($"hidden exit not revealed after {MaxSearchAttempts} searches");
            return;
        }

        _searchAttempts++;
        var attemptNote = _searchAttempts > 1 ? $" (attempt {_searchAttempts})" : "";
        Current.StatusMessage =
            $"Walking to {Current.Destination.Title} — searching for hidden exit{attemptNote} · Esc to cancel";
        _sessionChanges.OnNext(Current);

        _core.Commands.ProcessInput("search");

        // Let the search (and its roundtime) resolve, then re-send the held move.
        // StepsCompleted was NOT advanced, so DispatchNextStep re-issues the same
        // step; its search-directive parse sends the inner go again.
        ScheduleMovabilityRetry(TimeSpan.FromMilliseconds(750));
    }

    /// <summary>
    /// Start the boat-wait countdown for a cross-zone hop. Fires every
    /// second; updates <see cref="CurrentWaitSecondsLeft"/> and the
    /// derived display string. Stops automatically when the destination
    /// zone arrives (room-change handler) or the walk is cancelled.
    /// </summary>
    private void StartWaitCountdown(string label, int totalSeconds)
    {
        StopWaitCountdown();   // belt-and-braces in case a previous timer leaked

        CurrentWaitLabel        = label;
        CurrentWaitTotalSeconds = Math.Max(1, totalSeconds);
        CurrentWaitSecondsLeft  = CurrentWaitTotalSeconds;
        CurrentWaitDisplay      = FormatWait(CurrentWaitSecondsLeft);
        UpdateWaitText();
        IsWaitingForCrossZone   = true;

        _waitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _waitTimer.Tick += (_, _) =>
        {
            if (CurrentWaitSecondsLeft > 0)
                CurrentWaitSecondsLeft--;
            CurrentWaitDisplay = FormatWait(CurrentWaitSecondsLeft);
            UpdateWaitText();
            // We let the timer continue past 0 — the bar stays empty
            // until the room change actually arrives. The user sees
            // "any moment now…" if the boat is running late.
        };
        _waitTimer.Start();
    }

    /// <summary>Stop the wait timer and clear the indicator. Idempotent;
    /// safe to call from any of the walk-terminating paths (room-
    /// change, cancel, pause, disconnect).</summary>
    private void StopWaitCountdown()
    {
        _waitTimer?.Stop();
        _waitTimer = null;
        IsWaitingForCrossZone   = false;
        CurrentWaitLabel        = "";
        CurrentWaitSecondsLeft  = 0;
        CurrentWaitTotalSeconds = 0;
        CurrentWaitDisplay      = "";
        CurrentWaitText         = "";
    }

    private void UpdateWaitText()
        => CurrentWaitText = string.IsNullOrEmpty(CurrentWaitLabel)
            ? CurrentWaitDisplay
            : $"{CurrentWaitLabel} · {CurrentWaitDisplay}";

    private static string FormatWait(int seconds)
    {
        if (seconds <= 0) return "any moment now…";
        var mins = seconds / 60;
        var secs = seconds % 60;
        return mins > 0 ? $"~{mins}:{secs:D2} left" : $"~{secs}s left";
    }

    private void FlashStatus(string text)
    {
        LastStatusFlash = text;
        // Auto-clear after 5 seconds so the flash doesn't linger.
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (LastStatusFlash == text) LastStatusFlash = null;
        }, TimeSpan.FromSeconds(5));
    }
}
