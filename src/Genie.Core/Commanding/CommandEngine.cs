using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Persistence;
using Genie.Core.Queue;
using Genie.Core.Parsing;
using Genie.Core.Runtime;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Commanding;

public sealed class CommandEngine
{
    private readonly GenieConfig  _config;
    private readonly CommandQueue _commandQueue;
    private readonly EventQueue   _eventQueue;
    private readonly ICommandHost _host;

    // Re-entrancy guard for ProcessInput. Alias expansion
    // (AliasEngine.TryProcess) and trigger actions
    // (TriggerEngineFinal.ProcessLine) both dispatch back through
    // ProcessInput on the SAME (UI) thread, so a self-referencing alias — or a
    // trigger whose action re-fires its own pattern — recurses on the call
    // stack until it throws StackOverflowException. That's UNCATCHABLE: it
    // kills the process instantly with no dialog and no crash-log entry (#40 —
    // "crashes with no error message"). We cap the synchronous depth and abort
    // with a diagnostic well before the stack blows. 100 is far above any
    // legitimate alias/trigger nesting (which is single-digit) yet far below
    // the frame count that would overflow.
    private int _processInputDepth;

    /// <summary>
    /// Whether the current outermost <see cref="ProcessInput"/> call was
    /// interactive (the user typed it) vs automated (fired by a trigger or
    /// script). Set once at depth 0; nested alias/separator expansions inherit
    /// it. Used by <c>#var</c> / <c>#tvar</c> to print a "Variable set:"
    /// confirmation only for a directly-typed command — Genie 4 parity, where
    /// automation sets variables silently (community report: trigger-fired
    /// `#var RP OFF` spamming "Variable set: RP=OFF").
    /// </summary>
    private bool _interactive = true;
    private const int MaxProcessInputDepth = 100;

    // Serializes #log file writes (multiple scripts can #log concurrently).
    private readonly object _logLock = new();

    // ── Engines wired after construction ─────────────────────────────────────
    // GenieCore creates the command engine first (so other engines can route
    // through it) and then back-fills these references. Each one is nullable
    // so unit tests can spin up a bare CommandEngine without dragging the
    // whole graph in.

    /// <summary>Class registry. Backs <c>#class</c>.</summary>
    public ClassEngine?         Classes     { get; set; }

    /// <summary>Alias table. Backs <c>#alias</c> and bare-input expansion.</summary>
    public AliasEngine?         Aliases     { get; set; }

    /// <summary>User variable store. Backs <c>#var</c>.</summary>
    public VariableEngine?      Variables   { get; set; }

    /// <summary>Highlight rules. Backs <c>#highlight</c>.</summary>
    public HighlightEngine?     Highlights  { get; set; }

    /// <summary>Player-name highlight rules. Backs <c>#names</c> / <c>#name</c>.</summary>
    public NameHighlightEngine? Names       { get; set; }

    /// <summary>Preset colour palette (roomdesc, speech, …). Backs <c>#preset</c>.</summary>
    public Presets.PresetEngine? Presets    { get; set; }

    /// <summary>Trigger rules (auto-fire actions on game text). Backs <c>#trigger</c>.</summary>
    public TriggerEngineFinal?  Triggers    { get; set; }

    /// <summary>Substitute rules (transform incoming text). Backs <c>#substitute</c>.</summary>
    public SubstituteEngine?    Substitutes { get; set; }

    /// <summary>Gag rules (suppress lines from the display). Backs <c>#gag</c>.</summary>
    public GagEngine?           Gags        { get; set; }

    /// <summary>Keyboard macros (F-keys, Ctrl-letter, etc.). Backs <c>#macro</c>.</summary>
    public MacroEngine?         Macros      { get; set; }

    /// <summary>Client-side <c>/command</c> claim hook (extension console
    /// commands — /track, /calc, /tt, …). Consulted at the send fall-through
    /// for any game-bound segment starting with '/'; return true to consume it
    /// so it never reaches the wire. Wired by GenieCore to the extensions'
    /// slash dispatch — Genie 4 equivalent: plugins' ParseInput ran on every
    /// outbound send at FormMain's send sink.</summary>
    public Func<string, bool>?  ClientSlashCommand { get; set; }

    public CommandEngine(GenieConfig config, CommandQueue commandQueue, EventQueue eventQueue, ICommandHost host)
    {
        _config       = config;
        _commandQueue = commandQueue;
        _eventQueue   = eventQueue;
        _host         = host;
    }

    /// <summary>
    /// Pump user input through the pipeline. <paramref name="echoOverride"/>
    /// (when set) replaces the on-screen echo of plain game commands — used by
    /// the UI link-click path so the user sees the friendly link text instead
    /// of the raw item-id cmd. Aliases/script/internal-command paths ignore it
    /// (they have their own echo semantics; overriding there could mask which
    /// alias actually ran).
    /// </summary>
    /// <summary>Observe the top-level command issued (user / script / trigger),
    /// before it fans out into aliases/separators. Wired to the Live Audit so a
    /// diagnostic log shows exactly what a script fired (e.g. <c>#goto 171</c>).</summary>
    public Action<string>? CommandObserved { get; set; }

    public void ProcessInput(string input, string? echoOverride = null, bool interactive = true)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        // Live Audit: surface the top-level command only (depth 0) — recursive
        // alias/separator expansion below would otherwise flood the log. The
        // outermost call also fixes whether this whole expansion is interactive
        // (user typed it) or automated (trigger/script); nested calls inherit it.
        if (_processInputDepth == 0)
        {
            CommandObserved?.Invoke(input);
            _interactive = interactive;
        }

        // Re-entrancy guard (#40): abort a runaway alias/trigger recursion
        // before it StackOverflows and silently kills the client.
        if (_processInputDepth >= MaxProcessInputDepth)
        {
            var shown = input.Length > 80 ? input[..80] + "…" : input;
            _host.Echo($"[command] re-entrancy limit ({MaxProcessInputDepth}) hit on '{shown}' — aborting a runaway alias/trigger loop before it crashes the client. Check for a self-referencing alias or a trigger whose action re-fires its own pattern.");
            return;
        }

