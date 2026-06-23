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
    private IDisposable? _gameEventsRelaySub;
    private IDisposable? _rawXmlRelaySub;
    private IDisposable? _connStateRelaySub;

    // ── Configuration / runtime ────────────────────────────────────────────────
    private readonly LocalDirectoryService _localDir;

    // Stored at construction so BuildConnection() can (re)build the per-connection
    // layer on each connect: the logger factory makes new GameConnection/parser/
    // state-engine loggers; the AI config (if any) gates a fresh AiContextBuffer.
    private readonly ILoggerFactory _loggerFactory;
    private readonly AiConfig?      _aiConfig;

    /// <summary>Loaded configuration. Survives across sessions.</summary>
    public GenieConfig Config { get; }

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

    /// <summary>Raw XML stream — subscribe for logging, recording, or custom processing.</summary>
    public IObservable<string>          RawXmlStream    => _rawXmlRelay;

    /// <summary>Connection lifecycle events.</summary>
    public IObservable<ConnectionEvent> ConnectionState => _connStateRelay;

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

    // ── Constructor ────────────────────────────────────────────────────────────

    public GenieCore(
        string?         dataDirectoryOverride = null,
        AiConfig?       aiConfig      = null,
        ILoggerFactory? loggerFactory = null)
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

        Presets = new PresetEngine();   // seeded with Wrayth defaults

        Substitutes    = new SubstituteEngine();
        Substitutes.Classes = Classes;
        Commands.Substitutes = Substitutes;  // wire #substitute command → engine

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
        // can't take. Character class + level (circle) are pulled from
        // GameState below; both update as the parser sees them. LiveSkills is a
        // member of the persistent state, so this reference stays valid forever.
        AutoMapper.Skills = _state.LiveSkills;
        AutoMapper.CharacterClass = _state.Guild != Genie.Core.Models.DrGuild.Unknown
            ? _state.Guild.ToString()
            : null;
        AutoMapper.CharacterLevel = _state.Circle;

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
        Scripts    = new ScriptEngine(
            scriptsDir:    Config.ScriptDir,
            typeAhead:     _typeAhead,
            sendCommand:   cmd =>
                           {
                               ScriptOutputLine?.Invoke(cmd);
                               _typeAhead.NotifySent();
                               // Offline (no live connection) the game-bound send is
                               // dropped — a script can still run, set variables,
                               // toggle trigger classes and #connect (issue #88).
                               // Sends resume the moment a connection exists.
                               _ = _connection?.SendCommandAsync(cmd);
                           },
            echo:          msg => ScriptOutputLine?.Invoke(msg),
            handleHashCmd: cmd => Commands.ProcessInput(cmd, interactive: false));

        // Live config for the runtime script settings (ScriptTimeout,
        // MaxGoSubDepth, AbortDupeScript, ScriptExtension).
        Scripts.Config = Config;

        // Wire game-state callbacks for RT-gated script pausing (closures over the
        // persistent state, so they stay valid across reconnect).
        Scripts.InRoundtime              = () => _state.Combat.InRoundTime;
        Scripts.RoundTimeRemainingSeconds = () => (int)Math.Ceiling(_state.Combat.RoundTimeRemaining);
        // $spelltime — seconds since the current spell was prepared (Genie 4).
        Scripts.SpellTimeSeconds          = () => (int)_state.Combat.SpellTimeSeconds;
        Scripts.EchoTo                   = (msg, win, color) => EchoToWindow?.Invoke(msg, win, color);
        Scripts.EchoStyled               = (msg, color, mono) => EchoStyledLine?.Invoke(msg, color, mono);
        // Named-window seam for the built-in trackers (Spell Timer / Experience /
        // Time Tracker), which re-render a whole dock panel each prompt. Same event
        // the App's ExperienceViewModel + generic PluginWindowViewModel consume.
        Scripts.SetWindow                = (win, content) => SetPluginWindow?.Invoke(win, content);
        // Let scripts read $name for a persistent #var value (Variables.Store) —
        // the script engine's own Globals hold only live game state + #tvar
        // session globals. Fixes #82 ($var set via `put #var …` now readable +
        // persistable like a typed #var).
        Scripts.UserVarLookup            = name => Variables?.Store.Get(name);

        // Honour the settings.cfg tracker toggles, and re-sync when they change.
        SyncTrackerToggles();
        Config.ConfigChanged += field => { if (field == Genie.Core.Config.ConfigFieldUpdated.Trackers) SyncTrackerToggles(); };

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

        // ── Network stack (per connection) ───────────────────────────────────────
        var connection = new GameConnection(cfg,
            new SgeAuthClient(lf.CreateLogger<SgeAuthClient>()),
            lf.CreateLogger<GameConnection>());
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
                    // Self-calibrate the type-ahead limit from the server's own cap
                    // report ("(Sorry,) you may only type ahead N line(s).") so a
                    // wrong seed (e.g. a Lich/DevReplay default, or premium+LTB) is
                    // corrected by the authority. Cheap guard before any parsing.
                    if (te.Text.IndexOf("type ahead", StringComparison.OrdinalIgnoreCase) >= 0)
                        CalibrateTypeAhead(te.Text);
                    // Each per-line consumer is timed into its own stage so the
                    // overlay shows which feature is eating the frame. The Time
                    // wrapper is a zero-overhead passthrough while disabled.
                    Metrics.Time(PipelineStage.Scripts,  () => Scripts.OnGameLine(te.Text));   // match/waitfor + EXP/info trackers
                    // ParseGameOnly (Genie 4 parity): when on, fire triggers only on
                    // the main game stream, not secondary stream-windows (thoughts,
                    // logons, combat, …). Default off → triggers see every stream.
                    Metrics.Time(PipelineStage.Triggers, () =>
                    {
                        if (!(Config?.ParseGameOnly ?? false) || te.Stream == "main")
                            Triggers.ProcessLine(te.Text);                                     // user-defined triggers
                    });
                    // Plugins observe each line. Phase 1 ignores the transform
                    // return (display-pipeline rewrite/gag wiring is deferred);
                    // observe-only plugins return the text unchanged.
                    Metrics.Time(PipelineStage.Plugins,  () => Plugins.DispatchGameText(te.Text, te.Stream));
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
            }
        });

        // Plugins see raw XML chunks (Genie 4 ParseXML parity) for structured
        // data the typed events don't surface — e.g. <component id='exp X'>.
        _pluginXmlSub = connection.RawXmlStream.Subscribe(
            xml => Metrics.Time(PipelineStage.Plugins, () => Plugins.DispatchXml(xml)));

        // ── Ready-for-input signal ─────────────────────────────────────────────
        // StormFront / DevReplay: <settingsInfo/> is authoritative (see docs/SGE_PROTOCOL.md).
        // Wizard mode: no XML tags arrive, fire on TCP connect instead.
        if (cfg.ClientMode == GameClientMode.Wizard)
        {
            _settingsInfoSub = connection.StateStream
                .Where(e => e.Kind == ConnectionEventKind.Connected)
                .Take(1)
                .Subscribe(_ => OnConnectReady());
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
        if (reloadRules)
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
        _gameEventsRelaySub = parser.GameEvents.Subscribe(e => _gameEventsRelay.OnNext(e), static _ => { });
        _rawXmlRelaySub     = connection.RawXmlStream.Subscribe(x => _rawXmlRelay.OnNext(x), static _ => { });
        _connStateRelaySub  = connection.StateStream.Subscribe(s => _connStateRelay.OnNext(s), static _ => { });
    }

    // ── ICommandHost ───────────────────────────────────────────────────────────

    void ICommandHost.Echo(string text) => EchoLine?.Invoke(text);

    void ICommandHost.EchoTo(string text, string? window, string? color)
        => EchoToWindow?.Invoke(text, window, color);

    void ICommandHost.EchoMain(string text, string? color, bool mono)
        => EchoStyledLine?.Invoke(text, color, mono);

    void ICommandHost.SendToGame(string text, bool userInput, string origin, string? echoOverride)
    {
        // Local echo of user-typed commands so the player can see what they sent.
        // Script/alias commands are not echoed here — scripts have their own echo path.
        // When echoOverride is provided (e.g. UI link click passing the friendly
        // display name), use it instead of the raw text so the user sees
        // "get a tapered cutlass" instead of "get #49489411 in #49489410".
        if (userInput)
            EchoLine?.Invoke(echoOverride ?? text);

        // Let the mapper observe the outgoing command — it parses movement
        // directions ("n", "go bridge", etc.) so it can correlate the
        // subsequent room change with the direction we just moved.
        AutoMapper.OnCommandSent(text);

        // Genie 4 reserved $lastcommand — the last line sent to the game.
        Scripts.Globals["lastcommand"] = text;

        _typeAhead.NotifySent();
        // Offline (no live connection) the send is dropped — user input is still
        // echoed and observed by the mapper above, so the command bar stays usable
        // while disconnected (issue #88).
        _ = _connection?.SendCommandAsync(text);
    }

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

    private void OnConnectReady()
    {
        var __ = _connection?.SendCommandAsync("look");

        var connectScript = Config.ConnectScript;
        if (!string.IsNullOrWhiteSpace(connectScript))
        {
            try { Scripts.TryStart(connectScript.Trim(), Array.Empty<string>()); }
            catch { /* never let a connect-script error abort the session */ }
        }
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
                case "Experience":  ext.Enabled = Config.ShowExperience;  break;
                case "TimeTracker": ext.Enabled = Config.ShowTimeTracker; break;
            }
        }
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

    void ICommandHost.StopAllScripts() => Scripts.StopAll();

    void ICommandHost.PauseAllScripts() => Scripts.PauseAll();

    void ICommandHost.ResumeAllScripts() => Scripts.ResumeAll();

    void ICommandHost.SetTraceLevelAll(int level)
    {
        // Clamp to the same 0-10 range the engine recognises elsewhere; -1 turns it off.
        if (level < 0) level = 0;
        if (level > 10) level = 10;
        foreach (var inst in Scripts.Instances)
            inst.DebugLevel = level;
    }

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
    /// Simple <c>$name</c> expansion against <see cref="ScriptEngine.Globals"/>
    /// and the user variable store. Walks the text once, replaces each
    /// <c>$identifier</c> with the value found in (1) <c>Scripts.Globals</c>
    /// (live game state), or (2) <c>Variables.Store</c> (<c>#var</c> values).
    /// Unknown vars are left as the literal <c>$name</c> — matches Genie 4's
    /// <c>ParseGlobalVars</c> behavior. Identifier chars are letters, digits,
    /// <c>_</c>, <c>.</c>, <c>-</c> — same set the script-side
    /// <c>SubstituteVars</c> recognises.
    /// </summary>
    string ICommandHost.ExpandVariables(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('$') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '$') { sb.Append(c); continue; }
            int j = i + 1;
            while (j < text.Length &&
                   (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '.' || text[j] == '-'))
                j++;
            if (j == i + 1) { sb.Append(c); continue; }  // bare $ with no identifier
            var name = text[(i + 1)..j];
            if (name.Equals("spelltime", StringComparison.OrdinalIgnoreCase))
            {
                // Live countup (Genie 4) — not a stored global.
                sb.Append(((int)State.Combat.SpellTimeSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (Scripts.Globals.TryGetValue(name, out var liveVal))
            {
                sb.Append(liveVal ?? string.Empty);
            }
            else
            {
                var userVal = Variables?.Store.Get(name);
                if (userVal is not null) sb.Append(userVal);
                else sb.Append('$').Append(name);        // unknown → leave literal
            }
            i = j - 1;
        }
        return sb.ToString();
    }

    void ICommandHost.EditScript(string name)
    {
        // The actual editor launch lives in the App layer (cross-platform
        // Process.Start with the user's configured EditorPath). We just
        // raise the event so the App-layer subscriber can do the work.
        // If no one is listening (Console / headless test harness), echo a
        // diagnostic so callers know nothing happened.
        if (EditScriptRequested is null)
            EchoLine?.Invoke($"[editor] no editor host wired — '{name}' not opened.");
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
            EchoLine?.Invoke("[layout] no layout host wired (Console build).");
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
            EchoLine?.Invoke("[plugin] no plugin host wired (Console build).");
        else
            PluginCommandRequested.Invoke(args);
    }

    /// <summary>Raised by <c>#plugin …</c>. The App handles list/enable/disable/
    /// load/unload/reload against <see cref="Plugins"/> and the Plugins folder.</summary>
    public event Action<string>? PluginCommandRequested;

    void ICommandHost.ConfigCommand(string args)
    {
        if (ConfigCommandRequested is null)
            EchoLine?.Invoke("[config] no config host wired (Console build).");
        else
            ConfigCommandRequested.Invoke(args);
    }

    /// <summary>
    /// Raised by <c>#config</c> / <c>#set</c> / <c>#setting</c> / <c>#settings</c>.
    /// The App handles the bare-form (open Configuration dialog) and the
    /// save / load / edit / get / set subforms against <c>DisplaySettings</c>.
    /// </summary>
    public event Action<string>? ConfigCommandRequested;

    void ICommandHost.MapperGoto(string args)
    {
        // Mapper room resolution + the attended walk live in the App layer;
        // forward the raw destination argument. Console builds with no handler
        // just get a diagnostic (no UI mapper to drive).
        if (MapperGotoRequested is null)
            EchoLine?.Invoke("[goto] no mapper host wired (Console build).");
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

    void ICommandHost.Connect(ConnectRequest request)
    {
        // The connection lifecycle, saved profiles, and the Connect dialog all
        // live in the App layer; forward the parsed request. Console builds with
        // no handler just get a diagnostic. Note: $lastcommand is deliberately
        // NOT set for #connect (it's an internal command that never reaches the
        // game socket), so an explicit `#connect acct pw …` password never leaks
        // through the lastcommand global.
        if (ConnectRequested is null)
            EchoLine?.Invoke("[connect] no connect host wired (Console build).");
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

    void Genie.Plugins.IPluginHost.Echo(string text) => EchoLine?.Invoke(text);

    void Genie.Plugins.IPluginHost.EchoToWindow(string window, string text)
        => EchoToWindow?.Invoke(text, window, null);

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

    void Genie.Plugins.IPluginHost.Log(string message) => EchoLine?.Invoke(message);

    // ── Public API ─────────────────────────────────────────────────────────────

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

        _gameEventSub?.Dispose();    _gameEventSub    = null;
        _pluginXmlSub?.Dispose();    _pluginXmlSub    = null;
        _parserFeed?.Dispose();      _parserFeed      = null;
        _settingsInfoSub?.Dispose(); _settingsInfoSub = null;
        _gameHostSub?.Dispose();     _gameHostSub     = null;
        _connectedVarSub?.Dispose(); _connectedVarSub = null;
        _globalsSync?.Dispose();     _globalsSync     = null;
        _mapperAdapter?.Dispose();   _mapperAdapter   = null;
        AiBuffer?.Dispose();         AiBuffer         = null;
        _stateEngine?.Dispose();     _stateEngine     = null;
        _parser?.Dispose();          _parser          = null;

        var conn = _connection;
        _connection = null;
        if (conn is not null)
            await conn.DisposeAsync();
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
        // Console /commands owned by a built-in tracker (e.g. /spelltimer, /exp,
        // /tt) are handled client-side and never reach the game or the triggers.
        // Only offered at this genuine user-input boundary — programmatic sends go
        // straight to Commands.ProcessInput.
        if (!string.IsNullOrEmpty(input) && input.TrimStart().StartsWith('/')
            && Scripts.Extensions.DispatchSlashCommand(input))
            return;

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

        // Then the once-per-app layer.
        _liveAudit.Dispose();
        AutoMapper.CurrentNodeChanged -= SyncMapperGlobals;
        Plugins.Shutdown();
        Scripts.StopAll();

        // Complete the relay subjects so any lingering subscriber sees the stream
        // end (the feed subs were already disposed by TeardownConnectionAsync).
        _gameEventsRelay.OnCompleted();
        _rawXmlRelay.OnCompleted();
        _connStateRelay.OnCompleted();
        _gameEventsRelay.Dispose();
        _rawXmlRelay.Dispose();
        _connStateRelay.Dispose();
    }
}
