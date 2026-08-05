using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genie.Core.Config;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;
using Genie.Core.Scripting.Js;

namespace Genie.Core.Scripting;

/// <summary>
///   Genie .cmd script runner. Supports:
///   put/send, echo, pause/wait, goto, gosub/return, label (:name | name:),
///   match/matchre/matchwait, waitfor/waitforre, var/setvariable, exit,
///   if <expr>; then ... (inline + indented block w/ optional else),
///   if_1..if_9, eval, evalmath, include (resolved at parse time).
///
/// All <c>put</c>/<c>send</c> calls flow through the shared
/// <see cref="TypeAheadSession"/>: scripts honor the same in-flight budget the
/// mapper uses, and re-pump on each game prompt.
/// </summary>
public sealed class ScriptEngine
{
    private readonly List<ScriptInstance> _instances = new();
    private readonly TypeAheadSession     _typeAhead;
    private readonly Action<string>       _sendCommand;
    private readonly Action<string>       _echo;
    private readonly Action<string>?      _handleHashCmd;
    private readonly Action<string>?      _injectGameLine;
    private readonly string               _scriptsDir;

    // Re-entrancy guard for extension Echo → trigger/action feed (G4 parity):
    // while an extension's own game-window output is being fed back through the
    // pipeline, OnGameLine must NOT re-dispatch it to extensions (they'd re-parse
    // each other's rendered output and could loop), and a nested extension Echo
    // must display without re-feeding.
    private bool _inExtensionEcho;

    /// <summary>Threaded runtime for <c>.js</c> scripts. The .cmd tick loop is
    /// untouched; JS launches are delegated here from <see cref="TryStart"/>, and
    /// game lines/prompts/stop/pause are forwarded so both languages behave
    /// uniformly from the user's perspective.</summary>
    private readonly JsScriptRuntime      _js;

    /// <summary>
    /// Directed echo: (message, windowName, hexColor). When window or color
    /// are null the host falls back to the main game output / default colour.
    /// Set after construction by the UI layer.
    /// </summary>
    public Action<string, string?, string?>? EchoTo { get; set; }

    /// <summary>
    /// Styled main-window echo: (message, color, mono). For <c>#echo</c> with a
    /// colour and/or the <c>mono</c> flag but no <c>&gt;window</c> redirect —
    /// routes to the main game window (not a side window). Falls back to a plain
    /// <see cref="_echo"/> when unset (headless tests).
    /// </summary>
    public Action<string, string?, bool>? EchoStyled { get; set; }

    /// <summary>
    /// Echoes a command sent by a script to the game window. Args: (scriptName, command).
    /// Set by the UI layer to render with the "scriptecho" preset colour.
    /// </summary>
    public Action<string, string>? EchoCommand { get; set; }

    /// <summary>
    /// Returns true when the game character is currently in roundtime.
    /// Set by the UI layer; used by <c>pause</c> and <c>wait</c> to hold
    /// until roundtime resolves.
    /// </summary>
    public Func<bool>? InRoundtime { get; set; }

    /// <summary>
    /// Seconds remaining on the current roundtime (0 when none). Set by the
    /// UI layer alongside <see cref="InRoundtime"/>. The Tick loop reads this
    /// to schedule a wakeup when an instance is RT-gated — without it the
    /// script would sit forever waiting for a prompt the server has no
    /// reason to send (prompts arrive in response to commands, not when RT
    /// expires).
    /// </summary>
    public Func<int>? RoundTimeRemainingSeconds { get; set; }

    /// <summary>Seconds since the current spell was prepared (Genie 4
    /// <c>$spelltime</c>). Set by the host; computed live so the reserved
    /// variable counts up. 0 when no spell is prepared.</summary>
    public Func<int>? SpellTimeSeconds { get; set; }

    /// <summary>Unix-epoch seconds of when the current spell was prepared (Genie 4
    /// <c>$spellstarttime</c>). Set by the host; 0 when no spell is prepared.</summary>
    public Func<long>? SpellStartTimeEpoch { get; set; }

    /// <summary>Live read of <c>#config ignorescriptwarnings</c> (#151). When it
    /// returns true, non-fatal parse advisories (bad-condition / malformed-if) are
    /// suppressed. Read on each warning so a mid-session toggle takes effect at
    /// once. Null → warnings shown (default). Hard errors / aborts are never gated.</summary>
    public Func<bool>? WarningsSuppressed { get; set; }

    /// <summary>
    /// Schedule a <see cref="Tick"/> call after the given delay. Set by the
    /// UI layer (e.g. a DispatcherTimer) so that time-based unblocks (delay,
    /// pause) don't depend on the next server event arriving.
    /// </summary>
    public Action<TimeSpan>? ScheduleTick { get; set; }

    /// <summary>
    /// Wall-clock time of the currently-scheduled RT wakeup, used to avoid
    /// flooding <see cref="ScheduleTick"/> with duplicate timers each tick.
    /// When <c>DateTime.UtcNow &lt; _rtWakeupAt</c> we already have a wakeup
    /// pending; new schedule requests are dropped until it fires.
    /// </summary>
    private DateTime _rtWakeupAt = DateTime.MinValue;

    private int _inFlight;

    /// <summary>
    /// Session-wide global variables, accessible from scripts as <c>$Name</c>.
    /// Populated by <c>#tvar</c>, the EXP tracker, and host code (mapper, profile,
    /// <see cref="ScriptGlobalsSync"/>, etc). Case-insensitive.
    ///
    /// <para>
    /// Concurrent — writes can come from the parser thread (via
    /// <see cref="ScriptGlobalsSync"/> mirroring game events) while reads
    /// happen on the script tick thread (via <c>SubstituteVars</c>). Plain
    /// <see cref="Dictionary{TKey,TValue}"/> would throw under concurrent
    /// modification; the per-access overhead of ConcurrentDictionary (~25ns
    /// vs ~10ns) is invisible at script-script tick rates.
    /// </para>
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Globals { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In-process extension manager. Built-in trackers (EXP, info) are
    /// registered at construction; future plugins will register here too.
    /// </summary>
    public ExtensionManager Extensions { get; }

    /// <summary>
    /// Optional live config, wired by <c>GenieCore</c>. Supplies the runtime
    /// script settings (timeout / GoSub depth / dupe-abort / default extension).
    /// Null in headless tests — the per-setting fallbacks below then apply.
    /// </summary>
    public GenieConfig? Config { get; set; }

    private int    ScriptTimeoutMs => Config?.ScriptTimeout   ?? 5000;
    private int    GoSubDepthLimit  => Config?.MaxGoSubDepth   ?? 50;
    private bool   AbortDupeScript  => Config?.AbortDupeScript ?? true;
    private string DefaultScriptExt => Config?.ScriptExtension ?? "cmd";

    /// <summary>Rate-limit for the runaway-loop timeout notice, keyed by script
    /// name. A script that self-restarts (e.g. a menu that re-<c>#parse</c>s
    /// itself) re-trips the script-timeout guard every turn; without this it
    /// prints the same three-line warning dozens of times and buries the game
    /// window. We warn once per script per <see cref="RunawayWarnCooldown"/>.</summary>
    private readonly Dictionary<string, DateTime> _runawayWarnedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RunawayWarnCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Max wall-clock a single <see cref="Tick"/> spends running script
    /// lines before it yields the UI thread. A busy or runaway script is broken
    /// off here and resumed via <see cref="ScheduleTick"/>; returning from Tick
    /// lets the dispatcher service pending input first, so a Stop/pause/debug
    /// click always lands even while a heavy loop is churning. Without this, a
    /// non-yielding loop could hold the thread for the whole ScriptTimeout window
    /// (up to 5 s) and freeze the app.</summary>
    private const double MaxTickBudgetMs = 60;

    /// <summary>Continuous busy time (ms) across consecutive budget-limited ticks
    /// that never yielded naturally. Feeds the ScriptTimeout runaway detector so
    /// it still fires now that a single tick stops early at
    /// <see cref="MaxTickBudgetMs"/>. Reset to 0 whenever a tick ends because
    /// every script is idle / paused / waiting.</summary>
    private double _busyRunMs;

    public ScriptEngine(string scriptsDir, TypeAheadSession typeAhead,
                        Action<string> sendCommand, Action<string> echo,
                        Action<string>? handleHashCmd = null,
                        Action<string>? injectGameLine = null)
    {
        _scriptsDir   = scriptsDir;
        _typeAhead    = typeAhead;
        _sendCommand  = sendCommand;
        _echo         = echo;
        _handleHashCmd = handleHashCmd;
        _injectGameLine = injectGameLine;
        Extensions   = new ExtensionManager(new EngineExtensionHost(this));
        Extensions.Register(new InfoTrackerExtension());
        // The Spell Timer, Experience and Time Tracker trackers are built in to
        // Core (no longer external plugins). Each owns its Genie 4-parity script
        // globals ($SpellTimer.*, $Skill.* / $TDPs) and re-renders to its dock
        // panel via the named-window seam. The host gates them from settings.cfg
        // (spelltimer / showexperience / showtimetracker) after construction.
        Extensions.Register(new SpellTimerExtension());
        Extensions.Register(new ExperienceExtension());
        Extensions.Register(new TimeTrackerExtension());
        // Circle Calculator: a command-driven sibling of Experience (/calc, /sort).
        // No dock panel and always on (like InfoTracker), so it isn't in the
        // settings tracker-toggle gate.
        Extensions.Register(new global::Genie.Core.Extensions.Builtin.CircleCalc.CircleCalcExtension());
        // Inventory View: command-driven (/iv) cross-character item catalog with a
        // first-class dock window fed by CatalogChanged (not the text-window seam).
        // Always on — it costs nothing until a scan runs.
        Extensions.Register(new global::Genie.Core.Extensions.Builtin.InventoryView.InventoryViewExtension());
        Directory.CreateDirectory(_scriptsDir);

        _js = new JsScriptRuntime(
            scriptsDir:         _scriptsDir,
            send:               _sendCommand,
            echo:               _echo,
            globals:            Globals,
            roundtimeRemaining: () => RoundTimeRemainingSeconds?.Invoke() ?? 0,
            onStarted:          n => ScriptStarted?.Invoke(n),
            onFinished:         n => ScriptFinished?.Invoke(n),
            // genie.echoTo(window, text[, color]) reuses the same directed-echo
            // seam as the .cmd `#echo >Window #Color`; fall back to plain script
            // echo when the UI hasn't wired EchoTo (headless tests).
            echoTo:             (msg, win, col) =>
            {
                if (EchoTo is not null) EchoTo(msg, win, col);
                else                    _echo(msg);
            });
    }

    /// <summary>
    /// Adapts <see cref="ScriptEngine"/> to the <see cref="IExtensionHost"/>
    /// surface. Extensions only see this — never the engine itself.
    /// </summary>
    private sealed class EngineExtensionHost : IExtensionHost
    {
        private readonly ScriptEngine _engine;
        public EngineExtensionHost(ScriptEngine engine) { _engine = engine; }
        public IDictionary<string, string> Globals  => _engine.Globals;
        public void Echo(string text)               => _engine.EchoFromExtension(text);
        public void EchoRouted(string text, bool display, bool parse)
                                                    => _engine.EchoFromExtension(text, display, parse);
        public void SendCommand(string command)     => _engine._sendCommand(command);
        public void SetWindow(string window, string content)
            => _engine.SetWindow?.Invoke(window, content);
        public string ConfigDir                     => _engine.Config?.ConfigDir ?? _engine._scriptsDir;
        public void Log(string message)             => _engine._echo(message);
        public string? GetUserVar(string name)      => _engine.UserVarLookup?.Invoke(name);
        public string? GetConfig(string key)        => _engine.Config?.GetSetting(key);
        public string DataRoot                      => _engine.Config?.DataRoot ?? ConfigDir;
        public void InjectParsedLine(string line)   => _engine._injectGameLine?.Invoke(line);
        public void RunHashCommand(string command)  => _engine._handleHashCmd?.Invoke(command);
    }

    /// <summary>Wired by the host to the named-window seam (GenieCore's
    /// <c>SetPluginWindow</c> event / the App dock panels). Replaces a whole
    /// window's contents; the built-in trackers (Spell Timer, Experience, Time
    /// Tracker) re-render through this on each prompt.</summary>
    public Action<string, string>? SetWindow { get; set; }

    /// <summary>Read-through to the host's persistent user-variable store
    /// (<c>#var</c> values / <c>Variables.Store</c>). Lets a script resolve
    /// <c>$name</c> for a value set via <c>#var name value</c> — the script
    /// engine's own <see cref="Globals"/> holds only live game-state + #tvar
    /// session globals. Returns null when the name isn't a user variable.</summary>
    public Func<string, string?>? UserVarLookup { get; set; }

    /// <summary>Feed a fully-parsed game event to the extensions (the Spell Timer's
    /// percWindow stream + clear boundary, the Experience tracker's
    /// <c>&lt;component id='exp X'&gt;</c>). Called by the host from the parser's typed
    /// event stream — reliable across the connection's tag-splitting chunk
    /// boundaries, unlike raw XML.</summary>
    public void OnGameEvent(Genie.Core.Events.GameEvent ev)
    {
        if (ev is not null) Extensions.DispatchGameEvent(ev);
    }

    public string ScriptsDir => _scriptsDir;
    public IReadOnlyList<ScriptInstance> Instances => _instances;
    public bool AnyRunning => _instances.Any(i => i.Running) || _js.AnyRunning;

    /// <summary>Names of every running script, both .cmd and .js — used by the
    /// <c>$scriptlist</c> pseudo-variable and the scripts panel.</summary>
    public IReadOnlyList<string> RunningScriptNames() =>
        _instances.Where(i => i.Running).Select(i => i.Name)
                  .Concat(_js.RunningNames())
                  .ToList();

    /// <summary>True if a currently-running script of this name is a <c>.js</c>
    /// script (vs a .cmd script). Used by the UI to tag rows by language.</summary>
    public bool IsJavaScript(string name) =>
        _js.RunningNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Current per-line debug/trace level of a running <c>.cmd</c> script
    /// (0 if the name isn't a running .cmd script). Lets the Script Bar chip seed
    /// itself from the engine's real level instead of assuming 0.</summary>
    public int GetTrace(string name)
    {
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return inst.DebugLevel;
        return 0;
    }

    /// <summary>Wire (or clear) the JS line-dispatch timing sink — used by the
    /// performance overlay to time the JavaScript stage. Stays null (no-op) on a
    /// build with no overlay.</summary>
    public Action<double>? JsDispatchMsSink { set => _js.DispatchMsSink = value; }

    /// <summary>Stats for the currently-running <c>.js</c> scripts (name, elapsed
    /// seconds, paused) — surfaced in the performance overlay's running-.js list.</summary>
    public IReadOnlyList<Js.JsScriptStat> JsRunningStats() => _js.RunningStats();

    /// <summary>Fired when a script finishes (done or stopped). Arg is the script name.</summary>
    public event Action<string>? ScriptFinished;

    /// <summary>
    /// Fire <see cref="ScriptFinished"/> for <paramref name="inst"/> exactly once.
    /// Every death path must notify — the App's script bar removes its chip on
    /// this event, and several (goto/gosub to an unknown label, gosub depth
    /// limit, return on an empty stack) used to set <c>Running = false</c>
    /// silently; the tick loop then pruned the dead instance without the event,
    /// leaving a stale chip after "[script] unknown label:" (smoke 2026-08-03
    /// finding #8). The prune itself now calls this too as a structural
    /// catch-all, and the once-guard makes the double coverage safe (listeners
    /// like the mapper's replan-on-finish must not fire twice).
    /// </summary>
    private void NotifyFinished(ScriptInstance inst)
    {
        if (inst.FinishedNotified) return;
        inst.FinishedNotified = true;
        try { ScriptFinished?.Invoke(inst.Name); } catch { /* never rethrow from cleanup */ }
    }

    /// <summary>
    /// Fired when a new script begins running. Arg is the script name.
    /// Used by the Scripts panel to refresh its running-scripts list — the
    /// <see cref="Instances"/> collection is mutated directly so plain
    /// <see cref="INotifyCollectionChanged"/> isn't an option.
    /// </summary>
    public event Action<string>? ScriptStarted;

    /// <summary>
    /// Fired whenever a script's per-line debug/trace level changes — from the
    /// Script Bar chip (<see cref="SetTrace"/>) OR the script's own
    /// <c>#debug N</c> line. Args: (script name, new level). Lets the Script Bar
    /// chip track the script's live level instead of only reflecting the user's
    /// own chip clicks.
    /// </summary>
    public event Action<string, int>? DebugLevelChanged;

    public bool TryStart(string name, IReadOnlyList<string> args)
    {
        var path = ResolveScriptPath(name);
        if (path is null)
        {
            // Diagnostic: include the directory we searched so misconfigured
            // path resolution (portable mode triggering accidentally, etc.)
            // is obvious from the echo rather than requiring a debugger.
            _echo($"[script] not found: {name}  (searched in: {_scriptsDir})");
            return false;
        }

        // The script's own directory is the include base — for ScriptsDir
        // scripts that's _scriptsDir, preserving the prior behaviour.
        return StartInstance(name, Path.GetDirectoryName(path) ?? _scriptsDir, path, args);
    }

    /// <summary>
    /// Start a script from an explicit file path that may live OUTSIDE
    /// <see cref="ScriptsDir"/> — e.g. an analyst-capture recipe <c>.cmd</c>.
    /// The script name is the file's base name; the file's own directory is the
    /// include base. Mirrors <see cref="TryStart"/> in every other respect
    /// (reload semantics, arg seeding, ScriptStarted, gating), so a recipe runs
    /// exactly like any user script.
    /// </summary>
    public bool TryStartFile(string fullPath, IReadOnlyList<string>? args = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            _echo($"[script] not found: {fullPath}");
            return false;
        }

        var full = Path.GetFullPath(fullPath);
        var name = Path.GetFileNameWithoutExtension(full);
        return StartInstance(name, Path.GetDirectoryName(full)!, full, args ?? Array.Empty<string>());
    }

