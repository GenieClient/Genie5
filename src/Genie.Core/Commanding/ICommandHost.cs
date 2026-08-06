namespace Genie.Core.Commanding;

public interface ICommandHost
{
    void Echo(string text);
    void EchoTo(string text, string? window, string? color);

    /// <summary>
    /// Echo a styled line into the <em>main</em> game window (Genie 4
    /// <c>#echo</c> with a colour and/or the <c>mono</c> flag but no
    /// <c>&gt;window</c> redirect). <paramref name="color"/> is a named colour
    /// or <c>#rrggbb</c> hex (null = default echo colour); <paramref name="mono"/>
    /// renders the line in a monospaced font. Distinct from
    /// <see cref="EchoTo"/>, which targets a named side/plugin window.
    /// </summary>
    void EchoMain(string text, string? color, bool mono);

    /// <summary>
    /// Render a Genie 4 clickable menu link — <c>#link [&gt;window] {text}
    /// {command}</c>. <paramref name="text"/> shows as a clickable link;
    /// clicking runs <paramref name="command"/> through the command pipeline
    /// (so a <c>;</c>-chained body of <c>#</c>-commands fires on click, not when
    /// the link is created). <paramref name="window"/> is the optional
    /// <c>&gt;window</c> target (null = the main game window). Console / headless
    /// builds with no UI drop it silently.
    /// </summary>
    void EchoLink(string text, string command, string? window);

    /// <summary>
    /// Clear a window's contents (Genie 4 <c>#clear [&gt;window]</c>).
    /// <paramref name="window"/> null = the main game window; a name clears that
    /// side / plugin / menu window. Console / headless builds with no UI drop it.
    /// </summary>
    void EchoClear(string? window);

    /// <summary>
    /// Genie 4 <c>#window &lt;sub&gt; "name"</c> lifecycle for a named side
    /// window: <c>add</c> / <c>open</c> / <c>show</c> (create + reveal),
    /// <c>close</c> / <c>hide</c> / <c>remove</c> (hide the panel), and
    /// <c>clear</c> (empty it). The dock lives in the App layer, so Core forwards
    /// the parsed sub-command + window name; Console / headless builds drop it.
    /// </summary>
    void WindowCommand(string sub, string window);

    /// <summary>
    /// Set a Genie 4 <c>#comment</c> annotation on a window's title bar:
    /// <c>#comment Room 69. 120</c> shows the Room panel titled "Room (69. 120)".
    /// A blank comment clears it back to the base title. The dock lives in the
    /// App layer, so Core forwards the window name + resolved text; Console /
    /// headless builds drop it.
    /// <para>Default no-op so the many test/headless hosts need no change; the
    /// real App host (GenieCore) implements it.</para>
    /// </summary>
    void SetWindowComment(string window, string comment) { }

    /// <summary>
    /// Set the script status-bar text (Genie 4 <c>#statusbar</c> / <c>#status</c>).
    /// <paramref name="index"/> selects one of Genie 4's ten status slots
    /// (1-10; the command-bar layer defaults a missing/invalid index to 1); the
    /// App composes the non-empty slots into the strip shown to the right of the
    /// Script Bar (#111). An empty <paramref name="text"/> clears that slot.
    /// Console / headless builds with no UI strip drop it silently — Genie 4
    /// parity (a status write to nowhere is a no-op, not an error).
    /// </summary>
    void SetStatusBar(string text, int index);

