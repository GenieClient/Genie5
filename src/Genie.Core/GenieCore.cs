using System.Reactive.Linq;
using Genie.Core.AI;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Connection;
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
    private const string HostVersionString  = "5.0.0-alpha.3.5";

    // ── Network / parser layer ─────────────────────────────────────────────────
    private readonly GameConnection    _connection;
    private readonly DrXmlParser       _parser;
    private readonly GameStateEngine   _stateEngine;
    private readonly IDisposable       _parserFeed;
    private readonly IDisposable       _settingsInfoSub;
    private readonly IDisposable       _gameEventSub;
    private readonly IDisposable       _pluginXmlSub;
    private readonly Plugins.GameStateView _pluginStateView;

    // ── Configuration / runtime ────────────────────────────────────────────────
    private readonly LocalDirectoryService _localDir;

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

    /// <summary>Loaded plugins. Phase 1 = in-process registration; the DLL
    /// loader bolts discovery onto this same manager.</summary>
    public Plugins.PluginManager Plugins      { get; }

    // ── Mapper ────────────────────────────────────────────────────────────────
    private readonly MapperGameStateAdapter _mapperAdapter;

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
    private readonly ScriptGlobalsSync _globalsSync;

    /// <summary>.cmd script runner. Includes built-in EXP and info trackers.</summary>
    public ScriptEngine Scripts { get; }

    // ── Public observables (unchanged surface) ─────────────────────────────────

    /// <summary>Typed game events (TextEvent, ProgressBarEvent, RoundTimeEvent, …).</summary>
    public IObservable<GameEvent>       GameEvents      => _parser.GameEvents;

    /// <summary>Raw XML stream — subscribe for logging, recording, or custom processing.</summary>
    public IObservable<string>          RawXmlStream    => _connection.RawXmlStream;

    /// <summary>Connection lifecycle events.</summary>
    public IObservable<ConnectionEvent> ConnectionState => _connection.StateStream;

    /// <summary>Current live game state snapshot.</summary>
    public Models.GameState             State           => _stateEngine.State;

    /// <summary>AI context buffer and analyzer. Null if no AiConfig was provided.</summary>
    public AiContextBuffer?             AiBuffer        { get; }

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

    // ── Constructor ────────────────────────────────────────────────────────────

    public GenieCore(
        ConnectionConfig cfg,
        AiConfig?        aiConfig      = null,
        ILoggerFactory?  loggerFactory = null)
    {
        var lf = loggerFactory ?? NullLoggerFactory.Instance;

        // ── Config (loaded early so it can influence the network handshake) ────
        _localDir = new LocalDirectoryService("Genie5", AppContext.BaseDirectory);

        // Per-profile data root: if this connection's profile specified its own
        // folder, repoint the data root BEFORE loading settings so everything
        // (settings.cfg, Scripts, Maps, …) resolves under it.
        if (!string.IsNullOrWhiteSpace(cfg.DataDirectoryOverride))
            _localDir.UseExplicitRoot(cfg.DataDirectoryOverride);

        Config    = new GenieConfig(_localDir);
        Config.Load();

        // Apply user-configured FE identifier (e.g. STORM vs GENIE). DR appears
        // to send richer click markup to clients identifying as STORM. The
        // setting persists in settings.cfg; toggle via `#config frontend storm`.
        if (!string.IsNullOrWhiteSpace(Config.FrontEndIdentifier) &&
            !cfg.FrontEndId.Equals(Config.FrontEndIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            cfg = cfg with { FrontEndId = Config.FrontEndIdentifier };
        }

        // ── Network stack ──────────────────────────────────────────────────────
        _connection  = new GameConnection(cfg,
            new SgeAuthClient(lf.CreateLogger<SgeAuthClient>()),
            lf.CreateLogger<GameConnection>());

        _parser      = new DrXmlParser(lf.CreateLogger<DrXmlParser>());

        var state    = new Models.GameState();
        _stateEngine = new GameStateEngine(_parser.GameEvents, state,
            lf.CreateLogger<GameStateEngine>());

        // Plugin layer — read-only state view + manager (this GenieCore is the
        // IPluginHost). Must exist before the game-event subscription below
        // dispatches to it.
        _pluginStateView = new Plugins.GameStateView(state);
        Plugins          = new Plugins.PluginManager(this);
        // No builtin plugins — the Experience tracker is now an external DLL
        // (Plugin_EXPTrackerV5) loaded from {AppData}/Genie5/Plugins by the App
        // on connect. Drop the DLL there to enable exp tracking.

        // Wire raw XML → parser
        _parserFeed = _connection.RawXmlStream.Subscribe(_parser.Feed);

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
        _mapperAdapter = new MapperGameStateAdapter(state, _parser.GameEvents);
        AutoMapper.Attach(_mapperAdapter);

        // Skill-weighted pathfinding: hand the engine a reference to the
        // live SkillStore so FindPath can filter out exits the character
        // can't take. Character class + level (circle) are pulled from
        // GameState below; both update as the parser sees them.
        AutoMapper.Skills = state.LiveSkills;
        AutoMapper.CharacterClass = state.Guild != Genie.Core.Models.DrGuild.Unknown
            ? state.Guild.ToString()
            : null;
        AutoMapper.CharacterLevel = state.Circle;

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
                               _ = _connection.SendCommandAsync(cmd);
                           },
            echo:          msg => ScriptOutputLine?.Invoke(msg),
            handleHashCmd: cmd => Commands.ProcessInput(cmd));

        // Wire game-state callbacks for RT-gated script pausing
        Scripts.InRoundtime              = () => state.Combat.InRoundTime;
        Scripts.RoundTimeRemainingSeconds = () => (int)Math.Ceiling(state.Combat.RoundTimeRemaining);
        Scripts.EchoTo                   = (msg, win, color) => EchoToWindow?.Invoke(msg, win, color);

        // Mirror live game state into Scripts.Globals so community scripts
        // can read $righthand / $stamina / $hidden / $gameroomid / the
        // per-exit booleans ($north etc.) and the rest of Genie 4's
        // reserved-variable vocabulary. Subscribes to game events with
        // event-typed dispatch so each callback only touches the 1-12
        // variables relevant to that event.
        _globalsSync = new ScriptGlobalsSync(
            state, Scripts.Globals, _parser.GameEvents,
            gameCode:      cfg.GameCode,
            characterName: cfg.CharacterName,
            accountName:   cfg.AccountName,
            clientVersion: HostVersionString);

        // ── Game event routing ─────────────────────────────────────────────────
        // Note: Scripts.OnGameLine already calls Extensions.DispatchGameLine internally —
        // no need to route TextEvents to the extension manager separately.
        _gameEventSub = _parser.GameEvents.Subscribe(evt =>
        {
            switch (evt)
            {
                case TextEvent te:
                    Scripts.OnGameLine(te.Text);   // match/waitfor + EXP/info trackers
                    Triggers.ProcessLine(te.Text); // user-defined triggers
                    // Plugins observe each line. Phase 1 ignores the transform
                    // return (display-pipeline rewrite/gag wiring is deferred);
                    // observe-only plugins return the text unchanged.
                    Plugins.DispatchGameText(te.Text, te.Stream);
                    break;

                case PromptEvent:
                    Scripts.OnPrompt();            // advance RT-gated script execution
                    Plugins.DispatchPrompt();
                    break;

                case NavEvent:
                    Scripts.OnRoomChanged();       // unblock `move` in running scripts
                    break;
            }
        });

        // Plugins see raw XML chunks (Genie 4 ParseXML parity) for structured
        // data the typed events don't surface — e.g. <component id='exp X'>.
        _pluginXmlSub = _connection.RawXmlStream.Subscribe(xml => Plugins.DispatchXml(xml));

        // ── Ready-for-input signal ─────────────────────────────────────────────
        // StormFront / DevReplay: <settingsInfo/> is authoritative (see docs/SGE_PROTOCOL.md).
        // Wizard mode: no XML tags arrive, fire on TCP connect instead.
        if (cfg.ClientMode == GameClientMode.Wizard)
        {
            _settingsInfoSub = _connection.StateStream
                .Where(e => e.Kind == ConnectionEventKind.Connected)
                .Take(1)
                .Subscribe(_ => { var __ = _connection.SendCommandAsync("look"); });
        }
        else
        {
            _settingsInfoSub = _parser.GameEvents
                .OfType<SettingsInfoEvent>()
                .Take(1)
                .Subscribe(_ => { var __ = _connection.SendCommandAsync("look"); });
        }

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

        // ── AI buffer (optional) ───────────────────────────────────────────────
        if (aiConfig is not null)
        {
            AiBuffer = new AiContextBuffer(
                _connection.AiRawStream,
                state,
                aiConfig,
                lf.CreateLogger<AiContextBuffer>());
        }
    }

    // ── ICommandHost ───────────────────────────────────────────────────────────

    void ICommandHost.Echo(string text) => EchoLine?.Invoke(text);

    void ICommandHost.EchoTo(string text, string? window, string? color)
        => EchoToWindow?.Invoke(text, window, color);

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

        _ = _connection.SendCommandAsync(text);
    }

    void ICommandHost.RunScript(string text)
    {
        var parts = ArgumentParser.ParseArgs(text);
        if (parts.Count == 0) return;
        Scripts.TryStart(parts[0], [.. parts.Skip(1)]);
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
        => Scripts.Instances.Where(i => i.Running).Select(i => i.Name).ToList();

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
            if (Scripts.Globals.TryGetValue(name, out var liveVal))
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

    /// <summary>
    /// Raised by <c>#goto</c> / <c>#go2</c> from the command bar or a script.
    /// Carries the raw destination argument (numeric id, note label, or title
    /// text). The App resolves it against the active zone and starts an
    /// attended, RT-gated walk — the typed/scripted equivalent of clicking a
    /// room in the Mapper.
    /// </summary>
    public event Action<string>? MapperGotoRequested;

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

    public Task ConnectAsync(CancellationToken ct = default)
        => _connection.ConnectAsync(ct);

    /// <summary>
    /// Sends a raw game command, bypassing the alias/separator pipeline.
    /// Use <c>ProcessInput</c> for user-typed input.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (!command.Contains(';'))
        {
            await _connection.SendCommandAsync(command, ct);
            return;
        }
        foreach (var part in command.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                await _connection.SendCommandAsync(trimmed, ct);
        }
    }

    /// <summary>
    /// Route user input through the full command pipeline:
    /// alias expansion → separator split → #cmd routing → game send.
    /// </summary>
    public void ProcessInput(string input, string? echoOverride = null)
        => Commands.ProcessInput(input, echoOverride);

    public Task DisconnectAsync()
        => _connection.DisconnectAsync();

    /// <summary>
    /// Toggle the AI raw stream pipe without disconnecting.
    /// When disabled, AI processing is suspended but the game connection and parser continue.
    /// </summary>
    public bool AiPipeEnabled
    {
        get => _connection.AiPipeEnabled;
        set => _connection.AiPipeEnabled = value;
    }

    public async ValueTask DisposeAsync()
    {
        _gameEventSub.Dispose();
        _pluginXmlSub.Dispose();
        Plugins.Shutdown();
        _parserFeed.Dispose();
        _settingsInfoSub.Dispose();
        _mapperAdapter.Dispose();
        _globalsSync.Dispose();
        Scripts.StopAll();
        AiBuffer?.Dispose();
        _stateEngine.Dispose();
        _parser.Dispose();
        await _connection.DisposeAsync();
    }
}