    /// <summary>Shared start path for <see cref="TryStart"/> /
    /// <see cref="TryStartFile"/>: parse <paramref name="path"/> (with
    /// <paramref name="baseDir"/> as the include base), apply reload semantics,
    /// seed args, register, and tick.</summary>
    private bool StartInstance(string name, string baseDir, string path, IReadOnlyList<string> args)
    {
        // .js scripts run on the threaded JavaScript runtime, not the .cmd tick
        // loop. Everything downstream (this method's reload/var seeding) is
        // .cmd-specific, so dispatch before any of it.
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return _js.TryStart(name, args, path, AbortDupeScript);

        ScriptInstance inst;
        try
        {
            inst = ScriptParser.Parse(name, baseDir, File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _echo($"[script] parse error in {name}: {ex.Message}");
            return false;
        }
        inst.SourcePath = Path.GetFullPath(path);
        inst.BaseDir    = baseDir;

        // Reload semantics (gated by AbortDupeScript, default true): starting a
        // script that's already running stops the existing instance and replaces
        // it with the new one. Matches Genie 4's behaviour and avoids two copies
        // fighting over the same game state, action triggers, and type-ahead
        // budget. With AbortDupeScript=false the existing instance is left
        // running and a second copy starts alongside it (Genie 4 parity).
        //
        // Important: do NOT fire ScriptFinished for the killed instance.
        // The caller initiated the reload and doesn't expect a "finished"
        // callback for the corpse — and downstream listeners (e.g. the
        // mapper's OnAutoMapperScriptFinished, which replans on finish)
        // would re-enter TryStart and spin in an infinite loop.
        if (AbortDupeScript)
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_instances[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _instances[i].Running = false;
                    _instances.RemoveAt(i);
                    _echo($"[script] {name} reloaded (previous instance stopped)");
                }
            }
        }

        for (int i = 0; i < args.Count; i++)
            inst.Vars[(i + 1).ToString()] = args[i];
        // Genie4 parity (Script.cs:2114-2118): missing %1..%9 default to "" so
        // a direct read of an unpassed arg yields empty (not undefined), and
        // %argcount holds the real arg count — the authority for if_N/shift.
        for (int i = args.Count + 1; i <= 9; i++)
            inst.Vars[i.ToString()] = string.Empty;
        inst.Vars["0"] = string.Join(" ", args);
        inst.Vars["argcount"] = args.Count.ToString(CultureInfo.InvariantCulture);
        inst.Vars["scriptname"] = name;

        // Seed the $-scope with the same values so $1..$9 work at the top
        // level. Gosubs push new frames onto this stack without touching %N.
        var initDollar = new string[10];
        initDollar[0] = inst.Vars["0"];
        for (int i = 0; i < 9; i++)
            initDollar[i + 1] = i < args.Count ? args[i] : string.Empty;
        inst.DollarStack.Push(initDollar);
        inst.DollarCounts.Push(args.Count);

