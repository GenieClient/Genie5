using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
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

    /// <summary>Trigger rules (auto-fire actions on game text). Backs <c>#trigger</c>.</summary>
    public TriggerEngineFinal?  Triggers    { get; set; }

    /// <summary>Substitute rules (transform incoming text). Backs <c>#substitute</c>.</summary>
    public SubstituteEngine?    Substitutes { get; set; }

    /// <summary>Gag rules (suppress lines from the display). Backs <c>#gag</c>.</summary>
    public GagEngine?           Gags        { get; set; }

    /// <summary>Keyboard macros (F-keys, Ctrl-letter, etc.). Backs <c>#macro</c>.</summary>
    public MacroEngine?         Macros      { get; set; }

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
            var commands = input.Split(_config.SeparatorChar);

            // Echo override only applies to single-command, plain-send pipeline
            // paths. If the input fan-outs into multiple commands via the
            // separator (e.g. an alias expanded mid-flight) we drop the override
            // — there's no meaningful 1:1 echo-text mapping at that point.
            var applyOverride = echoOverride is not null && commands.Length == 1;

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
            case "send":
            case "put":
                // Genie 4: #put is the canonical "send to game" command in
                // scripts and triggers (#send is a less-used synonym). The
                // body is everything after the verb, joined with spaces so
                // multi-word arguments survive (#put glance left).
                if (parts.Count > 1) _host.SendToGame(string.Join(" ", parts.Skip(1)));
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
                if (parts.Count > 1) _host.RunScript(string.Join(" ", parts.Skip(1)));
                break;
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
            {
                // List currently-running scripts.
                var running = _host.RunningScripts();
                if (running.Count == 0)
                {
                    _host.Echo("No scripts running.");
                }
                else
                {
                    _host.Echo("Running scripts:");
                    foreach (var n in running) _host.Echo($"  {n}");
                }
                break;
            }
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
            case "mapper":
            {
                // #mapper reset — re-resolve the current room (Genie 3/4 parity).
                // Unknown/empty subcommands echo usage rather than reaching the
                // game, so a stray "#mapper foo" never leaks to the server.
                var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "";
                if (sub == "reset")
                {
                    _host.Echo("[mapper] Re-resolving current room…");
                    _host.MapperReset();
                }
                else
                    _host.Echo("Usage: #mapper reset");
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
                _host.Connect(new ConnectRequest(parts.Skip(1).ToList(), IsLich: true));
                break;
            case "reconnect":
                // Always reconnect the last session, regardless of any tokens.
                _host.Connect(new ConnectRequest(Array.Empty<string>(), IsLich: false));
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
                HandleVar(parts);
                break;
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
                // across scripts (and the engine writes them to tvars.cfg
                // for round-trip across launches via #tvar save / load).
                if (parts.Count >= 3)
                {
                    var tname  = parts[1];
                    var tvalue = string.Join(" ", parts.Skip(2));
                    _host.SetGlobalVariable(tname, tvalue);
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
                HandleGag(parts);
                break;
            case "ungag":
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

    private void SaveClasses()
    {
        if (Classes is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "classes.cfg");
        var lines = Classes.GetAll()
            .Where(kvp => !kvp.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => $"#class {kvp.Key} {(kvp.Value ? "on" : "off")}");
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
        foreach (var line in lines) ProcessInput(line);
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
            _host.Echo($"Alias added: {name}={expansion}");
            return;
        }

        // #alias {name} {expansion} — implicit add (Genie 4 also supports this shape).
        var implicitName      = parts[1];
        var implicitExpansion = string.Join(" ", parts.Skip(2));
        Aliases.RemoveAlias(implicitName);
        Aliases.AddAlias(implicitName, implicitExpansion);
        _host.Echo($"Alias added: {implicitName}={implicitExpansion}");
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
        var lines = Aliases.Aliases.Select(a =>
            $"#alias add {ConfigPersistence.FormatArg(a.Name)} {ConfigPersistence.FormatArg(a.Expansion)}");
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
        foreach (var line in lines) ProcessInput(line);
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
    private void HandleVar(IReadOnlyList<string> parts)
    {
        if (Variables is null) { _host.Echo("#var is unavailable."); return; }

        if (parts.Count == 1) { ListVars(null); return; }

        var sub = parts[1].ToLowerInvariant();

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
            var value = string.Join(" ", parts.Skip(3));
            Variables.Store.Set(name, value);
            if (_processInputDepth == 1 && _interactive)
                _host.Echo($"Variable set: {name}={value}");
            return;
        }

        var implicitName  = parts[1];
        var implicitValue = string.Join(" ", parts.Skip(2));
        Variables.Store.Set(implicitName, implicitValue);
        if (_processInputDepth == 1 && _interactive)
            _host.Echo($"Variable set: {implicitName}={implicitValue}");
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
        var lines = Variables.Store.GetAll()
            .Where(kvp => kvp.Value.Scope == VariableScope.User)
            .Select(kvp => $"#var {ConfigPersistence.FormatArg(kvp.Key)} {ConfigPersistence.FormatArg(kvp.Value.Value)}");
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
        foreach (var line in lines) ProcessInput(line);
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
            if (parts.Count < 4) { _host.Echo("Usage: #highlight add {pattern} {fg} [{bg}] [{matchType}] [{class}] [{sound}]"); return; }
            var pattern  = parts[2];
            var fg       = parts[3];
            var bg       = parts.Count > 4 ? parts[4] : "";
            var match    = ParseMatchType(parts.Count > 5 ? parts[5] : "string");
            var cls      = parts.Count > 6 ? parts[6] : "";
            var sound    = parts.Count > 7 ? parts[7] : "";
            Highlights.RemoveRule(pattern);            // upsert
            Highlights.AddRule(pattern, fg, bg, match, false, true, cls, sound);
            _host.Echo($"Highlight added: {pattern} fg={fg}{(string.IsNullOrEmpty(bg) ? "" : $" bg={bg}")}");
            return;
        }

        // Implicit positional: #highlight {pattern} {fg} [{bg}] [{matchType}] [{class}] [{sound}]
        var pPattern = parts[1];
        var pFg      = parts.Count > 2 ? parts[2] : "";
        var pBg      = parts.Count > 3 ? parts[3] : "";
        var pMatch   = ParseMatchType(parts.Count > 4 ? parts[4] : "string");
        var pCls     = parts.Count > 5 ? parts[5] : "";
        var pSound   = parts.Count > 6 ? parts[6] : "";
        Highlights.RemoveRule(pPattern);
        Highlights.AddRule(pPattern, pFg, pBg, pMatch, false, true, pCls, pSound);
        _host.Echo($"Highlight added: {pPattern} fg={pFg}{(string.IsNullOrEmpty(pBg) ? "" : $" bg={pBg}")}");
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
            _host.Echo($"{r.Pattern} → {r.ForegroundColor}{bg} ({r.MatchType}){cls}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveHighlights()
    {
        if (Highlights is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "highlights.cfg");
        var lines = Highlights.Rules.Select(r =>
            $"#highlight add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.ForegroundColor)} {ConfigPersistence.FormatArg(r.BackgroundColor)} {ConfigPersistence.FormatArg(r.MatchType.ToString())} {ConfigPersistence.FormatArg(r.ClassName)} {ConfigPersistence.FormatArg(r.SoundFile)}");
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
        foreach (var line in lines) ProcessInput(line);
        _host.Echo("Highlights Loaded");
    }

    private static HighlightMatchType ParseMatchType(string token) => token.ToLowerInvariant() switch
    {
        "regex"      => HighlightMatchType.Regex,
        "line"       => HighlightMatchType.Line,
        "beginswith" => HighlightMatchType.BeginsWith,
        _            => HighlightMatchType.String,
    };

    // ── #trigger / #action ──────────────────────────────────────────────────

    /// <summary>
    /// Genie 4 parity for <c>#trigger</c>. Forms:
    /// <list type="bullet">
    /// <item><c>#trigger</c> — list.</item>
    /// <item><c>#trigger add {pattern} {action} [{class}]</c> — add or replace.</item>
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
            if (parts.Count < 4) { _host.Echo("Usage: #trigger add {pattern} {action} [{class}] [{sound}]"); return; }
            var pattern = parts[2];
            var action  = parts[3];
            var cls     = parts.Count > 4 ? parts[4] : "";
            var sound   = parts.Count > 5 ? parts[5] : "";
            Triggers.RemoveTrigger(pattern);
            try
            {
                Triggers.AddTrigger(pattern, action, false, true, cls, sound);
                _host.Echo($"Trigger added: {pattern} → {action}");
            }
            catch (ArgumentException) { _host.Echo($"Invalid regexp in trigger: {pattern}"); }
            return;
        }

        // Implicit: #trigger {pattern} {action} [{class}] [{sound}]
        if (parts.Count >= 3)
        {
            var pattern = parts[1];
            var action  = parts[2];
            var cls     = parts.Count > 3 ? parts[3] : "";
            var sound   = parts.Count > 4 ? parts[4] : "";
            Triggers.RemoveTrigger(pattern);
            try
            {
                Triggers.AddTrigger(pattern, action, false, true, cls, sound);
                _host.Echo($"Trigger added: {pattern} → {action}");
            }
            catch (ArgumentException) { _host.Echo($"Invalid regexp in trigger: {pattern}"); }
        }
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
            _host.Echo($"{t.Pattern} → {t.Action}{cls}{flag}");
            shown++;
        }
        if (shown == 0) _host.Echo("None.");
    }

    private void SaveTriggers()
    {
        if (Triggers is null) return;
        var path  = Path.Combine(_config.ConfigProfileDir, "triggers.cfg");
        var lines = Triggers.Triggers.Select(t =>
            $"#trigger add {ConfigPersistence.FormatArg(t.Pattern)} {ConfigPersistence.FormatArg(t.Action)} {ConfigPersistence.FormatArg(t.ClassName)} {ConfigPersistence.FormatArg(t.SoundFile)}");
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
        foreach (var line in lines) ProcessInput(line);
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
            _host.Echo($"Substitute added: {pattern} → {replacement}");
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
            _host.Echo($"Substitute added: {pattern} → {replacement}");
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
        var lines = Substitutes.Rules.Select(r =>
            $"#substitute add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.Replacement)} {ConfigPersistence.FormatArg(r.ClassName)}");
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
        foreach (var line in lines) ProcessInput(line);
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
                    _host.Echo($"Gag added: {parts[1]}");
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
            _host.Echo($"Gag added: {pattern}");
            return;
        }

        // Implicit: #gag {pattern} {class}
        var pImp   = parts[1];
        var clsImp = parts[2];
        Gags.RemoveRule(pImp);
        Gags.AddRule(pImp, false, true, clsImp);
        _host.Echo($"Gag added: {pImp}");
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
        var lines = Gags.Rules.Select(r =>
            $"#gag add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.ClassName)}");
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
        foreach (var line in lines) ProcessInput(line);
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
            _host.Echo($"Macro added: {key} → {action}");
            return;
        }

        // Implicit: #macro {key} {action}
        if (parts.Count >= 3)
        {
            var key    = parts[1];
            var action = string.Join(" ", parts.Skip(2));
            Macros.Add(key, action);
            _host.Echo($"Macro added: {key} → {action}");
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
        var lines = Macros.Rules.Select(m =>
            $"#macro add {ConfigPersistence.FormatArg(m.Key)} {ConfigPersistence.FormatArg(m.Action)}");
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
        foreach (var line in lines) ProcessInput(line);
        _host.Echo("Macros Loaded");
    }

    // ── #tvar save / load (stubbed) ─────────────────────────────────────────
    //
    // Tvars are session-globals. Genie 4 persists them to tvars.cfg between
    // sessions. We don't yet — Scripts.Globals is the in-memory home for
    // both user tvars and parser-pumped live game state (health, stamina,
    // etc.), and separating them is a small refactor. The save/load
    // commands echo "not implemented" so users get a clear signal instead
    // of silent failure.

    private void SaveTvars()
    {
        _host.Echo("#tvar save is not yet implemented (tvars are in-memory only for now).");
    }

    private void LoadTvars()
    {
        _host.Echo("#tvar load is not yet implemented (tvars are in-memory only for now).");
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
