using System.Reactive.Linq;
using System.Reflection;
using Genie.Core.AI;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Connection;
using Genie.Core.Diagnostics;
using Genie.Core.Events;
using Genie.Core.Gags;
using Genie.Core.GameState;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Mapper;
using Genie.Core.Parser;
using Genie.Core.Parsing;
using Genie.Core.Presets;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Genie.Core;

/// <summary>
/// Top-level facade for Genie.Core.
///
/// Usage:
/// <code>
/// var cfg = new ConnectionConfig
/// {
///     Mode            = ConnectionMode.DirectSGE,
///     AccountName     = "myaccount",
///     AccountPassword = "...",
///     CharacterName   = "Tirost",
///     GameCode        = "DR"
/// };
///
/// await using var core = new GenieCore(cfg);
///
/// // Subscribe to game text
/// core.GameEvents.OfType&lt;TextEvent&gt;().Subscribe(e => Console.WriteLine(e.Text));
///
/// // Route user input through the full pipeline
/// core.ProcessInput("north");
///
/// await core.ConnectAsync();
/// </code>
/// </summary>
public sealed class GenieCore : IAsyncDisposable, ICommandHost, Genie.Plugins.IPluginHost
{
    /// <summary>Plugin-API contract version (bumped only on a breaking change
    /// to <see cref="Genie.Plugins.IGeniePlugin"/> / <see cref="Genie.Plugins.IPluginHost"/>).</summary>
    public const int PluginInterfaceVersion = 1;

    /// <summary>
    /// App version string read from the running entry assembly's
    /// <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>.
    /// The csproj's <c>&lt;InformationalVersion&gt;</c> is the single source of
    /// truth — every UI surface (title bar, Updates dialog, About box, plugin
    /// host's <see cref="Genie.Plugins.IPluginHost.HostVersion"/>) reads back
    /// to here, so a csproj bump propagates everywhere without touching code.
    ///
    /// Strips any <c>+commit-sha</c> suffix the .NET SDK appends when
    /// <c>IncludeSourceRevisionInInformationalVersion</c> is on (false in our
    /// csproj today, but defensive against future toggles).
    ///
    /// Falls back to <c>"(dev)"</c> when the entry assembly is missing the
    /// attribute (unit tests, harness invocations).
    /// </summary>
    public static readonly string HostVersionString = ResolveHostVersion();

    private static string ResolveHostVersion()
    {
        var attr = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
        var raw = attr?.InformationalVersion ?? "(dev)";
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }

    // ── Network / parser layer ─────────────────────────────────────────────────
    // PERSISTENT-CORE NOTE: the connection/parser/state-engine and their event
    // subscriptions are the *per-connection* layer — rebuilt by BuildConnection()
    // on every ConnectAsync(cfg) and torn down by TeardownConnection(). They are
    // therefore mutable (no `readonly`). Everything above the relay subjects (the
    // engines, ScriptEngine, AutoMapper, Plugins, the persistent GameState) lives
    // for the whole app session and is built once in the constructor.
    private GameConnection?    _connection;
    private DrXmlParser?       _parser;
    private GameStateEngine?   _stateEngine;
    private IDisposable?       _parserFeed;
    private IDisposable?       _settingsInfoSub;
    private IDisposable?       _gameHostSub;
    private IDisposable?       _connectedVarSub;
    private IDisposable?       _gameEventSub;
    private IDisposable?       _pluginXmlSub;
    private readonly Plugins.GameStateView _pluginStateView;

    /// <summary>The one persistent game-state snapshot. Lives for the core's whole
    /// life (consumers hold it by reference); <see cref="Models.GameState.Reset"/>
    /// clears it in place at the start of each connect.</summary>
    private readonly Models.GameState _state;

    // ── Relay subjects (stable public-observable identity — the linchpin) ───────
    // The public GameEvents / RawXmlStream / ConnectionState observables are
    // long-lived relays the core owns. Each new per-connection parser/connection
    // is subscribed *into* these relays by BuildConnection(); the inner feed subs
    // are torn down per connect but the relays themselves persist. App-side
    // subscribers (the 12 VM Attach() calls, which have no Detach) subscribe ONCE
    // and survive every reconnect — that's what makes a persistent core possible
    // without re-attaching the UI on each connect.
    private readonly System.Reactive.Subjects.Subject<GameEvent>       _gameEventsRelay = new();
    private readonly System.Reactive.Subjects.Subject<string>          _rawXmlRelay     = new();
    private readonly System.Reactive.Subjects.Subject<ConnectionEvent> _connStateRelay  = new();
    private readonly System.Reactive.Subjects.Subject<string>          _sentRelay       = new();
    private IDisposable? _gameEventsRelaySub;
    private IDisposable? _rawXmlRelaySub;
    private IDisposable? _connStateRelaySub;
    private IDisposable? _sentRelaySub;

    // ── Game loop (#251 — docs/internal/GAME_THREAD_DESIGN.md) ────────────────
    /// <summary>The dedicated game thread. Null when <c>#config gamethread off</c>
    /// (the escape hatch) — then the pipeline runs inline on whatever thread the
    /// read loop resumes on (the legacy UI-thread behavior) and the App host
    /// retains its DispatcherTimer pump. Core-lifetime, like the old heartbeat.</summary>
    private readonly GameLoop? _loop;
    private IDisposable? _heartbeat;

    /// <summary>True when the #251 game thread is active. The App host reads
    /// this to skip its legacy DispatcherTimer script pump (the core owns
    /// <see cref="ScriptEngine.ScheduleTick"/> and the 100 ms heartbeat when
    /// the loop exists).</summary>
    public bool GameThreadEnabled => _loop is not null;

    /// <summary>Raised (from a watchdog thread, NOT the UI or game thread) when
    /// one game-loop work item has run longer than <see cref="GameLoop.StallThreshold"/>
    /// — i.e. the game pipeline is wedged in a single statement. The UI stays
    /// alive and should tell the user (and offer #stopall, which posts and takes
    /// effect the moment the wedge ends). At most once per wedged item.</summary>
    public event Action<TimeSpan>? PipelineStalled;

    /// <summary>Run <paramref name="work"/> on the game loop: post when called
    /// from any other thread, invoke inline when already on the loop (or when
    /// the loop is off). Inline-on-loop keeps synchronous re-entrancy semantics
    /// (#parse, trigger feedback) intact.</summary>
    private void RunOnLoop(Action work)
    {
        if (_loop is { IsOnLoop: false } loop) loop.Post(work);
        else work();
    }

    /// <summary>Post a programmatic command line into the command pipeline on
    /// the game thread — the App-side entry point for everything that used to
    /// call <c>Commands.ProcessInput</c> directly from the UI (menu items,
    /// Script Bar chips, mapper walk verbs, Esc's #stopall). Fire-and-forget:
    /// feedback arrives via the echo events, exactly as before.</summary>
    public void PostCommand(string command)
        => RunOnLoop(() => Commands.ProcessInput(command));

    /// <summary>Feed a synthetic protocol line to running scripts' matchers on
    /// the game thread — the automapper's <c>YOU HAVE ARRIVED!</c> /
    /// <c>AUTOMAPPER MOVEMENT FAILED</c> signals (AutoWalkService), which
    /// matchwait-driven scripts (travel.cmd, hunt.cmd) block on.</summary>
    public void PostScriptGameLine(string line)
        => RunOnLoop(() => Scripts.OnGameLine(line));

    // ── Configuration / runtime ────────────────────────────────────────────────
    private readonly LocalDirectoryService _localDir;

    // Stored at construction so BuildConnection() can (re)build the per-connection
    // layer on each connect: the logger factory makes new GameConnection/parser/
    // state-engine loggers; the AI config (if any) gates a fresh AiContextBuffer.
    private readonly ILoggerFactory _loggerFactory;
    private readonly AiConfig?      _aiConfig;

    /// <summary>Loaded configuration. Survives across sessions.</summary>
    public GenieConfig Config { get; }

    /// <summary>Set by the App host (public #257): it performs the entire
    /// two-layer rule load (global + profile, .json + .cfg, Character-over-
    /// Global) itself, so <see cref="ConnectAsync"/> must skip its own
    /// profile-dir .cfg auto-load — that replay clears each engine and would
    /// wipe the layered Global rules. Headless hosts leave this false.</summary>
    public bool HostOwnsRuleLoad { get; set; }

    /// <summary>The #156 dialog capture journal — lazily created against
    /// <see cref="GenieConfig.LogDir"/> on the first dialog event.</summary>
    private Dialogs.DialogJournal? _dialogJournal;

    /// <summary>Session inventory of server dialogs (#156 Phase 0c) —
    /// backs <c>#dialogs list</c> / <c>#dialogs report</c>.</summary>
    private readonly Dialogs.DialogSessionTracker _dialogTracker = new();

    // ── Command pipeline ───────────────────────────────────────────────────────
    private readonly CommandQueue _commandQueue;
    private readonly EventQueue   _eventQueue;

    /// <summary>Input router: alias expansion, separator split, #cmd dispatch, game send.</summary>
    public CommandEngine Commands { get; }

    // ── Rule engines ───────────────────────────────────────────────────────────
    public ClassEngine         Classes        { get; }
    public VariableEngine      Variables      { get; }
    public AliasEngine         Aliases        { get; }
    public TriggerEngineFinal  Triggers       { get; }
    public HighlightEngine     Highlights     { get; }
    public NameHighlightEngine NameHighlights { get; }
    public PresetEngine        Presets        { get; }
    public SubstituteEngine    Substitutes    { get; }
    public GagEngine           Gags           { get; }
    public MacroEngine         Macros         { get; }
    public AutoMapperEngine    AutoMapper     { get; }

    /// <summary>Live per-stage timing for the performance overlay. Collection is
    /// gated by <see cref="Diagnostics.PipelineMetrics.Enabled"/> (off until the
    /// overlay is shown); regex match-timeouts are always counted.</summary>
    public Diagnostics.PipelineMetrics Metrics { get; } = new();

    /// <summary>Loaded plugins. Phase 1 = in-process registration; the DLL
    /// loader bolts discovery onto this same manager.</summary>
    public Plugins.PluginManager Plugins      { get; }

    // ── Mapper ────────────────────────────────────────────────────────────────
    // AutoMapperEngine itself is persistent (built once); the adapter that feeds
    // it from the live parser/state is per-connection (rebuilt by BuildConnection).
    private MapperGameStateAdapter? _mapperAdapter;

    /// <summary>
    /// JSON load/save for <see cref="MapZone"/>. Exposed so the UI layer can
    /// list, load, save, and merge zones — including pulling refreshes from
    /// the public GenieClient/Maps repo via a
    /// <see cref="Update.Updaters.MapsUpdater"/> constructed against this
    /// repository and the user's chosen Maps dir.
    /// </summary>
    public MapZoneRepository  ZoneRepository { get; } = new();

    // ── Scripting ──────────────────────────────────────────────────────────────
    private readonly TypeAheadSession _typeAhead;
    // ScriptGlobalsSync mirrors live game state → script globals; it binds to the
    // per-connection parser + identity, so it is rebuilt per connect.
    private ScriptGlobalsSync? _globalsSync;
    // Skill-history recorder (Analytics) — bound to the per-connection character
    // identity + connection state, so it is rebuilt per connect like _globalsSync.
    private Analytics.SkillHistoryRecorder? _skillHistory;
    private readonly Diagnostics.LiveAudit _liveAudit;

    /// <summary>Set when a room-defining event arrives (NavEvent or the
    /// room-title component); consumed on the next prompt to unblock the script
    /// <c>move</c> command. Coalescing to the prompt lets uid-less "(**)" rooms —
    /// which emit no NavEvent — still wake a paused move (PR #92). Reset per
    /// connect (the persistent core keeps this field across reconnect).</summary>
    private bool _roomChangedSincePrompt;

    /// <summary>.cmd script runner. Includes built-in EXP and info trackers.</summary>
    public ScriptEngine Scripts { get; }

    // ── Public observables (stable surface, relay-backed) ───────────────────────
    // These return the persistent relay subjects (see above), NOT the per-connection
    // parser/connection directly — so a subscription taken once survives reconnect.

    /// <summary>Typed game events (TextEvent, ProgressBarEvent, RoundTimeEvent, …).</summary>
    public IObservable<GameEvent>       GameEvents      => _gameEventsRelay;

    /// <summary>Test-only seam: pushes an event onto <see cref="GameEvents"/>
    /// without a live connection. Internal — visible to Genie.Core.Tests and
    /// Genie.App.Tests via <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// (see AssemblyInfo.cs / Genie.Core.csproj) so VM Attach() tests can drive
    /// the real subscribe callback instead of reflecting into the private relay.</summary>
    internal void PublishGameEventForTests(GameEvent e) => _gameEventsRelay.OnNext(e);

    /// <summary>Raw XML stream — subscribe for logging, recording, or custom processing.</summary>
    public IObservable<string>          RawXmlStream    => _rawXmlRelay;

    /// <summary>Commands actually written to the socket, in wire order — the
    /// outbound twin of <see cref="RawXmlStream"/>. Survives reconnects like
    /// the other relays. Subscribe to attribute a server reply to the command
    /// that caused it, including sends the client made on its own.</summary>
    public IObservable<string>          SentCommands    => _sentRelay;

    /// <summary>Connection lifecycle events.</summary>
    public IObservable<ConnectionEvent> ConnectionState => _connStateRelay;

    /// <summary>The active skill-history recorder (Analytics window's data
    /// source), or null when recording isn't active for this connection —
    /// analytics off at connect, no character identity (LIST mode), or a
    /// replay session without <c>#config analyticsreplay on</c>.</summary>
    public Analytics.SkillHistoryRecorder? SkillHistory => _skillHistory;