        _processInputDepth++;
        try
        {
            // Expand $variables BEFORE splitting on the separator so a value
            // that happens to contain `;` doesn't accidentally fragment the
            // command. Matches Genie 4's ParseGlobalVars-then-ParseCommand
            // ordering (Core/Command.cs:233).
            input = _host.ExpandVariables(input);
            // Escape-aware split (#132): a `\;` is a literal separator, and a `;`
            // inside "quotes" or {braces} doesn't fragment the command — matches
            // Genie 4's Utility.SafeSplit. A plain string.Split truncated values
            // like `#var t a\;b` at the semicolon and leaked the tail to the game.
            var commands = ArgumentParser.SafeSplit(input, _config.SeparatorChar);

            // Echo override only applies to single-command, plain-send pipeline
            // paths. If the input fan-outs into multiple commands via the
            // separator (e.g. an alias expanded mid-flight) we drop the override
            // — there's no meaningful 1:1 echo-text mapping at that point.
            var applyOverride = echoOverride is not null && commands.Count == 1;

            foreach (var raw in commands)
            {
                var command = raw.Trim();
                if (command.Length == 0) continue;
                if (command[0] == _config.CommandChar)
                {
                    HandleInternalCommand(command[1..]);
                }
                else if (command[0] == _config.ScriptChar)
                {
                    // ScriptChar (default '.') invokes a script by name. Genie 4
                    // parity: `.foo arg1 arg2` runs Scripts/foo.cmd with `arg1
                    // arg2` as %1 / %2 / etc. The host owns the script engine,
                    // so we just pass the rest through.
                    _host.RunScript(command[1..]);
                }
                else if (Aliases is not null && Aliases.TryProcess(command))
                {
                    // Alias matched and was dispatched recursively.
                }
                else if (command[0] == QuickSend.Char &&
                         QuickSend.TryParse(command, out var qsDelay, out var qsCmd))
                {
                    // Genie 4 quick-send (public #278): a ';'-segment starting
                    // with '-' is `#send` + remainder (G4 Core/Command.cs:254),
                    // i.e. a POSITIVE wait-before-send on the RT-gated command
                    // queue — the community idiom for pacing chains:
                    // `#put health;-0.05 encumbrance` sends `health` now and
                    // `encumbrance` 0.05s later. Glued digits work (`-3knock
                    // concealed door`); a bare `-verb` is a 0-delay RT-gated
                    // send. Alias lookup runs first (G4 checks aliases before
                    // the rewrite); degenerate dash rows ("-", "-3") fall
                    // through to the game literally.
                    HandleInternalCommand(qsDelay.Length > 0
                        ? $"send {qsDelay} {qsCmd}"
                        : $"send {qsCmd}");
                }
                else if (command[0] == '/' && ClientSlashCommand?.Invoke(command) == true)
                {
                    // Claimed client-side (extension /commands — /track, /calc,
                    // /tt, …). Genie 4 ran every outbound send through the
                    // plugins' ParseInput at the send sink (FormMain
                    // ClassCommand_SendText:4122), so a queued `#send /track
                    // clear`, an alias expansion, or a trigger action gets the
                    // same client-side chance here as typed input and script
                    // puts do at their own boundaries. Unclaimed slashes fall
                    // through and go to the game verbatim.
                }
                else
                {
                    _host.SendToGame(command, true,
                        echoOverride: applyOverride ? echoOverride : null);
                }
            }
        }
        finally
        {
            _processInputDepth--;
        }
    }

    private void HandleInternalCommand(string command)
    {
        var parts = ArgumentParser.ParseArgs(command);
        if (parts.Count == 0) return;
        switch (parts[0].ToLowerInvariant())
        {
            case "echo":
                if (parts.Count > 1)
                {
                    EchoArgs.Parse(parts, 1, out var window, out var color, out var mono, out var msg);
                    if (window != null)         _host.EchoTo(msg, window, color);
                    else if (color != null || mono) _host.EchoMain(msg, color, mono);
                    else                        _host.Echo(msg);
                }
                break;
            case "log":
            {
                // Genie 4 #log — append text to a file under <LogDir> (Core/Command.cs:366,
                // Utility/Log.cs). Two forms:
                //   #log >filename text   — append "text" + newline to <LogDir>/filename
                //                           (a filename containing a path separator is used
                //                           verbatim). No banner (Log.LogLine).
                //   #log text             — append "text" + newline to the default
                //                           per-character log <LogDir>/<char><game>_<yyyy-MM-dd>.log,
                //                           prepending a "LOG CREATED" banner on first write
                //                           (Log.LogText). No-op when no character is known.
                // Variables in the body are already expanded upstream (the script engine's
                // substitution / ProcessInput.ExpandVariables), so `parts` hold resolved text.
                if (parts.Count < 2) break;
                if (parts[1].StartsWith(">", StringComparison.Ordinal))
                {
                    var fileName = parts[1][1..];
                    if (fileName.Length == 0 || parts.Count < 3) break;   // ">file" with no text
                    var path = fileName.IndexOf('\\') >= 0 || fileName.IndexOf('/') >= 0
                        ? fileName
                        : Path.Combine(_config.LogDir, fileName);
                    WriteLogFile(path, string.Join(" ", parts.Skip(2)), banner: false);
                }
                else
                {
                    var globals = _host.GetGlobalVariables();
                    globals.TryGetValue("charactername", out var charName);
                    globals.TryGetValue("game", out var gameName);
                    if (string.IsNullOrEmpty(charName)) break;   // Genie 4: no character → no-op
                    var name = $"{charName}{gameName}_{DateTime.Now:yyyy-MM-dd}.log";
                    WriteLogFile(Path.Combine(_config.LogDir, name),
                                 string.Join(" ", parts.Skip(1)), banner: true);
                }
                break;
            }
            case "link":
            {
                // Genie 4 #link [>window] {text} {command} — a clickable menu
                // link. {text} renders as a link; clicking runs {command}
                // through the normal pipeline (ProcessInput), so a ';'-chained
                // body like "#clear >w;#echo done;#var x 1;#parse cont" fires on
                // CLICK, not at #link time. That is the whole point: the command
                // is stored, not executed now — which is why routing it straight
                // through the processor (the old behaviour) self-referenced and
                // tripped the re-entrancy guard. Quote/brace grouping in
                // ParseArgs keeps {text} and {command} intact even with spaces or
                // ';' inside them. Arg shape mirrors Genie 4 (Core/Command.cs):
                // parts[0] == "link", optional ">window", then text, then command.
                string? linkWindow = null;
                string  linkText, linkCommand;
                if (parts.Count > 3 && parts[1].StartsWith(">", StringComparison.Ordinal))
                {
                    linkWindow  = parts[1][1..];
                    linkText    = parts[2];
                    linkCommand = string.Join(" ", parts.Skip(3));
                }
                else if (parts.Count > 2)
                {
                    linkText    = parts[1];
                    linkCommand = string.Join(" ", parts.Skip(2));
                }
                else break;   // need at least {text} and {command}

                if (linkText.Length > 0)
                    _host.EchoLink(linkText, linkCommand, linkWindow);
                break;
            }
            case "clear":
            {
                // #clear [window] — wipe a window's contents. Genie 4
                // (Command.cs: ClearWindow(ParseAllArgs(oArgs, 1))) takes the
                // whole remainder as the window name with NO ">" prefix, and
                // never clears main when a name is given — menu scripts
                // (mm_train) redraw with `#clear "Moonmage Training Menu"`.
                // Accept the redirect-style ">name" too, but only a bare
                // #clear may clear the main game window.
                string? clearWindow = null;
                if (parts.Count > 1)
                {
                    var target = string.Join(" ", parts.Skip(1)).Trim();
                    if (target.StartsWith(">", StringComparison.Ordinal))
                        target = target[1..].Trim();
                    if (target.Length > 0) clearWindow = target;
                }
                _host.EchoClear(clearWindow);
                break;
            }
            case "window":
            {
                // #window <add|open|show|close|hide|remove|clear> "name" —
                // Genie 4 named-window lifecycle. Menu scripts (mm_train) add +
                // open a window, write to it with #echo / #link, then remove it.
                // ParseArgs already grouped a quoted multi-word name into one
                // token, so parts[2] is the whole "Moonmage Training Menu". The
                // dock lives in the App, so forward the sub-command + name.
                var sub   = parts.Count > 1 ? parts[1] : string.Empty;
                var wname = parts.Count > 2 ? parts[2] : string.Empty;
                _host.WindowCommand(sub, wname);
                break;
            }
            case "comment":
            {
                // #comment <window> <text> — Genie 4 window-title annotation.
                // A common trigger use is `#comment Room $zoneid. $roomid` to
                // show "Room (69. 120)" in the Room panel's title. parts[1] is
                // the window; the rest (joined) is the comment. `#comment
                // <window>` with no text clears the annotation. The whole line
                // is already $/%-expanded before dispatch, so the text carries
                // resolved values.
                if (parts.Count >= 2)
                    _host.SetWindowComment(parts[1],
                        parts.Count > 2 ? string.Join(" ", parts.Skip(2)) : string.Empty);
                break;
            }
            case "status":
            case "statusbar":
                // Genie 4 #statusbar [N] {text} — write text to one of ten status
                // slots (N = 1-10, default 1). The App renders the slots as a
                // positional row under the vitals Status Bar (#111). With no args
                // it's a no-op; a bare `#statusbar N` (no text) clears slot N, and
                // `#statusbar clearall` (Genie 5 extension — G4 had no bulk clear)
                // empties all ten slots. clearall must be the sole argument so
                // "clearall done" can still be displayed as slot-1 text.
                if (parts.Count > 1)
                {
                    if (parts.Count == 2 &&
                        parts[1].Equals("clearall", StringComparison.OrdinalIgnoreCase))
                    {
                        for (var i = 1; i <= 10; i++)
                            _host.SetStatusBar(string.Empty, i);
                        break;
                    }
                    var slot = 1;
                    var textFrom = 1;
                    if (int.TryParse(parts[1], out var n) && n is >= 1 and <= 10)
                    {
                        slot = n;
                        textFrom = 2;
                    }
                    _host.SetStatusBar(string.Join(" ", parts.Skip(textFrom)), slot);
                }
                break;
            case "put":
                // Genie 4: #put is the canonical "send to game" command in
                // scripts and triggers — it sends immediately. The body is
                // everything after the verb, joined with spaces so multi-word
                // arguments survive (#put glance left).
                if (parts.Count > 1) _host.SendToGame(string.Join(" ", parts.Skip(1)));
                break;
            case "send":
                // Genie 4 #send (Core/Command.cs Send()) is NOT an alias for
                // #put: it routes through the roundtime-gated CommandQueue with
                // an optional leading numeric delay, and "#send clear" empties
                // the queue. hunt.cmd relies on this — e.g. `#send 5 $lastcommand`
                // (retry after 5s + RT when webbed) and the `clear` subcommand.
                // Delay parse is shared with the in-script `send` verb via
                // ParseSendDelay, so `#send 5 x` and `send 5 x` behave alike.
                // A `-N`-prefixed body is the quick-send form (public #278):
                // G4 nets it out to a POSITIVE N-second RT-gated pause (its
                // queue re-parses the dash row into `#send N …`), so normalize
                // it here — glued digits included (`#send -3knock door`).
                // A dashless number still needs a trailing space (`5fire`
                // stays literal — deliberate G5 deviation; G4's scanner would
                // read it as delay 5 + `fire`).
                if (parts.Count > 1)
                {
                    var body = string.Join(" ", parts.Skip(1));
                    if (body.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                    {
                        _commandQueue.Clear();
                    }
                    else
                    {
                        if (body.Length > 0 && body[0] == QuickSend.Char &&
                            QuickSend.TryParse(body, out var qDelay, out var qCmd))
                            body = qDelay.Length > 0 ? $"{qDelay} {qCmd}" : qCmd;
                        var (sendDelay, sendCmd) =
                            Scripting.ScriptEngine.ParseSendDelay(body);
                        if (sendCmd.Length > 0)
                            _commandQueue.AddToQueue(sendDelay, sendCmd,
                                waitForRoundtime: true, waitForWebbed: false,
                                waitForStunned: false);
                    }
                }
                break;
            case "wait":
                if (parts.Count > 2 && double.TryParse(parts[1], out var delay))
                    _commandQueue.AddToQueue(delay, string.Join(" ", parts.Skip(2)), false, false, false);
                break;
            case "event":
                if (parts.Count > 2 && double.TryParse(parts[1], out var evDelay))
                    _eventQueue.Add(evDelay, string.Join(" ", parts.Skip(2)));
                break;
            case "script":
            {
                // Genie 4 #script <sub> [name|all] [except <name>] — script
                // lifecycle dispatcher (Core/Command.cs:2188 + the FormMain
                // Command_Script* handlers). It NEVER starts a script (scripts
                // start with ".name"); mm_train's wait mode relies on
                // "#script abort <name>". Target grammar: blank or "all" means
                // every running script; abort/pause/resume/pauseorresume also
                // honour a trailing "except <name>" (reload does not — Genie 4
                // parity). Bare #script and unknown sub-commands list what's
                // running, like #scripts (Genie 4 EventListScripts).
                var scriptSub  = parts.Count > 1 ? parts[1].ToLowerInvariant() : string.Empty;
                var scriptRest = parts.Count > 2 ? string.Join(" ", parts.Skip(2)).Trim() : string.Empty;
                switch (scriptSub)
                {
                    case "abort":
                    {
                        var (target, except) = ParseScriptTarget(scriptRest);
                        if (target is null && except is null) _host.StopAllScripts();
                        else ForEachTargetScript(target, except, n => _host.StopScript(n));
                        break;
                    }
                    case "pause":
                    {
                        var (target, except) = ParseScriptTarget(scriptRest);
                        if (except is null) _host.PauseScript(target);
                        else ForEachTargetScript(target, except, n => _host.PauseScript(n));
                        break;
                    }
                    case "resume":
                    {
                        var (target, except) = ParseScriptTarget(scriptRest);
                        if (except is null) _host.ResumeScript(target);
                        else ForEachTargetScript(target, except, n => _host.ResumeScript(n));
                        break;
                    }
                    case "pauseorresume":
                    {
                        var (target, except) = ParseScriptTarget(scriptRest);
                        if (except is null) _host.PauseOrResumeScript(target);
                        else ForEachTargetScript(target, except, n => _host.PauseOrResumeScript(n));
                        break;
                    }
                    case "reload":
                    {
                        var (target, _) = ParseScriptTarget(scriptRest);
                        _host.ReloadScript(target);
                        break;
                    }
                    case "trace":
                        _host.ShowScriptTrace(NullIfAll(scriptRest));
                        break;
                    case "vars":
                    case "variables":
                    {
                        // #script vars <name|all> [filter] — the filter is a
                        // substring match on the name=value rows.
                        var varName   = parts.Count > 2 ? parts[2] : string.Empty;
                        var varFilter = parts.Count > 3 ? string.Join(" ", parts.Skip(3)) : string.Empty;
                        _host.ShowScriptVars(NullIfAll(varName), varFilter);
                        break;
                    }
                    case "debug":
                    case "debuglevel":
                    {
                        // #script debug <level> [name|all]
                        if (parts.Count < 3 || !int.TryParse(parts[2], out var dbgLevel))
                        {
                            _host.Echo("Usage: #script debug <0-10> [name|all]");
                            break;
                        }
                        var dbgName = parts.Count > 3 ? string.Join(" ", parts.Skip(3)).Trim() : string.Empty;
                        _host.SetScriptDebugLevel(dbgLevel, NullIfAll(dbgName));
                        break;
                    }
                    case "explorer":
                        _host.ShowScriptExplorer();
                        break;
                    default:
                        // Genie 4's default: list active scripts. The text
                        // AFTER the unknown sub-word is the exact-name filter
                        // (ArrayToString(oArgs, 2)); the sub-word itself is not.
                        ListScripts(scriptRest);
                        break;
                }
                break;
            }
            case "stop":
            case "kill":
                // #stop [name] — stop the named script, or the most recent one
                // if no name is given. Genie 4 calls this #stop; we accept
                // #kill as a more decisive synonym.
                _host.StopScript(parts.Count > 1 ? parts[1] : null);
                break;
            case "stopall":
            case "killall":
                _host.StopAllScripts();
                break;
            case "pauseall":
                // #pauseall — pause every running script. Matches Genie 4's
                // Scripts → Pause All Scripts menu entry. Pause is non-
                // destructive (state preserved); use #resumeall to continue.
                _host.PauseAllScripts();
                _host.Echo("[script] all scripts paused");
                break;
            case "resumeall":
                // #resumeall — clear UserPaused on every running script.
                _host.ResumeAllScripts();
                _host.Echo("[script] all scripts resumed");
                break;
            case "traceall":
            {
                // #traceall <0-10> — apply a debug / tracing level to every
                // running script. Matches Genie 4's Scripts → Trace All
                // Scripts menu entry. 0 disables tracing; higher values
                // surface progressively more script-internal echoes.
                int level = 0;
                if (parts.Count > 1) int.TryParse(parts[1], out level);
                _host.SetTraceLevelAll(level);
                _host.Echo(level <= 0
                    ? "[script] trace disabled on all scripts"
                    : $"[script] trace level {level} applied to all scripts");
                break;
            }
            case "scripts":
                // List currently-running scripts (same as bare #script).
                ListScripts(parts.Count > 1 ? string.Join(" ", parts.Skip(1)).Trim() : string.Empty);
                break;
            case "edit":
            {
                // #edit <name> — open Scripts/<name>.cmd in the user's
                // configured external editor (or OS default). Genie 4
                // pairs this with the `editor` setting in settings.cfg;
                // we honour the same setting via DisplaySettings.EditorPath.
                if (parts.Count < 2)
                {
                    _host.Echo("Usage: #edit <script-name>");
                    break;
                }
                _host.EditScript(parts[1]);
                break;
            }
            case "layout":
            case "layouts":
                // #layout <sub> [args] — forwarded whole (minus the verb) to
                // the App layer, which owns layout storage + dock state. The
                // remainder is passed verbatim so layout names may contain
                // spaces (e.g. "#layout save My Big Layout").
                _host.LayoutCommand(string.Join(" ", parts.Skip(1)));
                break;
            case "plugin":
            case "plugins":
                // #plugin <sub> [args] — forwarded to the App layer, which owns
                // the plugin loader + Plugins folder.
                _host.PluginCommand(string.Join(" ", parts.Skip(1)));
                break;
            case "config":
            case "set":
            case "setting":
            case "settings":
                // #config / #set / #setting / #settings — Genie 4 parity for
                // the Configuration dialog + settings.cfg ops. Forwarded whole
                // (minus the verb) so the App layer can dispatch save / load /
                // edit / get-key / set-key, or open the Configuration dialog
                // for the bare-verb form.
                _host.ConfigCommand(string.Join(" ", parts.Skip(1)));
                break;
            case "play":
            case "playsound":
            case "playwave":
                // #play <sound> — play a sound effect (Genie 4 parity). The host
                // applies the PlaySounds gate + SoundDir/.wav resolution. Useful
                // from scripts and as a trigger action.
                if (parts.Count > 1)
                    _host.PlaySound(string.Join(" ", parts.Skip(1)));
                else
                    _host.Echo("Usage: #play <sound file>");
                break;
            case "speak":
            case "say":
                // #speak <text> — read text aloud via TTS. The host owns the
                // engine + audio and synthesizes off-thread. Useful from
                // scripts and as a trigger action. (#say is an alias; the game
                // 'say' verb is unaffected — this only fires on the # prefix.)
                if (parts.Count > 1)
                    _host.Speak(string.Join(" ", parts.Skip(1)));
                else
                    _host.Echo("Usage: #speak <text>");
                break;
            case "tts":
                // #tts <install|use|voices|read|mute|rate|volume|stop|status> —
                // manage TTS. Forwarded whole (minus the verb) to the App host.
                _host.TtsCommand(string.Join(" ", parts.Skip(1)));
                break;
            case "flash":
                // #flash — flash the taskbar / dock entry (Genie 4 parity).
                // Takes no arguments; useful as a trigger action so a whisper
                // or script alert grabs attention while the window is in the
                // background.
                _host.FlashWindow();
                break;
            case "browser":
                // #browser <url> — open the OS-default browser (Genie 4 parity;
                // G4 prepends http:// when no scheme is given). Used by wiki
                // lookups (Inventory View) and scripts.
                if (parts.Count > 1)
                {
                    var url = string.Join(" ", parts.Skip(1)).Trim();
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = "https://" + url;
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _host.Echo($"#browser: could not open the browser ({ex.Message}). Visit {url} manually.");
                    }
                }
                else
                    _host.Echo("Usage: #browser <url>");
                break;
            case "beep":
            case "bell":
                // #beep / #bell — sound the system alert (Genie 4 parity, gated
                // on PlaySounds by the host). No arguments; a common trigger
                // action and hunting-script alert. hunt.cmd et al. use `#beep`
                // liberally — before this they printed "Unknown command: beep".
                _host.Beep();
                break;
            case "goto":
            case "go2":
                // #goto <room> — start a mapper walk to a room identified by
                // numeric map id, note label, or title text. Forwarded whole
                // (minus the verb) so multi-word labels survive. Genie 4
                // parity; the App resolves + drives the attended walk.
                if (parts.Count > 1)
                    _host.MapperGoto(string.Join(" ", parts.Skip(1)));
                else
                    _host.Echo("Usage: #goto <room id | label | title>");
                break;
            case "audit":
            {
                // #audit on|off|xmlhunting — Live Audit diagnostic log (raw XML +
                // events + zone/room) for real-time troubleshooting. xmlhunting
                // adds the XML tag-coverage pass (flags data DR sends that the
                // parser doesn't consume).
                var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "";
                var mode = sub switch
                {
                    "on"                              => (Diagnostics.AuditMode?)Diagnostics.AuditMode.On,
                    "off"                             => Diagnostics.AuditMode.Off,
                    "xmlhunting" or "xml" or "hunt"   => Diagnostics.AuditMode.XmlHunting,
                    _                                 => null,
                };
                if (mode is { } m)
                {
                    var path = _host.SetLiveAudit(m);
                    var label = m == Diagnostics.AuditMode.XmlHunting ? "XML HUNTING" : m.ToString().ToUpperInvariant();
                    _host.Echo($"[audit] live audit {label} → {path}");
                }
                else
                    _host.Echo("Usage: #audit on | off | xmlhunting");
                break;
            }
            case "parse":
            {
                // #parse <text> — inject a synthetic game line (Genie 4 parity).
                // Feeds scripts (waitfor/match), the global trigger list, and
                // plugins as if the server emitted it; never echoes or sends to
                // the game. Take the verbatim tail after the verb (not a
                // re-joined parts list) so meaningful whitespace a trigger might
                // match on is preserved. ProcessInput already $-expanded the line.
                int sp = command.IndexOf(' ');
                var tail = sp >= 0 ? command[(sp + 1)..] : string.Empty;
                if (tail.Length > 0) _host.InjectParsedLine(tail);
                break;
            }
            case "queue":
            {
                // #queue clear (Genie 4 Core/Command.cs) — flush every command
                // waiting to be sent: the RT-gated CommandQueue (mapper walks,
                // #put delays) and each running script's pending put/send
                // segments. Commands already on the wire are untouched — this
                // clears what WOULD be sent, it can't unsend. travel.cmd's
                // RETURN_CLEAR depends on it before rerouting.
                var qsub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "";
                if (qsub == "clear")
                {
                    _commandQueue.Clear();
                    _host.ClearSendQueue();
                    _host.Echo("[queue] cleared");
                }
                else
                    _host.Echo("Usage: #queue clear");
                break;
            }
            case "mapper":
            {
                // #mapper subcommands (Genie 3/4 parity, #75 + #146). `reset`
                // re-resolves the current room via the shared engine (works in
                // Console too); everything else forwards to the App mapper host,
                // which echoes usage for anything unknown. Either way a stray
                // "#mapper foo" never reaches the game.
                var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "";
                if (sub == "reset")
                {
                    _host.Echo("[mapper] Re-resolving current room…");
                    _host.MapperReset();
                }
                else
                    _host.MapperCommand(string.Join(" ", parts.Skip(1)));
                break;
            }
            // Genie 4 parity (#connect / #reconnect / #lichconnect). Args are
            // already $variable-expanded by ProcessInput, so a login script's
            // `#connect $acct $pw Char DR` arrives literal. The App owns the
            // connection lifecycle + profiles, so forward the parsed request;
            // argument-count interpretation (0=reconnect, 1=profile, 4=explicit)
            // lives in the App handler so it can resolve profiles and echo usage.
            case "connect":
                _host.Connect(new ConnectRequest(parts.Skip(1).ToList(), IsLich: false));
                break;
            case "lichconnect":
            case "lconnect":
            case "lc":   // Genie 4 aliases for #lichconnect
                _host.Connect(new ConnectRequest(parts.Skip(1).ToList(), IsLich: true));
                break;
            case "reconnect":
                // Always reconnect the last session, regardless of any tokens.
                _host.Connect(new ConnectRequest(Array.Empty<string>(), IsLich: false));
                break;
            case "lichsettings":
            case "ls":   // Genie 4 #ls — dump the Lich auto-launch config
                _host.Echo("Lich Settings");
                _host.Echo("----------------------------------------------------");
                _host.Echo($"Auto-launch:\t {(_config.LichAutoLaunch ? "on" : "off")}");
                _host.Echo($"Ruby Path:\t {(_config.LichRubyPath.Length == 0 ? "(from PATH)" : _config.LichRubyPath)}");
                _host.Echo($"Lich Path:\t {(_config.LichPath.Length == 0 ? "(not set)" : _config.LichPath)}");
                _host.Echo($"Lich Args:\t {(_config.LichArguments.Length == 0 ? "(none)" : _config.LichArguments)}");
                _host.Echo($"Start Pause:\t {_config.LichStartPause}s");
                _host.Echo($"Lich Debug:\t {(_config.LichDebug ? "on" : "off")} (mirror owned temp/debug-*.log)");
                if (_config.LichArguments.Contains("{character}", StringComparison.OrdinalIgnoreCase)
                    || _config.LichArguments.Contains("{port}", StringComparison.OrdinalIgnoreCase))
                    _host.Echo("Placeholders:\t {character} and {port} expand from the Lich-proxy Character / port at connect.");
                if (!_config.LichAutoLaunch)
                    _host.Echo("Auto-launch is off — #lc / #lichconnect attach to an already-running Lich. " +
                               "Set #config lichpath {path} and #config lichautolaunch on to have Genie start it.");
                break;
            case "class":
            case "classes":
                HandleClass(parts);
                break;
            case "alias":
            case "aliases":
                HandleAlias(parts);
                break;
            case "unalias":
                if (parts.Count > 1 && Aliases is not null)
                {
                    Aliases.RemoveAlias(parts[1]);
                    _host.Echo($"Alias removed: {parts[1]}");
                }
                break;
            case "var":
            case "variable":
                HandleVar(command, parts);
                break;
            case "dialogs":
                HandleDialogs(parts);
                break;
            case "eval":
            case "evalmath":
            {
                // Genie 4 #eval / #evalmath (Core/Command.cs:1037) — evaluate an
                // expression. In Genie 4 the result feeds ParseAllArgs, which is
                // how `#var x {#eval expr}` stores a computed value (that path is
                // ResolveValueCommand here); typed standalone, echo the result.
                if (parts.Count > 1)
                {
                    var evalResult = ResolveValueCommand(
                        "#" + parts[0].ToLowerInvariant() + " " + ValueTail(command, parts, 1));
                    _host.Echo(evalResult);
                }
                break;
            }
            case "unvar":
            case "unsetvariable":
                if (parts.Count > 1 && Variables is not null)
                {
                    Variables.Store.Remove(parts[1]);
                    _host.Echo($"Variable removed: {parts[1]}");
                }
                break;
            case "tvar":
                // #tvar name value — set a session-global $variable that all
                // scripts can read via $name. Genie 4 parity: tvars persist
                // across scripts within the session. `save`/`load` round-trip
                // them to tvars.cfg — a Genie 5 convenience (Genie 4 tvars are
                // VariableType.Temporary and are never written to disk).
                if (parts.Count >= 3)
                {
                    var tname  = parts[1];
                    var tvalue = ResolveValueCommand(ValueTail(command, parts, 2));
                    _host.SetGlobalVariable(tname, tvalue);
                    _userTvars.Add(tname);
                    if (_processInputDepth == 1 && _interactive)
                        _host.Echo($"Global variable set: {tname}={tvalue}");
                }
                else if (parts.Count == 2)
                {
                    // Single-arg subcommands: save / load / list filter.
                    switch (parts[1].ToLowerInvariant())
                    {
                        case "save": SaveTvars(); break;
                        case "load": LoadTvars(); break;
                        default:     _host.Echo($"Usage: #tvar name value  |  #tvar save  |  #tvar load"); break;
                    }
                }
                break;
            case "untvar":
                if (parts.Count > 1)
                {
                    _host.RemoveGlobalVariable(parts[1]);
                    _userTvars.Remove(parts[1]);
                    _host.Echo($"Global variable removed: {parts[1]}");
                }
                break;
            case "highlight":
            case "highlights":
                HandleHighlight(parts);
                break;
            case "unhighlight":
                if (parts.Count > 1 && Highlights is not null)
                {
                    Highlights.RemoveRule(parts[1]);
                    _host.Echo($"Highlight removed: {parts[1]}");
                }
                break;
            case "name":
            case "names":
                HandleNames(parts);
                break;
            case "unname":
                if (parts.Count > 1 && Names is not null)
                {
                    if (Names.Remove(parts[1])) _host.Echo($"Name removed: {parts[1]}");
                }
                break;
            case "preset":
            case "presets":
                HandlePreset(parts);
                break;
            case "trigger":
            case "triggers":
            case "action":
            case "actions":
                HandleTrigger(parts);
                break;
            case "untrigger":
            case "unaction":
                if (parts.Count > 1 && Triggers is not null)
                {
                    Triggers.RemoveTrigger(parts[1]);
                    _host.Echo($"Trigger removed: {parts[1]}");
                }
                break;
            case "substitute":
            case "substitutes":
            case "sub":
            case "subs":
                HandleSubstitute(parts);
                break;
            case "unsubstitute":
            case "unsub":
                if (parts.Count > 1 && Substitutes is not null)
                {
                    Substitutes.RemoveRule(parts[1]);
                    _host.Echo($"Substitute removed: {parts[1]}");
                }
                break;
            case "gag":
            case "gags":
            case "ignore":     // Genie 4 synonym for #gag (#151)
            case "ignores":
                HandleGag(parts);
                break;
            case "ungag":
            case "unignore":   // Genie 4 synonym for #ungag (#151)
            case "unignores":
                if (parts.Count > 1 && Gags is not null)
                {
                    Gags.RemoveRule(parts[1]);
                    _host.Echo($"Gag removed: {parts[1]}");
                }
                break;
            case "macro":
            case "macros":
                HandleMacro(parts);
                break;
            case "unmacro":
                if (parts.Count > 1 && Macros is not null)
                {
                    Macros.Remove(parts[1]);
                    _host.Echo($"Macro removed: {parts[1]}");
                }
                break;
            default:
                _host.Echo($"Unknown command: {parts[0]}");
                break;
        }
    }

    // ── #log ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Append a line to a log file for <c>#log</c> (Genie 4 <c>Utility/Log.cs</c>).
    /// Creates the target directory if needed. When <paramref name="banner"/> is
    /// true and the file does not yet exist, a "LOG CREATED" header is written
    /// first (matches Genie 4 <c>Log.LogText</c>; the named-file <c>LogLine</c>
    /// form passes false). Serialized on <see cref="_logLock"/> since multiple
    /// scripts can log concurrently. Write failures are reported, not thrown —
    /// a bad log path must never abort the command that issued the <c>#log</c>.
    /// </summary>
    /// <summary>
    /// Parse a <c>#script</c> sub-command target: <c>&lt;name&gt;</c>,
    /// <c>all</c>, or blank, with an optional trailing <c>except &lt;name&gt;</c>
    /// (Genie 4 FormMain.Command_Script* semantics — the except keyword must
    /// follow something, and "all"/blank both mean every running script).
    /// A null Target means the every-script case.
    /// </summary>
    private static (string? Target, string? Except) ParseScriptTarget(string rest)
    {
        string? except = null;
        int i = rest.ToLowerInvariant().IndexOf("except ", StringComparison.Ordinal);
        if (i > 0)
        {
            except = rest[(i + 7)..].Trim();
            rest   = rest[..i].TrimEnd();
            if (except.Length == 0) except = null;
        }
        if (rest.Length == 0 || rest.Equals("all", StringComparison.OrdinalIgnoreCase))
            return (null, except);
        return (rest, except);
    }

    private static string? NullIfAll(string s)
        => s.Length == 0 || s.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : s;

    /// <summary>Apply <paramref name="op"/> to every running script matching
    /// <paramref name="target"/> (null = all) and not excluded by
    /// <paramref name="except"/>.</summary>
    private void ForEachTargetScript(string? target, string? except, Action<string> op)
    {
        foreach (var n in _host.RunningScripts())
        {
            if (target is not null && !n.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            if (except is not null && n.Equals(except, StringComparison.OrdinalIgnoreCase)) continue;
            op(n);
        }
    }

    /// <summary>The <c>#script</c>/<c>#scripts</c> listing — Genie 4's
    /// EventListScripts format: an "Active scripts:" header, then one status
    /// line per running script, or "None."</summary>
    private void ListScripts(string filter)
    {
        var lines = _host.ScriptStatusLines(NullIfAll(filter));
        _host.Echo("Active scripts:");
        if (lines.Count == 0) _host.Echo("None.");
        else foreach (var l in lines) _host.Echo(l);
    }

    private void WriteLogFile(string path, string text, bool banner)
    {
        try
        {
            lock (_logLock)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new System.Text.StringBuilder();
                if (banner && !File.Exists(path))
                    sb.Append("*** LOG CREATED AT ").Append(DateTime.Now).Append(" ***")
                      .Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append(text).Append(Environment.NewLine);
                File.AppendAllText(path, sb.ToString());
            }
        }
        catch (Exception ex)
        {
            _host.Echo($"[#log] could not write '{path}': {ex.Message}");
        }
    }

    // ── #class ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#class</c>. Supports:
    /// <list type="bullet">
    /// <item><c>#class</c> — list all classes with active/inactive state.</item>
    /// <item><c>#class +name -other +all -all</c> — bulk activation/deactivation (any +/- token).</item>
    /// <item><c>#class name on|off|true|false|1|0</c> — explicit set; <c>name=all</c> applies to every class.</item>
    /// <item><c>#class name</c> — list classes whose name contains the filter string.</item>
    /// <item><c>#class clear</c> — drop everything except <c>default</c>.</item>
    /// <item><c>#class save</c> — write <c>classes.cfg</c> in <see cref="GenieConfig.ConfigProfileDir"/>.</item>
    /// <item><c>#class load</c> — re-run <c>classes.cfg</c> through the pipeline.</item>
    /// </list>
    /// Reference: Genie 4 <c>Core/Command.cs:1254-1374</c>.
    /// </summary>
    private void HandleClass(IReadOnlyList<string> parts)
    {
        if (Classes is null)
        {
            _host.Echo("#class is unavailable (no class engine wired).");
            return;
        }

        // #class
        if (parts.Count == 1)
        {
            ListClasses(null);
            return;
        }

        var first = parts[1];

        // #class +foo -bar +all  (any +/- prefix triggers bulk mode)
        if (first.Length > 0 && (first[0] == '+' || first[0] == '-'))
        {
            for (int i = 1; i < parts.Count; i++)
            {
                var token = parts[i];
                if (token.Length < 2) continue;
                var sign = token[0];
                var name = token[1..].ToLowerInvariant();
                if (sign == '+')
                {
                    if (name == "all")
                    {
                        _host.Echo("All Classes Activated");
                        Classes.ActivateAll();
                    }
                    else Classes.Set(name, true);
                }
                else if (sign == '-')
                {
                    if (name == "all")
                    {
                        _host.Echo("All Classes InActivated");
                        Classes.DeactivateAll();
                    }
                    else Classes.Set(name, false);
                }
            }
            return;
        }

        // Explicit subcommands (#97): a bare `list` / `set` used to be treated as
        // a class-name filter / class name — `#class list` filtered by "list" and
        // `#class set foo on` was silently ignored. Recognise them explicitly.
        var firstLower = first.ToLowerInvariant();
        // `#class list on|off` is a CLASS NAMED "list" being toggled (classes.cfg
        // replays exactly that shape), not "list filtered by on" — a filter of
        // on/off would be meaningless anyway. Everything else keeps the #97
        // explicit-list behaviour.
        bool listToggle = parts.Count == 3 &&
                          parts[2].ToLowerInvariant() is "on" or "off" or "true" or "false" or "1" or "0";
        if (firstLower == "list" && !listToggle)
        {
            ListClasses(parts.Count > 2 ? parts[2] : null);
            return;
        }
        if (firstLower == "set")
        {
            if (parts.Count < 3) { _host.Echo("Usage: #class set <name> [on|off]"); return; }
            var setName = parts[2].ToLowerInvariant();
            var setVal  = parts.Count > 3 ? parts[3].ToLowerInvariant() : "on";
            if (setVal is "off" or "false" or "0") Classes.Set(setName, false);
            else                                   Classes.Set(setName, true);
            return;
        }

        // 2-arg subcommands or single-name filter
        if (parts.Count == 2)
        {
            switch (first.ToLowerInvariant())
            {
                case "load":
                    LoadClasses();
                    break;
                case "save":
                    SaveClasses();
                    break;
                case "clear":
                    _host.Echo("Classes Cleared");
                    Classes.Clear();
                    break;
                case "edit":
                    _host.Echo("#class edit is not yet implemented.");
                    break;
                default:
                    ListClasses(first);
                    break;
            }
            return;
        }

        // 3+ args: #class name on|off|true|false|1|0
        var target = first.ToLowerInvariant();
        var value  = parts[2].ToLowerInvariant();
        var on     = value is "on" or "true" or "1";
        var off    = value is "off" or "false" or "0";

        if (target == "all")
        {
            if (on)
            {
                _host.Echo("All Classes Activated");
                Classes.ActivateAll();
            }
            else if (off)
            {
                _host.Echo("All Classes InActivated");
                Classes.DeactivateAll();
            }
            return;
        }

        if (on)       Classes.Set(target, true);
        else if (off) Classes.Set(target, false);
        // Genie 4 silently ignores unrecognised on/off tokens; mirror that.
    }

    private void ListClasses(string? filter)
    {
        if (Classes is null) return;
        _host.Echo("");
        _host.Echo("Active classes: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");

        // Genie 4 sorts alphabetically (case-insensitive). Our underlying
        // Dictionary preserves insertion order, so sort here at list time.
        var sorted = Classes.GetAll()
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
        int shown = 0;
        foreach (var kvp in sorted)
        {
            if (!string.IsNullOrEmpty(filter) &&
                kvp.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _host.Echo($"{kvp.Key}={(kvp.Value ? "True" : "False")}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    // ── Saved-cfg loading guards ─────────────────────────────────────────────
    // A .cfg rule file is a script of #commands, nothing else. The loaders
    // used to replay every line through ProcessInput, whose fallthrough is
    // send-to-game — so a wrong-format file (the Genie 4 Import dialog
    // briefly wrote JSON into .cfg names) was transmitted to the SERVER
    // line by line at connect. Loaders now dispatch only command lines and
    // self-heal JSON-format saves via PersistenceService + a cfg rewrite.

    /// <summary>Dispatch a cfg file's lines: only #command lines run; anything
    /// else is counted and reported, never sent to the game. Every line goes
    /// straight to <see cref="HandleInternalCommand"/> — NOT ProcessInput —
    /// because its variable-expansion pass bakes any $var that happens to be
    /// defined at load time into the stored rule (a trigger saved with pattern
    /// <c>$monstercount</c> came back as pattern <c>0</c>, and the next #save
    /// would have persisted the corruption). cfg files are machine-written,
    /// one #command per line, so expansion and separator-splitting are never
    /// wanted here. A <c>#</c> line under a custom commandchar dispatches the
    /// same way — saves always write <c>#</c>.</summary>
    private void RunCfgFileLines(IReadOnlyList<string> lines, string fileName)
    {
        int skipped = 0;
        _quietLoad = true;
        try
        {
            foreach (var raw in lines)
            {
                var line = raw.TrimStart();
                if (line.Length == 0) continue;
                if (line[0] == _config.CommandChar || line[0] == '#')
                    HandleInternalCommand(line[1..]);
                else
                    skipped++;
            }
        }
        finally { _quietLoad = false; }
        if (skipped > 0)
            _host.Echo($"{fileName}: skipped {skipped} line(s) that are not #commands.");
    }

    /// <summary>True while replaying a saved cfg file. Per-rule add
    /// confirmations ("Alias added: …") are suppressed then — a per-character
    /// profile replays a couple thousand rules at every connect, and the
    /// login screen is not the place to announce each one. Warnings and the
    /// per-file "X Loaded" summaries still print.</summary>
    private bool _quietLoad;

    /// <summary>Echo an add/set confirmation — silenced during cfg loads.</summary>
    private void EchoRule(string text) { if (!_quietLoad) _host.Echo(text); }

    /// <summary>True when the file body opens a JSON array/object — a legacy
    /// JSON-format save that must be deserialized, not replayed as commands.</summary>
    private static bool LooksLikeJson(IReadOnlyList<string> lines)
    {
        foreach (var raw in lines)
        {
            var t = raw.TrimStart();
            if (t.Length == 0) continue;
            return t[0] == '[' || t[0] == '{';
        }
        return false;
    }

    /// <summary>Report a completed JSON-format self-heal (deserialize → apply →
    /// the type's #save handler already rewrote the file in cfg format).</summary>
    private void EchoJsonHealed(string fileName, int count) =>
        _host.Echo($"{fileName} was a JSON-format save — converted {count} entr{(count == 1 ? "y" : "ies")} to cfg format.");

    private void SaveClasses()
    {
        if (Classes is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "classes.cfg");
        var lines = CfgFormat.ClassLines(Classes.GetAll());
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Classes Saved");
        else
            _host.Echo($"Failed to save classes: {path}");
    }

    private void LoadClasses()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "classes.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No classes file: {path}"); return; }
        if (Classes is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadClasses(path);
            if (models.Count == 0)
            {
                _host.Echo($"classes.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models) Classes.Set(m.Name, m.IsActive);
            SaveClasses();
            EchoJsonHealed("classes.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "classes.cfg");
        _host.Echo("Classes Loaded");
    }

    // ── #alias ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#alias</c>. Supports:
    /// <list type="bullet">
    /// <item><c>#alias</c> — list all aliases.</item>
    /// <item><c>#alias add {name} {expansion}</c> — add or replace.</item>
    /// <item><c>#alias remove {name}</c> — remove (also via <c>#unalias name</c>).</item>
    /// <item><c>#alias clear</c> — drop all.</item>
    /// <item><c>#alias save</c> / <c>#alias load</c> — round-trip <c>aliases.cfg</c>.</item>
    /// </list>
    /// Reference: Genie 4 <c>Core/Command.cs:1126-1252</c>.
    /// </summary>
    private void HandleAlias(IReadOnlyList<string> parts)
    {
        if (Aliases is null) { _host.Echo("#alias is unavailable."); return; }

        if (parts.Count == 1) { ListAliases(null); return; }

        var sub = parts[1].ToLowerInvariant();

        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":   LoadAliases();  return;
                case "save":   SaveAliases();  return;
                case "clear":  Aliases.Clear(); _host.Echo("Aliases Cleared"); return;
                case "edit":   _host.Echo("#alias edit is not yet implemented."); return;
                default:       ListAliases(parts[1]); return;
            }
        }

        // 3+ args: #alias add {name} {expansion}  or  #alias remove {name}
        if (sub == "remove" || sub == "delete")
        {
            if (Aliases.RemoveAlias(parts[2]))
                _host.Echo($"Alias removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #alias add {name} {expansion}"); return; }
            var name      = parts[2];
            var expansion = string.Join(" ", parts.Skip(3));
            Aliases.RemoveAlias(name);                 // upsert
            Aliases.AddAlias(name, expansion);
            EchoRule($"Alias added: {name}={expansion}");
            return;
        }

        // #alias {name} {expansion} — implicit add (Genie 4 also supports this shape).
        var implicitName      = parts[1];
        var implicitExpansion = string.Join(" ", parts.Skip(2));
        Aliases.RemoveAlias(implicitName);
        Aliases.AddAlias(implicitName, implicitExpansion);
        EchoRule($"Alias added: {implicitName}={implicitExpansion}");
    }

    private void ListAliases(string? filter)
    {
        if (Aliases is null) return;
        _host.Echo("");
        _host.Echo("Aliases: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");

        int shown = 0;
        foreach (var a in Aliases.Aliases.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                a.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var flag = a.IsEnabled ? "" : " (disabled)";
            _host.Echo($"{a.Name}={a.Expansion}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveAliases()
    {
        if (Aliases is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "aliases.cfg");
        var lines = CfgFormat.AliasLines(Aliases.Aliases);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Aliases Saved");
        else
            _host.Echo($"Failed to save aliases: {path}");
    }

    private void LoadAliases()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "aliases.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No aliases file: {path}"); return; }
        if (Aliases is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadAliases(path);
            if (models.Count == 0)
            {
                _host.Echo($"aliases.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models)
            {
                Aliases.RemoveAlias(m.Name);
                Aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
            }
            SaveAliases();
            EchoJsonHealed("aliases.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "aliases.cfg");
        _host.Echo("Aliases Loaded");
    }

    // ── #var ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#var</c>. Supports:
    /// <list type="bullet">
    /// <item><c>#var</c> — list all variables: user-defined plus reserved/
    /// live-state ones ($health, $roomid, the status flags, …) tagged
    /// <c>(reserved)</c> (#72).</item>
    /// <item><c>#var {name}</c> — show variables matching {name}, including a
    /// reserved one (e.g. <c>#var health</c>).</item>
    /// <item><c>#var {name} {value}</c> — set (or replace) a user variable.</item>
    /// <item><c>#var remove {name}</c> — drop (also via <c>#unvar name</c>).</item>
    /// <item><c>#var clear</c> — drop all user variables (reserved/live-state vars are not affected).</item>
    /// <item><c>#var save</c> / <c>#var load</c> — round-trip <c>variables.cfg</c>.</item>
    /// </list>
    /// Reference: Genie 4 <c>Core/Command.cs:854-952</c>.
    /// </summary>
    private void HandleVar(string command, IReadOnlyList<string> parts)
    {
        if (Variables is null) { _host.Echo("#var is unavailable."); return; }

        if (parts.Count == 1) { ListVars(null); return; }

        var sub = parts[1].ToLowerInvariant();

        // Explicit subcommands (#97): a bare `list` / `set` used to be treated as
        // a name filter / variable name — `#var list` filtered by the text "list"
        // and `#var set x 1` created a variable literally named "set". Recognise
        // them so they behave as users expect. (A variable genuinely named
        // "list"/"set"/"save"/… must be set via `#var set <name> <value>`.)
        if (sub == "list")
        {
            ListVars(parts.Count > 2 ? string.Join(" ", parts.Skip(2)) : null);
            return;
        }
        if (sub == "set")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #var set <name> <value>"); return; }
            var setName  = parts[2];
            var setValue = ResolveValueCommand(ValueTail(command, parts, 3));
            SetVarValue(setName, setValue);
            if (_processInputDepth == 1 && _interactive)
                EchoRule($"Variable set: {setName}={setValue}");
            return;
        }

        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":   LoadVars();  return;
                case "save":   SaveVars();  return;
                case "clear":  Variables.Store.ClearUserVariables(); _host.Echo("Variables Cleared"); return;
                case "edit":   _host.Echo("#var edit is not yet implemented."); return;
                default:       ListVars(parts[1]); return;
            }
        }

        // 3+ args
        if (sub == "remove" || sub == "delete")
        {
            Variables.Store.Remove(parts[2]);
            _host.Echo($"Variable removed: {parts[2]}");
            return;
        }

        // #var {name} {value}  (Genie 4 form — also matches #var add explicit)
        if (sub == "add" && parts.Count >= 4)
        {
            var name  = parts[2];
            var value = ResolveValueCommand(ValueTail(command, parts, 3));
            SetVarValue(name, value);
            if (_processInputDepth == 1 && _interactive)
                EchoRule($"Variable set: {name}={value}");
            return;
        }

        var implicitName  = parts[1];
        var implicitValue = ResolveValueCommand(ValueTail(command, parts, 2));
        SetVarValue(implicitName, implicitValue);
        if (_processInputDepth == 1 && _interactive)
            EchoRule($"Variable set: {implicitName}={implicitValue}");
    }

    /// <summary>
    /// Store a <c>#var</c> value. A reserved connection-state name (currently
    /// <c>connected</c>) is routed to the LIVE session globals instead of the
    /// persisted store: Genie 4 had one variable list, so <c>#var connected 0</c>
    /// overwrote the live reserved var — with the split stores a store row
    /// would be inert while connected (globals outrank it), shadow
    /// <c>$connected</c> for scripts run before the session's first connect,
    /// and pin a stale copy into variables.json/.cfg forever (public #294).
    /// </summary>
    private void SetVarValue(string name, string value)
    {
        if (ReservedConnectionVars.Contains(name))
            _host.SetGlobalVariable(name, value);
        else
            Variables!.Store.Set(name, value);
    }

    // ── #dialogs (public #156 Phase 0c) ──────────────────────────────────────

    /// <summary>Session dialog inventory — wired by GenieCore; null offline
    /// (the command explains itself in that case).</summary>
    public Dialogs.DialogSessionTracker? DialogTracker { get; set; }

    /// <summary><c>#dialogs</c> / <c>#dialogs list</c> — the server dialogs
    /// seen this session (DynamicWindows <c>/debugwindows</c> parity);
    /// <c>#dialogs report &lt;id&gt;</c> — draft a redacted GitHub coverage
    /// issue for one of them and open it in the browser for the user to
    /// review and submit (same human-in-the-loop model as the xmlhunting
    /// gap reporter). #307 lesson: a browser-launch failure is echoed on its
    /// own line and the URL is always printed — never folded into the draft.</summary>
    private void HandleDialogs(IReadOnlyList<string> parts)
    {
        var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "list";
        if (DialogTracker is null)
        {
            _host.Echo("#dialogs: no session dialog data (connect first).");
            return;
        }

        if (sub == "list")
        {
            var rows = DialogTracker.Snapshot();
            if (rows.Count == 0)
            {
                _host.Echo("#dialogs: no server dialogs seen this session.");
                return;
            }
            _host.Echo("Server dialogs seen this session:");
            foreach (var row in rows)
            {
                var census = row.ControlCounts.Count == 0
                    ? "no controls"
                    : string.Join(", ", row.ControlCounts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key}×{kv.Value}"));
                var title = string.IsNullOrEmpty(row.Title) ? "" : $" “{row.Title}”";
                _host.Echo($"  {row.Id}{title} — {row.Blocks} block(s): {census}");
            }
            _host.Echo("First sightings are journaled to Logs/" +
                       Dialogs.DialogJournal.FileName +
                       "; report one with: #dialogs report <id>");
            return;
        }

        if (sub == "report" && parts.Count > 2)
        {
            var id  = parts[2];
            var row = DialogTracker.TryGet(id);
            if (row is null)
            {
                _host.Echo($"#dialogs: no dialog '{id}' seen this session. #dialogs list shows what has been.");
                return;
            }
            var draft = Dialogs.DialogGapReport.Build(row, BuildReportContext());
            _host.Echo($"Drafted “{draft.Title}” — review it in the browser and submit if it looks right.");
            _host.Echo($"If the browser didn't open: {draft.Url}");
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(draft.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _host.Echo($"#dialogs: could not open the browser ({ex.Message}) — use the link above.");
            }
            return;
        }

        _host.Echo("Usage: #dialogs [list] | #dialogs report <id>");
    }

    /// <summary>Version/OS/commit lines for gap-report drafts, from the entry
    /// assembly's informational version (same derivation the App host uses).</summary>
    private static Diagnostics.XmlGapReport.ReportContext BuildReportContext()
    {
        var asm  = System.Reflection.Assembly.GetEntryAssembly();
        var info = (asm is null ? null : System.Reflection.CustomAttributeExtensions
                       .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion)
                   ?? "5.0.0";
        var plus = info.IndexOf('+');
        return new Diagnostics.XmlGapReport.ReportContext(
            AppVersion: plus >= 0 ? info[..plus] : info,
            Os:         System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Commit:     plus >= 0 ? info[(plus + 1)..] : "local",
            Trigger:    "#dialogs report");
    }

    /// <summary>
    /// Value-position text for a command (public #300). A single (final) token
    /// keeps its historical grouping strip — <c>#var x {#eval …}</c> stores the
    /// brace CONTENT, the mm_train idiom. A multi-token value is sliced RAW
    /// from the original command instead of rejoined from parsed tokens:
    /// ParseArgs strips quotes per token, so the rejoin turned
    /// <c>#eval replacere("$1"," ","_")</c> into <c>#eval replacere($1, ,_)</c>
    /// — an unevaluatable expression that then stored as its literal text
    /// (the reporter's trigger). Raw slicing also matches Genie 4, whose
    /// command args never treated quotes as grouping.
    /// </summary>
    private static string ValueTail(string command, IReadOnlyList<string> parts, int skip)
        => parts.Count - skip == 1
            ? parts[skip]
            : Parsing.ArgumentParser.RawTail(command, skip);

    /// <summary>
    /// Genie 4 <c>ParseAllArgs</c> parity (Core/Command.cs:2883): a command's
    /// VALUE text is routed back through the command processor, so a value
    /// that is itself a result-returning <c>#</c>-command becomes that
    /// command's RESULT — the menu-script idiom
    /// <c>#var selection {#eval toupper("$selection")}</c> (mm_train) must
    /// store <c>MAGIC</c>, not the literal <c>#eval toupper(...)</c> text.
    /// Genie 5 supports the two result-returning commands the script corpus
    /// actually uses in value position (<c>#eval</c> / <c>#evalmath</c>);
    /// anything else passes through unchanged. Variables are already expanded
    /// upstream, so the expression sees resolved text. A malformed expression
    /// falls back to storing the raw text (better diagnosable than "").
    /// </summary>
    private string ResolveValueCommand(string value)
    {
        var t = value.TrimStart();
        if (t.Length < 2 || t[0] != '#') return value;

        string expr;
        bool   numeric;
        if      (t.StartsWith("#eval ",     StringComparison.OrdinalIgnoreCase)) { expr = t[6..];  numeric = false; }
        else if (t.StartsWith("#evalmath ", StringComparison.OrdinalIgnoreCase)) { expr = t[10..]; numeric = true;  }
        else return value;

        try
        {
            var inst   = new Scripting.ScriptInstance();
            var result = Scripting.ScriptExpression.Eval(expr.Trim(), inst);
            return numeric
                ? Scripting.ScriptExpression.ToStr(Scripting.ScriptExpression.ToNum(result))
                : Scripting.ScriptExpression.ToStr(result);
        }
        catch
        {
            return value;
        }
    }

    private void ListVars(string? filter)
    {
        if (Variables is null) return;
        _host.Echo("");
        _host.Echo("Variables: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");

        bool Match(string key) => string.IsNullOrEmpty(filter)
            || key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        int shown = 0;

        // User-defined variables (the #var-managed set).
        foreach (var kvp in Variables.Store.GetAll().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kvp.Value.Scope != VariableScope.User) continue;
            if (!Match(kvp.Key)) continue;
            _host.Echo($"{kvp.Key}={kvp.Value.Value}");
            shown++;
        }

        // Reserved / live-state variables ($health, $roomid, $zoneid, the status
        // flags, hands, clock family, …) mirrored from the game stream into the
        // script globals, plus any #tvar session-globals. Genie 4 lists these
        // with a "(reserved)" tag and a $ prefix (#72).
        foreach (var kvp in _host.GetGlobalVariables().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!Match(kvp.Key)) continue;
            _host.Echo($"(reserved) ${kvp.Key}={kvp.Value}");
            shown++;
        }

        if (shown == 0) _host.Echo("None.");
    }

    private void SaveVars()
    {
        if (Variables is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "variables.cfg");
        var lines = CfgFormat.VariableLines(Variables.Store);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Variables Saved");
        else
            _host.Echo($"Failed to save variables: {path}");
    }

    private void LoadVars()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "variables.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No variables file: {path}"); return; }
        if (Variables is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadVariables(path);
            if (models.Count == 0)
            {
                _host.Echo($"variables.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models) Variables.Store.Set(m.Name, m.Value);
            SaveVars();
            EchoJsonHealed("variables.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "variables.cfg");
        _host.Echo("Variables Loaded");
    }

    // ── #highlight ──────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#highlight</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#highlight</c> — list.</item>
    /// <item><c>#highlight add {pattern} {fg} [{bg}] [{matchType}] [{class}]</c> — add or replace.</item>
    /// <item><c>#highlight remove {pattern}</c> — drop (or <c>#unhighlight</c>).</item>
    /// <item><c>#highlight clear</c>, <c>#highlight save</c>, <c>#highlight load</c>.</item>
    /// </list>
    /// <c>matchType</c> is one of <c>string</c> | <c>line</c> | <c>beginswith</c> | <c>regex</c>.
    /// </summary>
    private void HandleHighlight(IReadOnlyList<string> parts)
    {
        if (Highlights is null) { _host.Echo("#highlight is unavailable."); return; }

        if (parts.Count == 1) { ListHighlights(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadHighlights(); return;
                case "save":  SaveHighlights(); return;
                case "clear": Highlights.Clear(); _host.Echo("Highlights Cleared"); return;
                default:      ListHighlights(parts[1]); return;
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Highlights.RemoveRule(parts[2])) _host.Echo($"Highlight removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #highlight add {pattern} {fg} [{bg}] [{matchType}] [{class}] [{sound}] [{speak: * = line, or text}] [{windows: comma-separated, blank = all}]"); return; }
            var pattern  = parts[2];
            var fg       = parts[3];
            var bg       = parts.Count > 4 ? parts[4] : "";
            var match    = ParseMatchType(parts.Count > 5 ? parts[5] : "string");
            var cls      = parts.Count > 6 ? parts[6] : "";
            var sound    = parts.Count > 7 ? parts[7] : "";
            var speak    = parts.Count > 8 ? parts[8] : "";
            var windows  = ParseWindowList(parts.Count > 9 ? parts[9] : "");
            Highlights.RemoveRule(pattern);            // upsert
            Highlights.AddRule(pattern, fg, bg, match, false, true, cls, sound, speak, windows);
            EchoRule($"Highlight added: {pattern} fg={fg}{(string.IsNullOrEmpty(bg) ? "" : $" bg={bg}")}");
            return;
        }

        // Implicit positional: #highlight {pattern} {fg} [{bg}] [{matchType}] [{class}] [{sound}] [{speak}]
        var pPattern = parts[1];
        var pFg      = parts.Count > 2 ? parts[2] : "";
        var pBg      = parts.Count > 3 ? parts[3] : "";
        var pMatch   = ParseMatchType(parts.Count > 4 ? parts[4] : "string");
        var pCls     = parts.Count > 5 ? parts[5] : "";
        var pSound   = parts.Count > 6 ? parts[6] : "";
        var pSpeak   = parts.Count > 7 ? parts[7] : "";
        Highlights.RemoveRule(pPattern);
        Highlights.AddRule(pPattern, pFg, pBg, pMatch, false, true, pCls, pSound, pSpeak);
        EchoRule($"Highlight added: {pPattern} fg={pFg}{(string.IsNullOrEmpty(pBg) ? "" : $" bg={pBg}")}");
    }

    private void ListHighlights(string? filter)
    {
        if (Highlights is null) return;
        _host.Echo("");
        _host.Echo("Highlights: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var r in Highlights.Rules.OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                r.Pattern.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var flag = r.IsEnabled ? "" : " (disabled)";
            var cls  = string.IsNullOrEmpty(r.ClassName) ? "" : $" [{r.ClassName}]";
            var bg   = string.IsNullOrEmpty(r.BackgroundColor) ? "" : $"/{r.BackgroundColor}";
            var win  = r.Windows.Count == 0 ? "" : $" @{string.Join(",", r.Windows)}";
            _host.Echo($"{r.Pattern} → {r.ForegroundColor}{bg} ({r.MatchType}){cls}{win}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveHighlights()
    {
        if (Highlights is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "highlights.cfg");
        var lines = CfgFormat.HighlightLines(Highlights.Rules);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Highlights Saved");
        else
            _host.Echo($"Failed to save highlights: {path}");
    }

    private void LoadHighlights()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "highlights.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No highlights file: {path}"); return; }
        Highlights?.Clear();
        if (Highlights is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadHighlights(path);
            if (models.Count == 0)
            {
                _host.Echo($"highlights.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models)
                Highlights.AddRule(
                    m.Pattern, m.ForegroundColor, m.BackgroundColor,
                    Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt)
                        ? mt
                        : (m.IsRegex ? HighlightMatchType.Regex : HighlightMatchType.String),
                    m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
            SaveHighlights();
            EchoJsonHealed("highlights.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "highlights.cfg");
        _host.Echo("Highlights Loaded");
    }

    // ── #names / #name — player-name highlight list (#148) ───────────────────
    // Typed parity for the Names config tab: add / remove / list / clear plus
    // save / load. Colour rendering of these rules is #154 (DefaultHighlights).
    private void HandleNames(IReadOnlyList<string> parts)
    {
        if (Names is null) { _host.Echo("#names is unavailable."); return; }

        if (parts.Count == 1) { ListNames(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadNamesFile(); return;
                case "save":  SaveNamesFile(); return;
                case "clear": Names.Clear(); _host.Echo("Names Cleared"); return;
                default:      ListNames(parts[1]); return;   // #names Fred → filtered list
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Names.Remove(parts[2])) _host.Echo($"Name removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            // #names add {name} [{fg}] [{bg}]
            var name = parts[2];
            var fg   = parts.Count > 3 ? parts[3] : "";
            var bg   = parts.Count > 4 ? parts[4] : "";
            Names.Add(name, fg, bg);                          // Add upserts by name
            EchoNameAdded(name, fg, bg);
            return;
        }

        // Implicit positional: #names {name} {fg} [{bg}] (needs a colour, else
        // the count==2 branch above treats a lone token as a list filter).
        var pName = parts[1];
        var pFg   = parts[2];
        var pBg   = parts.Count > 3 ? parts[3] : "";
        Names.Add(pName, pFg, pBg);
        EchoNameAdded(pName, pFg, pBg);
    }

    private void EchoNameAdded(string name, string fg, string bg) =>
        EchoRule($"Name added: {name}{(string.IsNullOrEmpty(fg) ? "" : $" fg={fg}")}{(string.IsNullOrEmpty(bg) ? "" : $" bg={bg}")}");

    private void ListNames(string? filter)
    {
        if (Names is null) return;
        _host.Echo("");
        _host.Echo("Names: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var r in Names.Rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                r.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var fg = string.IsNullOrEmpty(r.ForegroundColor) ? "Default" : r.ForegroundColor;
            var bg = string.IsNullOrEmpty(r.BackgroundColor) ? "" : $"/{r.BackgroundColor}";
            _host.Echo($"{r.Name} → {fg}{bg}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveNamesFile()
    {
        if (Names is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "names.cfg");
        var lines = Names.Rules.Select(r =>
            $"#names add {ConfigPersistence.FormatArg(r.Name)} {ConfigPersistence.FormatArg(r.ForegroundColor)} {ConfigPersistence.FormatArg(r.BackgroundColor)}");
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Names Saved");
        else
            _host.Echo($"Failed to save names: {path}");
    }

    // Names / presets were never written by the JSON-misnamed import path, so
    // they only need the no-send guard, not the self-heal.
    private void LoadNamesFile()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "names.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No names file: {path}"); return; }
        Names?.Clear();
        RunCfgFileLines(lines, "names.cfg");
        _host.Echo("Names Loaded");
    }

    // ── #preset / #presets — token colour palette (#149) ─────────────────────
    // Genie 4 parity (Command.cs "preset"): list / filtered-list, save / load /
    // reset, and `#preset {id} {fg} [{bg}]` to override a KNOWN token's colour.
    // Unknown ids are rejected (the palette is a fixed vocabulary, not free-form).
    private void HandlePreset(IReadOnlyList<string> parts)
    {
        if (Presets is null) { _host.Echo("#preset is unavailable."); return; }

        if (parts.Count == 1) { ListPresets(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":         LoadPresetsFile(); return;
                case "save":         SavePresetsFile(); return;
                case "reset":
                case "clear":        Presets.ResetToDefaults(); _host.Echo("Presets reset to defaults"); return;
                default:             ListPresets(parts[1]); return;   // #preset speech → filtered list
            }
        }

        // #preset {id} {fg} [{bg}] — override a known token. Foreground is the
        // colour string ("Silver", "#FF8000", "Default"); background optional and
        // otherwise preserved, along with the token's HighlightLine flag.
        var id = parts[1];
        if (Presets.Get(id) is not { } existing) { _host.Echo($"Invalid #preset keyword: {id}"); return; }
        var fg = parts[2];
        var bg = parts.Count > 3 ? parts[3] : existing.BackgroundColor;
        Presets.Apply(new Presets.PresetRule
        {
            Id              = existing.Id,   // canonical-cased id from the palette
            ForegroundColor = fg,
            BackgroundColor = bg,
            HighlightLine   = existing.HighlightLine,
        });
        EchoRule($"Preset {existing.Id} → {fg}{(string.IsNullOrEmpty(bg) ? "" : $"/{bg}")}");
    }

    private void ListPresets(string? filter)
    {
        if (Presets is null) return;
        _host.Echo("");
        _host.Echo("Presets: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var r in Presets.Presets.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                r.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var bg = string.IsNullOrEmpty(r.BackgroundColor) ? "" : $"/{r.BackgroundColor}";
            _host.Echo($"{r.Id} → {r.ForegroundColor}{bg}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SavePresetsFile()
    {
        if (Presets is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "presets.cfg");
        var lines = Presets.Presets.Values.Select(r =>
            $"#preset {ConfigPersistence.FormatArg(r.Id)} {ConfigPersistence.FormatArg(r.ForegroundColor)} {ConfigPersistence.FormatArg(r.BackgroundColor)}");
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Presets Saved");
        else
            _host.Echo($"Failed to save presets: {path}");
    }

    private void LoadPresetsFile()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "presets.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No presets file: {path}"); return; }
        RunCfgFileLines(lines, "presets.cfg");
        _host.Echo("Presets Loaded");
    }

    private static HighlightMatchType ParseMatchType(string token) => token.ToLowerInvariant() switch
    {
        "regex"      => HighlightMatchType.Regex,
        "line"       => HighlightMatchType.Line,
        "beginswith" => HighlightMatchType.BeginsWith,
        _            => HighlightMatchType.String,
    };

    /// <summary>A comma/semicolon-separated window list ("main,room") → ids.
    /// Empty / "all" = no restriction (paints everywhere).</summary>
    private static IEnumerable<string> ParseWindowList(string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        return token.Split(new[] { ',', ';' },
                           StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(w => w.ToLowerInvariant());
    }

    // ── #trigger / #action ──────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#trigger</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#trigger</c> — list.</item>
    /// <item><c>#trigger add {pattern} {action} [{class}]</c> — add or replace.</item>
    /// <item><c>#action {command} when {pattern}</c> — Genie 4's action-first form (#162).</item>
    /// <item><c>#trigger remove {pattern}</c> — drop (or <c>#untrigger</c>).</item>
    /// <item><c>#trigger clear</c>, <c>#trigger save</c>, <c>#trigger load</c>.</item>
    /// </list>
    /// Reference: Genie 4 <c>Core/Command.cs:1386-1449</c>.
    /// </summary>
    private void HandleTrigger(IReadOnlyList<string> parts)
    {
        if (Triggers is null) { _host.Echo("#trigger is unavailable."); return; }

        if (parts.Count == 1) { ListTriggers(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadTriggers(); return;
                case "save":  SaveTriggers(); return;
                case "clear": Triggers.Clear(); _host.Echo("Triggers Cleared"); return;
                default:      ListTriggers(parts[1]); return;
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Triggers.RemoveTrigger(parts[2])) _host.Echo($"Trigger removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #trigger add {pattern} {action} [{class}] [{sound}] [{speak: * = line, or text}] [eval] [matchall]"); return; }
            AddTriggerFromArgs(parts.Skip(2).ToList());
            return;
        }

        // Implicit: #trigger {pattern} {action} [{class}] [{sound}] [{speak}] [eval]
        if (parts.Count >= 3)
            AddTriggerFromArgs(parts.Skip(1).ToList());
    }

    /// <summary>Shared #trigger add / implicit-form handler. <paramref name="args"/>
    /// is pattern, action, then optional class / sound / speak, plus an optional
    /// bare <c>eval</c> keyword (anywhere after the action) that opts the rule into
    /// script-expression evaluation of its action (#150). A bare <c>when</c> /
    /// <c>whenre</c> token instead selects Genie 4's action-first form —
    /// <c>#action {command} when {pattern}</c> — mirroring the script engine's
    /// <c>HandleAction</c>; without it the positional read below stored such
    /// rules transposed (#162).</summary>
    private void AddTriggerFromArgs(List<string> args)
    {
        if (Triggers is null) return;

        for (int i = 1; i < args.Count - 1; i++)
        {
            if (!args[i].Equals("when",   StringComparison.OrdinalIgnoreCase) &&
                !args[i].Equals("whenre", StringComparison.OrdinalIgnoreCase)) continue;

            var whenAction  = string.Join(" ", args.Take(i));
            var whenPattern = string.Join(" ", args.Skip(i + 1));
            if (whenAction.Length == 0 || whenPattern.Length == 0) break;

            Triggers.RemoveTrigger(whenPattern);
            try
            {
                Triggers.AddTrigger(whenPattern, whenAction, false, true, "", "", "", false, false);
                EchoRule($"Trigger added: {whenPattern} → {whenAction}");
            }
            catch (ArgumentException) { _host.Echo($"Invalid regexp in trigger: {whenPattern}"); }
            return;
        }

        var eval     = ExtractFlag(args, "eval");
        var matchAll = ExtractFlag(args, "matchall");
        var pattern  = args[0];
        var action   = args[1];
        var cls      = args.Count > 2 ? args[2] : "";
        var sound    = args.Count > 3 ? args[3] : "";
        var speak    = args.Count > 4 ? args[4] : "";
        Triggers.RemoveTrigger(pattern);
        try
        {
            Triggers.AddTrigger(pattern, action, false, true, cls, sound, speak, eval, matchAll);
            var flags = (eval ? " (eval)" : "") + (matchAll ? " (matchall)" : "");
            EchoRule($"Trigger added: {pattern} → {action}{flags}");
        }
        catch (ArgumentException) { _host.Echo($"Invalid regexp in trigger: {pattern}"); }
    }

    /// <summary>Remove and report a bare keyword flag (<c>eval</c> / <c>matchall</c>)
    /// among the post-action args so it doesn't shift the positional class/sound/
    /// speak slots. Braced values (a literal class/sound named that) are unaffected
    /// — only a plain token matches.</summary>
    private static bool ExtractFlag(List<string> args, string flag)
    {
        for (int i = 2; i < args.Count; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) { args.RemoveAt(i); return true; }
        return false;
    }

    private void ListTriggers(string? filter)
    {
        if (Triggers is null) return;
        _host.Echo("");
        _host.Echo("Triggers: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var t in Triggers.Triggers.OrderBy(t => t.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                t.Pattern.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var flag = t.IsEnabled ? "" : " (disabled)";
            var cls  = string.IsNullOrEmpty(t.ClassName) ? "" : $" [{t.ClassName}]";
            var ev   = (t.Eval ? " (eval)" : "") + (t.MatchAll ? " (matchall)" : "");
            _host.Echo($"{t.Pattern} → {t.Action}{cls}{ev}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveTriggers()
    {
        if (Triggers is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "triggers.cfg");
        var lines = CfgFormat.TriggerLines(Triggers.Triggers);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Triggers Saved");
        else
            _host.Echo($"Failed to save triggers: {path}");
    }

    private void LoadTriggers()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "triggers.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No triggers file: {path}"); return; }
        Triggers?.Clear();
        if (Triggers is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadTriggers(path);
            if (models.Count == 0)
            {
                _host.Echo($"triggers.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models)
                Triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled,
                                    m.ClassName, m.SoundFile, m.Speak, m.Eval, m.MatchAll);
            SaveTriggers();
            EchoJsonHealed("triggers.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "triggers.cfg");
        _host.Echo("Triggers Loaded");
    }

    // ── #substitute ─────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#substitute</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#substitute</c> — list.</item>
    /// <item><c>#substitute add {pattern} {replacement} [{class}]</c> — add or replace.</item>
    /// <item><c>#substitute remove {pattern}</c> — drop (or <c>#unsubstitute</c>).</item>
    /// <item><c>#substitute clear</c>, <c>#substitute save</c>, <c>#substitute load</c>.</item>
    /// </list>
    /// </summary>
    private void HandleSubstitute(IReadOnlyList<string> parts)
    {
        if (Substitutes is null) { _host.Echo("#substitute is unavailable."); return; }

        if (parts.Count == 1) { ListSubstitutes(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadSubstitutes(); return;
                case "save":  SaveSubstitutes(); return;
                case "clear": Substitutes.Clear(); _host.Echo("Substitutes Cleared"); return;
                default:      ListSubstitutes(parts[1]); return;
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Substitutes.RemoveRule(parts[2])) _host.Echo($"Substitute removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #substitute add {pattern} {replacement} [{class}]"); return; }
            var pattern     = parts[2];
            var replacement = parts[3];
            var cls         = parts.Count > 4 ? parts[4] : "";
            Substitutes.RemoveRule(pattern);
            Substitutes.AddRule(pattern, replacement, false, true, cls);
            EchoRule($"Substitute added: {pattern} → {replacement}");
            return;
        }

        // Implicit positional
        if (parts.Count >= 3)
        {
            var pattern     = parts[1];
            var replacement = parts[2];
            var cls         = parts.Count > 3 ? parts[3] : "";
            Substitutes.RemoveRule(pattern);
            Substitutes.AddRule(pattern, replacement, false, true, cls);
            EchoRule($"Substitute added: {pattern} → {replacement}");
        }
    }

    private void ListSubstitutes(string? filter)
    {
        if (Substitutes is null) return;
        _host.Echo("");
        _host.Echo("Substitutes: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var r in Substitutes.Rules.OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                r.Pattern.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var flag = r.IsEnabled ? "" : " (disabled)";
            var cls  = string.IsNullOrEmpty(r.ClassName) ? "" : $" [{r.ClassName}]";
            _host.Echo($"{r.Pattern} → {r.Replacement}{cls}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveSubstitutes()
    {
        if (Substitutes is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "substitutes.cfg");
        var lines = CfgFormat.SubstituteLines(Substitutes.Rules);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Substitutes Saved");
        else
            _host.Echo($"Failed to save substitutes: {path}");
    }

    private void LoadSubstitutes()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "substitutes.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No substitutes file: {path}"); return; }
        Substitutes?.Clear();
        if (Substitutes is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadSubstitutes(path);
            if (models.Count == 0)
            {
                _host.Echo($"substitutes.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models)
                Substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
            SaveSubstitutes();
            EchoJsonHealed("substitutes.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "substitutes.cfg");
        _host.Echo("Substitutes Loaded");
    }

    // ── #gag ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#gag</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#gag</c> — list.</item>
    /// <item><c>#gag add {pattern} [{class}]</c> — add or replace.</item>
    /// <item><c>#gag remove {pattern}</c> — drop (or <c>#ungag</c>).</item>
    /// <item><c>#gag clear</c>, <c>#gag save</c>, <c>#gag load</c>.</item>
    /// </list>
    /// </summary>
    private void HandleGag(IReadOnlyList<string> parts)
    {
        if (Gags is null) { _host.Echo("#gag is unavailable."); return; }

        if (parts.Count == 1) { ListGags(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadGags(); return;
                case "save":  SaveGags(); return;
                case "clear": Gags.Clear(); _host.Echo("Gags Cleared"); return;
                default:
                    // Two args, not a subcommand → treat as implicit "add this pattern".
                    Gags.RemoveRule(parts[1]);
                    Gags.AddRule(parts[1]);
                    EchoRule($"Gag added: {parts[1]}");
                    return;
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Gags.RemoveRule(parts[2])) _host.Echo($"Gag removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 3) { _host.Echo("Usage: #gag add {pattern} [{class}]"); return; }
            var pattern = parts[2];
            var cls     = parts.Count > 3 ? parts[3] : "";
            Gags.RemoveRule(pattern);
            Gags.AddRule(pattern, false, true, cls);
            EchoRule($"Gag added: {pattern}");
            return;
        }

        // Implicit: #gag {pattern} {class}
        var pImp   = parts[1];
        var clsImp = parts[2];
        Gags.RemoveRule(pImp);
        Gags.AddRule(pImp, false, true, clsImp);
        EchoRule($"Gag added: {pImp}");
    }

    private void ListGags(string? filter)
    {
        if (Gags is null) return;
        _host.Echo("");
        _host.Echo("Gags: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var r in Gags.Rules.OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                r.Pattern.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var flag = r.IsEnabled ? "" : " (disabled)";
            var cls  = string.IsNullOrEmpty(r.ClassName) ? "" : $" [{r.ClassName}]";
            _host.Echo($"{r.Pattern}{cls}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveGags()
    {
        if (Gags is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "gags.cfg");
        var lines = CfgFormat.GagLines(Gags.Rules);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Gags Saved");
        else
            _host.Echo($"Failed to save gags: {path}");
    }

    private void LoadGags()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "gags.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No gags file: {path}"); return; }
        Gags?.Clear();
        if (Gags is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadGags(path);
            if (models.Count == 0)
            {
                _host.Echo($"gags.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models)
                Gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
            SaveGags();
            EchoJsonHealed("gags.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "gags.cfg");
        _host.Echo("Gags Loaded");
    }

    // ── #macro ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#macro</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#macro</c> — list.</item>
    /// <item><c>#macro add {key} {action}</c> — bind (or replace).</item>
    /// <item><c>#macro remove {key}</c> — drop (or <c>#unmacro</c>).</item>
    /// <item><c>#macro clear</c>, <c>#macro save</c>, <c>#macro load</c>.</item>
    /// </list>
    /// <c>key</c> is the Genie 4 key string: <c>F1</c>, <c>ctrl+h</c>,
    /// <c>alt+shift+f5</c>, etc. <c>action</c> is what gets fed to
    /// <c>ProcessInput</c> when the key fires — so a macro can chain
    /// commands with <c>;</c>, send to game directly, or invoke a script
    /// via the script char.
    /// </summary>
    private void HandleMacro(IReadOnlyList<string> parts)
    {
        if (Macros is null) { _host.Echo("#macro is unavailable."); return; }

        if (parts.Count == 1) { ListMacros(null); return; }

        var sub = parts[1].ToLowerInvariant();
        if (parts.Count == 2)
        {
            switch (sub)
            {
                case "load":  LoadMacros(); return;
                case "save":  SaveMacros(); return;
                case "clear": Macros.Clear(); _host.Echo("Macros Cleared"); return;
                default:      ListMacros(parts[1]); return;
            }
        }

        if (sub == "remove" || sub == "delete")
        {
            if (Macros.Remove(parts[2])) _host.Echo($"Macro removed: {parts[2]}");
            return;
        }

        if (sub == "add")
        {
            if (parts.Count < 4) { _host.Echo("Usage: #macro add {key} {action}"); return; }
            var key    = parts[2];
            var action = string.Join(" ", parts.Skip(3));
            Macros.Add(key, action);
            EchoRule($"Macro added: {key} → {action}");
            return;
        }

        // Implicit: #macro {key} {action}
        if (parts.Count >= 3)
        {
            var key    = parts[1];
            var action = string.Join(" ", parts.Skip(2));
            Macros.Add(key, action);
            EchoRule($"Macro added: {key} → {action}");
        }
    }

    private void ListMacros(string? filter)
    {
        if (Macros is null) return;
        _host.Echo("");
        _host.Echo("Macros: ");
        if (!string.IsNullOrEmpty(filter)) _host.Echo($"Filter: {filter}");
        int shown = 0;
        foreach (var m in Macros.Rules.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(filter) &&
                m.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _host.Echo($"{m.Key} → {m.Action}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveMacros()
    {
        if (Macros is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "macros.cfg");
        var lines = CfgFormat.MacroLines(Macros.Rules);
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo("Macros Saved");
        else
            _host.Echo($"Failed to save macros: {path}");
    }

    private void LoadMacros()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "macros.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No macros file: {path}"); return; }
        Macros?.Clear();
        if (Macros is not null && LooksLikeJson(lines))
        {
            var models = new PersistenceService().LoadMacros(path);
            if (models.Count == 0)
            {
                _host.Echo($"macros.cfg: JSON-format save with no readable entries — not converted: {path}");
                return;
            }
            foreach (var m in models) Macros.Add(m.Key, m.Action);
            SaveMacros();
            EchoJsonHealed("macros.cfg", models.Count);
            return;
        }
        RunCfgFileLines(lines, "macros.cfg");
        _host.Echo("Macros Loaded");
    }

    // ── #tvar save / load ────────────────────────────────────────────────────
    //
    // A Genie 5 convenience: Genie 4's tvars are VariableType.Temporary and
    // are NEVER written to disk (verified against Genie 4 Command.cs /
    // Globals.cs — variables.cfg carries only #var-type SaveToFile entries).
    // Scripts.Globals is the in-memory home for BOTH user tvars and the
    // parser-pumped live game state (health, roomname, righthand, …), so
    // save writes only the names actually set via #tvar this session
    // (_userTvars) — never the live state, which must not round-trip into
    // a file and stomp real values on load. The file is standard cfg format
    // (one replayable #tvar command per line), so load just replays it
    // through the guarded cfg-line runner.

    /// <summary>Names set via <c>#tvar</c> this session — the only globals
    /// <c>#tvar save</c> persists. <c>#untvar</c> removes; a <c>#tvar load</c>
    /// replay re-adds via the normal <c>#tvar</c> path.</summary>
    private readonly HashSet<string> _userTvars = new(StringComparer.OrdinalIgnoreCase);

    private void SaveTvars()
    {
        var path = Path.Combine(_config.ConfigProfileDir, "tvars.cfg");
        var all  = _host.GetGlobalVariables();
        var lines = _userTvars
            .Where(all.ContainsKey)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => $"#tvar {ConfigPersistence.FormatArg(n)} {ConfigPersistence.FormatArg(all[n])}")
            .ToList();
        if (ConfigPersistence.WriteLines(path, lines))
            _host.Echo($"Global variables saved ({lines.Count})");
        else
            _host.Echo($"Failed to save global variables: {path}");
    }

    private void LoadTvars()
    {
        var path  = Path.Combine(_config.ConfigProfileDir, "tvars.cfg");
        var lines = ConfigPersistence.ReadLines(path);
        if (lines is null) { _host.Echo($"No tvars file: {path}"); return; }
        RunCfgFileLines(lines, "tvars.cfg");
        _host.Echo("Global variables loaded");
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public void Tick(bool inRoundtime = false, bool isWebbed = false, bool isStunned = false)
    {
        var queued = _commandQueue.Poll(inRoundtime, isWebbed, isStunned);
        if (!string.IsNullOrEmpty(queued)) ProcessInput(queued);
        var ev = _eventQueue.Poll();
        if (!string.IsNullOrEmpty(ev)) ProcessInput(ev);
    }
}
