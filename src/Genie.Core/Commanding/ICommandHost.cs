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
    /// Apply a debug / tracing level to every running script. Mirrors Genie 4's
    /// Scripts → Trace All Scripts menu. Level 0 = no traces; higher values
    /// surface more script-internal echoes. Used by <c>#traceall &lt;level&gt;</c>.
    /// </summary>
    void SetTraceLevelAll(int level);

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
    /// and the live-game-state mirror). Matches Genie 4's
    /// <c>ParseGlobalVars</c>: called at the command-bar entry point so a
    /// user typing <c>#echo $health</c> sees the substituted number.
    /// Unknown vars are left as the literal <c>$name</c> text (Genie 4
    /// parity); use an empty fallback for read-or-empty intent.
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
    /// Play a sound effect by name (trigger/highlight SFX and the <c>#play</c>
    /// family). The host applies the <c>PlaySounds</c> gate + SoundDir/.wav
    /// resolution and dispatches to the platform audio backend; the Console
    /// build with no audio is a no-op. A blank name is ignored.
    /// </summary>
    void PlaySound(string soundName);

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