    // ── Type-ahead (UI counter) ─────────────────────────────────────────────
    /// <summary>Commands sent to the game awaiting a prompt — the live
    /// type-ahead buffer occupancy shown by the command-bar counter.</summary>
    public int TypeAheadInFlight => _typeAhead.InFlight;
    /// <summary>The current type-ahead cap (1 free / 2 premium / 3 +LTB, or the
    /// server-calibrated value).</summary>
    public int TypeAheadLimit    => _typeAhead.Limit;
    /// <summary>Raised when <see cref="TypeAheadInFlight"/> or
    /// <see cref="TypeAheadLimit"/> changes. May fire off the UI thread.</summary>
    public event Action? TypeAheadChanged
    {
        add    => _typeAhead.Changed += value;
        remove => _typeAhead.Changed -= value;
    }

    /// <summary>Timed connect-progress sink (TLS attempt, per-step SGE timings,
    /// fallback, game-server connect) surfaced to the game window so a stall can
    /// be isolated to an exact step. Set once by the host; persisted on the core
    /// and applied to each per-connection <see cref="GameConnection"/> as it's
    /// built (the connection doesn't exist until the first connect).</summary>
    private Action<string>? _connectionDiag;
    public Action<string>? ConnectionDiag
    {
        get => _connectionDiag;
        set { _connectionDiag = value; if (_connection is not null) _connection.Diag = value; }
    }

    /// <summary>When <c>true</c>, the granular per-step SGE marks are emitted to
    /// <see cref="ConnectionDiag"/> alongside the always-on high-level status
    /// lines. Driven by <c>#config conndebug</c>; set by the host. Persisted on the
    /// core and applied to each per-connection connection as it's built.</summary>
    private bool _connectionVerboseDiag;
    public bool ConnectionVerboseDiag
    {
        get => _connectionVerboseDiag;
        set { _connectionVerboseDiag = value; if (_connection is not null) _connection.VerboseDiag = value; }
    }

    /// <summary>Current live game state snapshot. One persistent instance for the
    /// core's life (reset in place per connect), so consumers can hold it by ref.</summary>
    public Models.GameState             State           => _state;

    /// <summary>AI context buffer and analyzer. Null until a connect builds it (and
    /// only when an AiConfig was provided). Rebuilt per connect.</summary>
    public AiContextBuffer?             AiBuffer        { get; private set; }

    /// <summary>Resolved per-character profile directory chosen at construction time.</summary>
    public string                       ProfileDirectory { get; private set; } = string.Empty;

    // ── UI echo hooks ──────────────────────────────────────────────────────────

    /// <summary>
    /// Raised by <c>#echo</c>, scripts, and internal command handlers.
    /// UI subscribes to display text in the main game window.
    /// </summary>
    public event Action<string>?                   EchoLine;

    /// <summary>
    /// Raised for every script-originated line — the single "this came from a
    /// script" signal. Covers anything the <see cref="ScriptEngine"/> emits via
    /// its echo channel (<c>[script]</c> status lines, <c>[dbg:N]</c> traces,
    /// <c>echo</c>/<c>#echo</c> output, abort messages) AND the game commands a
    /// script issues. The main game window classifies these as Script lines
    /// (governed by the "Script Lines" filter); the Scripts panel uses the same
    /// stream for its scrollback. Distinct from <see cref="EchoLine"/>, which
    /// carries user-typed command echoes and system diagnostics.
    /// </summary>
    public event Action<string>?                   ScriptOutputLine;

    /// <summary>
    /// Raised by directed <c>#echo &gt;window #color</c>. Args: (text, windowName?, hexColor?).
    /// Null window/color means fall back to main window / default colour.
    /// </summary>
    public event Action<string, string?, string?>? EchoToWindow;

    /// <summary>
    /// Raised by <c>#echo</c> with a colour and/or <c>mono</c> flag but no
    /// <c>&gt;window</c> redirect — a styled line for the <em>main</em> game
    /// window. Args: (text, colour?, mono). Colour is a named colour or
    /// <c>#rrggbb</c> (null = default echo colour); mono = monospaced font.
    /// </summary>
    public event Action<string, string?, bool>? EchoStyledLine;

    // ── Echo funnels ─────────────────────────────────────────────────────────
    // Every echoed display line runs through the plugin chain (OnEcho) before
    // its event fires — a deliberate Genie 5 extension (Genie 4 never ran
    // echoes through ParseText). A plugin can rewrite the line or gag it
    // (null return suppresses the event). Plugin echoes emitted from inside
    // OnEcho pass through undispatched (PluginManager re-entrancy guard).

    private void RaiseEchoLine(string text)
    {
        var t = Plugins is null ? text : Plugins.DispatchEcho(text, "main");
        if (t is not null) EchoLine?.Invoke(t);
    }

    private void RaiseScriptOutput(string text)
    {
        var t = Plugins is null ? text : Plugins.DispatchEcho(text, "main");
        if (t is not null) ScriptOutputLine?.Invoke(t);
    }

    private void RaiseEchoToWindow(string text, string? window, string? color)
    {
        if (IsPhantomVarWindow(window, "#echo")) window = null;   // → main, not a phantom panel
        var t = Plugins is null ? text : Plugins.DispatchEcho(text, string.IsNullOrEmpty(window) ? "main" : window!);
        if (t is not null) EchoToWindow?.Invoke(t, window, color);
    }

    // ── Phantom-window guard (directed-echo to an unresolved variable) ────────
    // A resolved #echo/#link/#clear/#window target that still contains '$' is an
    // unresolved script variable: Genie substitutes every DEFINED $var, so a
    // surviving '$' means the name was undefined or mistyped (e.g. '>$Log' for
    // '>Log'). Genie 4 dropped such directed echoes silently — an unknown window
    // fell through both AddText branches in FormMain.ClassCommand_EchoText, so the
    // text was discarded. Genie 5's dock factory would instead manufacture a
    // phantom '$Log' window (GetOrCreatePluginWindow). We route the text to the
    // main window (never eat it, per the App's directed-echo contract) and warn
    // ONCE per bad name so the typo is visible and fixable, without breaking the
    // legitimate custom-window feature (real names like ExpMods/Data still work).
    private readonly HashSet<string> _warnedVarWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a resolved window target still holds an unresolved
    /// variable (contains '$'). No legitimate window is named with a '$'; a
    /// surviving one means variable substitution found no such <c>$var</c>.</summary>
    public static bool IsUnresolvedVarWindow(string? window)
        => !string.IsNullOrEmpty(window) && window.IndexOf('$') >= 0;

    /// <summary>If <paramref name="window"/> is an unresolved-variable target,
    /// warn once (naming the likely intended <c>&gt;name</c>) and return true so
    /// the caller drops the phantom target. False for normal windows.</summary>
    private bool IsPhantomVarWindow(string? window, string context)
    {
        if (!IsUnresolvedVarWindow(window)) return false;
        var name = window!.Trim();
        if (_warnedVarWindows.Add(name))
            RaiseEchoLine(
                $"[genie] {context} >{name}: '{name}' is an unresolved variable " +
                $"(did you mean '>{name.TrimStart('$')}'?). Declare custom windows with '#window add'.");
        return true;
    }

    private void RaiseEchoStyled(string text, string? color, bool mono)
    {
        var t = Plugins is null ? text : Plugins.DispatchEcho(text, "main");
        if (t is not null) EchoStyledLine?.Invoke(t, color, mono);
    }

    /// <summary>
    /// Raised by <c>#link</c> (Genie 4 clickable menu link). Args: (text,
    /// command, window?). The App renders <c>text</c> as a clickable link in the
    /// target window (null = main); clicking runs <c>command</c> through
    /// <see cref="ProcessInput"/>.
    /// </summary>
    public event Action<string, string, string?>? EchoLinkLine;

    /// <summary>
    /// Raised by <c>#clear [&gt;window]</c>. Arg is the target window name, or
    /// null for the main game window. The App empties the matching panel.
    /// </summary>
    public event Action<string?>? ClearWindow;

    /// <summary>
    /// Raised by <c>#window &lt;sub&gt; "name"</c> (Genie 4 named-window
    /// lifecycle). Args: (sub-command, window name). The App creates / shows /
    /// hides / clears the matching dock panel.
    /// </summary>
    public event Action<string, string>? WindowCommandRequested;

    // ── Constructor ────────────────────────────────────────────────────────────