        _instances.Add(inst);
        // The resolved path is part of the start line: multiple copies of a
        // community script commonly exist side by side, and "my fix didn't
        // take" is usually the OTHER copy running (a Discord report lost half
        // an hour to exactly that).
        _echo($"[script] {name} started ({inst.SourcePath})");
        ScriptStarted?.Invoke(name);
        Tick();
        return true;
    }

    private string? ResolveScriptPath(string name)
    {
        // Try the configured default extension first (Genie 4 ScriptExtension),
        // then the standard fallbacks. "" handles a name given with an extension.
        var exts = new List<string> { "", "." + DefaultScriptExt };
        foreach (var e in new[] { ".cmd", ".inc", ".js" })
            if (!exts.Contains(e, StringComparer.OrdinalIgnoreCase)) exts.Add(e);
        foreach (var ext in exts)
        {
            var p = Path.Combine(_scriptsDir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Drop every not-yet-sent put/send segment across all running instances
    /// (the script side of Genie 4's <c>#queue clear</c>). In-flight type-ahead
    /// accounting is untouched — those commands are already on the wire.
    /// </summary>
    public void ClearPendingSends()
    {
        foreach (var inst in _instances)
        {
            inst.PendingSends.Clear();
            inst.NextSendAt = DateTime.MinValue;
        }
    }

    public void StopAll()
    {
        _js.StopAll();
        if (_instances.Count == 0) return;
        var stopped = _instances.ToList();
        foreach (var i in stopped) i.Running = false;
        _instances.Clear();
        _echo("[script] all scripts stopped");
        foreach (var i in stopped) NotifyFinished(i);
    }

    public void Stop(string name)
    {
        _js.Stop(name);
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            if (!_instances[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var inst = _instances[i];
            inst.Running = false;
            _instances.RemoveAt(i);
            _echo($"[script] {name} stopped");
            NotifyFinished(inst);
        }
    }

    public void PauseScript(string name)
    {
        _js.Pause(name);
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { inst.UserPaused = true; _echo($"[script] {name} paused"); }
    }

    public void ResumeScript(string name)
    {
        _js.Resume(name);
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { inst.UserPaused = false; _echo($"[script] {name} resumed"); }
    }

    /// <summary>Set the debug/trace level for a SINGLE running <c>.cmd</c> script
    /// by name (the per-chip control on the Script Bar, #94). Level is clamped to
    /// 0–10 (0 = off; higher surfaces more <c>[dbg:N]</c> traces). The global
    /// equivalent is <c>#traceall</c> (<see cref="GenieCore"/> ICommandHost).
    /// <c>.js</c> scripts have no per-line trace level, so they're unaffected.</summary>
    public void SetTrace(string name, int level)
    {
        if (level < 0) level = 0;
        if (level > 10) level = 10;
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                inst.DebugLevel = level;
                _echo($"[script] {name} debug level set to {level}");
                DebugLevelChanged?.Invoke(inst.Name, level);
            }
    }

    /// <summary>Toggle pause/resume per script (Genie 4 <c>#script
    /// pauseorresume</c> — each matching script flips its OWN state, so a mixed
    /// set inverts rather than synchronising). Null/empty name = every script.</summary>
    public void PauseOrResume(string? name)
    {
        bool all = string.IsNullOrEmpty(name);
        bool any = false;
        foreach (var inst in _instances)
        {
            if (!all && !inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            inst.UserPaused = !inst.UserPaused;
            _echo($"[script] {inst.Name} {(inst.UserPaused ? "paused" : "resumed")}");
        }
        foreach (var s in _js.RunningStats())
        {
            if (!all && !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (s.Paused) _js.Resume(s.Name); else _js.Pause(s.Name);
        }
        if (!any && !all) _echo($"[script] no running script named {name}");
        Tick();
    }

    /// <summary>Mark script(s) for hot reload at their next <c>goto</c>
    /// (Genie 4 <c>#script reload</c>). Null/empty name = every running .cmd
    /// script. .js scripts have no label structure, so a .js target is
    /// rejected with an explanatory echo.</summary>
    public void RequestReload(string? name)
    {
        bool all = string.IsNullOrEmpty(name);
        bool any = false;
        foreach (var inst in _instances)
        {
            if (!inst.Running) continue;
            if (!all && !inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (inst.PendingReload)
            {
                _echo($"[script] {inst.Name} is already pending reload at the next label.");
            }
            else
            {
                inst.PendingReload = true;
                _echo($"[script] {inst.Name} will be reloaded at the next label.");
            }
        }
        if (!any && !all)
        {
            _echo(IsJavaScript(name!)
                ? $"[script] reload is not supported for .js scripts ({name})"
                : $"[script] no running script named {name}");
        }
    }

    /// <summary>Structured snapshots of every running script (.cmd + .js) —
    /// the Script Manager panel's row model. Poll on a UI timer (the panel's
    /// analogue of <see cref="JsRunningStats"/>); also the single source the
    /// string-based <see cref="StatusLines"/> formats from.</summary>
    public IReadOnlyList<ScriptStatus> GetStatuses()
    {
        var list = new List<ScriptStatus>();
        foreach (var inst in _instances)
            if (inst.Running) list.Add(SnapshotOf(inst));
        foreach (var s in _js.RunningStats())
            list.Add(new ScriptStatus(s.Name, IsJs: true, s.Paused,
                s.Paused ? "Paused" : "Running", CurrentLine: 0, s.SourcePath,
                s.ElapsedSec, DebugLevel: 0, PendingMatchCount: 0, GosubDepth: 0,
                PendingReload: false));
        return list;
    }

    private static ScriptStatus SnapshotOf(ScriptInstance inst)
    {
        // Pc points at the NEXT line to execute (StepOne advances it before
        // dispatch), so the line the script is "at" — the blocking statement
        // for a waiting script — is Pc-1.
        int lineNo = 0;
        if (inst.Lines.Count > 0)
            lineNo = inst.Lines[Math.Clamp(inst.Pc - 1, 0, inst.Lines.Count - 1)].LineNumber;
        return new ScriptStatus(
            inst.Name, IsJs: false, inst.UserPaused, StateName(inst), lineNo,
            inst.SourcePath, inst.RunClock.Elapsed.TotalSeconds, inst.DebugLevel,
            inst.PendingMatches.Count, inst.GosubStack.Count, inst.PendingReload);
    }

    /// <summary>One Genie 4-format status line per running script (both .cmd
    /// and .js): <c>Name(Paused) [Debuglevel: N]: 12.30 seconds. State
    /// (file.cmd)</c>. Used by <c>#script</c>/<c>#scripts</c> listings and the
    /// vars/trace dump headers. <paramref name="filter"/> is an exact script
    /// name; null/empty/"all" = everything.</summary>
    public IReadOnlyList<string> StatusLines(string? filter)
    {
        bool all = string.IsNullOrEmpty(filter) ||
                   filter!.Equals("all", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var s in GetStatuses())
        {
            if (!all && !s.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(FormatStatus(s));
        }
        return lines;
    }

    /// <summary>Local-variable dump for <c>#script vars</c>: a status header
    /// per matching .cmd script followed by its <c>name=value</c> rows
    /// (sorted; <paramref name="valueFilter"/> is a case-insensitive substring
    /// match on the whole row). Null/empty/"all" name = every script.</summary>
    public IReadOnlyList<string> VarsLines(string? name, string valueFilter)
    {
        bool all = string.IsNullOrEmpty(name) ||
                   name!.Equals("all", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var inst in _instances)
        {
            if (!inst.Running) continue;
            if (!all && !inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(FormatStatus(SnapshotOf(inst)));
            foreach (var kv in inst.Vars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var row = $"{kv.Key}={kv.Value}";
                if (valueFilter.Length == 0 ||
                    row.Contains(valueFilter, StringComparison.OrdinalIgnoreCase))
                    lines.Add("  " + row);
            }
        }
        if (lines.Count == 0 && !all && IsJavaScript(name!))
            lines.Add($"[script] vars is not supported for .js scripts ({name})");
        return lines;
    }

    /// <summary>Control-flow trace dump for <c>#script trace</c>: a status
    /// header per matching .cmd script followed by its rolling
    /// <see cref="ScriptTrace"/> entries (oldest first).</summary>
    public IReadOnlyList<string> TraceDumpLines(string? name)
    {
        bool all = string.IsNullOrEmpty(name) ||
                   name!.Equals("all", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var inst in _instances)
        {
            if (!inst.Running) continue;
            if (!all && !inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(FormatStatus(SnapshotOf(inst)));
            foreach (var entry in inst.Trace.Lines())
                lines.Add("  " + entry);
        }
        if (lines.Count == 0 && !all && IsJavaScript(name!))
            lines.Add($"[script] trace is not supported for .js scripts ({name})");
        return lines;
    }

    private static string FormatStatus(ScriptStatus s)
    {
        var paused = s.Paused ? "(Paused)" : "";
        var dbg    = s.DebugLevel > 0 ? $" [Debuglevel: {s.DebugLevel}]" : "";
        var file   = s.SourcePath.Length > 0
            ? Path.GetFileName(s.SourcePath)
            : s.Name + (s.IsJs ? ".js" : ".cmd");
        var secs   = s.ElapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{s.Name}{paused}{dbg}: {secs} seconds. {s.State} ({file})";
    }

    /// <summary>Human-readable blocking state, mirroring Genie 4's
    /// <c>Script.State</c> strings closely enough for muscle memory.</summary>
    private static string StateName(ScriptInstance inst)
    {
        if (inst.UserPaused)             return "Paused";
        if (inst.InMatchWait)            return "MatchWait";
        if (inst.WaitForPattern != null) return "WaitFor";
        if (inst.WaitEvalExpr != null)   return "WaitEval";
        if (inst.Paused) return inst.PauseMode switch
        {
            PauseMode.Wait  => "Waiting",
            PauseMode.Move  => "Moving",
            PauseMode.Delay => "Sleeping",
            _               => "Pausing",
        };
        return "Running";
    }

    /// <summary>
    /// Genie 4 hot reload (Script.cs HotReload), consumed at a <c>goto</c> when
    /// <see cref="ScriptInstance.PendingReload"/> is set: re-read + re-parse the
    /// source file, keep the live state (Vars, DollarStack, Actions, pending
    /// matches, Trace, timer, debug level — all untouched on the instance),
    /// remap gosub return addresses into the new line list by content, and let
    /// the goto continue to <paramref name="label"/> in the new text. On any
    /// failure (unreadable file, parse error, label gone) the script stops with
    /// a clear message — Genie 4's AbortOnScriptError behaviour.
    /// </summary>
    private bool HotReload(ScriptInstance inst, string label)
    {
        ScriptInstance fresh;
        try
        {
            fresh = ScriptParser.Parse(inst.Name, inst.BaseDir, File.ReadAllText(inst.SourcePath));
        }
        catch (Exception ex)
        {
            _echo($"[script] {inst.Name}: hot reload failed at '{label}': {ex.Message}. Stopped.");
            inst.Running = false;
            NotifyFinished(inst);
            return false;
        }
        if (!fresh.Labels.ContainsKey(label))
        {
            _echo($"[script] {inst.Name}: hot reload failed at '{label}' — label not found in the new file. Stopped.");
            inst.Running = false;
            NotifyFinished(inst);
            return false;
        }

        // Remap gosub return addresses (indices into the OLD Lines list) onto
        // the new list by matching raw content within the same origin file —
        // Genie 4's FindNewJumpLineIndex.
        var returns = inst.GosubStack.ToArray();   // top → bottom
        inst.GosubStack.Clear();
        for (int i = returns.Length - 1; i >= 0; i--)
            inst.GosubStack.Push(FindNewLineIndex(inst.Lines, returns[i], fresh.Lines));

        inst.Lines         = fresh.Lines;
        inst.Labels        = fresh.Labels;
        inst.IfFalseJump   = fresh.IfFalseJump;
        inst.ElseJump      = fresh.ElseJump;
        inst.BraceEndJump  = fresh.BraceEndJump;
        inst.WhileBackJump = fresh.WhileBackJump;
        inst.PendingReload = false;
        inst.Trace.Add($"hot reload → {label}");
        _echo($"[script] {inst.Name} reloaded from disk (continuing at '{label}')");
        return true;
    }

    /// <summary>Nearest same-content line in the new list (same Raw text and
    /// include origin, smallest source-line distance). Falls back to line 0 —
    /// the Genie 4 behaviour — when the line was edited away.</summary>
    private static int FindNewLineIndex(List<ScriptLine> oldLines, int oldIdx, List<ScriptLine> newLines)
    {
        if (oldIdx < 0 || oldIdx >= oldLines.Count) return 0;
        var old = oldLines[oldIdx];
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < newLines.Count; i++)
        {
            var l = newLines[i];
            if (!l.Raw.Equals(old.Raw, StringComparison.Ordinal)) continue;
            if (!l.Origin.Equals(old.Origin, StringComparison.OrdinalIgnoreCase)) continue;
            int d = Math.Abs(l.LineNumber - old.LineNumber);
            if (d < bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    public void PauseAll()
    {
        _js.PauseAll();
        foreach (var inst in _instances) inst.UserPaused = true;
        _echo("[script] all scripts paused");
    }

    public void ResumeAll()
    {
        _js.ResumeAll();
        foreach (var inst in _instances) inst.UserPaused = false;
        _echo("[script] all scripts resumed");
        Tick();
    }

    /// <summary>An extension wrote a line to the game window (<see cref="IExtensionHost.Echo"/>).
    /// Display it, then — Genie 4 parity — feed it back through the parse pipeline so
    /// script <c>action</c>/<c>match</c>/<c>waitfor</c> and user <c>#trigger</c>s can match
    /// against it (in G4, plugin/script window text was trigger-matchable; a builtin like
    /// CircleCalc's <c>/sort</c> emits results a script's <c>action … when</c> is meant to
    /// capture). Guarded so an extension can't re-consume its own echo (OnGameLine skips
    /// extension dispatch while the flag is set) and a nested echo displays without looping.</summary>
    private void EchoFromExtension(string text) => EchoFromExtension(text, display: true, parse: true);

    /// <summary>Extension output with independent <paramref name="display"/> /
    /// <paramref name="parse"/> legs (Genie 4 plugin display/parse toggles). The
    /// parse feed is guarded by <see cref="_inExtensionEcho"/> so a plugin's own
    /// output never re-dispatches back into that plugin's OnGameLine.</summary>
    private void EchoFromExtension(string text, bool display, bool parse)
    {
        if (display) _echo(text);
        if (!parse || _inExtensionEcho || _injectGameLine is null) return;
        _inExtensionEcho = true;
        try { _injectGameLine(text); }
        finally { _inExtensionEcho = false; }
    }

    public void OnGameLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Always dispatch to extensions — trackers must populate their
        // globals even when no scripts are running, so the next script that
        // starts sees up-to-date values. Skip when this line is an extension's
        // own echo being fed back for trigger/action matching (G4 parity): re-
        // dispatching would let extensions re-parse each other's rendered output
        // and could loop.
        if (!_inExtensionEcho)
            Extensions.DispatchGameLine(line);

        // Feed JS waiters (genie.waitFor / waitForRe) on their own threads. Done
        // before the .cmd early-return so JS scripts get lines even when no .cmd
        // scripts are active.
        _js.OnGameLine(line);

        if (_instances.Count == 0) return;

        bool isMoveFailure = IsMovementFailure(line);

        for (int i = 0; i < _instances.Count; i++)
        {
            var inst = _instances[i];
            if (!inst.Running) continue;

            // Unblock `move` (PauseMode.Move) when the server reports a
            // movement-rejection. Without this, scripts that issue `move`
            // hang forever when the character is webbed, stunned, in RT,
            // etc — there's no room change to wake them. Re-running the
            // same `move` line is the script author's call (typical pattern
            // is `if ($webbed) then pause` then retry).
            if (isMoveFailure && inst.Paused && inst.PauseMode == PauseMode.Move)
            {
                inst.Paused     = false;
                inst.PauseMode  = PauseMode.None;
                inst.PauseUntil = DateTime.MinValue;
                DbgEcho(inst, 2, $"move unblocked by failure: \"{line}\"");
            }

            // waitfor / waitforre
            if (inst.WaitForPattern != null)
            {
                if (TryMatch(line, inst.WaitForPattern, inst.WaitForIsRegex, inst, capture: true))
                {
                    inst.WaitForPattern  = null;
                    inst.WaitForDeadline = DateTime.MaxValue;
                }
            }

            FireActions(inst, line);

            // match / matchre + matchwait
            if (inst.InMatchWait)
            {
                foreach (var (label, pattern, isRegex) in inst.PendingMatches)
                {
                    if (!TryMatch(line, pattern, isRegex, inst, capture: true)) continue;
                    if (!inst.Labels.TryGetValue(label, out var idx)) continue;

                    inst.Pc                = idx + 1;
                    inst.InMatchWait       = false;
                    inst.MatchWaitDeadline = DateTime.MaxValue;
                    inst.PendingMatches.Clear();
                    inst.Trace.Add($"match {label}");
                    break;
                }
            }
        }
        Tick();
    }

    public void OnPrompt()
    {
        if (_inFlight > 0) _inFlight--;
        Extensions.DispatchPrompt();
        _js.OnPrompt();   // wake JS scripts parked in genie.waitForPrompt()

        // Signal 'wait'-paused scripts that a prompt has arrived. The tick
        // loop will then check roundtime before actually unblocking.
        for (int i = 0; i < _instances.Count; i++)
        {
            var inst = _instances[i];
            if (inst.Paused && inst.PauseMode == PauseMode.Wait &&
                inst.PauseUntil == DateTime.MinValue)
            {
                inst.PauseUntil = DateTime.UtcNow;
            }
        }

        // Re-check eval-form actions each prompt so transitions in globals
        // (e.g. preset timers) fire even without a corresponding game line.
        for (int i = 0; i < _instances.Count; i++)
            FireActions(_instances[i], null);
        Tick();
    }

    /// <summary>
    /// Called by the host when a new room arrives (RoomTitleEvent or
    /// equivalent). Unblocks any script paused by the <c>move</c> command —
    /// matches Genie4's TriggerMove behavior.
    /// </summary>
    public void OnRoomChanged()
    {
        bool wokeAny = false;
        for (int i = 0; i < _instances.Count; i++)
        {
            var inst = _instances[i];
            if (inst.Paused && inst.PauseMode == PauseMode.Move)
            {
                inst.Paused     = false;
                inst.PauseMode  = PauseMode.None;
                inst.PauseUntil = DateTime.MinValue;
                wokeAny = true;
            }
        }
        if (wokeAny) Tick();
    }

    public void Tick()
    {
        // Cap the per-Tick statement count. The previous limit (10_000) was
        // there to handle pathological hand-written scripts but cost real
        // CPU when multiple scripts were active and one happened to be in
        // a tight `if … then goto label` loop — the outer while drained the
        // entire budget before yielding back to the dispatcher, blocking
        // the UI thread for tens of milliseconds at a stretch. 1_000 is
        // still plenty for any sane MUD script (interactive scripts pause
        // / wait every few statements); runaway loops just resume on the
        // next Tick call instead of monopolizing this one.
        var  tickStart = DateTime.UtcNow;
        bool progress  = true;
        bool yielded   = false;   // broke to free the UI thread, with work still pending
        int  guard     = 0;
        ScriptInstance? hot = null;   // last instance to execute a line this turn
        while (progress && guard++ < 1_000)
        {
            var elapsedMs = (DateTime.UtcNow - tickStart).TotalMilliseconds;

            // ScriptTimeout (Genie 4 parity): a run that churns longer than the
            // configured timeout without yielding is treated as a possible
            // infinite loop — warn, stop the offending script, and bail. Measured
            // as CUMULATIVE busy time (this tick + prior budget-limited ticks that
            // never yielded naturally), so it still fires now that a single tick
            // stops early at MaxTickBudgetMs. Never affects WAITFOR/MATCH blocking
            // (those make the tick end via progress==false, which resets the
            // accumulator below).
            if (ScriptTimeoutMs > 0 && _busyRunMs + elapsedMs > ScriptTimeoutMs)
            {
                var who = hot?.Name ?? "(script)";
                // Warn once per script per cooldown — a self-restarting runaway
                // re-trips this guard every turn and would otherwise flood the
                // window with identical notices (see _runawayWarnedAt).
                var nowUtc = DateTime.UtcNow;
                if (!_runawayWarnedAt.TryGetValue(who, out var lastWarn) ||
                    nowUtc - lastWarn > RunawayWarnCooldown)
                {
                    _runawayWarnedAt[who] = nowUtc;
                    _echo($"[script] {who}: possible infinite loop — exceeded script timeout " +
                          $"({ScriptTimeoutMs}ms) without a pause/wait. Stopped. Add a pause/wait, " +
                          $"raise it with #config scripttimeout, or #stop {who} if it keeps looping.");
                }
                if (hot is not null)
                {
                    hot.Running = false;
                    NotifyFinished(hot);
                }
                _busyRunMs = 0;
                break;
            }

            // UI-yield budget: never hold the UI thread more than MaxTickBudgetMs
            // in one turn. Break and resume via ScheduleTick so a busy/runaway
            // loop can't freeze input — a click on the Stop / pause / debug chip
            // (or a typed command) must always be serviceable. Carry the elapsed
            // time forward so the runaway detector above still converges.
            if (elapsedMs > MaxTickBudgetMs)
            {
                _busyRunMs += elapsedMs;
                yielded = true;
                break;
            }

            progress = false;
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                // The list can shrink during iteration (e.g. an action
                // calls StopAll/Stop, or a script exits). Re-check bounds.
                if (i >= _instances.Count) continue;
                var inst = _instances[i];
                if (!inst.Running) { NotifyFinished(inst); _instances.RemoveAt(i); continue; }
                if (inst.UserPaused) continue;

                if (inst.Paused)
                {
                    bool unblock = inst.PauseMode switch
                    {
                        // Pause: timer expired. Genie4 parity — `pause` is a
                        // pure timer, NOT a "wait for roundtime" primitive.
                        // The previous `&& !rt` gate stranded the script in
                        // hangs like a webbed player where no prompt event
                        // arrives to decrement RoundTimeRemaining.
                        PauseMode.Pause => inst.PauseUntil != DateTime.MinValue
                                           && DateTime.UtcNow >= inst.PauseUntil,
                        // Wait: prompt received (PauseUntil set by OnPrompt).
                        // Genie4 parity — also independent of RT.
                        PauseMode.Wait  => inst.PauseUntil != DateTime.MinValue
                                           && DateTime.UtcNow >= inst.PauseUntil,
                        // Delay: timer expired (unchanged).
                        PauseMode.Delay => inst.PauseUntil != DateTime.MinValue
                                           && DateTime.UtcNow >= inst.PauseUntil,
                        _ => false,
                    };
                    if (unblock)
                    {
                        // A completed `delay` opens the RT-bypass window: the
                        // resumed statements run regardless of roundtime until
                        // the script next blocks (see the gate below).
                        if (inst.PauseMode == PauseMode.Delay) inst.RtBypass = true;
                        inst.Paused = false; inst.PauseMode = PauseMode.None; inst.PauseUntil = DateTime.MinValue;
                    }
                    else continue;
                }

                if (inst.InMatchWait)
                {
                    if (DateTime.UtcNow >= inst.MatchWaitDeadline)
                    {
                        inst.InMatchWait       = false;
                        inst.MatchWaitDeadline = DateTime.MaxValue;
                        inst.PendingMatches.Clear();
                        inst.Trace.Add("matchwait timeout");
                    }
                    else continue;
                }

                if (inst.WaitForPattern != null)
                {
                    if (DateTime.UtcNow >= inst.WaitForDeadline)
                    { inst.WaitForPattern = null; inst.WaitForDeadline = DateTime.MaxValue; }
                    else continue;
                }

                if (inst.WaitEvalExpr != null)
                {
                    // Re-evaluate each tick — cheap and matches Genie4 semantics
                    // of "unblock as soon as the condition flips to true". The
                    // expression is stored raw so $/%/etc vars pick up live
                    // values; substitute each tick before passing to
                    // ScriptExpression, which doesn't itself parse $var refs.
                    // A malformed waiteval warns ONCE (inside EvalConditionSafe)
                    // — otherwise it reads as a silent hang until the deadline.
                    var waitCond = SubstituteVars(inst.WaitEvalExpr, inst);
                    bool done = EvalConditionSafe(waitCond, inst, "waiteval", out _);
                    if (done || DateTime.UtcNow >= inst.WaitEvalDeadline)
                    { inst.WaitEvalExpr = null; inst.WaitEvalDeadline = DateTime.MaxValue; }
                    else continue;
                }

                // Genie4 parity: skip script-line execution while RT is
                // active so scripts don't spam `put`/`send` commands the
                // server will just reject with "...wait N seconds." (see
                // Genie4 Script.cs:1535 — the tick returns early when
                // DateTime.Now < m_oRoundTimeEnd unless the instance is in
                // a `delayed` state). Pause/wait timers already ticked
                // above and progress as pure timers, so a `pause` initiated
                // before RT still expires — but the next line of the script
                // can't fire until RT drains. Delay is RT-independent by
                // design: when its timer expires the unblock above sets
                // RtBypass, and the instance keeps executing through this
                // gate until it next blocks (matching G4, where the resumed
                // RunScript burst runs without RT checks until the script
                // hits another blocking statement). The flag is cleared
                // after StepOne once IsBlocked turns true again.
                //
                // Critically, we must also schedule a wakeup. The server
                // doesn't send a prompt when RT expires — it only prompts
                // in response to commands. If the script is RT-gated and
                // nothing else is in flight, no further server traffic
                // arrives, so Tick() is never called again and the script
                // hangs forever. ScheduleTick fires a DispatcherTimer at
                // RT-end so the engine wakes up on its own.
                if (!inst.RtBypass && (InRoundtime?.Invoke() ?? false))
                {
                    ScheduleRoundTimeWakeup();
                    continue;
                }

                // Defensive boundary: a single bad line (e.g. an undefined
                // $var landing in an expression the parser can't tokenize)
                // must never escape the tick and take the whole client down.
                // Stop just this script with a clear message and keep the
                // engine — and the app — alive.
                try
                {
                    if (StepOne(inst)) { progress = true; hot = inst; }
                }
                catch (Exception ex)
                {
                    _echo($"[script] {inst.Name} stopped: {ex.Message}");
                    inst.Running = false;
                    NotifyFinished(inst);
                }

                // The delay RT-bypass window closes at the next blocking
                // statement (G4: once the state is no longer `delayed`,
                // the next TickScript re-applies the RT early-return).
                if (inst.RtBypass && (inst.IsBlocked || !inst.Running))
                    inst.RtBypass = false;
            }
        }

        if (yielded)
            // Work still pending, but we returned to free the UI thread. Resume
            // almost immediately — the reschedule runs after the dispatcher has
            // drained any queued input, so a Stop/pause click gets in first.
            ScheduleTick?.Invoke(TimeSpan.FromMilliseconds(1));
        else
            // Turn ended naturally (every script idle / paused / waiting, or the
            // 1_000-statement safety cap) — the engine yielded on its own, so the
            // continuous-busy accumulator resets.
            _busyRunMs = 0;
    }

    /// <summary>
    /// Schedule a timer-driven Tick at the predicted end of the current
    /// roundtime, so RT-gated scripts resume even when the server sends no
    /// further traffic. Idempotent within the same RT window: while a
    /// wakeup is pending (<see cref="_rtWakeupAt"/> in the future) we don't
    /// schedule another. The 0.2s pad covers parser/state-apply latency
    /// between the prompt that clears RT and our next gate check.
    /// </summary>
    private void ScheduleRoundTimeWakeup()
    {
        if (ScheduleTick is null || RoundTimeRemainingSeconds is null) return;
        var now = DateTime.UtcNow;
        if (now < _rtWakeupAt) return; // wakeup already pending
        int secs = RoundTimeRemainingSeconds();
        if (secs <= 0) return; // gate said RT>0, but now it's gone — let the
                               // outer loop re-check next iteration instead
                               // of scheduling a no-op timer
        var delay = TimeSpan.FromSeconds(secs + 0.2);
        _rtWakeupAt = now + delay;
        ScheduleTick(delay);
    }

    /// <summary>Run registered actions against either a game line (regex/literal
    /// patterns) or, when <paramref name="line"/> is null, against eval-form
    /// actions only. Eval actions fire on rising edge (false → true).</summary>
    private void FireActions(ScriptInstance inst, string? line)
    {
        if (!inst.ActionsEnabled || inst.Actions.Count == 0) return;
        var snapshot = inst.Actions.ToArray();
        foreach (var act in snapshot)
        {
            if (!act.Enabled) continue;
            bool fire;
            string[]? captures = null;
            if (act.IsEval)
            {
                bool cur;
                try { cur = ScriptExpression.EvalBool(act.Pattern, inst, Globals, UserVarLookup); }
                catch { cur = false; }
                fire = cur && !act.LastEvalResult;
                act.LastEvalResult = cur;
            }
            else
            {
                if (line is null) continue;
                // Single match pass using the pre-compiled Regex stored on
                // the action at registration. Avoids paying for both a
                // TryMatch-style fire detection AND a separate Regex.Match
                // for capture extraction on every game line.
                if (act.IsRegex)
                {
                    var rx = act.CompiledRegex;
                    if (rx is null) { fire = false; }
                    else
                    {
                        var m = rx.Match(line);
                        fire = m.Success;
                        if (fire)
                        {
                            captures = new string[10];
                            captures[0] = m.Value;
                            for (int i = 1; i < m.Groups.Count && i <= 9; i++)
                                captures[i] = m.Groups[i].Value;
                            for (int i = 0; i < 10; i++) captures[i] ??= string.Empty;
                        }
                    }
                }
                else
                {
                    fire = line.IndexOf(act.Pattern, StringComparison.Ordinal) >= 0;
                }
            }
            if (!fire) continue;
            DbgEcho(inst, 5, $"action fired: \"{act.Command}\" (pattern: \"{act.Pattern}\")");

            // Push a dedicated $-frame for the body so $1..$9 refer to the
            // regex captures in isolation. Eval actions and literal triggers
            // have no captures, so the pushed frame is empty — either way
            // the caller's script-arg frame is preserved, not clobbered.
            bool framePushed = false;
            if (captures != null || !act.IsEval)
            {
                inst.DollarStack.Push(captures ?? new string[10]);
                inst.DollarCounts.Push(captures is null
                    ? 0
                    : captures.Skip(1).Count(c => c is not null));
                framePushed = true;
            }
            try
            {
                // Action bodies commonly chain multiple statements with ';'
                // (e.g. `var kronars $1; eval kronars replacere("%kronars", ",", "")`).
                // Split first, then substitute each statement at dispatch
                // time so %-references to vars written earlier in the chain
                // see the fresh value, not the pre-fire one.
                foreach (var stmt in SplitSemicolons(act.Command))
                    Dispatch(SubstituteVars(stmt, inst), inst, 0, -1);
            }
            catch (Exception ex)
            { _echo($"[script] {inst.Name} action error: {ex.Message}"); }
            finally
            {
                if (framePushed && inst.DollarStack.Count > 0)
                {
                    inst.DollarStack.Pop();
                    if (inst.DollarCounts.Count > 0) inst.DollarCounts.Pop();
                }
            }
        }
    }

    // ── Statement dispatch ──────────────────────────────────────────────────

    private bool StepOne(ScriptInstance inst)
    {
        // Drain any pending semicolon-split sends before advancing the PC.
        if (inst.PendingSends.Count > 0)
        {
            // Honor a `send` segment's leading delay before it may be sent.
            // This is independent of (and stacks with) the engine-level
            // roundtime gate, which already skips StepOne entirely during RT
            // — so a delayed send fires at max(RT-end, delay-end). Schedule a
            // self-wakeup so we drain even if no server traffic arrives.
            if (DateTime.UtcNow < inst.NextSendAt)
            {
                ScheduleTick?.Invoke(inst.NextSendAt - DateTime.UtcNow + TimeSpan.FromSeconds(0.05));
                return false;
            }

            // Game-bound continuation of a `put a;b;c` / `send a;b;c` series.
            // Match the tighter script-side cap used by the put/send case so
            // the semicolon-split tail can't bypass the per-prompt throttle.
            var peek = inst.PendingSends.Peek().Command;
            bool nextSendsToGame = peek.Length > 0 && peek[0] != '#' && peek[0] != '.';
            int effectiveLimit = nextSendsToGame ? 1 : _typeAhead.Limit;
            if (_inFlight >= effectiveLimit) return false;
            var next = inst.PendingSends.Dequeue().Command;

            // Arm the gate for the new head from ITS leading delay, measured
            // from now (i.e. from when this segment was dispatched — Genie4
            // CommandQueue.SetNextTime parity). Non-positive delays clear the
            // gate so `put` tails and eager (negative) sends fire immediately.
            inst.NextSendAt = inst.PendingSends.Count > 0 && inst.PendingSends.Peek().Delay > 0
                ? DateTime.UtcNow.AddSeconds(inst.PendingSends.Peek().Delay)
                : DateTime.MinValue;

            if (next.Length > 0)
            {
                if (next[0] == '#')
                {
                    HandleMetaCommand(next, inst);
                }
                else if (TrySlashCommand(next))
                {
                    // claimed by an extension — client-side, no _inFlight bump.
                }
                else
                {
                    _inFlight++;
                    EchoCommand?.Invoke(inst.Name, next);
                    Extensions.DispatchCommand(next);
                    _sendCommand(next);
                }
            }
            return true;
        }

        if (inst.Pc >= inst.Lines.Count)
        {
            inst.Running = false;
            _echo($"[script] {inst.Name} done");
            NotifyFinished(inst);
            return false;
        }

        var line = inst.Lines[inst.Pc];
        int currentIdx = inst.Pc;
        inst.Pc++;

        var t = line.Trimmed;
        // Skip blank lines and comments. In Genie 4 a script line whose first
        // non-whitespace character is `#` is ALWAYS a comment — including
        // `#debug 10`, `#include foo`, commented-out code like `#goto LABEL`,
        // and prose (`#todo ...`). Meta-commands are never invoked as a bare
        // `#xxx` script line; a script runs one only by sending it to the
        // command line via put/send (`put #echo foo`, `put #goto 400`), which
        // the `put`/`send` case handles separately. So `#` here means "ignore
        // the rest of the line" — nothing is substituted or dispatched.
        if (t.Length == 0) return true;
        if (t[0] == '#') return true;
        if ((t[0] == ':' || t[^1] == ':') && !t.Contains(' '))
        {
            inst.Trace.Add($"passing label {t.Trim(':')}", line.Origin, line.LineNumber);
            return true;
        }

        // Brace block delimiters are structural — the parser already mapped
        // jumps over them; at runtime they're no-ops.
        if (t == "{") return true;
        if (t == "}")
        {
            // While-loop closing brace: jump back to the while line so the
            // condition is re-evaluated.
            if (inst.WhileBackJump.TryGetValue(currentIdx, out var back))
            {
                inst.Pc = back;
                return true;
            }
            // When the closing brace terminates a true branch inside an
            // if/elseif chain, skip past any following elseif/else branches.
            if (inst.BraceEndJump.TryGetValue(currentIdx, out var skipTo))
                inst.Pc = skipTo;
            return true;
        }

        // `action ... when <pattern>` is registered raw — HandleAction
        // substitutes the pattern itself at registration time, but the
        // command body must keep its $1/%var references intact so they
        // resolve against the action's regex captures when it later fires.
        // Pre-substituting here would bake the script's current scope into
        // the action body and lose the capture references entirely.
        bool isActionStmt = t.Length >= 7 &&
                            (t.StartsWith("action ", StringComparison.OrdinalIgnoreCase) ||
                             t.Equals("action",     StringComparison.OrdinalIgnoreCase));

        // `js <expr>` / `jscall <var> <expr>` hand their JS body to the engine
        // RAW: Genie 4 array libraries resolve %/$ themselves (getVar/getGlobal)
        // inside the function, so pre-substituting would bypass that AND risk
        // breaking the JS string literal / injecting. `__jsinclude` (parser-
        // emitted .js include) carries a file path that must pass through intact.
        bool isJsStmt = t.StartsWith("js ", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("javascript ", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("jscall ", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("__jsinclude ", StringComparison.OrdinalIgnoreCase);

        // Undefined vars never abort a line: Genie 4 leaves them literal in
        // the substituted text and the dispatch/eval layers treat the remnant
        // as inert string data. (An earlier design reserved an AbortReason
        // flag here for undefined-$var aborts; it was never set and the
        // mechanism is gone — G4 has no such stop.)
        var substituted = (isActionStmt || isJsStmt) ? t : SubstituteVars(t, inst);

        // Level 10: trace every executed line. Suppress repeats when a
        // statement backed off and re-attempted (the put-throttle, RT gate,
        // matchwait, etc. all leave Pc pointing at the same line until they
        // make progress) so the trace shows one entry per line, not one
        // entry per retry tick.
        if (inst.LastDebugLine != line.LineNumber)
        {
            DbgEcho(inst, 10, $"{inst.Name}:{line.LineNumber} {substituted}");
            inst.LastDebugLine = line.LineNumber;
        }

        return Dispatch(substituted, inst, line.LineNumber, currentIdx);
    }

    /// <param name="text">Statement text, already %var/$var-substituted.</param>
    /// <param name="currentIdx">Index in inst.Lines of the source line, used for if/else jump lookups.</param>
    private bool Dispatch(string text, ScriptInstance inst, int lineNo, int currentIdx)
    {
        var (cmd, rest) = SplitCmd(text);
        var lower = cmd.ToLowerInvariant();

        // if_1..if_9
        if (lower.Length == 4 && lower.StartsWith("if_") && char.IsDigit(lower[3]))
        {
            var key = lower[3..];
            // Genie4 parity (Script.cs:3817): if_N is `argcount >= N`, NOT
            // "%N is present and non-empty". They diverge only when a numbered
            // var was set manually (e.g. `var 3 x`) or an arg was passed empty.
            int n = key[0] - '0';
            int ac = GetArgCount(inst);
            bool present = ac >= n;
            DbgEcho(inst, 3, $"if_{key} (argcount={ac} >= {n}) = {present}");
            // The parser may have rewritten `if_N <stmt>` / `if_N then <stmt>`
            // into block form prefixed with " then" so BuildIfMaps could
            // treat it as a regular block-if. Strip the leading `then`
            // keyword before passing to HandleConditional, which expects
            // the inline body (or empty / "{" for block form).
            int tIdx = ScriptParser.FindThenKeyword(rest);
            var afterThen = tIdx >= 0 ? rest[(tIdx + 4)..].Trim() : rest;
            return HandleConditional(present, afterThen, inst, lineNo, currentIdx);
        }

        switch (lower)
        {
            case "if":
            case "elseif":
            case "while":
            {
                int thenIdx = ScriptParser.FindThenKeyword(rest);
                if (thenIdx < 0)
                {
                    // #135: the usual cause is a quote typo — an odd number of
                    // " makes the quote-aware then-scanner read the tail of the
                    // line (including the real `then`) as string content.
                    if (WarningsSuppressed?.Invoke() != true)   // #151 ignorescriptwarnings
                    {
                        int quotes = 0;
                        foreach (var ch in rest) if (ch == '"') quotes++;
                        var hint = quotes % 2 == 1 ? " (unbalanced \" quotes?)" : "";
                        _echo($"[script] {inst.Name}:{lineNo} '{lower}' missing 'then'{hint}");
                    }
                    return true;
                }
                var condText  = rest[..thenIdx].Trim();
                var afterThen = rest[(thenIdx + 4)..].Trim();
                bool cond = EvalConditionSafe(condText, inst, $"line {lineNo}", out var dbgNote);
                DbgEcho(inst, 3, $"{lower} ({condText}) = {cond}" +
                                 (dbgNote is null ? "" : $"  ⚠ {dbgNote}"));
                return HandleConditional(cond, afterThen, inst, lineNo, currentIdx);
            }

            case "else":
                // Block-form else: jump table already placed by the parser.
                if (inst.ElseJump.TryGetValue(currentIdx, out var elseTarget))
                {
                    inst.Pc = elseTarget;
                    return true;
                }
                // Inline-form else: `else <stmt>` on its own line trailing an
                // `if ... then <stmt>`. When reached by fall-through, the
                // preceding if's condition was false, so the else body runs.
                // (When true, the `then` branch jumps away and we never get
                // here.) Matches Genie4's split of `else <body>` into two
                // lines: `else` + `<body>`.
                if (rest.Length > 0)
                    return Dispatch(rest, inst, lineNo, currentIdx);
                return true;

            case "put":
            case "send":
            {
                // Genie meta-commands routed via 'put' (#tvar, #echo,
                // #mapper, .scriptname, ...) are intercepted here instead
                // of being sent to the game. The '.' prefix launches a
                // sub-script (Genie4 parity); critically it must NOT
                // increment _inFlight or the type-ahead budget will leak
                // because there's no game prompt to release it.
                if (rest.Length > 0 && (rest[0] == '#' || rest[0] == '.'))
                {
                    if (rest[0] == '.')
                        _handleHashCmd?.Invoke(rest);
                    else
                        HandleMetaCommand(rest, inst);
                    return true;
                }

                // Genie's ';' separates multiple commands in a single put/send.
                // Each is queued and drained one-per-tick so the type-ahead
                // budget is respected per command, not per statement.
                //
                // For `send` ONLY, each segment may carry a leading delay in
                // seconds (Genie4 CommandQueue parity) — e.g. `send fire;0.5
                // unload my $weapon` fires `fire`, waits 0.5s (plus roundtime),
                // then `unload …`. A leading '-' is accepted and treated as
                // "send eagerly" (no wait). `put` never parses a delay, so its
                // behavior is unchanged.
                bool isSend = lower == "send";
                var parts = SplitSemicolons(rest);
                if (parts.Count == 0) return true;

                var segs = new List<(double Delay, string Cmd)>(parts.Count);
                foreach (var p in parts)
                    segs.Add(isSend ? ParseSendDelay(p) : (0.0, p));

                var first = segs[0].Cmd;
                bool sendsToGame = first.Length > 0 && first[0] != '#' && first[0] != '.';

                // A game-bound first segment that must wait can't be fired
                // inline — push the whole series through PendingSends and let
                // the drain gate honor each delay (including this one). The
                // common (no-delay) case keeps the inline fast path below.
                if (isSend && sendsToGame && segs[0].Delay > 0)
                {
                    foreach (var s in segs)
                        inst.PendingSends.Enqueue(new PendingSend(s.Cmd, s.Delay));
                    inst.NextSendAt = DateTime.UtcNow.AddSeconds(segs[0].Delay);
                    return true;
                }

                // Game-bound puts pipeline at most 1 deep regardless of the
                // session-wide TypeAheadLimit (which is calibrated higher
                // for the mapper's batched walking). The tighter cap closes
                // the race in which command N triggered roundtime but the
                // server's <roundTime> tag hadn't arrived when command N+1
                // was about to fire — without it the script blasts past the
                // RT gate because GslGameState.RoundTimeRemaining is still
                // stale (genie4 parity: CommandQueue.Poll(InRoundtime, ...)
                // holds the next command in the queue until RT clears, see
                // Genie4 CommandQueue.cs:115). Meta commands (#…) and
                // sub-script launches (.…) never reach the server, so they
                // stay on the session-wide limit.
                int effectiveLimit = sendsToGame ? 1 : _typeAhead.Limit;
                if (_inFlight >= effectiveLimit)
                {
                    inst.Pc--; // re-execute next tick when budget frees up
                    return false;
                }

                for (int p = 1; p < segs.Count; p++)
                    inst.PendingSends.Enqueue(new PendingSend(segs[p].Cmd, segs[p].Delay));

                // Arm the gate for the first tail segment so its delay is
                // honored on the next drain; clear it otherwise (this also
                // resets any stale value left by a prior delayed send).
                inst.NextSendAt = segs.Count > 1 && segs[1].Delay > 0
                    ? DateTime.UtcNow.AddSeconds(segs[1].Delay)
                    : DateTime.MinValue;

                if (first.Length > 0 && first[0] == '#')
                {
                    HandleMetaCommand(first, inst);
                }
                else if (first.Length > 0 && first[0] == '.')
                {
                    // `put .scriptname args` — never goes to game, so no
                    // _inFlight bump.
                    _handleHashCmd?.Invoke(first);
                }
                else if (TrySlashCommand(first))
                {
                    // `put /sort …` claimed by an extension — client-side,
                    // so no _inFlight bump either.
                }
                else
                {
                    _inFlight++;
                    EchoCommand?.Invoke(inst.Name, first);
                    Extensions.DispatchCommand(first);
                    _sendCommand(first);
                }
                return true;
            }

            case "echo":
                _echo(rest);
                return true;

            case "pause":
            case "waitpause":
            {
                // pause [N] — block for N seconds (default 1). Genie4 parity:
                // pure timer, no roundtime coupling. Scripts that want to
                // wait out roundtime explicitly use `if ($roundtime > 0)
                // then pause $roundtime`.
                //
                // waitpause is a Genie 4 synonym used heavily in casting
                // scripts (cast.cmd, castm.cmd, etc.); strictly Genie 4
                // chains pause+wait, but in practice scripts use small
                // fractional values (waitpause .5) where the difference
                // doesn't matter. Aliasing to pause covers the real-world
                // usage without introducing a new PauseMode.
                double secs = 1.0;
                if (!string.IsNullOrWhiteSpace(rest) &&
                    double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    secs = p;
                inst.Paused     = true;
                inst.PauseMode  = PauseMode.Pause;
                inst.PauseUntil = DateTime.UtcNow.AddSeconds(secs);
                ScheduleTick?.Invoke(TimeSpan.FromSeconds(secs + 0.05));
                DbgEcho(inst, 2, $"pause {secs}s");
                return false;
            }

            case "wait":
            {
                // wait — block until next game prompt. Genie4 parity: no
                // roundtime gating, no timer component (script unblocks on
                // the first prompt event after this statement).
                inst.Paused     = true;
                inst.PauseMode  = PauseMode.Wait;
                inst.PauseUntil = DateTime.MinValue; // no timer — prompt-driven
                DbgEcho(inst, 2, "wait (prompt)");
                return false;
            }

            case "delay":
            {
                // delay [N] — block for N seconds (default 1). Ignores
                // roundtime and game prompts entirely.
                double secs = 1.0;
                if (!string.IsNullOrWhiteSpace(rest) &&
                    double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    secs = p;
                inst.Paused     = true;
                inst.PauseMode  = PauseMode.Delay;
                inst.PauseUntil = DateTime.UtcNow.AddSeconds(secs);
                ScheduleTick?.Invoke(TimeSpan.FromSeconds(secs + 0.05));
                DbgEcho(inst, 2, $"delay {secs}s (ignoring RT)");
                return false;
            }

            case "move":
            {
                // move <cmd> — send <cmd> to the game and pause the script
                // until a new room arrives (RoomTitleEvent → OnRoomChanged).
                // Matches Genie4: used by walking scripts like bank.cmd's
                // `move go bank` / `move s`. An empty arg just waits for a
                // room change without sending anything (rare).
                if (rest.Trim().Length > 0)
                {
                    if (_inFlight >= _typeAhead.Limit)
                    {
                        inst.Pc--; // re-run next tick when budget frees up
                        return false;
                    }
                    _inFlight++;
                    EchoCommand?.Invoke(inst.Name, rest);
                    Extensions.DispatchCommand(rest);
                    _sendCommand(rest);
                }
                inst.Paused     = true;
                inst.PauseMode  = PauseMode.Move;
                inst.PauseUntil = DateTime.MaxValue; // wakes only on room change
                DbgEcho(inst, 2, $"move \"{rest}\" (waiting for room change)");
                return false;
            }

            case "nextroom":
                // Pause until the next room change without sending anything.
                // Genie4 equivalent for waiting on someone else's movement
                // (e.g. dragging) or a passive room transition.
                inst.Paused     = true;
                inst.PauseMode  = PauseMode.Move;
                inst.PauseUntil = DateTime.MaxValue;
                DbgEcho(inst, 2, "nextroom (waiting for room change)");
                return false;

            case "goto":
            {
                var gLabel = rest.Trim();
                // Capture before HotReload — it replaces inst.Lines, so
                // currentIdx only indexes the OLD list.
                var gOrigin = inst.Lines[currentIdx].Origin;
                // #script reload is consumed here — Genie 4 hot-reloads at the
                // next goto (its "at the next label" message notwithstanding),
                // so a running loop picks up edits at its natural jump point.
                if (inst.PendingReload && !HotReload(inst, gLabel)) return false;
                if (inst.Labels.TryGetValue(gLabel, out var gi))
                {
                    inst.Pc = gi + 1;
                    DbgEcho(inst, 1, $"goto {gLabel} → line {TargetLineNo(inst, gi + 1)}");
                    inst.Trace.Add($"goto {gLabel}", gOrigin, lineNo);
                }
                else { _echo($"[script] unknown label: {rest}"); inst.Running = false; NotifyFinished(inst); }
                return true;
            }

            case "gosub":
            {
                var (label, gosubArgs) = SplitCmd(rest);
                // Genie4 parity: `gosub clear` wipes the return-address stack
                // without jumping. Subsequent `return` would then fail.
                if (label.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    // Match Genie4's m_oCurrentLine.Clear(): wipe BOTH the
                    // gosub return-address stack and the per-frame $-scope
                    // (DollarStack), restoring the initial script-arg frame
                    // at the bottom. Without this, repeated `gosub X / gosub
                    // clear` cycles (e.g. automapper.cmd's MOVE → MOVE.DONE)
                    // leak frames; eventually $0 reads stale gosub args
                    // instead of the current frame.
                    inst.GosubStack.Clear();
                    while (inst.DollarStack.Count > 1) inst.DollarStack.Pop();
                    while (inst.DollarCounts.Count > 1) inst.DollarCounts.Pop();
                    DbgEcho(inst, 1, "gosub clear (stack emptied)");
                    return true;
                }
                if (!inst.Labels.TryGetValue(label.Trim(), out var ss))
                { _echo($"[script] unknown label: {label}"); inst.Running = false; NotifyFinished(inst); return false; }
                // MaxGoSubDepth (Genie 4 parity): guard against runaway recursion
                // (e.g. a missing RETURN). Stop the script when the call stack
                // would exceed the configured depth.
                if (inst.GosubStack.Count >= GoSubDepthLimit)
                {
                    _echo($"[script] {inst.Name}: maximum GOSUB depth ({GoSubDepthLimit}) exceeded at '{label.Trim()}'. Stopped — check for a missing RETURN or runaway recursion.");
                    inst.Running = false;
                    NotifyFinished(inst);
                    return false;
                }
                inst.GosubStack.Push(inst.Pc);
                inst.Pc = ss + 1;
                DbgEcho(inst, 1, $"gosub {label.Trim()} → line {TargetLineNo(inst, ss + 1)}" +
                    (string.IsNullOrEmpty(gosubArgs) ? "" : $" args: {gosubArgs}"));
                inst.Trace.Add($"gosub {label.Trim()}", inst.Lines[currentIdx].Origin, lineNo);
                // Gosub arguments populate $0..$9 on a NEW stack frame — they
                // do NOT touch %0..%9 (script args). On return the frame is
                // popped, restoring the caller's $-scope. Matches Genie4.
                var frame = new string[10];
                frame[0] = gosubArgs ?? string.Empty;
                int gosubArgCount = 0;
                if (!string.IsNullOrEmpty(gosubArgs))
                {
                    // Quote/brace-aware split (Genie 4 parity), NOT a plain
                    // Split(' '): a quoted multi-word arg like
                    // "Moonmage Training Menu" must stay a single $-arg with its
                    // outer quotes stripped. The old space-split shattered it into
                    // $4="Moonmage / $5=Training / $6=Menu" and leaked stray quotes
                    // into the other args — which is what made menu scripts
                    // (mm_train) spray a fresh window per option and mangle the
                    // click commands.
                    var parts = Genie.Core.Parsing.ArgumentParser.ParseArgs(gosubArgs);
                    gosubArgCount = parts.Count;
                    for (int a = 0; a < 9; a++)
                        frame[a + 1] = a < parts.Count ? parts[a] : string.Empty;
                }
                inst.DollarStack.Push(frame);
                inst.DollarCounts.Push(gosubArgCount);
                return true;
            }

            case "return":
                if (inst.GosubStack.Count > 0)
                {
                    var retPc = inst.GosubStack.Pop();
                    if (inst.DollarStack.Count > 1) inst.DollarStack.Pop();
                    if (inst.DollarCounts.Count > 1) inst.DollarCounts.Pop();
                    DbgEcho(inst, 1, $"return → line {TargetLineNo(inst, retPc)}");
                    inst.Trace.Add("return", inst.Lines[currentIdx].Origin, lineNo);
                    inst.Pc = retPc;
                }
                else { inst.Running = false; NotifyFinished(inst); }   // return on an empty stack ends the script
                return true;

            case "exit":
                inst.Trace.Add("exit", inst.Lines[currentIdx].Origin, lineNo);
                inst.Running = false;
                NotifyFinished(inst);
                return false;

            case "match":
            {
                var (label, pat) = SplitCmd(rest);
                if (!string.IsNullOrEmpty(label))
                {
                    WarnUnknownMatchLabel(inst, label.Trim(), lineNo);
                    inst.PendingMatches.Add((label.Trim(), pat, false));
                    DbgEcho(inst, 2, $"match {label.Trim()} \"{pat}\"");
                }
                return true;
            }

            case "matchre":
            {
                var (label, pat) = SplitCmd(rest);
                if (!string.IsNullOrEmpty(label))
                {
                    WarnUnknownMatchLabel(inst, label.Trim(), lineNo);
                    inst.PendingMatches.Add((label.Trim(), pat, true));
                    DbgEcho(inst, 2, $"matchre {label.Trim()} \"{pat}\"");
                }
                return true;
            }

            case "matchwait":
                inst.InMatchWait = true;
                if (double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mw))
                    inst.MatchWaitDeadline = DateTime.UtcNow.AddSeconds(mw);
                else
                    inst.MatchWaitDeadline = DateTime.MaxValue;
                DbgEcho(inst, 2, $"matchwait ({inst.PendingMatches.Count} patterns" +
                    (mw > 0 ? $", timeout {mw}s)" : ")"));
                return false;

            case "waitfor":
                inst.WaitForPattern  = rest;
                inst.WaitForIsRegex  = false;
                inst.WaitForDeadline = DateTime.MaxValue;
                DbgEcho(inst, 2, $"waitfor \"{rest}\"");
                return false;

            case "waitforre":
                inst.WaitForPattern  = rest;
                inst.WaitForIsRegex  = true;
                inst.WaitForDeadline = DateTime.MaxValue;
                DbgEcho(inst, 2, $"waitforre \"{rest}\"");
                return false;

            case "waiteval":
                // waiteval <expression> — block until expression evaluates true.
                // The expression is stored raw (not substituted) so live
                // variable state is re-read on each evaluation.
                inst.WaitEvalExpr     = rest;
                inst.WaitEvalDeadline = DateTime.MaxValue;
                DbgEcho(inst, 2, $"waiteval {rest}");
                return false;

            case "debug":
            {
                var level = rest.Trim();
                if (int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dl))
                    inst.DebugLevel = dl;
                else
                    inst.DebugLevel = 0;
                _echo($"[script] {inst.Name} debug level set to {inst.DebugLevel}");
                DebugLevelChanged?.Invoke(inst.Name, inst.DebugLevel);
                return true;
            }

            case "shift":
            {
                // Shifts %1→drop, %2→%1, etc. — for walking command-line args.
                // Genie4 parity (Script.cs:2949 EvalShift): driven by argcount
                // (which can exceed 9, e.g. an 11-move walk), it decrements the
                // count, clears the vacated top slot to "" (not remove), and
                // rebuilds %0 from the remaining args.
                int ac = GetArgCount(inst);
                if (ac > 0)
                {
                    for (int k = 1; k < ac; k++)
                        inst.Vars[k.ToString()] =
                            inst.Vars.TryGetValue((k + 1).ToString(), out var nv) ? nv : string.Empty;
                    inst.Vars[ac.ToString()] = string.Empty;
                    inst.Vars["argcount"] = (ac - 1).ToString(CultureInfo.InvariantCulture);
                    var sb = new StringBuilder();
                    for (int k = 1; k < ac; k++)
                        if (inst.Vars.TryGetValue(k.ToString(), out var av) && av.Length > 0)
                            sb.Append(sb.Length > 0 ? " " : "").Append(av);
                    inst.Vars["0"] = sb.ToString();
                }
                return true;
            }

            case "var":
            case "vars":
            case "variable":
            case "setvar":
            case "setvariable":
            {
                var (vn, vv) = SplitCmd(rest);
                inst.Vars[vn.Trim()] = vv;
                DbgEcho(inst, 4, $"var {vn.Trim()} = \"{vv}\"");
                return true;
            }

            case "unvar":
            case "unvariable":
            case "unsetvar":
            case "unsetvariable":
            case "deletevariable":
                DbgEcho(inst, 4, $"unvar {rest.Trim()}");
                inst.Vars.Remove(rest.Trim());
                return true;

            case "save":
                // Genie4 stores the save command's argument in the local
                // variable "s" — scripts read it back as %s.
                inst.Vars["s"] = rest;
                DbgEcho(inst, 4, $"save → s = \"{rest}\"");
                return true;

            case "random":
            {
                // random <min> <max>  → picks an integer in [min, max] (inclusive)
                // and stores it in %r, matching Genie3/4 semantics.
                var (aStr, bStr) = SplitCmd(rest);
                if (!int.TryParse(aStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lo) ||
                    !int.TryParse(bStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hi))
                {
                    _echo($"[script] {inst.Name}:{lineNo} random: usage 'random <min> <max>'");
                    return true;
                }
                if (hi < lo) (lo, hi) = (hi, lo);
                inst.Vars["r"] = inst.Rng.Next(lo, hi + 1)
                    .ToString(CultureInfo.InvariantCulture);
                return true;
            }

            case "timer":
            {
                // timer start | stop | clear | reset
                // %timer reads the elapsed seconds since the last 'timer start'.
                var op = rest.Trim().ToLowerInvariant();
                switch (op)
                {
                    case "":
                    case "start": inst.TimerStart = DateTime.UtcNow; break;
                    case "stop":
                    case "clear":
                    case "reset": inst.TimerStart = null;            break;
                    default:
                        _echo($"[script] {inst.Name}:{lineNo} timer: unknown sub-command '{op}'");
                        break;
                }
                return true;
            }

            case "counter":
                // Genie4 shorthand: `counter <op> [n]` is `math c <op> [n]`.
                rest = "c " + rest;
                goto case "math";

            case "math":
            {
                // math <var> <op> <n>   ops: add | subtract | multiply | divide | set
                var (vn, tail) = SplitCmd(rest);
                var (op, arg)  = SplitCmd(tail);
                if (string.IsNullOrEmpty(vn) || string.IsNullOrEmpty(op))
                {
                    _echo($"[script] {inst.Name}:{lineNo} math: usage 'math <var> <op> <n>'");
                    return true;
                }
                double cur = inst.Vars.TryGetValue(vn, out var cv)
                          && double.TryParse(cv, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                ? d : 0;
                if (!double.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    n = 0;
                double result = op.ToLowerInvariant() switch
                {
                    "add"      => cur + n,
                    "subtract" => cur - n,
                    "multiply" => cur * n,
                    "divide"   => n == 0 ? 0 : cur / n,
                    "modulus"  => n == 0 ? 0 : cur % n,
                    "set"      => n,
                    _          => cur,
                };
                var resultStr = result == Math.Floor(result) && !double.IsInfinity(result)
                    ? ((long)result).ToString(CultureInfo.InvariantCulture)
                    : result.ToString("0.################", CultureInfo.InvariantCulture);
                inst.Vars[vn] = resultStr;
                DbgEcho(inst, 4, $"math {vn} {op} {arg.Trim()} → {resultStr}");
                return true;
            }

            case "action":
                HandleAction(rest, inst, lineNo);
                return true;

            // ── JavaScript interop (#104): call a .js function library from a
            // .cmd. `js <expr>` evaluates (result discarded); `jscall <var> <expr>`
            // stores the result in %var; `__jsinclude <path>` (parser-emitted by
            // `include <file>.js`) loads a library into this script's JS context.
            // The JS body is passed RAW (not %/$-substituted) — Genie 4 libraries
            // resolve those themselves via getVar/getGlobal. See JsLibraryContext.
            case "js":
            case "javascript":
                EnsureJsLib(inst).Evaluate(rest);
                return true;

            case "jscall":
            {
                var (vn, jexpr) = SplitCmd(rest);
                var jr = EnsureJsLib(inst).Evaluate(jexpr);
                if (vn.Length > 0) inst.Vars[vn.Trim()] = jr;
                DbgEcho(inst, 4, $"jscall {vn.Trim()} = \"{jr}\"");
                return true;
            }

            case "__jsinclude":   // parser-emitted by `include <file>.js`
                EnsureJsLib(inst).LoadLibrary(rest.Trim());
                return true;

            case "plugin":
            {
                var (vn, _) = SplitCmd(rest);
                if (vn.Length > 0) inst.Vars[vn.Trim()] = string.Empty;
                _echo($"[script] {inst.Name}:{lineNo} 'plugin' is not supported in Genie5; cleared %{vn.Trim()}");
                return true;
            }

            case "pluginscript":
                _echo($"[script] {inst.Name}:{lineNo} 'pluginscript' is not supported in Genie5");
                return true;

            case "eval":
            case "evaluate":
            case "evalmath":
            case "evaluatemath":
            {
                var (vn, expr) = SplitCmd(rest);
                if (string.IsNullOrEmpty(vn))
                {
                    _echo($"[script] {inst.Name}:{lineNo} {lower} needs varname");
                    return true;
                }
                try
                {
                    var result = ScriptExpression.Eval(expr, inst, Globals, UserVarLookup);
                    bool isMath = lower == "evalmath" || lower == "evaluatemath";
                    inst.Vars[vn.Trim()] = isMath
                        ? ScriptExpression.ToNum(result).ToString("0.################", CultureInfo.InvariantCulture)
                        : ScriptExpression.ToStr(result);
                }
                catch
                {
                    // Genie convention: failed eval leaves the var as empty string.
                    inst.Vars[vn.Trim()] = string.Empty;
                }
                DbgEcho(inst, 4, $"{lower} {vn.Trim()} = \"{inst.Vars.GetValueOrDefault(vn.Trim(), "")}\" (expr: {expr})");
                return true;
            }

            case "do":
                // Genie 4's `do` (re-send a command until $repeatregex matches)
                // is intentionally NOT implemented: zero usage across the whole
                // community corpus (Tirost + EtherianDR, ~130 scripts incl.
                // hunt.cmd). This guard consumes the line so a stray `do` warns
                // instead of silently leaking "do …" to the game via the default
                // case below. If a real need surfaces, build the full retry loop
                // (needs $repeatregex + a flood-safety cap) — see backlog group D.
                _echo($"[script] {inst.Name}:{lineNo} 'do' command is not supported — line ignored");
                return true;

            default:
            {
                // Genie 4 parity: lines whose first word isn't a recognized
                // script command are sent verbatim to the game socket. That's
                // the entire point of .cmd scripts — sequences of game
                // commands with control-flow sprinkled in. The earlier
                // "unknown command" echo broke every script that ran a bare
                // verb like `info`, `prep mb`, or `go north`. Mirrors
                // Genie 4's CommandParser fall-through behaviour.
                //
                // Gate through the same type-ahead-limit dance as `put` so a
                // tight bare-command sequence respects DR's "you may only
                // type ahead 2 commands" throttle. `text` is already
                // post-substitution so $/% vars are resolved.
                if (_inFlight >= 1)
                {
                    inst.Pc--; // re-execute next tick when the budget frees up
                    return false;
                }
                // A bare `/sort …` line works like `put /sort …` — extension
                // console commands are claimable from scripts too.
                if (TrySlashCommand(text)) return true;
                _inFlight++;
                EchoCommand?.Invoke(inst.Name, text);
                Extensions.DispatchCommand(text);
                _sendCommand(text);
                return true;
            }
        }
    }

    /// <summary>Offer a game-bound <c>/…</c> segment to the extensions' console
    /// command dispatch (/calc, /sort, /tt, …) so scripts can drive them the way
    /// typed input can. A claimed command is client-side: the text never reaches
    /// the game, so callers must NOT bump <c>_inFlight</c> — no game prompt will
    /// ever release the budget. Unclaimed slashes return false and are sent to
    /// the game verbatim, mirroring the typed-input fall-through.</summary>
    private bool TrySlashCommand(string cmd) =>
        cmd.Length > 0 && cmd[0] == '/' && Extensions.DispatchSlashCommand(cmd);

    /// <summary>
    /// Evaluate a condition, treating empty input and parse errors as false.
    /// Genie scripts routinely test variables that are not yet set; an undefined
    /// <c>$hidden</c> substitutes to <c>""</c> and we want <c>if ($hidden)</c>
    /// to silently mean false rather than crashing.
    ///
    /// Unbalanced parentheses get Genie 4's tolerance: G4's evaluator ignored a
    /// stray trailing <c>)</c> and implicitly closed unfinished groups at end of
    /// text, and community scripts shipped with both shapes for years —
    /// travel.cmd's ferry/exchange/bank checks are all missing a close
    /// (<c>if ("%lirums" != "0" then</c>), and uber-script loop exits carry an
    /// extra one (<c>if (%i >= %n) && (%loop = 0)) then</c>). Those evaluated
    /// fine in G4 and false-forever here, which reads as broken routing, not as
    /// a typo. So on a parse failure we retry once with the parens balanced
    /// (quote-aware count) and warn that we did; only a condition that is still
    /// unparseable after balancing is treated as false.
    ///
    /// Both warnings are once per source per run; <paramref name="dbgNote"/>
    /// carries the same information on EVERY evaluation so the #debug trace can
    /// mark it — the once-only warning scrolls away hours before a stuck loop
    /// gets looked at.
    /// </summary>
    private bool EvalConditionSafe(string condText, ScriptInstance inst, string where,
                                    out string? dbgNote)
    {
        dbgNote = null;
        if (string.IsNullOrWhiteSpace(condText)) return false;

        // Strip a single layer of empty parens / whitespace; "(  )" → false.
        var stripped = condText.Trim();
        if (stripped.Length >= 2 && stripped[0] == '(' && stripped[^1] == ')')
        {
            var inner = stripped[1..^1].Trim();
            if (inner.Length == 0) return false;
        }

        // Genie 4 debug aid (Script.cs Eval:1379): at debuglevel > 0, a
        // substituted condition that still carries a sigil is almost always an
        // unassigned variable left literal — hint at it, then evaluate anyway
        // (the remnant is a plain string atom; ordering compares false).
        if (condText.IndexOf('%') >= 0)
            DbgEcho(inst, 1, $"Eval ({condText}) contains variable character '%'. Did you use an unassigned variable?");
        else if (condText.IndexOf('$') >= 0)
            DbgEcho(inst, 1, $"Eval ({condText}) contains variable character '$'. Did you use an unassigned variable?");

        try { return ScriptExpression.EvalBool(condText, inst, Globals, UserVarLookup); }
        catch (Exception ex)
        {
            var balanced = BalanceParens(condText);
            if (balanced is not null)
            {
                try
                {
                    bool v = ScriptExpression.EvalBool(balanced, inst, Globals, UserVarLookup);
                    dbgNote = "unbalanced parens (auto-balanced, G4 compat)";
                    if (inst.WarnedBadConditions.Add(where) &&
                        WarningsSuppressed?.Invoke() != true)
                    {
                        _echo($"[script] {inst.Name} {where}: unbalanced parentheses in " +
                              $"'{condText}' — auto-balanced (Genie 4 compat)");
                    }
                    return v;
                }
                catch { /* still unparseable — fall through to the hard error */ }
            }
            dbgNote = $"parse error: {ex.Message} — treated as false";
            WarnBadCondition(inst, where, condText, ex);
            return false;
        }
    }

    /// <summary>
    /// Rebalance a condition whose parentheses don't pair up: append the missing
    /// <c>)</c>s at the end, or strip that many stray <c>)</c>s off the end.
    /// Counting is quote-aware (parens inside "…" are text, and \" doesn't end
    /// the string). Returns null when the text is already balanced (the parse
    /// error lies elsewhere) or when the surplus closes aren't a trailing tail;
    /// other malformations survive rebalancing and fail the retry parse instead.
    /// </summary>
    private static string? BalanceParens(string s)
    {
        int net = 0;
        bool inQuote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < s.Length) i++;      // skip escaped char
                else if (c == '"') inQuote = false;
                continue;
            }
            if      (c == '"') inQuote = true;
            else if (c == '(') net++;
            else if (c == ')') net--;
        }
        if (net > 0)
            return s + new string(')', net);
        if (net < 0)
        {
            // Only strip if the surplus closes sit at the very end.
            var trimmed = s.TrimEnd();
            int strip = -net;
            if (trimmed.Length < strip) return null;
            for (int i = trimmed.Length - strip; i < trimmed.Length; i++)
                if (trimmed[i] != ')') return null;
            return trimmed[..^strip];
        }
        return null;
    }

    /// <summary>
    /// One-time (per script run, per source) warning for a condition that failed
    /// to PARSE — as opposed to one that legitimately evaluated false. Deduped
    /// so a malformed condition inside a loop (or a waiteval re-checked every
    /// tick) warns once, not per iteration.
    /// </summary>
    private void WarnBadCondition(ScriptInstance inst, string where, string condText, Exception ex)
    {
        if (!inst.WarnedBadConditions.Add(where)) return;   // dedupe even when suppressed
        if (WarningsSuppressed?.Invoke() == true) return;   // #151 ignorescriptwarnings
        _echo($"[script] {inst.Name} {where}: bad condition '{condText}' — " +
              $"{ex.Message}; treated as false");
    }

    /// <summary>
    /// One-time (per label per run) warning when a <c>match</c>/<c>matchre</c>
    /// registers a label the script doesn't define. The matchwait scanner skips
    /// unknown labels, so such a match can NEVER fire — every hit becomes a
    /// silent matchwait timeout (a community travel script pastes a combat
    /// block whose target label never came along, costing 7s + error spam at
    /// every loaded-crossbow stow). Warn at registration, where the typo is.
    /// </summary>
    private void WarnUnknownMatchLabel(ScriptInstance inst, string label, int lineNo)
    {
        if (inst.Labels.ContainsKey(label)) return;
        if (!inst.WarnedBadConditions.Add($"matchlabel:{label}")) return;
        if (WarningsSuppressed?.Invoke() == true) return;   // #151 ignorescriptwarnings
        _echo($"[script] {inst.Name}:{lineNo} match label '{label}' not found — " +
              "this match can never fire");
    }

    private bool HandleConditional(bool cond, string afterThen,
                                    ScriptInstance inst, int lineNo, int currentIdx)
    {
        // "then {" on the same line is a brace block, not an inline body.
        // The parser records an IfFalseJump for this line just like when the
        // '{' appears on its own next line.
        if (afterThen.Length > 0 && afterThen != "{")
        {
            // inline form: execute the after-then as a statement (only when true)
            if (cond) return Dispatch(afterThen, inst, lineNo, currentIdx);
            return true;
        }

        // block form
        if (cond) return true; // fall through into the block
        if (inst.IfFalseJump.TryGetValue(currentIdx, out var j)) inst.Pc = j;
        return true;
    }

    /// <summary>
    /// Handle a Genie-style meta-command, e.g. <c>#tvar Foo 1</c>, <c>#var Foo 1</c>,
    /// <c>#echo &gt;Log #DAF7A6 ...</c>, <c>#mapper reset</c>. These arrive via
    /// <c>put #...</c> in scripts and never reach the game.
    /// </summary>
    private void HandleMetaCommand(string text, ScriptInstance inst)
    {
        var (cmd, rest) = SplitCmd(text); // cmd starts with '#'
        switch (cmd.ToLowerInvariant())
        {
            case "#tvar":
            {
                // #tvar Name Value
                var (name, value) = SplitCmd(rest);
                if (name.Length > 0) Globals[name] = value;
                return;
            }
            // NOTE: #var is intentionally NOT handled here. It is the global,
            // persistent user-variable command (Genie 4 parity) and must behave
            // identically whether typed or run from a script — including `#var save`
            // writing variables.cfg. It therefore falls through to the default case
            // and is forwarded to the host's command engine (→ HandleVar →
            // Variables.Store). Script-LOCAL variables use the `var` / `setvariable`
            // statement (%name), handled separately above.
            case "#echo":
            {
                // #echo [>Window] [#RRGGBB | ColorName] message
                // Mirrors Genie4: foreground may be a hex code or a KnownColor name
                // (e.g. Crimson, DodgerBlue) — the downstream Brush.Parse handles both.
                // The option tokens are QUOTE-aware: the classic menu-script form is
                //   send #echo ">Moonmage Training Menu" cyan text
                // where the multi-word window target rides inside quotes. A plain
                // space split saw a token starting with '"' (not '>'), fell out of
                // the option loop, and dumped the whole line to the main window.
                // The MESSAGE remainder is left verbatim (quotes and all).
                string? window = null;
                string? color  = null;
                bool    mono   = false;
                var msg = rest;
                while (msg.Length > 0)
                {
                    string tok, after;
                    var t = msg.TrimStart();
                    if (t.Length > 1 && t[0] == '"' && t.IndexOf('"', 1) > 0)
                    {
                        var close = t.IndexOf('"', 1);
                        tok   = t[1..close];
                        after = t[(close + 1)..].TrimStart();
                    }
                    else
                    {
                        (tok, after) = SplitCmd(t);
                    }
                    // TrimStart, not [1..]: a target variable whose value
                    // already carries the chevron ("var w >Log" + "#echo >$w")
                    // expands to ">>Log" — degrade to "Log" rather than
                    // manufacturing a junk window literally named ">Log".
                    if (tok.Length > 0 && tok[0] == '>')
                    { window = tok.TrimStart('>'); msg = after; continue; }
                    if (string.Equals(tok, "mono", StringComparison.OrdinalIgnoreCase))
                    { mono = true; msg = after; continue; }
                    if (IsEchoColor(tok))
                    { color = tok; msg = after; continue; }
                    msg = t;   // non-option token starts the message — keep it verbatim
                    break;
                }
                if (window != null && EchoTo != null)
                    EchoTo(msg, window, color);
                else if ((color != null || mono) && EchoStyled != null)
                    EchoStyled(msg, color, mono);
                else
                    _echo(msg);
                return;
            }
            // #goto / #go2 fall through to the default case below, which
            // forwards them to the host (→ CommandEngine → MapperGoto → the
            // App's mapper walk). Don't handle them here.
            case "#mapper":
                // Forward to the command engine so a script-issued #mapper reset
                // reaches the same handler as a typed one (was a no-op stub).
                _handleHashCmd?.Invoke(text);
                return;
            case "#parse":
                // Inject fake game text as if the server had emitted it. Genie 4's
                // #parse fed all three per-line legs — scripts (waitfor/match),
                // the global user-trigger list, and plugins — so route through the
                // host injector that runs the full pipeline. Fall back to the
                // script-only feed when no host is wired (Core-only / TestHarness):
                // calling OnGameLine directly here would double-feed scripts if the
                // injector also ran, so it's one or the other.
                if (_injectGameLine is not null) _injectGameLine(rest);
                else                             OnGameLine(rest);
                return;
            default:
                // Forward unhandled # commands (e.g. #goto, #script) to the
                // host so the UI / mapper can process them.
                _handleHashCmd?.Invoke(text);
                return;
        }
    }

    /// <summary>
    /// Handle the <c>action</c> family. Forms:
    ///   action on | off
    ///   action clear
    ///   action remove &lt;pattern&gt;
    ///   action &lt;command&gt; when &lt;pattern&gt;
    ///   action &lt;command&gt; whenre &lt;regex&gt;
    /// The 'when' / 'whenre' keyword splits left/right; whatever comes before
    /// is the command body, whatever comes after is the trigger pattern. The
    /// command body is dispatched as a normal script statement on each fire.
    /// </summary>
    private void HandleAction(string rest, ScriptInstance inst, int lineNo)
    {
        var trimmed = rest.Trim();
        if (trimmed.Length == 0)
        {
            _echo($"[script] {inst.Name}:{lineNo} action: missing arguments");
            return;
        }

        // Optional leading "(label)" attaches a name to this action so it
        // can be toggled or removed by label later.
        string label = string.Empty;
        if (trimmed[0] == '(')
        {
            int close = trimmed.IndexOf(')');
            if (close > 0)
            {
                label   = trimmed[1..close].Trim();
                trimmed = trimmed[(close + 1)..].Trim();
            }
        }

        // Global on/off/clear (only meaningful when no label).
        if (label.Length == 0)
        {
            if (trimmed.Equals("on",    StringComparison.OrdinalIgnoreCase)) { inst.ActionsEnabled = true;  return; }
            if (trimmed.Equals("off",   StringComparison.OrdinalIgnoreCase)) { inst.ActionsEnabled = false; return; }
            if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase)) { inst.Actions.Clear();        return; }
        }
        else
        {
            // Per-label control: enable/disable/remove all triggers with this label.
            if (trimmed.Equals("on",     StringComparison.OrdinalIgnoreCase))
            { foreach (var a in inst.Actions) if (LabelMatch(a, label)) a.Enabled = true;  return; }
            if (trimmed.Equals("off",    StringComparison.OrdinalIgnoreCase))
            { foreach (var a in inst.Actions) if (LabelMatch(a, label)) a.Enabled = false; return; }
            if (trimmed.Equals("remove", StringComparison.OrdinalIgnoreCase))
            { inst.Actions.RemoveAll(a => LabelMatch(a, label));                            return; }
        }

        if (trimmed.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
        {
            // Pattern is matched against the registered (substituted) pattern,
            // so substitute here too — `action remove %move_OK` removes the
            // action whose stored pattern is the resolved %move_OK regex.
            // A miss is a SILENT no-op (Genie 4: Script.cs:3121 just calls
            // m_oActions.Remove and returns) — community scripts remove-then-
            // re-register defensively all the time, so uber startups printed
            // "[script] action: no trigger matched '…'" three-plus times per
            // run and it read as an error (smoke 2026-08-03 finding #10). The
            // diagnostic survives at debuglevel ≥3 for authors chasing a
            // remove that genuinely should have matched.
            var pat = SubstituteVars(trimmed[7..].Trim(), inst);
            int n = inst.Actions.RemoveAll(a =>
                string.Equals(a.Pattern, pat, StringComparison.OrdinalIgnoreCase));
            if (n == 0) DbgEcho(inst, 3, $"action remove: no trigger matched '{pat}'");
            return;
        }

        // Optional Genie4 sub-keywords between `action` and the body:
        //   action add <body> when <pat>      — explicit "register" form (same
        //                                        as bare "action … when …")
        //   action instant <body> when <pat>  — flags the action so a body
        //                                        that issues `goto` doesn't
        //                                        wait for roundtime to drain.
        //                                        Our tick loop has no
        //                                        action-triggered RT gate, so
        //                                        the flag is currently a
        //                                        no-op for runtime behavior;
        //                                        we still parse + strip the
        //                                        keyword so the body doesn't
        //                                        absorb it.
        if (trimmed.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..].TrimStart();
        else if (trimmed.StartsWith("instant ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[8..].TrimStart();

        // Locate " when " or " whenre " outside of quoted strings.
        int whenIdx = FindKeywordOutsideQuotes(trimmed, "when");
        int whenreIdx = FindKeywordOutsideQuotes(trimmed, "whenre");
        int kwIdx, kwLen;
        if (whenreIdx >= 0 && (whenIdx < 0 || whenreIdx <= whenIdx))
        { kwIdx = whenreIdx; kwLen = 6; }
        else if (whenIdx >= 0)
        { kwIdx = whenIdx; kwLen = 4; }
        else
        {
            _echo($"[script] {inst.Name}:{lineNo} action: missing 'when' / 'whenre'");
            return;
        }

        var cmd = trimmed[..kwIdx].Trim();
        var pattern = trimmed[(kwIdx + kwLen)..].Trim();
        if (cmd.Length == 0 || pattern.Length == 0)
        {
            _echo($"[script] {inst.Name}:{lineNo} action: empty command or pattern");
            return;
        }

        // Genie4 semantics: `when` patterns are always regex (same as `whenre`);
        // variables in the pattern are substituted at registration time. The
        // only exception is the `eval <expr>` form, which is evaluated live.
        bool isEval = false;
        if (pattern.StartsWith("eval ", StringComparison.OrdinalIgnoreCase))
        {
            isEval  = true;
            pattern = pattern[5..].Trim();
        }
        else
        {
            pattern = SubstituteVars(pattern, inst);
        }
        bool isRegex = !isEval;

        // Pre-compile so the per-line FireActions hot path doesn't pay the
        // regex compile cost on every game line for every registered action
        // (travel.cmd alone registers ~30 of them).
        Regex? compiled = null;
        if (isRegex)
        {
            try { compiled = new Regex(pattern, RegexOptions.Compiled); }
            catch (ArgumentException) { /* bad regex — TryMatch will return false */ }
        }

        inst.Actions.Add(new ScriptAction
        {
            Label         = label,
            Command       = cmd,
            Pattern       = pattern,
            IsRegex       = isRegex,
            IsEval        = isEval,
            Enabled       = true,
            CompiledRegex = compiled,
        });
        DbgEcho(inst, 5, $"action registered: cmd=\"{cmd}\" " +
            (isEval ? "when eval" : isRegex ? "whenre" : "when") +
            $" \"{pattern}\"" + (label.Length > 0 ? $" label=({label})" : ""));
    }

    private static bool LabelMatch(ScriptAction a, string label)
        => string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Movement-rejection messages the DR server returns when a movement
    /// command (`move s`, raw direction, `go ferry`, etc.) cannot be
    /// performed. Mirrors the list in <c>MapperController</c>.
    /// </summary>
    private static bool IsMovementFailure(string line)
    {
        return line.StartsWith("You can't go there", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You can't do that", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("...wait", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Sorry, you may only", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You are still stunned", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You can't manage", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("You are unable to move", StringComparison.OrdinalIgnoreCase);
    }

    // KnownColor names between Transparent and ButtonFace, matching Genie4's
    // ColorCode.IsColorString filter — system UI colors are excluded so only
    // web-style named colors are accepted as #echo foregrounds.
    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "AliceBlue","AntiqueWhite","Aqua","Aquamarine","Azure","Beige","Bisque",
        "Black","BlanchedAlmond","Blue","BlueViolet","Brown","BurlyWood","CadetBlue",
        "Chartreuse","Chocolate","Coral","CornflowerBlue","Cornsilk","Crimson","Cyan",
        "DarkBlue","DarkCyan","DarkGoldenrod","DarkGray","DarkGreen","DarkKhaki",
        "DarkMagenta","DarkOliveGreen","DarkOrange","DarkOrchid","DarkRed","DarkSalmon",
        "DarkSeaGreen","DarkSlateBlue","DarkSlateGray","DarkTurquoise","DarkViolet",
        "DeepPink","DeepSkyBlue","DimGray","DodgerBlue","Firebrick","FloralWhite",
        "ForestGreen","Fuchsia","Gainsboro","GhostWhite","Gold","Goldenrod","Gray",
        "Green","GreenYellow","Honeydew","HotPink","IndianRed","Indigo","Ivory","Khaki",
        "Lavender","LavenderBlush","LawnGreen","LemonChiffon","LightBlue","LightCoral",
        "LightCyan","LightGoldenrodYellow","LightGray","LightGreen","LightPink",
        "LightSalmon","LightSeaGreen","LightSkyBlue","LightSlateGray","LightSteelBlue",
        "LightYellow","Lime","LimeGreen","Linen","Magenta","Maroon","MediumAquamarine",
        "MediumBlue","MediumOrchid","MediumPurple","MediumSeaGreen","MediumSlateBlue",
        "MediumSpringGreen","MediumTurquoise","MediumVioletRed","MidnightBlue",
        "MintCream","MistyRose","Moccasin","NavajoWhite","Navy","OldLace","Olive",
        "OliveDrab","Orange","OrangeRed","Orchid","PaleGoldenrod","PaleGreen",
        "PaleTurquoise","PaleVioletRed","PapayaWhip","PeachPuff","Peru","Pink","Plum",
        "PowderBlue","Purple","Red","RosyBrown","RoyalBlue","SaddleBrown","Salmon",
        "SandyBrown","SeaGreen","SeaShell","Sienna","Silver","SkyBlue","SlateBlue",
        "SlateGray","Snow","SpringGreen","SteelBlue","Tan","Teal","Thistle","Tomato",
        "Turquoise","Violet","Wheat","White","WhiteSmoke","Yellow","YellowGreen",
    };

    private static bool IsEchoColor(string tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        if (tok[0] == '#' && tok.Length >= 4)
        {
            for (int i = 1; i < tok.Length; i++)
            {
                var c = tok[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
        return NamedColors.Contains(tok);
    }

    private static int FindKeywordOutsideQuotes(string s, string keyword)
    {
        bool inStr = false;
        for (int i = 0; i + keyword.Length <= s.Length; i++)
        {
            if (s[i] == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (i > 0 && !char.IsWhiteSpace(s[i - 1])) continue;
            if (!string.Equals(s.Substring(i, keyword.Length), keyword,
                               StringComparison.OrdinalIgnoreCase)) continue;
            int after = i + keyword.Length;
            if (after < s.Length && !char.IsWhiteSpace(s[after])) continue;
            return i;
        }
        return -1;
    }

    // ── Debug helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Emit a debug trace line if the script's debug level is at or above
    /// <paramref name="minLevel"/>. Levels: 1=goto/gosub/return,
    /// 2=pause/wait, 3=if, 4=var/math, 5=actions, 10=all lines.
    /// </summary>
    private void DbgEcho(ScriptInstance inst, int minLevel, string msg)
    {
        if (inst.DebugLevel >= minLevel)
            _echo($"[dbg:{inst.DebugLevel}] {msg}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Split a command string on unquoted semicolons, trimming each part and
    /// dropping empty pieces. Quoted segments survive intact so a regex like
    /// <c>"foo;bar"</c> isn't accidentally split.
    /// </summary>
    private static List<string> SplitSemicolons(string s)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(s)) return result;

        bool inStr = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inStr = !inStr;
            else if (s[i] == ';' && !inStr)
            {
                var part = s[start..i].Trim();
                if (part.Length > 0) result.Add(part);
                start = i + 1;
            }
        }
        var tail = s[start..].Trim();
        if (tail.Length > 0) result.Add(tail);
        return result;
    }

    /// <summary>
    /// Parse an optional leading delay (seconds) off a <c>send</c> segment.
    /// Genie4's CommandQueue reads a leading run of digits/<c>.</c> as a
    /// wait-before-send; we additionally accept a leading <c>-</c> so scripts
    /// can request "send eagerly" (negative ⇒ no wait). The number must be
    /// followed by whitespace (or be the whole segment) to count as a delay —
    /// otherwise a command that merely starts with a digit (e.g. <c>2nd</c>)
    /// is left intact.
    /// <para>A leading <c>-</c> that is NOT part of a number (e.g. <c>-cast</c>,
    /// <c>-touch my orb</c>) is the same "fire eagerly" marker in bare-verb form:
    /// long-standing community scripts (uber.cmd et al.) prefix a verb with
    /// <c>-</c> as an inline no-wait hint. DR rejects a hyphen-prefixed verb
    /// ("Please rephrase"), so left intact it would silently bounce. We strip the
    /// lone leading <c>-</c> (zero delay) so the verb actually reaches the game.
    /// Only a hyphen at position 0 qualifies — a hyphen mid-token is left be.</para>
    /// Returns <c>(0, trimmedSegment)</c> when no delay/marker is present.
    /// <paramref name="seg"/> arrives already trimmed.
    /// </summary>
    internal static (double delay, string cmd) ParseSendDelay(string seg)
    {
        int i = 0;
        bool hadMinus = false;
        if (i < seg.Length && seg[i] == '-') { i++; hadMinus = true; }
        bool dot = false, sawDigit = false;
        while (i < seg.Length && (char.IsDigit(seg[i]) || (seg[i] == '.' && !dot)))
        {
            if (seg[i] == '.') dot = true; else sawDigit = true;
            i++;
        }
        bool boundary = i >= seg.Length || seg[i] == ' ' || seg[i] == '\t';
        if (sawDigit && boundary &&
            double.TryParse(seg[..i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (d, seg[i..].Trim());
        // Bare leading '-' (not a numeric delay) → eager, strip it. See remarks.
        if (hadMinus && !sawDigit) return (0.0, seg[1..].Trim());
        return (0.0, seg.Trim());
    }

    /// <summary>The current Genie4 <c>%argcount</c> for this instance — the
    /// number of args the script was launched with, decremented by <c>shift</c>.
    /// Stored as the local var <c>argcount</c> (G4 parity); 0 if absent/unparsable.</summary>
    private static int GetArgCount(ScriptInstance inst)
        => inst.Vars.TryGetValue("argcount", out var s)
           && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static (string cmd, string rest) SplitCmd(string s)
    {
        if (string.IsNullOrEmpty(s)) return (string.Empty, string.Empty);
        var i = s.IndexOf(' ');
        if (i < 0) return (s, string.Empty);
        return (s[..i], s[(i + 1)..].Trim());
    }

    /// <summary>
    /// The real 1-based source line at a compiled-<see cref="ScriptInstance.Lines"/>
    /// index, for the goto/gosub/return debug trace. The Lines array is the
    /// COMPILED program — its index is NOT the source line number (block braces
    /// split, blanks/comments dropped, includes expanded), so printing "index+1"
    /// misreported every jump target (e.g. `gosub mech.forage → line 1034` when
    /// the label sat at source line 878). Map the target Pc back through
    /// <see cref="ScriptLine.LineNumber"/> instead. Clamped so an end-of-script Pc
    /// (a label on the final line) still yields a sane number.
    /// </summary>
    private static int TargetLineNo(ScriptInstance inst, int pc)
        => inst.Lines.Count == 0
            ? 0
            : inst.Lines[Math.Clamp(pc, 0, inst.Lines.Count - 1)].LineNumber;

    /// <summary>Lazily build the per-script JavaScript library context (#104),
    /// bound to THIS instance's variable scope: bare getVar/setVar → the script's
    /// %vars; getGlobal/setGlobal → session globals / user #vars; echo/put → the
    /// script's normal output / game send.</summary>
    private Js.JsLibraryContext EnsureJsLib(ScriptInstance inst) =>
        inst.JsLib ??= new Js.JsLibraryContext(
            getVar:    n => inst.Vars.TryGetValue(n, out var v) ? v : "",
            setVar:    (n, v) => inst.Vars[n] = v,
            getGlobal: n => Globals.TryGetValue(n, out var g) ? g : (UserVarLookup?.Invoke(n) ?? ""),
            setGlobal: (n, v) => Globals[n] = v,
            echo:      m => _echo(m),
            put:       c => _sendCommand(c));

    private string SubstituteVars(string text, ScriptInstance inst)
    {
        if (text.IndexOf('%') < 0 && text.IndexOf('$') < 0) return text;

        // Genie 4 parity (Script.cs ParseVariables): scan RIGHT-TO-LEFT so nested
        // / stacked variables resolve inside-out (#128). A trailing var becomes
        // part of the name to its left before that outer sigil is looked up:
        //   • with counter=1 and harness1 set, "%harness%counter" resolves
        //     %counter→"1" first, forming "%harness1", then resolves that;
        //   • "$%output" (output="var1") resolves %output→"var1" first, forming
        //     "$var1", then resolves it.
        // This also yields the "%%name"/"$$name" double-eval for free — the inner
        // %name resolves, then the outer % reads the produced name — matching
        // Genie 4 without a special case. Values inserted by a resolution are
        // never re-triggered as new sigils: the scan only moves LEFT into the
        // untouched prefix, so positions ≥ p are read only as name content, and a
        // resolved value that itself contains % or $ stays literal (also Genie 4).
        var s = text;
        for (int p = s.Length - 1; p >= 0; p--)
        {
            char c = s[p];
            if (c != '%' && c != '$') continue;
            var (value, end) = ResolveTokenAt(s, p, c, inst);
            // Skip the splice when nothing changed — a bare sigil, or an
            // undefined name returned literally (G4 keeps it as-is).
            if ((end != p + 1 || value != c.ToString()) &&
                !(end - p == value.Length && string.CompareOrdinal(s, p, value, 0, value.Length) == 0))
                s = s.Substring(0, p) + value + s.Substring(end);
        }
        return s;
    }

    /// <summary>
    /// Resolve the single <c>%</c>/<c>$</c> variable token that begins at
    /// <paramref name="p"/> in <paramref name="s"/>. Returns the substituted
    /// value and the index just past the consumed token (name plus any array
    /// index). An unresolved name is returned LITERALLY, sigil included —
    /// Genie 4's undefined-var policy (both shrink loops end `return Line;`
    /// unchanged) — while still consuming the full name so it isn't
    /// reconsidered. A bare sigil with no name returns the sigil unchanged.
    /// Called by the right-to-left driver in <see cref="SubstituteVars"/>.
    /// </summary>
    private (string value, int end) ResolveTokenAt(string s, int p, char c, ScriptInstance inst)
    {
        int nameStart = p + 1;
        int j = nameStart;
        // Variable names allow letters, digits, _, . and - (the shrink-search
        // below trims '.'/'-' suffixes that aren't part of a defined name).
        while (j < s.Length &&
               (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.' || s[j] == '-'))
            j++;
        if (j == nameStart) return (c.ToString(), nameStart);   // bare sigil — leave literal

        // $0..$9 numeric slots: Genie 4 replaces these as a flat text pass
        // BEFORE the variable loop (Script.cs ParseVariables), so `$1s`
        // is arg1 + literal "s" — the slot consumes exactly ONE digit and
        // never participates in the name shrink-search below.
        if (c == '$' && char.IsDigit(s[nameStart]))
        {
            var slot = inst.DollarStack.Count > 0
                ? inst.DollarStack.Peek()[s[nameStart] - '0'] ?? string.Empty
                : string.Empty;
            return (slot, nameStart + 1);
        }

        // Genie 4 parity: shrink the candidate from the right until a defined
        // var is found. So `%caravan-there` resolves the full name when it
        // exists, but `%count-1` falls back to `%count` followed by literal
        // "-1" when only `count` is defined. If nothing resolves, the full
        // candidate is consumed and substituted as empty.
        //
        // A shrunk candidate may only break where the remainder does NOT start
        // with a letter — i.e. never in the middle of a word (public #171).
        // Without this, an UNDEFINED name gets eaten mid-word by a shorter
        // defined var: `$Outdoorsmanship.Ranks` matched the compass boolean
        // `$out` → "0doorsmanship.Ranks" (remainder "doorsmanship…" starts with
        // a letter), and `$SpellTimer.X.active` matched `$spelltime` →
        // "0r.X.active". (Genie 4 dodges this only via its case-sensitive
        // VariableList; our globals are case-insensitive.)
        //
        // But '.', '-', digits AND '_' are all legitimate suffix boundaries —
        // Genie 4 breaks at any of them. `_` in particular is load-bearing:
        // `%$selection_DESC` (mm_train #1071) must shrink `$selection_DESC` →
        // `$selection` + "_DESC", forming `%<value>_DESC`. An earlier version
        // of this rule wrongly rejected the '_' boundary too, so the inner
        // global collapsed to "" and the leading `%` survived as a bare sigil
        // → `!(% = "")` (regression, Jason's live mm_train run). The
        // documented shrinks `%count-1` and `%%spell.Prep` still work.
        int nameEnd = j;
        string value = string.Empty;
        bool resolved = false;
        while (nameEnd > nameStart)
        {
            if (nameEnd == j || !char.IsLetter(s[nameEnd]))
            {
                var name = s[nameStart..nameEnd];
                if (TryResolveVar(name, c, inst, out value)) { resolved = true; break; }
            }
            nameEnd--;
        }
        if (!resolved)
        {
            // Genie 4 parity: an UNDEFINED variable stays LITERAL — both sigils.
            // G4's shrink loops end `return Line;` unchanged (local %vars:
            // Script.cs ParseVariable:2469; $globals: Globals.cs
            // ParseVariable:306). Substituting empty instead turned uber.cmd's
            // never-defined %superjump inside a matchre pattern
            // (`\b(?i)%superjump\b`, uber.cmd:4130) into the degenerate
            // match-anything `\b(?i)\b`, firing a goto with no label and
            // killing the script (smoke 2026-08-03). Literal text is inert in
            // regexes and evaluates G4-false in conditions (the expression
            // evaluator reads a sigil token as a plain string atom).
            return (s[p..j], j);
        }

        // Array indexing: %Bags(0) splits the pipe-delimited value and returns
        // the element at that index (0-based). The index may itself hold vars
        // (e.g. %Bags(%BagLoop)) — already resolved by the right-to-left pass,
        // but substitute again defensively.
        if (nameEnd < s.Length && s[nameEnd] == '(')
        {
            int close = s.IndexOf(')', nameEnd + 1);
            if (close > nameEnd)
            {
                var idxStr = SubstituteVars(s[(nameEnd + 1)..close].Trim(), inst);
                if (int.TryParse(idxStr, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out var arrIdx))
                {
                    var parts = value.Split('|');
                    value = arrIdx >= 0 && arrIdx < parts.Length ? parts[arrIdx] : string.Empty;
                }
                nameEnd = close + 1;
            }
        }
        return (value, nameEnd);
    }

    /// <summary>
    /// Lookup helper for the shrink-search in <see cref="SubstituteVars"/>.
    /// Returns true when <paramref name="name"/> resolves to a defined
    /// variable (local for <c>%</c>, locals-then-globals for <c>$</c>) or to
    /// a pseudo-variable like <c>%timer</c>. The output is the resolved
    /// value (possibly an empty string if the var was set explicitly empty).
    /// </summary>
    private bool TryResolveVar(string name, char prefix, ScriptInstance inst, out string value)
    {
        value = string.Empty;
        if (name.Length == 0) return false;

        // Pseudo-variables (computed each substitution rather than stored).
        if (name.Equals("timer", StringComparison.OrdinalIgnoreCase))
        {
            value = inst.TimerStart is { } t
                ? ((int)(DateTime.UtcNow - t).TotalSeconds).ToString(CultureInfo.InvariantCulture)
                : "0";
            return true;
        }

        if (prefix == '$')
        {
            // $0..$9 are numeric slots from the top DollarStack frame
            // (gosub args or the most recent regex captures).
            if (name.Length == 1 && char.IsDigit(name[0]) && inst.DollarStack.Count > 0)
            {
                value = inst.DollarStack.Peek()[name[0] - '0'] ?? string.Empty;
                return true;
            }
            // $argcount — arg count of the current $-frame (Genie 4
            // Script.cs:2335 substitutes it from the same ArgList $0..$9 read):
            // script args at top level, gosub args inside a gosub, capture
            // count after a capturing match. Resolved before globals so a
            // stray global can't shadow it.
            if (name.Equals("argcount", StringComparison.OrdinalIgnoreCase))
            {
                value = inst.DollarCounts.Count > 0
                    ? inst.DollarCounts.Peek().ToString(CultureInfo.InvariantCulture)
                    : inst.Vars.TryGetValue("argcount", out var ac) ? ac : "0";
                return true;
            }
            // $spelltime — seconds since the current spell was prepared
            // (Genie 4). Computed live so it counts up; resolved before globals
            // (no stored snapshot).
            if (name.Equals("spelltime", StringComparison.OrdinalIgnoreCase))
            { value = (SpellTimeSeconds?.Invoke() ?? 0).ToString(CultureInfo.InvariantCulture); return true; }
            // $spellstarttime — Unix-epoch seconds of when the spell was prepared,
            // "0" when none (Genie 4). Computed live from the host, before globals.
            if (name.Equals("spellstarttime", StringComparison.OrdinalIgnoreCase))
            { value = (SpellStartTimeEpoch?.Invoke() ?? 0).ToString(CultureInfo.InvariantCulture); return true; }
            // $name: global session variables (set by #tvar, events, or host code).
            // Do NOT check inst.Vars here — that's for local %variables only.
            // $ prefix explicitly means "global", not "local with fallback to global".
            if (Globals.TryGetValue(name, out var gv))
            { value = gv ?? string.Empty; return true; }
            // Persistent user variables (#var). Resolved after live-state globals
            // (so reserved vars win, matching the command engine's ExpandVariables
            // order) but before the computed reserved vars below, so a #var can
            // still shadow $scriptlist / a clock var. This is what makes a value
            // set via `#var name value` — typed OR from a script's `put #var …` —
            // readable as $name in a running script.
            if (UserVarLookup?.Invoke(name) is { } uv)
            { value = uv; return true; }
            // $scriptlist — '|'-separated names of running scripts, or "none"
            // (Genie 4 parity). Computed on read so it's always current.
            if (name.Equals("scriptlist", StringComparison.OrdinalIgnoreCase))
            { value = BuildScriptList(); return true; }
            // Genie 4 reserved clock variables ($date/$time/$unixtime/...).
            // Computed on read so they're always current, and resolved as a
            // final fallback so a user var of the same name can still shadow.
            if (TryClockVar(name, out var clock))
            { value = clock; return true; }
            return false;
        }

        // %name: locals only.
        if (inst.Vars.TryGetValue(name, out var lv))
        { value = lv ?? string.Empty; return true; }
        return false;
    }

    /// <summary>
    /// Builds the <c>$scriptlist</c> value: the names of all currently running
    /// script instances joined with <c>'|'</c>, or the literal <c>"none"</c>
    /// when nothing is running (Genie 4 parity).
    /// </summary>
    private string BuildScriptList()
    {
        // Snapshot to a local array first: the same unlocked access pattern the
        // engine uses for Instances/AnyRunning, but the copy avoids enumerating
        // the live list if a start/stop mutates it mid-read. Includes .js scripts
        // so $scriptlist reflects everything the user is running.
        var joined = string.Join("|", RunningScriptNames());
        return joined.Length == 0 ? "none" : joined;
    }

    /// <summary>
    /// Resolves the Genie 4 "reserved" date/time variables, computed fresh on
    /// each read. Formats are copied verbatim from Genie 4
    /// (<c>Lists/Globals.cs</c>) for script parity — including the quirk that
    /// <c>$time24</c> still appends the AM/PM designator. Returns false for any
    /// name that isn't one of these so normal resolution can continue.
    /// </summary>
    private static bool TryClockVar(string name, out string value)
    {
        var now = DateTime.Now;
        value = name.ToLowerInvariant() switch
        {
            "time"         => now.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture).Trim(),
            "time24"       => now.ToString("HH:mm:ss tt", CultureInfo.InvariantCulture).Trim(),
            "date"         => now.ToString("M/d/yyyy",     CultureInfo.InvariantCulture).Trim(),
            "datetime"     => now.ToString("M/d/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture).Trim(),
            "datetime24"   => now.ToString("M/d/yyyy HH:mm:ss tt", CultureInfo.InvariantCulture).Trim(),
            "militarytime" => now.ToString("HHmm",         CultureInfo.InvariantCulture).Trim(),
            "dayofmonth"   => now.ToString("dd",           CultureInfo.InvariantCulture).Trim(),
            "dayofyear"    => now.DayOfYear.ToString(CultureInfo.InvariantCulture),
            // $year / $month (#151). Genie 4's Globals.cs uses "yyyy"/"mm" — but
            // "mm" is MINUTES in .NET, a Genie 4 bug that makes $month useless;
            // we deliberately use "MM" (2-digit month) so $month means the month.
            "year"         => now.ToString("yyyy",         CultureInfo.InvariantCulture),
            "month"        => now.ToString("MM",           CultureInfo.InvariantCulture),
            "unixtime"     => DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            _              => string.Empty,
        };
        return value.Length > 0;
    }

    // Bounded LRU cache of compiled regexes, shared across all TryMatch
    // callers (waitfor, matchwait, action-pattern fallback). Without it,
    // every game line compiled the same patterns from scratch — very
    // expensive when matchwait registers 10+ patterns on a single line and
    // they survive across many incoming lines.
    private static readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    private const int RegexCacheLimit = 256;

    private static Regex? GetCompiledRegex(string pattern)
    {
        lock (_regexCache)
        {
            if (_regexCache.TryGetValue(pattern, out var cached)) return cached;
            try
            {
                var rx = new Regex(pattern, RegexOptions.Compiled);
                if (_regexCache.Count >= RegexCacheLimit) _regexCache.Clear();
                _regexCache[pattern] = rx;
                return rx;
            }
            catch (ArgumentException) { return null; }
        }
    }

    private static bool TryMatch(string line, string pattern, bool isRegex,
                                  ScriptInstance inst, bool capture)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // Genie4 actions / matchwait / waitfor / waitforre default to
        // case-SENSITIVE matching. Mirror that here so a pattern like
        // `Night Sky` doesn't fire on environmental flavor "the night sky".
        if (!isRegex)
            return line.IndexOf(pattern, StringComparison.Ordinal) >= 0;

        var rx = GetCompiledRegex(pattern);
        if (rx is null) return false;
        var m = rx.Match(line);
        if (!m.Success) return false;
        if (capture)
        {
            // Regex captures land in the $-scope ($0=full match, $1..$9=groups)
            // on the current frame — they do NOT overwrite script args (%N).
            var frame = inst.DollarStack.Count > 0
                ? inst.DollarStack.Peek()
                : null;
            if (frame is null)
            {
                frame = new string[10];
                inst.DollarStack.Push(frame);
                inst.DollarCounts.Push(0);
            }
            for (int i = 0; i < 10; i++) frame[i] = string.Empty;
            frame[0] = m.Value;
            for (int i = 1; i < m.Groups.Count && i <= 9; i++)
                frame[i] = m.Groups[i].Value;
            // The frame was overwritten in place; its $argcount follows suit
            // (Genie 4: the count always describes what $0..$9 currently hold).
            if (inst.DollarCounts.Count > 0) inst.DollarCounts.Pop();
            inst.DollarCounts.Push(Math.Min(9, m.Groups.Count - 1));
        }
        return true;
    }
}