    /// <summary>
    /// Send a command line to the game socket.
    /// </summary>
    /// <param name="text">The literal bytes to put on the wire — what the
    /// server will parse. For DR's UI-link clicks this is the cmd attribute
    /// with item-exist-IDs (e.g. <c>get #49489411 in #49489410</c>).</param>
    /// <param name="userInput">True when this came from typed input / link
    /// click — produces a local echo. False for script/alias-emitted sends
    /// (those have their own echo paths).</param>
    /// <param name="origin">Optional tag for telemetry / mapper.</param>
    /// <param name="echoOverride">Optional friendly string to echo INSTEAD of
    /// <paramref name="text"/> when <paramref name="userInput"/> is true.
    /// Used by the UI link click path so the user sees
    /// "get a tapered cutlass" instead of "get #49489411 in #49489410" in
    /// the Game window — the server still receives the IDs.</param>
    void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null);
    void RunScript(string text);

    /// <summary>
    /// Inject a synthetic line into the full per-line pipeline as if the server
    /// had emitted it — the Genie 4 <c>#parse</c> command. Feeds running scripts'
    /// <c>waitfor</c>/<c>match</c>, the global user-trigger list, and plugins, but
    /// never echoes to a window and never reaches the game socket. The argument
    /// arrives already <c>$</c>/<c>%</c>-expanded (the command bar expands at entry).
    /// The Console build with no live session feeds whatever script engine exists.
    /// </summary>
    void InjectParsedLine(string line);

    /// <summary>
    /// Stop a running script. <paramref name="name"/> null/empty stops the
    /// most recently started script; a name stops that specific script.
    /// Used by <c>#stop</c> from the command bar.
    /// </summary>
    void StopScript(string? name);

    /// <summary>Stop every running script. Used by <c>#stopall</c>.</summary>
    void StopAllScripts();

    /// <summary>
    /// Pause every running script (sets <c>UserPaused</c>). Mirrors Genie 4's
    /// Scripts → Pause All Scripts menu. Used by <c>#pauseall</c>.
    /// </summary>
    void PauseAllScripts();

    /// <summary>Resume every paused script. Used by <c>#resumeall</c>.</summary>
    void ResumeAllScripts();

    /// <summary>
    /// Pause one running script by name (sets <c>UserPaused</c>); null/empty
    /// pauses every script. Used by <c>#script pause [name|all]</c> (Genie 4
    /// Core/Command.cs script dispatcher).
    /// </summary>
    void PauseScript(string? name);

    /// <summary>
    /// Resume one paused script by name; null/empty resumes every script.
    /// Used by <c>#script resume [name|all]</c>.
    /// </summary>
    void ResumeScript(string? name);

    /// <summary>
    /// Apply a debug / tracing level to every running script. Mirrors Genie 4's
    /// Scripts → Trace All Scripts menu. Level 0 = no traces; higher values
    /// surface more script-internal echoes. Used by <c>#traceall &lt;level&gt;</c>.
    /// </summary>
    void SetTraceLevelAll(int level);

    // ── #script sub-commands (Genie 4 Core/Command.cs:2188 dispatcher) ──────
    // Default-bodied for the same reason as Beep(): these are forwarding-style,
    // nothing-lost-if-absent members that the many ICommandHost test doubles
    // and headless hosts need not implement — only GenieCore overrides them.

    /// <summary>Toggle pause/resume per script (<c>#script pauseorresume</c>).
    /// Null/empty toggles every running script individually.</summary>
    void PauseOrResumeScript(string? name) { }

    /// <summary>Mark script(s) for hot reload at their next <c>goto</c>
    /// (<c>#script reload</c>). Null/empty = every running script.</summary>
    void ReloadScript(string? name) { }

    /// <summary>Dump a script's local variables to the main window
    /// (<c>#script vars &lt;name&gt; [filter]</c>). Null/empty name = every
    /// script; <paramref name="filter"/> is a substring match on the
    /// <c>name=value</c> rows.</summary>
    void ShowScriptVars(string? name, string filter) { }

    /// <summary>Dump a script's rolling control-flow trace to the main window
    /// (<c>#script trace &lt;name|all&gt;</c>).</summary>
    void ShowScriptTrace(string? name) { }

    /// <summary>Set one script's debug/trace level (<c>#script debug
    /// &lt;level&gt; &lt;name&gt;</c>); null/empty name = every script.</summary>
    void SetScriptDebugLevel(int level, string? name) { }

    /// <summary>Open the Script Manager / Explorer window (<c>#script
    /// explorer</c>). Lives in the App layer; headless hosts explain that.</summary>
    void ShowScriptExplorer() => Echo("The Script Explorer requires the App UI.");

    /// <summary>
    /// Genie 4-format status lines for the <c>#script</c>/<c>#scripts</c>
    /// listing — <c>Name(Paused) [Debuglevel: N]: 12.30 seconds. State
    /// (file.cmd)</c>. <paramref name="filter"/> is an exact script name
    /// (null/empty/"all" = everything). The default falls back to the plain
    /// names from <see cref="RunningScripts"/> for hosts without a script
    /// engine surface.
    /// </summary>
    IReadOnlyList<string> ScriptStatusLines(string? filter)
    {
        var names = RunningScripts();
        if (string.IsNullOrEmpty(filter) ||
            filter!.Equals("all", StringComparison.OrdinalIgnoreCase))
            return names;
        var hits = new List<string>();
        foreach (var n in names)
            if (n.Equals(filter, StringComparison.OrdinalIgnoreCase)) hits.Add(n);
        return hits;
    }

    /// <summary>
    /// Names of currently running scripts. Used by <c>#scripts</c> to list
    /// them at the command bar.
    /// </summary>
    IReadOnlyList<string> RunningScripts();

    /// <summary>
    /// Set (or replace) a session-global <c>$variable</c>. Used by
    /// <c>#tvar</c> from the command bar — the value lives in the same
    /// dictionary scripts read for <c>$name</c> expansion.
    /// </summary>
    void SetGlobalVariable(string name, string value);

    /// <summary>Remove a session-global variable.</summary>
    void RemoveGlobalVariable(string name);

    /// <summary>
    /// The reserved / live-state script variables ($health, $roomid, $zoneid,
    /// the status flags, hands, clock family, …) mirrored from the game stream
    /// into the script engine's Globals (plus any <c>#tvar</c> session-globals).
    /// A read-only view for <c>#var</c> to list them alongside user variables
    /// (#72). Empty in Console builds with no live session.
    /// </summary>
    System.Collections.Generic.IReadOnlyDictionary<string, string> GetGlobalVariables();

    /// <summary>
    /// Set the Live Audit diagnostic mode (the <c>#audit</c> command) — tees
    /// raw XML + parsed events + live zone/room to a log file for real-time
    /// troubleshooting; <see cref="Diagnostics.AuditMode.XmlHunting"/> adds the
    /// tag-coverage pass. Returns the log file path.
    /// </summary>
    string SetLiveAudit(Diagnostics.AuditMode mode);

    /// <summary>
    /// Expand <c>$name</c> references to their current global value (from
    /// the script engine's Globals — populated by <c>#var</c>/<c>#tvar</c>
    /// and the live-game-state mirror — plus user <c>#var</c> values and the
    /// reserved clock vars). Matches Genie 4's <c>ParseGlobalVars</c>,
    /// including the shrink-search with the word-boundary rule, so a dotted
    /// arg like <c>&gt;Ranklog-$charactername.txt</c> resolves
    /// <c>$charactername</c> + ".txt". Called at the command-bar entry point
    /// so a user typing <c>#echo $health</c> sees the substituted number.
    /// Unknown vars are left as the literal <c>$name</c> text (Genie 4
    /// parity); use an empty fallback for read-or-empty intent.
    /// GenieCore implements this via
    /// <c>ScriptEngine.ExpandGlobalVars</c>.
    /// </summary>
    string ExpandVariables(string text);

    /// <summary>
    /// Open the named script file (<c>{ScriptsDir}/{name}.cmd</c> or
    /// <c>.inc</c>) in the user's external editor — either the path
    /// configured via Display Settings or the OS default `.cmd` handler.
    /// Wired to <c>#edit &lt;name&gt;</c> from the command bar plus the
    /// pencil button on the Script Bar.
    /// <para>
    /// Implemented in the App layer because it needs <c>Process.Start</c>
    /// plus cross-platform launch semantics that don't belong in
    /// <see cref="Genie.Core"/>. <see cref="GenieCore"/>'s implementation
    /// is a no-op (Echo a "no editor host" message) — the App overrides
    /// it via the same <c>ICommandHost</c> instance.
    /// </para>
    /// </summary>
    void EditScript(string name);

    /// <summary>
    /// Run a <c>#layout</c> command — the raw argument string after
    /// <c>#layout </c> (e.g. <c>save global My Layout</c>, <c>load Base</c>,
    /// <c>list</c>). Layout storage + dock manipulation live in the App layer,
    /// so <see cref="Genie.Core"/> forwards the args to a host handler; the
    /// Console build with no handler echoes a diagnostic.
    /// </summary>
    void LayoutCommand(string args);

    /// <summary>
    /// Run a <c>#plugin</c> command — the raw argument string after
    /// <c>#plugin </c> (e.g. <c>list</c>, <c>enable Experience</c>,
    /// <c>unload genie.experience</c>, <c>load Plugin_EXPTrackerV5</c>). Plugin
    /// management (loader + folder) is orchestrated by the App layer, so
    /// <see cref="Genie.Core"/> forwards the args to a host handler; the Console
    /// build with no handler echoes a diagnostic.
    /// </summary>
    void PluginCommand(string args);

    /// <summary>
    /// Run a <c>#config</c> command — the raw argument string after
    /// <c>#config </c>. Operates on <c>settings.cfg</c> (<c>GenieConfig</c>),
    /// the Genie 4 config store — NOT <c>display.json</c> (the App-only visual
    /// store, edited via the Configuration dialog + menus).
    /// <list type="bullet">
    ///   <item>(empty) — open the Configuration dialog.</item>
    ///   <item><c>&lt;key&gt;</c> — echo the setting's current value.</item>
    ///   <item><c>&lt;key&gt; &lt;value&gt;</c> — set it and persist
    ///         <c>settings.cfg</c> (Genie 4 parity).</item>
    ///   <item><c>list</c> — dump every key and its current value.</item>
    ///   <item><c>save</c> — flush <c>settings.cfg</c>.</item>
    ///   <item><c>load</c> — re-read <c>settings.cfg</c> into the live config.</item>
    ///   <item><c>edit</c> — shell-open <c>settings.cfg</c> in the user's editor.</item>
    /// </list>
    /// Aliases accepted at the command-bar layer: <c>#set</c>, <c>#setting</c>,
    /// <c>#settings</c>. Settings storage + the Configuration dialog live in
    /// the App layer, so <see cref="Genie.Core"/> forwards the args to a host
    /// handler; the Console build with no handler echoes a diagnostic.
    /// </summary>
    void ConfigCommand(string args);

    /// <summary>
    /// Run a <c>#goto</c> (Genie 4 parity; <c>#go2</c> accepted as a synonym)
    /// command — the raw argument after the verb, identifying a destination
    /// room by numeric map id, by a <c>note</c> label, or by room-title text.
    /// The mapper + walker live in the App layer, so <see cref="Genie.Core"/>
    /// forwards the argument to a host handler; the Console build with no
    /// handler echoes a diagnostic. Resolution + the actual click-equivalent
    /// walk are the App's responsibility.
    /// <para>
    /// This is the typed/scripted equivalent of clicking a room in the
    /// Mapper: it starts an attended, RT-gated, fully-interruptible walk —
    /// the same path <c>travel.cmd</c> and friends rely on.
    /// </para>
    /// </summary>
    void MapperGoto(string args);

    /// <summary>
    /// Re-resolve the mapper's current room from scratch (<c>#mapper reset</c>,
    /// Genie 3/4 parity). Used when the mapper loses track — notably across
    /// rooms that share a description — to re-lock location without the player
    /// having to move. Does not alter the loaded map.
    /// </summary>
    void MapperReset();

    /// <summary>
    /// The remaining <c>#mapper</c> subcommands beyond <c>reset</c> (#146):
    /// <c>save</c> / <c>load</c> / <c>clear</c> / <c>zone</c> / <c>color</c> /
    /// <c>allowdupes</c> / <c>record</c>, plus usage for anything unknown.
    /// <paramref name="args"/> is the text after <c>#mapper</c> (the subcommand
    /// and its arguments). The mapper + zone files live in the App layer, so
    /// <see cref="Genie.Core"/> forwards the whole thing; the Console build with
    /// no handler echoes a diagnostic.
    /// </summary>
    void MapperCommand(string args);

    /// <summary>
    /// Play a sound effect by name (trigger/highlight SFX and the <c>#play</c>
    /// family). The host applies the <c>PlaySounds</c> gate + SoundDir/.wav
    /// resolution and dispatches to the platform audio backend; the Console
    /// build with no audio is a no-op. A blank name is ignored.
    /// </summary>
    void PlaySound(string soundName);

    /// <summary>
    /// Speak <paramref name="text"/> aloud via text-to-speech (the <c>#speak</c>
    /// command and per-rule trigger/highlight speak). The host owns the TTS
    /// engine and platform audio; the Console build with no engine is a no-op.
    /// Blank text is ignored. Synthesis runs off the caller's thread so a slow
    /// voice never blocks the game loop. <paramref name="urgent"/> marks a
    /// hand-picked alert (per-rule speak) that should be spoken first and may
    /// barge in over ordinary read-aloud chatter.
    /// </summary>
    void Speak(string text, bool urgent = false);

    /// <summary>
    /// Handle a <c>#tts</c> management subcommand (<c>install</c>, <c>voices</c>,
    /// <c>status</c>). The voice catalog, downloader, and engine all live in the
    /// App layer, so <see cref="Genie.Core"/> just forwards the argument string;
    /// the Console build with no handler is a no-op.
    /// </summary>
    void TtsCommand(string args);

    /// <summary>
    /// Flash the main window's taskbar / dock entry to pull the player's eye
    /// back to the client (Genie 4 <c>#flash</c> — its classic use is a trigger
    /// action on whispers or a hunting-script alert while the window is in the
    /// background). The window + platform attention API live in the App layer;
    /// Console / headless builds drop it silently.
    /// </summary>
    void FlashWindow();

    /// <summary>
    /// Sound the system alert / bell (Genie 4 <c>#beep</c> / <c>#bell</c> — its
    /// FormMain called <c>Interaction.Beep()</c>). Honors the same
    /// <c>PlaySounds</c> gate as <see cref="PlaySound"/>. Its classic use is a
    /// trigger action on a whisper or a hunting-script alert.
    /// <para>Provided as a default no-op so the many <see cref="ICommandHost"/>
    /// test doubles and headless/Console hosts need not implement it — only the
    /// real host (GenieCore) overrides it to raise its beep event. The
    /// <c>#script</c> sub-command members above follow the same deliberate
    /// pattern: default-bodied to avoid a no-op tax across ~17 implementers for
    /// fire-and-forget, nothing-lost-if-absent capabilities.</para>
    /// </summary>
    void Beep() { }

    /// <summary>
    /// Drop every script-queued command that has not yet been sent to the game
    /// (each instance's pending put/send segments). Part of <c>#queue clear</c>
    /// (Genie 4 parity) — the CommandEngine clears its own RT-gated
    /// <c>CommandQueue</c> and calls this for the script side. Default no-op for
    /// the same reason as <see cref="Beep"/>: test doubles and headless hosts
    /// lose nothing by not implementing it.
    /// </summary>
    void ClearSendQueue() { }

    /// <summary>
    /// Handle a <c>#connect</c> / <c>#reconnect</c> / <c>#lichconnect</c> command
    /// (Genie 4 parity). The connection lifecycle, saved profiles, and the
    /// Connect dialog all live in the App layer, so <see cref="Genie.Core"/>
    /// forwards the parsed request to a host handler; the Console build with no
    /// handler echoes a diagnostic. See <see cref="ConnectRequest"/> for the
    /// argument grammar.
    /// </summary>
    void Connect(ConnectRequest request);
}