    public GenieCore(
        string?         dataDirectoryOverride = null,
        AiConfig?       aiConfig      = null,
        ILoggerFactory? loggerFactory = null,
        bool?           gameThreadOverride = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _aiConfig      = aiConfig;

        // ── Config (data root fixed for the whole app session) ─────────────────
        // The root is the discovered AppData/portable location, or an explicit
        // override taken from the startup/first profile. It is NOT repointed per
        // connect — a later connect to a profile with a different data directory
        // warns (App-side) rather than relocating everything live.
        _localDir = new LocalDirectoryService("Genie5", AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
            _localDir.UseExplicitRoot(dataDirectoryOverride);

        Config = new GenieConfig(_localDir);
        Config.Load();

        // ── Game loop (#251) ───────────────────────────────────────────────────
        // Created before anything that posts to it; lives for the core's whole
        // life (spanning reconnects). `#config gamethread off` restores the
        // legacy inline/UI-thread pipeline at the next app launch.
        // gameThreadOverride bypasses the config: unit tests pass false so the
        // pipeline stays inline/deterministic on the test thread.
        if (gameThreadOverride ?? Config.GameThread)
        {
            _loop = new GameLoop();
            _loop.ItemFailed = ex =>
                RaiseEchoLine($"[genie] game-pipeline error (contained): {ex.Message}");
            _loop.Stalled += elapsed => PipelineStalled?.Invoke(elapsed);
        }

        // ── Persistent game state ───────────────────────────────────────────────
        // One instance for the core's whole life: the plugin state view, the script
        // globals mirror, the mapper adapter and AutoMapper.Skills all hold it by
        // reference, so each connect clears it in place (State.Reset) — never
        // replaces it — and those consumers keep working across reconnect.
        _state = new Models.GameState();

        // Plugin layer — read-only state view + manager (this GenieCore is the
        // IPluginHost). The view wraps the persistent state, so it survives reconnect.
        _pluginStateView = new Plugins.GameStateView(_state);
        Plugins          = new Plugins.PluginManager(this);
        // The Spell Timer, Experience and Time Tracker trackers are built in to
        // Core (Genie.Core/Extensions/Builtin), registered on the ScriptEngine's
        // ExtensionManager. The PluginManager handles only external DLL plugins
        // (e.g. InventoryView) loaded from {AppData}/Genie5/Plugins on connect.

        // Route caught regex match-timeouts (from the trigger/highlight/
        // substitute/gag safety layer) into the metrics collector so the overlay
        // can surface a per-component timeout count.
        RegexSafety.TimeoutSink = Metrics.RecordTimeout;

        // ── Command pipeline ───────────────────────────────────────────────────
        _commandQueue = new CommandQueue();
        _eventQueue   = new EventQueue();
        Commands      = new CommandEngine(Config, _commandQueue, _eventQueue, this);

        // ── Rule engines (dependency order: ClassEngine first) ─────────────────
        Classes    = new ClassEngine();
        Commands.Classes = Classes;        // wire #class command → engine
        Variables  = new VariableEngine(Commands);
        Commands.Variables = Variables;    // wire #var command → engine
        Aliases    = new AliasEngine(Commands);
        Aliases.Classes  = Classes;        // class-scope filter (Genie 4 parity)
        Commands.Aliases = Aliases;        // wire #alias command + bare-input expansion

        Triggers       = new TriggerEngineFinal(this, Commands);
        Triggers.Classes = Classes;
        Commands.Triggers = Triggers;        // wire #trigger command → engine

        Highlights     = new HighlightEngine();
        Highlights.Classes = Classes;
        Commands.Highlights = Highlights;    // wire #highlight command → engine

        NameHighlights = new NameHighlightEngine();
        Commands.Names = NameHighlights;     // wire #names command → engine

        Presets = new PresetEngine();   // seeded with Wrayth defaults
        Commands.Presets = Presets;     // wire #preset command → engine

        Substitutes    = new SubstituteEngine();
        Substitutes.Classes = Classes;
        Commands.Substitutes = Substitutes;  // wire #substitute command → engine
        Commands.DialogTracker = _dialogTracker;   // wire #dialogs → session inventory (#156)

        Gags           = new GagEngine();
        Gags.Classes   = Classes;
        Commands.Gags = Gags;                // wire #gag command → engine

        Macros = new MacroEngine();
        Macros.Classes  = Classes;           // class-scope filter (Genie 4 parity)
        Commands.Macros = Macros;            // wire #macro command → engine

        // ── Mapper ─────────────────────────────────────────────────────────────
        // Start with an empty zone; the user opts in to auto-mapping via the UI
        // (IsEnabled = false by default = lookup-only mode).
        AutoMapper     = new AutoMapperEngine(new MapZone { Name = "(unsaved)" });
        // The adapter that feeds AutoMapper from the live parser/state is built
        // per connect (BuildConnection); the engine itself persists.

        // Skill-weighted pathfinding: hand the engine a reference to the
        // live SkillStore so FindPath can filter out exits the character
        // can't take. LiveSkills is a member of the persistent state, so this
        // reference stays valid forever.
        AutoMapper.Skills = _state.LiveSkills;
        // CharacterClass + CharacterLevel are deliberately NOT set here. Setting
        // them once at construction would freeze them at the app-start values
        // (guild Unknown, circle 0), so class/level-gated exits would never
        // enforce (#95). They're refreshed from live state in SyncMapperGlobals
        // (runs once at build, then on every room change) once the parser has
        // seen the guild and `info` has filled in the circle.

        // ── Scripting ──────────────────────────────────────────────────────────
        _typeAhead = new TypeAheadSession();
        // All script-originated output flows through ScriptOutputLine — the
        // single "this came from a script" signal the UI uses to classify a
        // line as a Script line (governed by the "Script Lines" filter):
        //   • echo callback — "[script] X started/done", "[dbg:N]" traces,
        //     `echo`/`#echo` output, abort messages;
        //   • sendCommand   — the actual game commands a script issues
        //     (`put north`, …), so they're visible/toggleable as script
        //     activity (Genie 4 surfaces script-sent commands too).
        // Routed to ScriptOutputLine ONLY (not EchoLine) so the game window
        // shows each script line once; EchoLine is reserved for user-typed
        // command echoes and system diagnostics. The Scripts panel also reads
        // ScriptOutputLine for its own scrollback.
        // The send/echo delegates are wrapped in RunOnLoop: .cmd ticks already
        // run on the game loop (inline invoke), but .js scripts call them from
        // their own threads — posting marshals those host calls onto the loop,
        // which fixes the pre-existing JS-thread races for free (non-atomic
        // TypeAheadSession._inFlight, PluginManager.DispatchEcho concurrency).
        Scripts    = new ScriptEngine(
            scriptsDir:    Config.ScriptDir,
            typeAhead:     _typeAhead,
            sendCommand:   cmd => RunOnLoop(() =>
                           {
                               RaiseScriptOutput(cmd);
                               _typeAhead.NotifySent();
                               // Offline (no live connection) the game-bound send is
                               // dropped — a script can still run, set variables,
                               // toggle trigger classes and #connect (issue #88).
                               // Sends resume the moment a connection exists.
                               _ = _connection?.SendCommandAsync(cmd);
                           }),
            echo:          msg => RunOnLoop(() => RaiseScriptOutput(msg)),
            handleHashCmd: cmd => RunOnLoop(() => Commands.ProcessInput(cmd, interactive: false)),
            injectGameLine: line => RunOnLoop(() => InjectParsedLine(line)));

        // Script-issued `/commands` get the same client-side offer as typed
        // input: built-in extensions first (the engine calls
        // Extensions.DispatchSlashCommand itself), then external plugins here.
        // Without this leg a Genie 4 plugin's commands worked when typed but
        // leaked to the game when a .cmd script issued them, so every dependent
        // legacy script had to be rewritten — public #325 (Barnacus' Tracker
        // `send /timers start BLESS`). Plugins is constructed above, so the
        // capture is safe. Only '/'-prefixed script sends reach this.
        Scripts.PluginInput = cmd => Plugins.DispatchInput(cmd);

        // Live config for the runtime script settings (ScriptTimeout,
        // MaxGoSubDepth, AbortDupeScript, ScriptExtension).
        Scripts.Config = Config;

        // ── Script pump (#251: the core owns it when the game loop is on) ──────
        // ScheduleTick gives RT-precise one-shot wakeups; the 100 ms heartbeat
        // covers pause/delay/condition waits the engine doesn't self-schedule,
        // and pumps the #send/#event queues. Both run ON the loop, so
        // Commands.Tick's live State reads stop being cross-thread. With the
        // loop off, the App host wires its legacy DispatcherTimer pump instead
        // (it checks GameThreadEnabled).
        if (_loop is { } gameLoop)
        {
            Scripts.ScheduleTick = delay => gameLoop.PostDelayed(delay, () => Scripts.Tick());
            _heartbeat = gameLoop.PostRepeating(TimeSpan.FromMilliseconds(100), () =>
            {
                Scripts.Tick();
                Commands.Tick(
                    _state.Combat.InRoundTime,
                    _state.ActiveStatuses.Contains(Models.CharacterStatus.Webbed),
                    _state.ActiveStatuses.Contains(Models.CharacterStatus.Stunned));
            });
        }

        // $connected exists from app launch, seeded "0" (Genie 4 parity:
        // Lists/Globals.cs:882 seeds the reserved var "0" at startup). Without
        // this the global didn't exist until the first BuildConnection, so a
        // script started BEFORE the session's first connect — the natural way
        // to run a reconnect watcher — resolved $connected through the
        // persisted #var store (a stale imported connected=1 row read "1"
        // while fully offline) or, with no row, left it literal (public #294).
        // The connection layer owns it from here: ScriptGlobalsSync re-seeds
        // "0" per connect and _connectedVarSub tracks the live link.
        Scripts.Globals["connected"] = "0";

        // Wire game-state callbacks for RT-gated script pausing (closures over the
        // persistent state, so they stay valid across reconnect).
        Scripts.InRoundtime              = () => _state.Combat.InRoundTime;
        Scripts.RoundTimeRemainingSeconds = () => (int)Math.Ceiling(_state.Combat.RoundTimeRemaining);
        // $spelltime — seconds since the current spell was prepared (Genie 4).
        Scripts.SpellTimeSeconds          = () => (int)_state.Combat.SpellTimeSeconds;
        // $spellstarttime — RAW SERVER epoch of the prep start, 0 if none (#151).
        // Deliberately the raw twin, not the clock-offset-converted
        // SpellTimeStart: scripts compose it against the equally-raw $casttime
        // (#261 — the server-minus-server difference must stay skew-immune).
        Scripts.SpellStartTimeEpoch       = () => _state.Combat.SpellTimeStartServerEpoch;
        // $casttimeremaining — live countdown to fully-prepped (G4 Globals.cs:215:
        // spellpreptime − elapsed, clamped at 0; 0 with no spell held). Same
        // integer-epoch prepLen arithmetic as $spellpreptime (see
        // ScriptGlobalsSync.OnPrompt) so the two variables always agree.
        Scripts.CastTimeRemainingSeconds  = () =>
        {
            if (_state.Combat.SpellTimeStart is not { } prepStart) return 0;
            var end = _state.Combat.CastTimeEnd;
            if (end <= prepStart) return 0;
            var prepLen  = end.ToUnixTimeSeconds() - prepStart.ToUnixTimeSeconds();
            var elapsed  = (long)_state.Combat.SpellTimeSeconds;
            return (int)Math.Max(0L, prepLen - elapsed);
        };
        // #config ignorescriptwarnings — suppress non-fatal script parse advisories (#151).
        Scripts.WarningsSuppressed        = () => Config.IgnoreScriptWarnings;
        Scripts.EchoTo                   = (msg, win, color) => RunOnLoop(() => RaiseEchoToWindow(msg, win, color));
        Scripts.EchoStyled               = (msg, color, mono) => RunOnLoop(() => RaiseEchoStyled(msg, color, mono));
        // Named-window seam for the built-in trackers (Spell Timer / Experience /
        // Time Tracker), which re-render a whole dock panel each prompt. Same event
        // the App's ExperienceViewModel + generic PluginWindowViewModel consume.
        Scripts.SetWindow                = (win, content) => SetPluginWindow?.Invoke(win, content);
        // Let scripts read $name for a persistent #var value (Variables.Store) —
        // the script engine's own Globals hold only live game state + #tvar
        // session globals. Fixes #82 ($var set via `put #var …` now readable +
        // persistable like a typed #var).
        Scripts.UserVarLookup            = name => Variables?.Store.Get(name);

        // Client-side /command claim for CommandEngine-routed sends (queued
        // #send/#event items, alias expansions, trigger actions): same
        // extension dispatch that typed input (ProcessInput above) and script
        // puts (ScriptEngine.TrySlashCommand) already get. Genie 4 ran every
        // outbound send through the plugins' ParseInput at the send sink, so
        // e.g. `#send /track clear` was consumed there too (finding #9).
        Commands.ClientSlashCommand      = s => Scripts.Extensions.DispatchSlashCommand(s);

        // Honour the settings.cfg tracker toggles, and re-sync when they change.
        SyncTrackerToggles();
        Config.ConfigChanged += field => { if (field == Genie.Core.Config.ConfigFieldUpdated.Trackers) SyncTrackerToggles(); };

        // Honour the rule-engine master toggles (highlights / triggers /
        // substitutes / gags / aliases), and re-sync on File ▸ Master Toggles
        // or a typed `#config triggers off`.
        SyncMasterToggles();
        Config.ConfigChanged += field => { if (field == Genie.Core.Config.ConfigFieldUpdated.MasterToggles) SyncMasterToggles(); };

        // Re-filter the room's creature list the moment the monster-count
        // ignore list changes (Mobs-panel editor or typed `#config
        // monstercountignorelist`), and re-mirror $monstercount/$monsterlist
        // so scripts stay consistent with the panel. The Mobs panel subscribes
        // to the same ConfigChanged event later (at Attach), so it reads the
        // recomputed state — the same ordering guarantee the room-objs path
        // relies on.
        Config.ConfigChanged += field =>
        {
            if (field != Genie.Core.Config.ConfigFieldUpdated.MonsterIgnore) return;
            _stateEngine?.RecomputeCreatures();
            _globalsSync?.RefreshMonsterVars();
        };

        // ScriptGlobalsSync (the live-game-state → $globals mirror) binds to the
        // per-connection parser + the connect's character identity, so it is built
        // per connect in BuildConnection().

        // Mirror the AutoMapper's current location into the script globals so
        // scripts can read $roomid / $zoneid / $zonename / $roomnote (Genie 4
        // parity — the mapper-sourced reserved vars deferred from #45). $roomid is the MAPPER
        // node id (the numbers #goto and scripts compare against, e.g.
        // `if $roomid != 156`), distinct from the server's $gameroomid. Seeded
        // now for scripts that start before any move; refreshed on every
        // CurrentNodeChanged. Scripts.Globals is concurrent, so the mapper-thread
        // write is safe against script-thread reads.
        SyncMapperGlobals();
        AutoMapper.CurrentNodeChanged += SyncMapperGlobals;

        // Mapper room-resolution trace (`#config mapperdebug on`). Attached and
        // detached live so a player hunting a "the map stopped following me"
        // report can turn it on mid-session, walk the stretch, and turn it off
        // without reconnecting. Detaching nulls the sink so the engine pays
        // nothing (it never builds the message strings) when off.
        SyncMapperDebug();
        Config.ConfigChanged += field =>
        {
            if (field == Genie.Core.Config.ConfigFieldUpdated.MapperDebug) SyncMapperDebug();
        };

        // ── Live Audit (developer troubleshooting) ────────────────────────────
        // Off until `#audit on`. Tees raw XML + parsed events + live zone/room
        // into <LogDir>/live_audit.log so a collaborator can follow the session
        // without the user pasting XML/screenshots.
        _liveAudit = new Diagnostics.LiveAudit(
            System.IO.Path.Combine(Config.LogDir, "live_audit.log"),
            RawXmlStream, GameEvents,
            name => Scripts.Globals.TryGetValue(name, out var v) ? v : "");
        // Log every top-level command (incl. script-fired #goto) to the audit.
        Commands.CommandObserved = cmd => _liveAudit.Note("CMD", cmd);

        // ── Injuries auto-refresh (#18) ───────────────────────────────────────
        // Opt-in silent `health` poll that refines the nervous-system reading
        // (wound vs scar — the dialog image can't say). A coarse 5 s ticker
        // reads Config.InjuriesPollSeconds live each tick, so `#config
        // injuriespoll N` (or the panel picker) applies without a restart and
        // 0 keeps it fully idle. The tick itself gates on connection + prior
        // injuries-dialog data, so Wizard/plain-text sessions never poll.
        _injuriesPollTimer = new System.Threading.Timer(
            _ => InjuriesPollTick(), null,
            dueTime: TimeSpan.FromSeconds(5), period: TimeSpan.FromSeconds(5));
    }

    private System.Threading.Timer? _injuriesPollTimer;
    private DateTimeOffset _lastInjuriesPoll = DateTimeOffset.MinValue;
    private int _injuriesPollBusy;

    /// <summary>
    /// Live gate the host wires once: returns true while the Injuries panel is
    /// actually open. The poll exists solely to feed that panel, so when the
    /// window is closed there is no reason to send anything — the tick skips
    /// (and resumes on the configured cadence when the panel reopens). A
    /// callback rather than a pushed flag so it can never go stale; null
    /// (headless Core, TestHarness) means "no panel concept — allow".
    /// Read a cheap thread-safe snapshot here — the timer fires off the UI
    /// thread, so don't walk UI trees in this callback.
    /// </summary>
    public Func<bool>? InjuriesPanelVisible { get; set; }

    private void InjuriesPollTick()
    {
        var interval = Config.InjuriesPollSeconds;
        if (interval <= 0) return;                       // feature off (default)
        if (_connection is null || _parser is null) return;
        if (InjuriesPanelVisible is { } panelOpen && !panelOpen()) return;

        // Only poll sessions that have actually received the injuries dialog —
        // this is what makes the poll meaningful AND excludes Wizard mode
        // (plain text has no dialog, and no output-class brackets to gag).
        if (_state.Injuries.IsEmpty) return;

        if ((DateTimeOffset.UtcNow - _lastInjuriesPoll).TotalSeconds < interval) return;
        if (System.Threading.Interlocked.Exchange(ref _injuriesPollBusy, 1) == 1) return;

        _lastInjuriesPoll = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                // Arm the parser's suppression window FIRST so the response is
                // consumed silently, then send raw (no echo, no triggers —
                // this is not user input).
                _parser?.BeginSilentHealthWindow();
                await SendCommandAsync("health");
            }
            catch { /* disconnected mid-poll — next tick re-checks */ }
            finally { System.Threading.Volatile.Write(ref _injuriesPollBusy, 0); }
        });
    }

    /// <summary>
    /// Build (or rebuild) the per-connection layer for <paramref name="cfg"/>: the
    /// <see cref="GameConnection"/>, parser, state-engine, and all their event
    /// subscriptions, the mapper adapter, the script-globals mirror, and the AI
    /// buffer. Called by <see cref="ConnectAsync(ConnectionConfig, CancellationToken)"/>
    /// after <see cref="TeardownConnection"/> has disposed any previous connection.
    /// The engines, ScriptEngine, AutoMapper, Plugins and the relay subjects are
    /// NOT rebuilt — they persist for the whole app session.
    /// </summary>
    private void BuildConnection(ConnectionConfig cfg, bool reloadRules = true)
    {
        var lf = _loggerFactory;

        // Apply user-configured FE identifier (e.g. STORM vs GENIE). DR appears
        // to send richer click markup to clients identifying as STORM. The
        // setting persists in settings.cfg; toggle via `#config frontend storm`.
        if (!string.IsNullOrWhiteSpace(Config.FrontEndIdentifier) &&
            !cfg.FrontEndId.Equals(Config.FrontEndIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            cfg = cfg with { FrontEndId = Config.FrontEndIdentifier };
        }

        // Server-activity watchdog default (`#config activitytimeout`, seconds).
        // DR can end a session without closing the TCP socket — the peer keeps
        // ACKing keepalive probes while the game stream is dead, so nothing ever
        // fires: no Disconnected, no "disconnected" line, $connected stuck at 1
        // (observed live 2026-08-04/06: sessions the server logged off sat
        // "Connected" for 40+ min / all night). The server never goes quiet for
        // more than ~30s on a healthy link, so a minutes-scale silence watchdog
        // is a safe default. Callers that set their own timeout (TestHarness
        // DROP) keep it; Config 0 turns the watchdog off.
        if (cfg.ServerActivityTimeoutMs == 0 && Config.ActivityTimeout > 0)
            cfg = cfg with { ServerActivityTimeoutMs = Config.ActivityTimeout * 1000 };

        // ── Network stack (per connection) ───────────────────────────────────────
        var connection = new GameConnection(cfg,
            new SgeAuthClient(lf.CreateLogger<SgeAuthClient>()),
            lf.CreateLogger<GameConnection>(),
            _loop);
        var parser      = new DrXmlParser(lf.CreateLogger<DrXmlParser>());
        var stateEngine = new GameStateEngine(parser.GameEvents, _state,
            lf.CreateLogger<GameStateEngine>());
        // Live config for RoundTimeOffset (applied to each RoundTimeEvent).
        stateEngine.Config = Config;

        _connection  = connection;
        _parser      = parser;
        _stateEngine = stateEngine;

        // Re-apply the host-set connect-progress sinks to the fresh connection
        // (the host sets these once on the core; the connection is new each time).
        connection.Diag        = _connectionDiag;
        connection.VerboseDiag = _connectionVerboseDiag;

        // Wire raw XML → parser (timed as the Parse stage; no-op overhead when
        // the overlay is hidden because Metrics.Enabled is false).
        _parserFeed = connection.RawXmlStream.Subscribe(
            xml => Metrics.Time(PipelineStage.Parse, () => parser.Feed(xml)));

        // Mapper adapter — feeds the persistent AutoMapper from this parser/state.
        var mapperAdapter = new MapperGameStateAdapter(_state, parser.GameEvents);
        _mapperAdapter = mapperAdapter;
        AutoMapper.Attach(mapperAdapter);

        // Mirror live game state into Scripts.Globals so community scripts can read
        // $righthand / $stamina / $hidden / $gameroomid / the per-exit booleans
        // ($north etc.) and the rest of Genie 4's reserved-variable vocabulary.
        // Bound to this connection's parser + character identity; its ctor seeds the
        // initial globals (gamehost="", gameport="0", clientVersion, …).
        _globalsSync = new ScriptGlobalsSync(
            _state, Scripts.Globals, parser.GameEvents,
            gameCode:      cfg.GameCode,
            characterName: cfg.CharacterName,
            accountName:   cfg.AccountName,
            clientVersion: HostVersionString);

        // Skill-history recorder (Analytics). Needs a character identity to
        // key its folder; skipped for LIST/anonymous sessions and for replay
        // unless #config analyticsreplay is on (replay timestamps are fake).
        // #config analytics off gates writes live inside the recorder.
        _skillHistory?.Dispose();
        _skillHistory = null;
        if (!string.IsNullOrWhiteSpace(cfg.CharacterName)
            && !string.IsNullOrWhiteSpace(cfg.AccountName)
            && (cfg.Mode != ConnectionMode.DevReplay || Config.AnalyticsReplay))
        {
            var expExt = Scripts.Extensions.Extensions
                .OfType<Extensions.Builtin.ExperienceExtension>()
                .FirstOrDefault();
            if (expExt is not null)
            {
                var histLog = _loggerFactory.CreateLogger("Genie.Core.Analytics");
                _skillHistory = new Analytics.SkillHistoryRecorder(
                    Config, expExt, connection.StateStream,
                    cfg.CharacterName, cfg.AccountName,
                    isReplay: cfg.Mode == ConnectionMode.DevReplay,
                    log: msg => histLog.LogInformation("{Message}", msg));
            }
        }

        // ── Game event routing ─────────────────────────────────────────────────
        // Note: Scripts.OnGameLine already calls Extensions.DispatchGameLine internally —
        // no need to route TextEvents to the extension manager separately.
        _gameEventSub = parser.GameEvents.Subscribe(evt =>
        {
            // Built-in trackers consume fully-parsed events (Spell Timer's
            // percWindow TextEvents + ClearStreamEvent, the Experience tracker's
            // exp ComponentEvents) — reliable across the connection's tag-splitting
            // chunk boundaries, unlike raw XML.
            Scripts.OnGameEvent(evt);

            switch (evt)
            {
                case TextEvent te:
                    ProcessGameTextEvent(te);
                    break;

                case PromptEvent:
                    _typeAhead.NotifyConsumed();   // server caught up → free a type-ahead slot
                    // Unblock `move` BEFORE resuming RT/pause scripts (OnPrompt).
                    // We coalesce the room-change to the prompt (turn boundary)
                    // because DR emits NO NavEvent for "(**)" rooms (no server uid)
                    // — yet the player IS in a new room; the flag is set by NavEvent
                    // OR the room-title component below, so both uid and uid-less
                    // rooms wake the script (PR #92). Order matters: a `move` that a
                    // pause/wait-resumed script issues DURING OnPrompt must wait for
                    // the NEXT room change, not be unblocked by THIS turn's (which
                    // preceded it). Running OnRoomChanged first restores the pre-#92
                    // NavEvent-before-prompt order and avoids that premature unblock
                    // (harness MOVEORDER / the F1 finding); it does not regress
                    // normal walking, where a move parked from a prior turn unblocks
                    // here either way.
                    if (_roomChangedSincePrompt)
                    {
                        _roomChangedSincePrompt = false;
                        Scripts.OnRoomChanged();   // unblock `move` in running scripts
                    }
                    Scripts.OnPrompt();            // advance RT-gated script execution
                    Plugins.DispatchPrompt();
                    break;

                case NavEvent:
                    _roomChangedSincePrompt = true;
                    break;

                case ComponentEvent ce
                    when ce.ComponentId.Equals("room title", StringComparison.OrdinalIgnoreCase):
                    // Room arrival without a NavEvent (e.g. "(**)" no-uid rooms).
                    _roomChangedSincePrompt = true;
                    break;

                case FlagsReportEvent fr:
                    // Connect-time `flags` probe result (issue #29): warn if any
                    // stream-affecting flag is in an untested state.
                    HandleFlagsReport(fr);
                    break;

                case OpenDialogEvent od:
                    // #156 dialog capture journal: an unseen dialog's open tag
                    // is logged so its geometry/title accompany the content
                    // block below.
                    _dialogTracker.Observe(od);
                    (_dialogJournal ??= new Dialogs.DialogJournal(Config.LogDir))
                        .ObserveOpen(od.Id, od.RawXml);
                    break;

                case DialogDataEvent dd:
                    // First sighting of a dialog id → journal its raw block
                    // (the renderer fixtures for #156 accrue from normal play)
                    // and say so once.
                    _dialogTracker.Observe(dd);
                    if ((_dialogJournal ??= new Dialogs.DialogJournal(Config.LogDir))
                        .Observe(dd.DialogId, dd.RawXml))
                        RaiseEchoLine(
                            $"[dialogs] new server dialog '{dd.DialogId}' captured → Logs/{Dialogs.DialogJournal.FileName}");
                    break;
            }
        });

        // Plugins see raw XML chunks (Genie 4 ParseXML parity) for structured
        // data the typed events don't surface — e.g. <component id='exp X'>.
        _pluginXmlSub = connection.RawXmlStream.Subscribe(
            xml => Metrics.Time(PipelineStage.Plugins, () => Plugins.DispatchXml(xml)));

        // ── Ready-for-input signal ─────────────────────────────────────────────
        // StormFront / DevReplay: <settingsInfo/> is authoritative (see docs/SGE_PROTOCOL.md).
        // Wizard mode: no XML tags arrive, fire on TCP connect instead.
        // Lich proxy: Lich performed the login and consumed <settingsInfo/>
        // (and the whole login block, room components included) before we
        // attached — it never reaches us, so waiting on it means the auto-look
        // and connect script never run (issues #126/#127). Lich's detachable
        // listener only accepts clients after login completes, so TCP
        // Connected IS the ready signal in this mode.
        // Flags probe (issue #29) needs the XML stream — skip it in Wizard mode.
        _flagsProbeEligible = cfg.ClientMode != GameClientMode.Wizard;

        if (cfg.ClientMode == GameClientMode.Wizard)
        {
            _settingsInfoSub = connection.StateStream
                .Where(e => e.Kind == ConnectionEventKind.Connected)
                .Take(1)
                .Subscribe(_ => OnConnectReady());
        }
        else if (cfg.Mode == ConnectionMode.LichProxy)
        {
            _settingsInfoSub = connection.StateStream
                .Where(e => e.Kind == ConnectionEventKind.Connected)
                .Take(1)
                .Subscribe(_ => OnConnectReady(lichAttachParser: parser));
        }
        else
        {
            _settingsInfoSub = parser.GameEvents
                .OfType<SettingsInfoEvent>()
                .Take(1)
                .Subscribe(_ => OnConnectReady());
        }

        // ── $gamehost / $gameport ──────────────────────────────────────────────
        // Publish the resolved game endpoint into the script globals each time
        // the connection reports Connected (Genie 4 parity, unblocks #45). NOT
        // Take(1) — it must refresh on every reconnect (e.g. a #connect to a
        // different character). Seeded empty/0 by ScriptGlobalsSync.SeedInitial.
        _gameHostSub = connection.StateStream
            .Where(e => e.Kind == ConnectionEventKind.Connected)
            .Subscribe(_ =>
            {
                Scripts.Globals["gamehost"] = connection.ResolvedGameHost;
                Scripts.Globals["gameport"] = connection.ResolvedGamePort > 0
                    ? connection.ResolvedGamePort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0";

                // Seed the DR type-ahead limit from the account tier reported by
                // the SGE login: free/basic = 1 line, premium = 2 (premium grants
                // an extra type-ahead line). A premium+LTB account (3 lines) self-
                // calibrates up via the server cap message if it ever overruns.
                // Only DirectSGE knows the tier; Lich/DevReplay keep the default
                // and rely on cap-message calibration. Refreshes each (re)connect.
                if (cfg.Mode == ConnectionMode.DirectSGE)
                    _typeAhead.Limit = connection.AccountPremium ? 2 : 1;

                // Fresh session → clear any stale type-ahead count so the UI
                // counter starts empty.
                _typeAhead.ResetInFlight();
            });

        // ── $connected ─────────────────────────────────────────────────────────
        // Genie 4 parity (Game.cs GameSocket_EventConnected/EventDisconnected):
        // $connected tracks the live link, "1" while connected and "0" once it
        // drops — for ANY reason (clean close, server idle-out, dead link). Before
        // this, $connected was seeded "1" at construction and never reset, so a
        // script polling it after a drop saw a stale "1" forever (issue #87).
        // Plugins are notified too (OnVariableChanged), matching Genie 4's
        // VariableChanged broadcast.
        //
        // ORDERING: this must subscribe BEFORE _connStateRelaySub (end of this
        // method) — Subjects dispatch in subscription order, so the global is
        // already "1" when outside observers (App, harness) see Connected.
        // Since the per-connect seed became "0" (public #294), a relay
        // subscriber reading $connected on the Connected event depends on it.
        _connectedVarSub = connection.StateStream.Subscribe(e =>
        {
            var value = e.Kind == ConnectionEventKind.Connected ? "1" : "0";
            if (Scripts.Globals.TryGetValue("connected", out var current) && current == value)
                return;   // no change — don't churn the var or spam plugins
            Scripts.Globals["connected"] = value;
            Plugins.DispatchVariableChanged("connected", value);
        });

        // ── Per-character profile directory ────────────────────────────────────
        // Switch ConfigProfileDir to Profiles/{Char}-{Acct}/ so each character
        // has its own cfg files. First time a given character connects, any
        // legacy Config/*.cfg files seed the new directory automatically.
        // LIST mode and unauthenticated replay sessions fall back to the
        // shared Config dir.
        ProfileDirectory = Config.ApplyCharacterProfile(cfg.CharacterName, cfg.AccountName);

        // ── Auto-load persisted rule sets ──────────────────────────────────────
        // Genie 4 loads classes.cfg / aliases.cfg / variables.cfg /
        // highlights.cfg / triggers.cfg / substitutes.cfg / gags.cfg from
        // the profile directory at startup. We mirror that here so #*  save
        // persists across launches. Skip silently when a file doesn't exist
        // so a fresh install isn't greeted by a wall of "no … file" warnings.
        //
        // Gated by reloadRules: the host clears the persistent engines and reloads
        // only on a CHARACTER change (clear-then-load). A same-character reconnect
        // passes reloadRules=false so a running script's runtime-added rules survive
        // (issue #88 / #46 Phase 3). The host's own .json rule load is gated the
        // same way, so both formats stay in lockstep.
        //
        // Also gated by !HostOwnsRuleLoad (public #257): the App host performs
        // the ENTIRE two-layer load itself — global + profile, .json + .cfg,
        // with Character-over-Global layering — BEFORE this connect runs.
        // Replaying the profile .cfg here on top would clear each engine and
        // wipe the layered Global rules. Headless hosts (TestHarness, REPLAY)
        // leave the flag false and keep this Genie 4-parity load.
        if (reloadRules && !HostOwnsRuleLoad)
        {
            // On the loop (#251): BuildConnection runs on the connect caller's
            // thread while the heartbeat ticks the engines on the game thread.
            // FIFO puts these loads ahead of any game data from the dial below.
            RunOnLoop(() =>
            {
                var profileDir = Config.ConfigProfileDir;
                if (File.Exists(Path.Combine(profileDir, "classes.cfg")))
                    Commands.ProcessInput("#class load");
                if (File.Exists(Path.Combine(profileDir, "aliases.cfg")))
                    Commands.ProcessInput("#alias load");
                if (File.Exists(Path.Combine(profileDir, "variables.cfg")))
                    Commands.ProcessInput("#var load");
                if (File.Exists(Path.Combine(profileDir, "highlights.cfg")))
                    Commands.ProcessInput("#highlight load");
                if (File.Exists(Path.Combine(profileDir, "triggers.cfg")))
                    Commands.ProcessInput("#trigger load");
                if (File.Exists(Path.Combine(profileDir, "substitutes.cfg")))
                    Commands.ProcessInput("#substitute load");
                if (File.Exists(Path.Combine(profileDir, "gags.cfg")))
                    Commands.ProcessInput("#gag load");
                if (File.Exists(Path.Combine(profileDir, "macros.cfg")))
                    Commands.ProcessInput("#macro load");
            });
        }

        // ── AI buffer (optional) ───────────────────────────────────────────────
        if (_aiConfig is not null)
        {
            AiBuffer = new AiContextBuffer(
                connection.AiRawStream,
                _state,
                _aiConfig,
                lf.CreateLogger<AiContextBuffer>());
        }

        // ── Relay feeds (LAST — so the internal consumers above run before UI) ───
        // Forward THIS connection's streams into the persistent relay subjects the
        // App subscribed to once (and which survive every reconnect). onError is
        // swallowed and OnCompleted is intentionally NOT forwarded, so tearing down
        // a connection can never complete/error the relays out from under live
        // subscribers. These three subs are disposed first in TeardownConnection.
        //
        // TextEvents are NOT forwarded here — ProcessGameTextEvent relays them
        // itself after the plugin transform (rewrite/gag), so UI consumers only
        // ever see the plugin-approved text. Everything else passes through raw.
        _gameEventsRelaySub = parser.GameEvents.Subscribe(
            e => { if (e is not TextEvent) _gameEventsRelay.OnNext(e); }, static _ => { });
        _rawXmlRelaySub     = connection.RawXmlStream.Subscribe(x => _rawXmlRelay.OnNext(x), static _ => { });
        // SentCommandStream is the one choke point every outbound command passes
        // through — GameConnection publishes it after the flush, in wire order,
        // whatever the source (typed, alias, trigger action, script put/send,
        // JS genie.put, link click). That makes it the correct home for the
        // plugins' observe-only OnCommandSent hook, whose contract reads "a
        // command was actually sent to the game (user, alias, script, or link
        // click) — after OnInput transforms". PluginManager.DispatchCommand had
        // existed since the plugin host landed but had no caller anywhere, so
        // OnCommandSent had never once fired for any plugin (found while fixing
        // public #325). Each() swallows a throwing plugin, so a bad hook cannot
        // break the relay.
        _sentRelaySub       = connection.SentCommandStream.Subscribe(x =>
        {
            Plugins.DispatchCommand(x);
            _sentRelay.OnNext(x);
        }, static _ => { });
        _connStateRelaySub  = connection.StateStream.Subscribe(s => _connStateRelay.OnNext(s), static _ => { });
    }

    // ── ICommandHost ───────────────────────────────────────────────────────────

    void ICommandHost.Echo(string text) => RaiseEchoLine(text);

    void ICommandHost.EchoTo(string text, string? window, string? color)
        => RaiseEchoToWindow(text, window, color);

    void ICommandHost.EchoMain(string text, string? color, bool mono)
        => RaiseEchoStyled(text, color, mono);

    void ICommandHost.EchoLink(string text, string command, string? window)
        => EchoLinkLine?.Invoke(text, command, IsPhantomVarWindow(window, "#link") ? null : window);

    void ICommandHost.EchoClear(string? window)
    {
        if (IsPhantomVarWindow(window, "#clear")) return;   // don't clear Main for a typo'd target
        ClearWindow?.Invoke(window);
    }

    void ICommandHost.WindowCommand(string sub, string window)
    {
        if (IsPhantomVarWindow(window, "#window " + sub)) return;   // refuse to create a $-named window
        WindowCommandRequested?.Invoke(sub, window);
    }

    void ICommandHost.SetWindowComment(string window, string comment)
        => WindowCommentRequested?.Invoke(window, comment);

    /// <summary>Raised by <c>#comment &lt;window&gt; &lt;text&gt;</c> (Genie 4
    /// title annotation). Args: (window, comment); a blank comment clears it.
    /// The App sets the matching dock panel's title bar.</summary>
    public event Action<string, string>? WindowCommentRequested;

    void ICommandHost.SetStatusBar(string text, int index)
        => StatusBarRequested?.Invoke(text, index);

    /// <summary>
    /// Raised by <c>#statusbar</c> / <c>#status</c>. Carries the text and the
    /// 1-10 slot index. The App routes it to the ten positional slots under
    /// the vitals Status Bar (#111); Console / headless builds with no
    /// subscriber drop it (Genie 4 parity — a status write with no bar is a
    /// silent no-op).
    /// </summary>
    public event Action<string, int>? StatusBarRequested;

    // ── Unexpanded-variable-in-outgoing-command guard ────────────────────────
    // Sibling of the phantom-window echo guard (IsUnresolvedVarWindow): there a
    // '$'-var leaked into an #echo TARGET; here a %var/$var leaks into a game
    // COMMAND. A command reaching SendToGame that still holds a %name/$name token
    // is an UNDEFINED variable substitution left literal — e.g. a script's
    // `put go %offtransport` sent verbatim because `%offtransport` was never set,
    // which DR answers with the unhelpful "What were you referring to?". We warn
    // once per distinct bad command so the cause is explicit instead of silent.
    private readonly HashSet<string> _warnedRawVarCommands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="command"/> still contains a
    /// <c>%name</c>/<c>$name</c> token — an unexpanded (undefined) variable about
    /// to be sent to the game literally. Detects a <c>%</c>/<c>$</c> immediately
    /// followed by a variable-name char (letter or underscore); a bare
    /// <c>%</c>/<c>$</c> or one before a digit is left alone to avoid
    /// false-positives on literal text. Outputs the first offending token.</summary>
    public static bool ContainsUnexpandedVar(string? command, out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(command)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(command, @"[%$][A-Za-z_][A-Za-z0-9_.]*");
        if (!m.Success) return false;
        token = m.Value;
        return true;
    }

    void ICommandHost.SendToGame(string text, bool userInput, string origin, string? echoOverride)
    {
        // Local echo of user-typed commands so the player can see what they sent.
        // Script/alias commands are not echoed here — scripts have their own echo path.
        // When echoOverride is provided (e.g. UI link click passing the friendly
        // display name), use it instead of the raw text so the user sees
        // "get a tapered cutlass" instead of "get #49489411 in #49489410".
        if (userInput)
            RaiseEchoLine(echoOverride ?? text);

        // Genie 4 mycommandchar (Game.cs SendText: `!sText.StartsWith(
        // cMyCommandChar)` gates the socket write, lastcommand, and activity
        // stamp): a line starting with the char is echoed and fed to triggers
        // (below) but NEVER reaches the game — "for trigger systems and such".
        // Pairs with mm_train-style typed-reply capture: `#config
        // mycommandchar ~` keeps a "~armband" reply out of DR (no "Please
        // rephrase that command.").
        var localOnly = text.Length > 0 && Config is not null && text[0] == Config.MyCommandChar;

        if (!localOnly)
        {
            // Let the mapper observe the outgoing command — it parses movement
            // directions ("n", "go bridge", etc.) so it can correlate the
            // subsequent room change with the direction we just moved.
            AutoMapper.OnCommandSent(text);

            // Genie 4 reserved $lastcommand — the last line sent to the game.
            Scripts.Globals["lastcommand"] = text;

            _typeAhead.NotifySent();

            // Diagnostic: a command going to the game with a surviving %var/$var
            // is an undefined variable sent literally. Warn once per distinct bad
            // command (default on; #config warnrawvars false to silence).
            if ((Config?.WarnRawVars ?? true) && ContainsUnexpandedVar(text, out var rawVar)
                && _warnedRawVarCommands.Add(text.Trim()))
                RaiseEchoLine(
                    $"[genie] sent '{text.Trim()}' with an unexpanded variable '{rawVar}' — " +
                    $"it is undefined, so the game got it literally (that's the \"What were you " +
                    $"referring to?\" cause). Silence with #config warnrawvars false.");

            // Offline (no live connection) the send is dropped — user input is
            // still echoed and observed by the mapper above, so the command bar
            // stays usable while disconnected (issue #88).
            _ = _connection?.SendCommandAsync(text);
        }

        // Genie 4 triggeroninput (Config.cs:19 default TRUE; FormMain:4127):
        // SENT text also runs through the trigger/action pipeline, so menu
        // scripts can capture typed input — mm_train's
        // `action (input) var input $1;… when ~(.*)` fires on a typed "~500".
        // Scripts + global triggers only (Genie 4's ParseTriggers): plugins
        // already receive typed input via Plugins.DispatchInput in
        // ProcessInput. Guarded like Genie 4's m_bParseTriggers so a trigger
        // action that sends text can't re-fire triggers for its own send.
        if ((Config?.TriggerOnInput ?? true) && !_triggerOnInputActive)
        {
            _triggerOnInputActive = true;
            try
            {
                Scripts.OnGameLine(text);
                if (!(Config?.ParseGameOnly ?? false))
                    Triggers.ProcessLine(text);
            }
            finally { _triggerOnInputActive = false; }
        }
    }

    /// <summary>Re-entrancy guard for the triggeroninput feed above — a
    /// trigger/action that sends text must not re-fire triggers for that
    /// same sent text (Genie 4 FormMain.m_bParseTriggers).</summary>
    private bool _triggerOnInputActive;

    void ICommandHost.RunScript(string text)
    {
        var parts = ArgumentParser.ParseArgs(text);
        if (parts.Count == 0) return;
        Scripts.TryStart(parts[0], [.. parts.Skip(1)]);
    }

    /// <summary>
    /// Server-ready handler (fires once per connect: on <c>&lt;settingsInfo/&gt;</c>
    /// for StormFront/DevReplay, on TCP Connected for Wizard mode). Sends the
    /// initial <c>look</c>, then launches <see cref="GenieConfig.ConnectScript"/>
    /// if one is configured (Genie 4 parity). A bad/missing connect-script name
    /// must never break the connect, so failures are swallowed.
    /// </summary>
    /// <summary>
    /// Parse the server's type-ahead cap report — "(Sorry,) you may only type
    /// ahead N line(s)." — and set the shared <see cref="TypeAheadSession.Limit"/>
    /// to N. DR phrases the single-line case as the word "one"; multi-line as a
    /// digit. This is the authoritative correction for a mis-seeded limit.
    /// </summary>
    private void CalibrateTypeAhead(string line)
    {
        const string marker = "type ahead ";
        int i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return;
        var rest = line[(i + marker.Length)..].TrimStart();
        // Grab the token after the marker (a digit run or a number word).
        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]) && rest[end] != '.') end++;
        var token = rest[..end];

        int cap = token.ToLowerInvariant() switch
        {
            "one"   => 1,
            "two"   => 2,
            "three" => 3,
            "four"  => 4,
            _ => int.TryParse(token, out var n) ? n : 0,
        };
        if (cap >= 1 && cap != _typeAhead.Limit)
            _typeAhead.Limit = cap;
    }

    /// <summary>
    /// The per-line game-text pipeline for a live <see cref="TextEvent"/>.
    /// Genie 4 order — plugins FIRST (its <c>ParsePluginText</c> ran before
    /// <c>TriggerParse</c>), then scripts, triggers, and finally the relay to
    /// UI consumers — and, as a deliberate Genie 5 extension, the plugin
    /// transform is HONORED: a rewrite feeds the modified text to scripts,
    /// triggers, and the display; a <c>null</c> gags the line everywhere
    /// downstream. A rewritten line drops its link/bold/preset spans (their
    /// offsets are meaningless in the new text). <see cref="GameStateEngine"/>,
    /// <see cref="Scripting.ScriptGlobalsSync"/>, the mapper, and the built-in
    /// extensions all run off the RAW parser events, so a plugin gag can never
    /// corrupt game state — it is authoritative over what is *seen*, not what
    /// *happened*. Type-ahead calibration also reads the raw text: it is a
    /// safety net the server owns, not something a plugin may disable.
    /// </summary>
    internal void ProcessGameTextEvent(TextEvent te)
    {
        // Self-calibrate the type-ahead limit from the server's own cap
        // report ("(Sorry,) you may only type ahead N line(s).") so a
        // wrong seed (e.g. a Lich/DevReplay default, or premium+LTB) is
        // corrected by the authority. Cheap guard before any parsing.
        if (te.Text.IndexOf("type ahead", StringComparison.OrdinalIgnoreCase) >= 0)
            CalibrateTypeAhead(te.Text);

        // Each per-line consumer is timed into its own stage so the overlay
        // shows which feature is eating the frame. The Time wrapper is a
        // zero-overhead passthrough while disabled.
        string? transformed = te.Text;
        Metrics.Time(PipelineStage.Plugins,
                     () => transformed = Plugins.DispatchGameText(te.Text, te.Stream));
        if (transformed is null) return;                    // gagged — nothing downstream
        var ev = string.Equals(transformed, te.Text, StringComparison.Ordinal)
            ? te
            : te with { Text = transformed, Links = null, BoldSpans = null, PresetSpans = null };

        Metrics.Time(PipelineStage.Scripts,  () => Scripts.OnGameLine(ev.Text));   // match/waitfor + EXP/info trackers
        // ParseGameOnly (Genie 4 parity): when on, fire triggers only on
        // the main game stream, not secondary stream-windows (thoughts,
        // logons, combat, …). Default off → triggers see every stream.
        //
        // DR double-sends a talk/whispers line — once on its own stream,
        // once again as a bare `main` copy tagged ev.DuplicateEcho (see
        // TextEvent). Left ungated that's a double trigger pass with
        // ParseGameOnly off (both copies satisfy "every stream"), so the
        // duplicate is skipped there — the stream-tagged original already
        // covered it. With ParseGameOnly on, the duplicate is instead the
        // ONLY copy on "main", so it's exactly the Main-targeted line Genie 4
        // fires TriggerParse on for a talk line — skipping it there would
        // silently drop speech triggers. Either mode nets exactly one pass.
        Metrics.Time(PipelineStage.Triggers, () =>
        {
            var parseGameOnly = Config?.ParseGameOnly ?? false;
            var fires = parseGameOnly ? ev.Stream == "main" : !ev.DuplicateEcho;
            if (fires)
                Triggers.ProcessLine(ev.Text);                                     // user-defined triggers
        });
        _gameEventsRelay.OnNext(ev);                        // UI consumers see the approved text
    }

    /// <summary>
    /// Inject a synthetic line into the full per-line pipeline as if the server
    /// had emitted it — the Genie 4 <c>#parse</c> primitive. Runs the same legs
    /// as a live <see cref="TextEvent"/> in the same order — plugins first (the
    /// transform is honored: rewrite feeds scripts/triggers, <c>null</c> gags
    /// the injection; Genie 4 fed #parse to plugins observe-only and LAST), then
    /// scripts' waitfor/match + built-in trackers, then the global user-trigger
    /// list — but deliberately omits the game-only bits: it never echoes to a
    /// window, never reaches the game socket, and skips type-ahead calibration.
    /// Reached from a scripted <c>#parse</c> (via the ScriptEngine injector
    /// callback) and a typed <c>#parse</c> from the command bar (via the command
    /// host). The argument arrives already <c>$</c>/<c>%</c>-expanded by the
    /// caller, matching Genie 4's <c>ParseGlobalVars</c> on the #parse argument.
    /// </summary>
    /// <summary>Re-entry depth of <see cref="InjectParsedLine"/>: a #parse whose
    /// injected line fires a trigger/action that #parses again nests
    /// synchronously with a fresh tick budget each level — unbounded recursion
    /// (deep-dive Phase 0). Single-threaded (loop/pipeline thread), so a plain
    /// field suffices.</summary>
    private int _parseDepth;
    private const int MaxParseDepth = 16;

    public void InjectParsedLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (_parseDepth >= MaxParseDepth)
        {
            RaiseEchoLine($"[genie] #parse: re-entry depth limit ({MaxParseDepth}) reached — " +
                          $"dropped '{line}' (a #parse trigger loop?).");
            return;
        }
        _parseDepth++;
        try
        {
            // Treat as the main stream so the ParseGameOnly gate (below) always
            // lets injected text reach triggers — Genie 4 fed #parse to triggers
            // unconditionally.
            var transformed = Plugins.DispatchGameText(line, "main");
            if (transformed is null) return;                    // plugin gagged the injection
            Scripts.OnGameLine(transformed);                    // scripts (waitfor/match) + built-in trackers + JS waiters
            if (!(Config?.ParseGameOnly ?? false))
                Triggers.ProcessLine(transformed);              // global user-defined triggers
        }
        finally { _parseDepth--; }
    }

    private void OnConnectReady(DrXmlParser? lichAttachParser = null)
    {
        // Lich attach (#126/#127): the login block Lich consumed was the only
        // unprompted source of room components and character identity. Arm the
        // parser's one-shot captures, then reconstruct both — `look` re-emits
        // the room as display text (folded into synthetic components while the
        // seed is armed), and the `,eq` ident query asks Lich itself for the
        // bare character name once XMLData.name is populated (`info` can't be
        // used: its Name field embeds optional pre-titles and surname).
        if (lichAttachParser is not null)
        {
            lichAttachParser.BeginRoomSeedCapture();
            lichAttachParser.BeginLichIdentWindow();
        }
        var __ = SendConnectSeedAsync(lichAttach: lichAttachParser is not null);

        var connectScript = Config.ConnectScript;
        if (!string.IsNullOrWhiteSpace(connectScript))
        {
            try { Scripts.TryStart(connectScript.Trim(), Array.Empty<string>()); }
            catch { /* never let a connect-script error abort the session */ }
        }
    }

    /// <summary>The connect-ready sends, sequenced so the ident query can't
    /// interleave with `look` on the socket. Failures are swallowed — a link
    /// that dies mid-seed is reported by the disconnect path, not here.</summary>
    private async Task SendConnectSeedAsync(bool lichAttach)
    {
        try
        {
            if (_connection is null) return;
            await _connection.SendCommandAsync("look").ConfigureAwait(false);
            if (lichAttach)
                // XMLData.name is often still nil for a beat after FE attach (fresh
                // Lich launch / before game XML fills identity). Bare
                // `"…" + XMLData.name` raises NilClass#to_str in Lich ExecScript.
                // Wait briefly (≤3s), then respond only when we have a non-empty name.
                await _connection.SendCommandAsync(
                        ",eq 30.times{ break unless XMLData.name.to_s.empty?; sleep 0.1 }; " +
                        "n=XMLData.name.to_s; respond \"GENIE5-IDENT #{n}\" unless n.empty?")
                    .ConfigureAwait(false);

            // Flag-state probe (issue #29): silently read the DR `flags` verb and
            // warn if any stream-affecting flag is in a state the parser wasn't
            // verified against. Arm the parser's suppression window FIRST so the
            // response never displays, then send. Skipped in Wizard/plain-text
            // mode and when #config flagscheck is off.
            if (Config.FlagsCheck && _flagsProbeEligible && _parser is not null)
            {
                _parser.BeginFlagsCaptureWindow();
                await _connection.SendCommandAsync("flags").ConfigureAwait(false);
            }
        }
        catch { /* connection dropped during the seed — nothing to do */ }
    }

    /// <summary>False in Wizard/plain-text mode (no XML stream to verify against);
    /// set per connect in <see cref="BuildConnection"/>. Gates the connect-time
    /// flags probe together with <see cref="GenieConfig.FlagsCheck"/>.</summary>
    private bool _flagsProbeEligible;

    /// <summary>The stream-affecting DR flags and the state the parser is verified
    /// against (issue #29 — captured from a live StormFront session 2026-07-09).
    /// A connect-time probe warns when any of these differs; the other ~24 flags
    /// are cosmetic/social and don't change the XML the parser reads. Keyed
    /// case-insensitively on DR's own spelling.</summary>
    private static readonly IReadOnlyDictionary<string, bool> VerifiedFlagBaseline =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["RoomNames"]       = true,    // room title component present
            ["Description"]     = true,    // room-desc <preset> present
            ["RoomBrief"]       = false,   // full (not brief) room text
            ["BattleBrief"]     = true,
            ["CombatBrief"]     = true,
            ["MonsterBold"]     = true,    // creatures wrapped in <pushBold> (ties to #160)
            ["StatusPrompt"]    = true,    // status prepended to the prompt line
            ["ConciseThoughts"] = false,
            ["HidePreStrings"]  = false,   // other players' titles shown in room LOOKs
            ["HidePostStrings"] = false,
            ["ShowRoomID"]      = true,    // room IDs shown on LOOK
        };

    /// <summary>Compare a <see cref="FlagsReportEvent"/> against
    /// <see cref="VerifiedFlagBaseline"/> and echo one advisory line listing any
    /// stream-affecting flag in an untested state. Silent when everything matches.</summary>
    private void HandleFlagsReport(FlagsReportEvent report)
    {
        var deviations = new List<string>();
        foreach (var (flag, expected) in VerifiedFlagBaseline)
        {
            if (report.Flags.TryGetValue(flag, out var actual) && actual != expected)
                deviations.Add($"{flag} is {(actual ? "ON" : "OFF")} (verified {(expected ? "ON" : "OFF")})");
        }
        if (deviations.Count == 0) return;   // all stream-affecting flags as expected — stay quiet

        RaiseEchoLine(
            $"[genie] flags check — parser input differs from the tested baseline: {string.Join("; ", deviations)}. " +
            "Room/combat/prompt parsing may misbehave; use FLAG <name> ON|OFF to restore, or #config flagscheck off to silence.");
    }

    /// <summary>Apply the settings.cfg tracker toggles (spelltimer / showexperience /
    /// showtimetracker) to the built-in tracker extensions' Enabled flags. Matching
    /// is by extension Name; unknown names are ignored.</summary>
    private void SyncTrackerToggles()
    {
        foreach (var ext in Scripts.Extensions.Extensions)
        {
            switch (ext.Name)
            {
                case "SpellTimer":  ext.Enabled = Config.ShowSpellTimer;  break;
                case "Experience":
                    ext.Enabled = Config.ShowExperience;
                    // Density (and any other render-affecting setting) changed: re-render
                    // now so the View → Density menu / #config give instant feedback.
                    if (ext is Extensions.Builtin.ExperienceExtension exp) exp.Refresh();
                    break;
                case "TimeTracker": ext.Enabled = Config.ShowTimeTracker; break;
            }
        }
    }

    /// <summary>Apply the settings.cfg master toggles to the rule engines'
    /// Enabled flags. Rules stay loaded; the engines just skip applying them.</summary>
    private void SyncMasterToggles()
    {
        Highlights.Enabled  = Config.EnableHighlights;
        Triggers.Enabled    = Config.EnableTriggers;
        Substitutes.Enabled = Config.EnableSubstitutes;
        Gags.Enabled        = Config.EnableGags;
        Aliases.Enabled     = Config.EnableAliases;
    }

    void ICommandHost.StopScript(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // No name → stop the most-recently-started script (Genie 4 #stop
            // semantics). With nothing running, the call is a quiet no-op.
            var last = Scripts.Instances.LastOrDefault();
            if (last is not null) Scripts.Stop(last.Name);
        }
        else
        {
            Scripts.Stop(name);
        }
    }

    void ICommandHost.ClearSendQueue() => Scripts.ClearPendingSends();

    void ICommandHost.StopAllScripts() => Scripts.StopAll();

    void ICommandHost.PauseAllScripts() => Scripts.PauseAll();

    void ICommandHost.ResumeAllScripts() => Scripts.ResumeAll();

    void ICommandHost.PauseScript(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) Scripts.PauseAll();
        else                                 Scripts.PauseScript(name);
    }

    void ICommandHost.ResumeScript(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) Scripts.ResumeAll();
        else                                 Scripts.ResumeScript(name);
    }

    void ICommandHost.SetTraceLevelAll(int level)
    {
        // Clamp to the same 0-10 range the engine recognises elsewhere; -1 turns it off.
        if (level < 0) level = 0;
        if (level > 10) level = 10;
        foreach (var inst in Scripts.Instances)
            inst.DebugLevel = level;
    }

    void ICommandHost.PauseOrResumeScript(string? name) => Scripts.PauseOrResume(name);

    void ICommandHost.ReloadScript(string? name) => Scripts.RequestReload(name);

    void ICommandHost.ShowScriptVars(string? name, string filter)
        => EchoDumpLines(Scripts.VarsLines(name, filter ?? string.Empty), name);

    void ICommandHost.ShowScriptTrace(string? name)
        => EchoDumpLines(Scripts.TraceDumpLines(name), name);

    private void EchoDumpLines(IReadOnlyList<string> lines, string? name)
    {
        if (lines.Count == 0)
        {
            RaiseEchoLine(string.IsNullOrEmpty(name) || name!.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? "No scripts running."
                : $"No running script named {name}.");
            return;
        }
        foreach (var l in lines) RaiseEchoLine(l);
    }

    void ICommandHost.SetScriptDebugLevel(int level, string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // Per-name so each script echoes and fires DebugLevelChanged (the
            // Script Bar chips track the live level) — unlike the quiet
            // #traceall path above, which predates the event.
            foreach (var n in Scripts.RunningScriptNames())
                if (!Scripts.IsJavaScript(n)) Scripts.SetTrace(n, level);
        }
        else
        {
            Scripts.SetTrace(name!, level);
        }
    }

    /// <summary>Raised when <c>#script explorer</c> asks the App layer to open
    /// the Script Manager window. No subscriber (Console/headless build) falls
    /// back to an explanatory echo.</summary>
    public event Action? ScriptExplorerRequested;

    void ICommandHost.ShowScriptExplorer()
    {
        if (ScriptExplorerRequested is null)
            RaiseEchoLine("The Script Explorer requires the App UI.");
        else
            ScriptExplorerRequested.Invoke();
    }

    IReadOnlyList<string> ICommandHost.ScriptStatusLines(string? filter)
        => Scripts.StatusLines(filter);

    IReadOnlyList<string> ICommandHost.RunningScripts()
        => Scripts.RunningScriptNames();

    void ICommandHost.SetGlobalVariable(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Scripts.Globals[name] = value ?? string.Empty;
    }

    void ICommandHost.RemoveGlobalVariable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Scripts.Globals.TryRemove(name, out _);
    }

    // ConcurrentDictionary is itself an IReadOnlyDictionary; enumeration is
    // thread-safe, so #var can list these while the parser thread updates them.
    IReadOnlyDictionary<string, string> ICommandHost.GetGlobalVariables() => Scripts.Globals;

    /// <summary>
    /// <c>$name</c> expansion for the command pipeline (typed input, trigger
    /// actions, script-forwarded <c>#</c> commands — everything that flows
    /// through <c>CommandEngine.ProcessInput</c>). Delegates to
    /// <see cref="ScriptEngine.ExpandGlobalVars"/> so command text resolves
    /// with the SAME Genie 4-parity rules as script text: globals then user
    /// <c>#var</c> values, the reserved clock vars ($date/$time/…), and the
    /// shrink-search with the word-boundary rule. The previous hand-rolled
    /// single-pass expander here parsed greedily and never shrank, so a
    /// dotted filename arg like <c>#log &gt;Ranklog-$charactername.txt</c>
    /// looked up the undefined "charactername.txt" and stayed literal —
    /// creating a file literally named with the <c>$var</c>. Unknown names
    /// still stay literal (Genie 4 <c>ParseGlobalVars</c>); <c>%locals</c>
    /// are untouched at this level.
    /// </summary>
    string ICommandHost.ExpandVariables(string text)
        => Scripts.ExpandGlobalVars(text);

    void ICommandHost.EditScript(string name)
    {
        // The actual editor launch lives in the App layer (cross-platform
        // Process.Start with the user's configured EditorPath). We just
        // raise the event so the App-layer subscriber can do the work.
        // If no one is listening (Console / headless test harness), echo a
        // diagnostic so callers know nothing happened.
        if (EditScriptRequested is null)
            RaiseEchoLine($"[editor] no editor host wired — '{name}' not opened.");
        else
            EditScriptRequested.Invoke(name);
    }

    /// <summary>
    /// Raised when something asks to open a script in the external editor
    /// — <c>#edit foo</c> from the command bar, or the pencil button on
    /// the Script Bar. The App subscribes and handles the actual file
    /// resolution + <c>Process.Start</c> with the user's configured editor.
    /// </summary>
    public event Action<string>? EditScriptRequested;

    void ICommandHost.LayoutCommand(string args)
    {
        // Layout storage + dock manipulation are App-layer concerns; forward
        // the raw args to the subscriber. Console builds with no handler just
        // get a diagnostic.
        if (LayoutCommandRequested is null)
            RaiseEchoLine("[layout] no layout host wired (Console build).");
        else
            LayoutCommandRequested.Invoke(args);
    }

    /// <summary>
    /// Raised by <c>#layout …</c> from the command bar. Carries the raw
    /// argument string (verb + args). The App subscribes and performs the
    /// save/load/list/default/delete against its layout stores and dock state.
    /// </summary>
    public event Action<string>? LayoutCommandRequested;

    void ICommandHost.PluginCommand(string args)
    {
        if (PluginCommandRequested is null)
            RaiseEchoLine("[plugin] no plugin host wired (Console build).");
        else
            PluginCommandRequested.Invoke(args);
    }

    /// <summary>Raised by <c>#plugin …</c>. The App handles list/enable/disable/
    /// load/unload/reload against <see cref="Plugins"/> and the Plugins folder.</summary>
    public event Action<string>? PluginCommandRequested;

    void ICommandHost.ConfigCommand(string args)
    {
        if (ConfigCommandRequested is null)
            RaiseEchoLine("[config] no config host wired (Console build).");
        else
            ConfigCommandRequested.Invoke(args);
    }

    /// <summary>
    /// Raised by <c>#config</c> / <c>#set</c> / <c>#setting</c> / <c>#settings</c>.
    /// The App handles the bare form (open Configuration dialog) plus the
    /// <c>save</c> / <c>load</c> (alias <c>reload</c>) / <c>list</c> /
    /// <c>edit</c> subforms and the default <c>#config &lt;key&gt; [value]</c>
    /// read/set form — all against settings.cfg (<c>GenieConfig</c>), not
    /// display.json.
    /// </summary>
    public event Action<string>? ConfigCommandRequested;

    void ICommandHost.MapperGoto(string args)
    {
        // Mapper room resolution + the attended walk live in the App layer;
        // forward the raw destination argument. Console builds with no handler
        // just get a diagnostic (no UI mapper to drive).
        if (MapperGotoRequested is null)
            RaiseEchoLine("[goto] no mapper host wired (Console build).");
        else
            MapperGotoRequested.Invoke(args);
    }

    void ICommandHost.MapperReset()
    {
        // The AutoMapper engine is shared with the App's MapperViewModel, so a
        // direct re-resolve here repaints the UI + refreshes $roomid/$zoneid via
        // the existing CurrentNodeChanged subscription — no App round-trip.
        AutoMapper.Reset();
    }

    void ICommandHost.MapperCommand(string args)
    {
        // save / load / clear / zone / color / allowdupes / record touch the
        // App's MapperViewModel (zone files, canvas colours, UI state), so —
        // unlike reset — they round-trip to the App handler (#146). Console
        // builds with no handler get a diagnostic.
        if (MapperCommandRequested is null)
            RaiseEchoLine("[mapper] no mapper host wired (Console build).");
        else
            MapperCommandRequested.Invoke(args);
    }

    /// <summary>Raised by the non-<c>reset</c> <c>#mapper</c> subcommands (#146),
    /// from the command bar or a script. Carries the text after <c>#mapper</c>;
    /// the App parses the subcommand, drives the mapper, and echoes the result.</summary>
    public event Action<string>? MapperCommandRequested;

    /// <summary>
    /// Raised by <c>#goto</c> / <c>#go2</c> from the command bar or a script.
    /// Carries the raw destination argument (numeric id, note label, or title
    /// text). The App resolves it against the active zone and starts an
    /// attended, RT-gated walk — the typed/scripted equivalent of clicking a
    /// room in the Mapper.
    /// </summary>
    public event Action<string>? MapperGotoRequested;

    void ICommandHost.PlaySound(string soundName) => PlaySound(soundName);

    /// <summary>
    /// Play a sound effect by name. Central gate + resolution for ALL SFX
    /// (trigger/highlight sounds, <c>#play</c>): honors the <c>PlaySounds</c>
    /// config, resolves a bare name against <see cref="GenieConfig.SoundDir"/>
    /// and appends <c>.wav</c> when no extension is given (Genie 4 convention),
    /// then raises <see cref="SoundRequested"/> with the absolute path. The App
    /// plays it; Console builds with no handler are a silent no-op.
    /// </summary>
    public void PlaySound(string soundName)
    {
        if (!Config.PlaySounds || string.IsNullOrWhiteSpace(soundName)) return;
        var name = soundName.Trim();
        var path = Path.IsPathRooted(name) ? name : Path.Combine(Config.SoundDir, name);
        if (!Path.HasExtension(path)) path += ".wav";
        SoundRequested?.Invoke(path);
    }

    /// <summary>Raised with an absolute, gate-passed sound-file path whenever a
    /// sound should play. The App subscribes and dispatches to its audio
    /// backend.</summary>
    public event Action<string>? SoundRequested;

    void ICommandHost.Speak(string text, bool urgent) => Speak(text, urgent);

    /// <summary>
    /// Speak <paramref name="text"/> aloud via TTS (<c>#speak</c>, per-rule
    /// trigger/highlight speak). Trims and ignores blank input, then raises
    /// <see cref="SpeakRequested"/>. The App owns the TTS engine + audio and
    /// synthesizes off-thread; Console builds with no handler are a silent
    /// no-op. The voice/engine lives in the App layer (native libs) so
    /// <see cref="Genie.Core"/> stays UI- and platform-free. <paramref name="urgent"/>
    /// = speak first / barge in (per-rule alerts).
    /// </summary>
    public void Speak(string text, bool urgent = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SpeakRequested?.Invoke(text.Trim(), urgent);
    }

    /// <summary>Raised with the (trimmed, non-empty) text to speak and its
    /// urgency whenever TTS is requested. The App subscribes and dispatches to
    /// its TTS backend (urgent → high priority / barge-in).</summary>
    public event Action<string, bool>? SpeakRequested;

    void ICommandHost.TtsCommand(string args) => TtsCommandRequested?.Invoke(args ?? "");

    /// <summary>Raised with a <c>#tts</c> subcommand argument string (may be
    /// empty for the bare verb). The App handles install / list / status; a
    /// Console build with no handler is a silent no-op.</summary>
    public event Action<string>? TtsCommandRequested;

    void ICommandHost.FlashWindow() => FlashRequested?.Invoke();

    /// <summary>Raised when <c>#flash</c> wants the main window's taskbar /
    /// dock entry flashed for attention. The App subscribes and calls the
    /// platform attention API; a Console build with no handler is a silent
    /// no-op. May fire from a script thread — subscribers marshal to the UI
    /// thread themselves.</summary>
    public event Action? FlashRequested;

    void ICommandHost.Beep()
    {
        // Same PlaySounds gate as PlaySound (Genie 4 gated Interaction.Beep on
        // bPlaySounds). Gate here so the App backend stays a dumb "make noise".
        if (Config.PlaySounds) BeepRequested?.Invoke();
    }

    /// <summary>Raised when <c>#beep</c> / <c>#bell</c> wants the system alert
    /// sound, after the <c>PlaySounds</c> gate. The App subscribes and plays the
    /// platform bell; a Console build with no handler is a silent no-op. May
    /// fire from a script thread — subscribers marshal as needed.</summary>
    public event Action? BeepRequested;

    void ICommandHost.Connect(ConnectRequest request)
    {
        // The connection lifecycle, saved profiles, and the Connect dialog all
        // live in the App layer; forward the parsed request. Console builds with
        // no handler just get a diagnostic. Note: $lastcommand is deliberately
        // NOT set for #connect (it's an internal command that never reaches the
        // game socket), so an explicit `#connect acct pw …` password never leaks
        // through the lastcommand global.
        if (ConnectRequested is null)
            RaiseEchoLine("[connect] no connect host wired (Console build).");
        else
            ConnectRequested.Invoke(request);
    }

    /// <summary>
    /// Raised by <c>#connect</c> / <c>#reconnect</c> / <c>#lichconnect</c> from
    /// the command bar or a script. The App layer interprets the
    /// <see cref="ConnectRequest"/> (reconnect / saved profile / explicit
    /// credentials) and drives the actual connect.
    /// </summary>
    public event Action<ConnectRequest>? ConnectRequested;

    // ── IPluginHost (explicit — avoids name clashes with ICommandHost.Echo,
    //    the public State (GameState) and Variables (VariableEngine)) ──────────

    string Genie.Plugins.IPluginHost.HostVersion      => HostVersionString;
    int    Genie.Plugins.IPluginHost.InterfaceVersion => PluginInterfaceVersion;

    void Genie.Plugins.IPluginHost.Echo(string text) => RaiseEchoLine(text);

    void Genie.Plugins.IPluginHost.EchoToWindow(string window, string text)
        => RaiseEchoToWindow(text, window, null);

    void Genie.Plugins.IPluginHost.SetWindow(string window, string content)
        => SetPluginWindow?.Invoke(window, content);

    /// <summary>Raised when a plugin replaces a named window's full contents
    /// (<see cref="Genie.Plugins.IPluginHost.SetWindow"/>). The App surfaces the
    /// window as a dock panel and swaps its text.</summary>
    public event Action<string, string>? SetPluginWindow;

    void Genie.Plugins.IPluginHost.SendCommand(string command) => Commands.ProcessInput(command);

    IReadOnlyDictionary<string, string> Genie.Plugins.IPluginHost.Variables
        => new Dictionary<string, string>(Scripts.Globals);

    string? Genie.Plugins.IPluginHost.GetVariable(string name)
        => Scripts.Globals.TryGetValue(name, out var v) ? v : Variables?.Store.Get(name);

    void Genie.Plugins.IPluginHost.SetVariable(string name, string value)
        => Scripts.Globals[name] = value ?? string.Empty;

    Genie.Plugins.IGameStateView Genie.Plugins.IPluginHost.State => _pluginStateView;

    void Genie.Plugins.IPluginHost.Log(string message) => RaiseEchoLine(message);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Serializes <see cref="ConnectAsync"/> so a manual connect racing
    /// the App's auto-reconnect can't interleave teardown / BuildConnection / dial
    /// (issue #88 / #46 Phase 3 — the "M5" re-entrancy guard). Last caller wins: a
    /// concurrent connect awaits the in-flight one, then runs.</summary>
    private readonly System.Threading.SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>
    /// Connect (or reconnect) using <paramref name="cfg"/>. Tears down any previous
    /// connection, resets the persistent game state in place, builds a fresh
    /// per-connection layer (<see cref="BuildConnection"/>), and dials. The engines,
    /// ScriptEngine, AutoMapper, Plugins, a running <c>.cmd</c>, and all script
    /// globals/rule state SURVIVE this — that's the persistent core (issue #88 /
    /// #46 Phase 3). The host builds one <see cref="GenieCore"/> per app session and
    /// calls this for every connect.
    /// </summary>
    public async Task ConnectAsync(ConnectionConfig cfg, CancellationToken ct = default,
                                   bool reloadRules = true, bool clearPerCharacter = true)
    {
        // Guard against overlapping connects (auto-reconnect racing a manual
        // connect, or a double-click): without this, two callers interleave and the
        // second BuildConnection clobbers the _connection the first just dialed.
        await _connectGate.WaitAsync(ct);
        try
        {
            await TeardownConnectionAsync();
            // Fresh session → clear the persistent state in place (consumers hold it by
            // reference). Transient world/vitals always reset; per-character identity +
            // live skill ranks only on a genuine character SWITCH (clearPerCharacter) —
            // a same-char reconnect or the first connect from offline keeps them so the
            // Mapper doesn't re-prompt for info/exp (issue #88 / #46 Phase 3, Change #4).
            _state.Reset(clearPerCharacter);
            _roomChangedSincePrompt = false;   // don't carry a dangling room-change flag across reconnect (PR #92)
            // On a character SWITCH, also drop the builtin trackers' accumulated
            // per-character state so the Experience/Active-Spells panels and their
            // $Skill.*/$SpellTimer.* globals don't bleed the previous character's data
            // into the next. A same-character reconnect / first-from-offline keeps it.
            if (clearPerCharacter) Scripts.Extensions.DispatchReset();
            // reloadRules drives the per-connection .cfg auto-load: replay the
            // connecting character's saved rules (true on first connect AND on a switch;
            // false on a same-char reconnect so runtime-added rules survive).
            BuildConnection(cfg, reloadRules);
            await _connection!.ConnectAsync(ct);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Clear every per-character rule set from the persistent engines (classes,
    /// user variables, aliases, triggers, highlights, substitutes, gags, macros)
    /// so the NEXT character's saved rules load into a clean slate instead of
    /// inheriting the previous character's. The host calls this on a character
    /// change before re-loading rule files (clear-then-load); a same-character
    /// reconnect skips it so runtime-added rules and a running script's state
    /// survive (issue #88 / #46 Phase 3). NameHighlights and Presets are
    /// session/global (not per-character loaded), so they are left intact; only
    /// USER-scope variables are dropped (system/reserved globals persist).
    /// </summary>
    public void ResetRuleEngines()
    {
        Classes.Clear();
        Variables.Store.ClearUserVariables();
        Aliases.Clear();
        Triggers.Clear();
        Highlights.Clear();
        Substitutes.Clear();
        Gags.Clear();
        Macros.Clear();
    }

    /// <summary>
    /// Tear down ONLY the per-connection layer (connection, parser, state-engine and
    /// their subscriptions, mapper adapter, globals mirror, AI buffer). The engines,
    /// ScriptEngine, AutoMapper, Plugins and the relay subjects are left intact. A
    /// no-op when no connection has been built yet (cold start / offline).
    /// </summary>
    private async Task TeardownConnectionAsync()
    {
        // Stop feeding the relays FIRST: a disposing parser/connection must never
        // complete/error the persistent relay subjects the App is subscribed to.
        _gameEventsRelaySub?.Dispose(); _gameEventsRelaySub = null;
        _rawXmlRelaySub?.Dispose();     _rawXmlRelaySub     = null;
        _connStateRelaySub?.Dispose();  _connStateRelaySub  = null;
        _sentRelaySub?.Dispose();       _sentRelaySub       = null;

        _gameEventSub?.Dispose();    _gameEventSub    = null;
        _pluginXmlSub?.Dispose();    _pluginXmlSub    = null;
        _parserFeed?.Dispose();      _parserFeed      = null;
        _settingsInfoSub?.Dispose(); _settingsInfoSub = null;
        _gameHostSub?.Dispose();     _gameHostSub     = null;
        _globalsSync?.Dispose();     _globalsSync     = null;
        _skillHistory?.Dispose();    _skillHistory    = null;
        _mapperAdapter?.Dispose();   _mapperAdapter   = null;
        AiBuffer?.Dispose();         AiBuffer         = null;
        _stateEngine?.Dispose();     _stateEngine     = null;
        _parser?.Dispose();          _parser          = null;

        var conn = _connection;
        _connection = null;
        if (conn is not null)
            await conn.DisposeAsync();

        // $connected must survive teardown ordering: when a still-live connection
        // is torn down (a #connect over an open session, app shutdown), the read
        // loop's finally emits Disconnected only DURING conn.DisposeAsync() above
        // — so this subscription is disposed after the connection, not with the
        // other subs, or that final event would land in an unsubscribed subject
        // and leave $connected stuck at "1" for scripts still running offline.
        _connectedVarSub?.Dispose(); _connectedVarSub = null;
    }

    /// <summary>
    /// Sends a raw game command, bypassing the alias/separator pipeline.
    /// Use <c>ProcessInput</c> for user-typed input. A no-op while disconnected.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn is null) return;   // offline → drop (issue #88)
        if (!command.Contains(';'))
        {
            await conn.SendCommandAsync(command, ct);
            return;
        }
        foreach (var part in command.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                await conn.SendCommandAsync(trimmed, ct);
        }
    }

    /// <summary>
    /// Route user input through the full command pipeline:
    /// alias expansion → separator split → #cmd routing → game send.
    /// </summary>
    public void ProcessInput(string input, string? echoOverride = null)
    {
        // #251: typed input runs on the game thread, interleaving with game
        // traffic at line boundaries (the loop item is the serialization unit,
        // as the dispatcher job used to be). Fire-and-forget is correct — the
        // feedback (echo, errors) arrives via events; the App renders the local
        // input echo itself at submit time, so typing feels instant under load.
        if (_loop is { IsOnLoop: false } loop)
        {
            loop.Post(() => ProcessInputCore(input, echoOverride));
            return;
        }
        ProcessInputCore(input, echoOverride);
    }

    private void ProcessInputCore(string input, string? echoOverride)
    {
        // Console /commands owned by a built-in tracker (e.g. /spelltimer, /exp,
        // /tt) are handled client-side and never reach the game or the triggers.
        // Only offered at this genuine user-input boundary — programmatic sends go
        // straight to Commands.ProcessInput.
        if (!string.IsNullOrEmpty(input) && input.TrimStart().StartsWith('/')
            && Scripts.Extensions.DispatchSlashCommand(input))
            return;

        // External plugins see the typed line next (IGeniePlugin.OnInput) — each
        // may transform it or swallow it entirely (null), the Genie 4 ParseInput
        // contract. Command-driven plugins (e.g. InventoryView's /iv) depend on
        // this to keep their commands off the wire. EVERY typed line is offered
        // here; the script engine additionally offers its '/'-prefixed sends via
        // ScriptEngine.PluginInput (public #325), so a legacy plugin's commands
        // behave the same typed or scripted. Aliases, trigger actions and the
        // mapper still bypass this. Re-entrancy is handled inside
        // PluginManager.DispatchInput — do NOT rely on the bypass for it.
        if (!string.IsNullOrEmpty(input))
        {
            var fromPlugins = Plugins.DispatchInput(input);
            if (fromPlugins is null) return;   // swallowed — no triggers, no game send
            input = fromPlugins;
        }

        // TriggerOnInput (Genie 4 parity): evaluate triggers against the user's
        // typed line itself, not just game output, when enabled. Fired here at
        // the genuine user-input entry point — programmatic sends (scripts,
        // aliases, trigger actions, autowalk, mapper) call Commands.ProcessInput
        // directly and are intentionally NOT re-triggered, which also prevents
        // a trigger action from looping back into its own pattern.
        if ((Config?.TriggerOnInput ?? false) && !string.IsNullOrWhiteSpace(input))
            Triggers.ProcessLine(input);

        Commands.ProcessInput(input, echoOverride);
    }

    /// <summary>Gracefully close the live connection (a no-op while disconnected).
    /// The core, engines, scripts and relay subscriptions stay alive — a subsequent
    /// <see cref="ConnectAsync(ConnectionConfig, CancellationToken)"/> tears the dead
    /// connection down and builds a fresh one.</summary>
    public Task DisconnectAsync()
        => _connection?.DisconnectAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Toggle the AI raw stream pipe without disconnecting.
    /// When disabled, AI processing is suspended but the game connection and parser continue.
    /// No-op while disconnected (the pipe lives on the per-connection layer).
    /// </summary>
    public bool AiPipeEnabled
    {
        get => _connection?.AiPipeEnabled ?? false;
        set { if (_connection is not null) _connection.AiPipeEnabled = value; }
    }

    /// <summary>
    /// Attach or detach the mapper's room-resolution trace to match
    /// <c>#config mapperdebug</c>. Lines land in the game window like any other
    /// system echo, so the player can copy the stretch straight out of the log.
    /// </summary>
    private void SyncMapperDebug()
    {
        AutoMapper.Diagnostics = Config.MapperDebug ? RaiseEchoLine : null;
        if (Config.MapperDebug)
            RaiseEchoLine("[mapper] Room-resolution trace ON. `#config mapperdebug off` to stop.");
    }

    /// <summary>
    /// Push the AutoMapper's current room/zone into the script globals
    /// (<c>$roomid</c> / <c>$zoneid</c> / <c>$zonename</c> / <c>$roomnote</c>).
    /// <c>$roomid</c> is the mapper node id (what <c>#goto</c> and scripts compare
    /// against), not the server's <c>$gameroomid</c>. <c>$roomid</c> and
    /// <c>$zoneid</c> default to <c>"0"</c> (and <c>$zonename</c>/<c>$roomnote</c>
    /// to empty) when there's no current node / unmapped zone (off-map),
    /// matching Genie 3/4 / Genie5.Kzin.
    /// </summary>
    private void SyncMapperGlobals()
    {
        // Mapper browse-hold: the DISPLAYED zone isn't where the character is.
        // Freeze the character-scoped globals at their last-known values —
        // overwriting them from the browsed map rewrote $zoneid under running
        // $zoneid-driven scripts (travel.cmd) and printed the browsed map's id
        // for `#echo $zoneid` (2026-08-06). The #45 off-map "0" semantics are
        // unaffected: genuine can't-place-you (not browsing) still writes 0s.
        if (AutoMapper.ViewIsBrowsing) return;

        var node = AutoMapper.CurrentNode;
        var zone = AutoMapper.ActiveZone;
        Scripts.Globals["roomid"]   = node?.Id.ToString() ?? "0";
        // $zoneid → "0" (not empty) when the active zone has no Genie 4 id,
        // matching $roomid's off-map default and the Genie 3/4 parity SaragosDR
        // asked for in #45 (both should read 0 when the mapper can't place you).
        Scripts.Globals["zoneid"]   = string.IsNullOrEmpty(zone?.Genie4Id) ? "0" : zone.Genie4Id;
        Scripts.Globals["zonename"] = zone?.Name ?? string.Empty;
        // $roomnote — the current room's map note/label (Genie 4 reserved var,
        // the last of the deferred mapper-sourced set from #45). Empty off-map.
        Scripts.Globals["roomnote"] = node?.Notes ?? string.Empty;

        // #95: refresh the pathfinder's character class + circle from live state
        // so class/level-gated exits (climbs, swims, guild passages) actually
        // enforce once the game has told us who we are. Setting these once at
        // construction froze them at app-start (guild Unknown, circle 0).
        //  • Class comes straight from the live guild.
        //  • Circle is mirrored from the $circle global the InfoTracker fills in
        //    from `info` — nothing else assigns _state.Circle, so this is also
        //    what finally populates it for every other consumer (AI context,
        //    GameStateView). Guard on > 0 so a missing/zero circle never clobbers
        //    a good value back to 0.
        AutoMapper.CharacterClass = _state.Guild != Genie.Core.Models.DrGuild.Unknown
            ? _state.Guild.ToString()
            : null;
        if (Scripts.Globals.TryGetValue("circle", out var circleStr) &&
            int.TryParse(circleStr, out var circle) && circle > 0)
            _state.Circle = circle;
        AutoMapper.CharacterLevel = _state.Circle;
    }

    /// <summary>
    /// Set the Live Audit diagnostic mode (the <c>#audit</c> command). <c>On</c>
    /// tees raw XML + parsed events + live zone/room to
    /// <c>&lt;LogDir&gt;/live_audit.log</c>; <c>XmlHunting</c> adds the XML
    /// tag-coverage pass; <c>Off</c> stops. Returns the log path.
    /// </summary>
    public string SetLiveAudit(Diagnostics.AuditMode mode)
    {
        // Disable first so switching directly between On and XmlHunting takes
        // effect (Enable is a no-op while already enabled) and restarts the log.
        _liveAudit.Disable();
        if (mode == Diagnostics.AuditMode.On)         _liveAudit.Enable();
        else if (mode == Diagnostics.AuditMode.XmlHunting) _liveAudit.Enable(hunting: true);
        return _liveAudit.Path;
    }

    /// <summary>The Live Audit sink — exposed so the App can annotate it (e.g.
    /// the mapper's LoadStatus) into the same chronological stream.</summary>
    public Diagnostics.LiveAudit Audit => _liveAudit;

    /// <summary>App-shutdown disposal: tear down the per-connection layer (if any),
    /// then the persistent engines/relays/scripts. After this the core is dead — the
    /// host builds a new one only on a fresh app session.</summary>
    public async ValueTask DisposeAsync()
    {
        // Per-connection layer first (connection, parser, subs, mapper adapter, AI).
        await TeardownConnectionAsync();

        // Then the once-per-app layer. The game loop drains and joins BEFORE the
        // relay subjects are completed/disposed below, so a queued pipeline item
        // can't land on a dead relay.
        _heartbeat?.Dispose();
        _heartbeat = null;
        _loop?.Dispose();
        _injuriesPollTimer?.Dispose();
        _injuriesPollTimer = null;
        _liveAudit.Dispose();
        AutoMapper.CurrentNodeChanged -= SyncMapperGlobals;
        Plugins.Shutdown();
        Scripts.StopAll();

        // Complete the relay subjects so any lingering subscriber sees the stream
        // end (the feed subs were already disposed by TeardownConnectionAsync).
        _gameEventsRelay.OnCompleted();
        _rawXmlRelay.OnCompleted();
        _connStateRelay.OnCompleted();
        _sentRelay.OnCompleted();
        _gameEventsRelay.Dispose();
        _rawXmlRelay.Dispose();
        _connStateRelay.Dispose();
        _sentRelay.Dispose();
    }
}
