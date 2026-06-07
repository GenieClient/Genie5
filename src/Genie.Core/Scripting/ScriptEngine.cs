using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;

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
    private readonly string               _scriptsDir;

    /// <summary>
    /// Directed echo: (message, windowName, hexColor). When window or color
    /// are null the host falls back to the main game output / default colour.
    /// Set after construction by the UI layer.
    /// </summary>
    public Action<string, string?, string?>? EchoTo { get; set; }

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

    public ScriptEngine(string scriptsDir, TypeAheadSession typeAhead,
                        Action<string> sendCommand, Action<string> echo,
                        Action<string>? handleHashCmd = null)
    {
        _scriptsDir   = scriptsDir;
        _typeAhead    = typeAhead;
        _sendCommand  = sendCommand;
        _echo         = echo;
        _handleHashCmd = handleHashCmd;
        Extensions   = new ExtensionManager(new EngineExtensionHost(this));
        // Exp tracking moved to the Experience plugin (Plugin_EXPTrackerV5), which
        // now owns the $Skill.* / TDPs globals scripts read. The old
        // ExpTrackerExtension was removed to avoid a second, parallel exp parser
        // (it also had a stale mindstate table).
        Extensions.Register(new InfoTrackerExtension());
        Directory.CreateDirectory(_scriptsDir);
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
        public void Echo(string text)               => _engine._echo(text);
        public void SendCommand(string command)     => _engine._sendCommand(command);
    }

    public string ScriptsDir => _scriptsDir;
    public IReadOnlyList<ScriptInstance> Instances => _instances;
    public bool AnyRunning => _instances.Any(i => i.Running);

    /// <summary>Fired when a script finishes (done or stopped). Arg is the script name.</summary>
    public event Action<string>? ScriptFinished;

    /// <summary>
    /// Fired when a new script begins running. Arg is the script name.
    /// Used by the Scripts panel to refresh its running-scripts list — the
    /// <see cref="Instances"/> collection is mutated directly so plain
    /// <see cref="INotifyCollectionChanged"/> isn't an option.
    /// </summary>
    public event Action<string>? ScriptStarted;

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

        ScriptInstance inst;
        try
        {
            inst = ScriptParser.Parse(name, _scriptsDir, File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _echo($"[script] parse error in {name}: {ex.Message}");
            return false;
        }

        // Reload semantics: starting a script that's already running stops
        // the existing instance and replaces it with the new one. Matches
        // Genie4's behaviour and avoids two copies fighting over the same
        // game state, action triggers, and type-ahead budget.
        //
        // Important: do NOT fire ScriptFinished for the killed instance.
        // The caller initiated the reload and doesn't expect a "finished"
        // callback for the corpse — and downstream listeners (e.g. the
        // mapper's OnAutoMapperScriptFinished, which replans on finish)
        // would re-enter TryStart and spin in an infinite loop.
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_instances[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _instances[i].Running = false;
                _instances.RemoveAt(i);
                _echo($"[script] {name} reloaded (previous instance stopped)");
            }
        }

        for (int i = 0; i < args.Count; i++)
            inst.Vars[(i + 1).ToString()] = args[i];
        inst.Vars["0"] = string.Join(" ", args);
        inst.Vars["scriptname"] = name;

        // Seed the $-scope with the same values so $1..$9 work at the top
        // level. Gosubs push new frames onto this stack without touching %N.
        var initDollar = new string[10];
        initDollar[0] = inst.Vars["0"];
        for (int i = 0; i < 9; i++)
            initDollar[i + 1] = i < args.Count ? args[i] : string.Empty;
        inst.DollarStack.Push(initDollar);

        _instances.Add(inst);
        _echo($"[script] {name} started");
        ScriptStarted?.Invoke(name);
        Tick();
        return true;
    }

    private string? ResolveScriptPath(string name)
    {
        foreach (var ext in new[] { "", ".cmd", ".inc" })
        {
            var p = Path.Combine(_scriptsDir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public void StopAll()
    {
        if (_instances.Count == 0) return;
        var names = _instances.Select(i => i.Name).ToList();
        foreach (var i in _instances) i.Running = false;
        _instances.Clear();
        _echo("[script] all scripts stopped");
        foreach (var n in names) ScriptFinished?.Invoke(n);
    }

    public void Stop(string name)
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            if (!_instances[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            _instances[i].Running = false;
            _instances.RemoveAt(i);
            _echo($"[script] {name} stopped");
            ScriptFinished?.Invoke(name);
        }
    }

    public void PauseScript(string name)
    {
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { inst.UserPaused = true; _echo($"[script] {name} paused"); }
    }

    public void ResumeScript(string name)
    {
        foreach (var inst in _instances)
            if (inst.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { inst.UserPaused = false; _echo($"[script] {name} resumed"); }
    }

    public void PauseAll()
    {
        foreach (var inst in _instances) inst.UserPaused = true;
        _echo("[script] all scripts paused");
    }

    public void ResumeAll()
    {
        foreach (var inst in _instances) inst.UserPaused = false;
        _echo("[script] all scripts resumed");
        Tick();
    }

    public void OnGameLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Always dispatch to extensions — trackers must populate their
        // globals even when no scripts are running, so the next script that
        // starts sees up-to-date values.
        Extensions.DispatchGameLine(line);

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
        bool progress = true;
        int  guard    = 0;
        while (progress && guard++ < 1_000)
        {
            progress = false;
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                // The list can shrink during iteration (e.g. an action
                // calls StopAll/Stop, or a script exits). Re-check bounds.
                if (i >= _instances.Count) continue;
                var inst = _instances[i];
                if (!inst.Running) { _instances.RemoveAt(i); continue; }
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
                    { inst.Paused = false; inst.PauseMode = PauseMode.None; inst.PauseUntil = DateTime.MinValue; }
                    else continue;
                }

                if (inst.InMatchWait)
                {
                    if (DateTime.UtcNow >= inst.MatchWaitDeadline)
                    {
                        inst.InMatchWait       = false;
                        inst.MatchWaitDeadline = DateTime.MaxValue;
                        inst.PendingMatches.Clear();
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
                    bool done = false;
                    try { done = ScriptExpression.EvalBool(SubstituteVars(inst.WaitEvalExpr, inst), inst, Globals); }
                    catch { /* treat parse error as still-waiting */ }
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
                // design and bypasses this gate.
                //
                // Critically, we must also schedule a wakeup. The server
                // doesn't send a prompt when RT expires — it only prompts
                // in response to commands. If the script is RT-gated and
                // nothing else is in flight, no further server traffic
                // arrives, so Tick() is never called again and the script
                // hangs forever. ScheduleTick fires a DispatcherTimer at
                // RT-end so the engine wakes up on its own.
                if (inst.PauseMode != PauseMode.Delay &&
                    (InRoundtime?.Invoke() ?? false))
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
                    if (StepOne(inst)) progress = true;
                }
                catch (Exception ex)
                {
                    _echo($"[script] {inst.Name} stopped: {ex.Message}");
                    inst.Running = false;
                    try { ScriptFinished?.Invoke(inst.Name); } catch { /* never rethrow from cleanup */ }
                }
            }
        }
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
                try { cur = ScriptExpression.EvalBool(act.Pattern, inst, Globals); }
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
                    inst.DollarStack.Pop();
            }
        }
    }

    // ── Statement dispatch ──────────────────────────────────────────────────

    private bool StepOne(ScriptInstance inst)
    {
        // Drain any pending semicolon-split sends before advancing the PC.
        if (inst.PendingSends.Count > 0)
        {
            // Game-bound continuation of a `put a;b;c` series. Match the
            // tighter script-side cap used by the put case so the
            // semicolon-split tail can't bypass the per-prompt throttle.
            var peek = inst.PendingSends.Peek();
            bool nextSendsToGame = peek.Length > 0 && peek[0] != '#' && peek[0] != '.';
            int effectiveLimit = nextSendsToGame ? 1 : _typeAhead.Limit;
            if (_inFlight >= effectiveLimit) return false;
            var next = inst.PendingSends.Dequeue();
            if (next.Length > 0)
            {
                if (next[0] == '#')
                {
                    HandleMetaCommand(next, inst);
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
            ScriptFinished?.Invoke(inst.Name);
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
        if ((t[0] == ':' || t[^1] == ':') && !t.Contains(' ')) return true;

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

        // Clear stale AbortReason before substituting this line. The flag
        // is set by SubstituteVars when an undefined $var is encountered and
        // is checked again after the call.
        inst.AbortReason = null;
        var substituted = isActionStmt ? t : SubstituteVars(t, inst);

        // Undefined $var encountered during substitution — stop now with a
        // clear reason rather than dispatching a malformed line. Patterns
        // (waitforre, matchre) substitute via separate code paths that do
        // NOT trip this guard, so a regex anchor like ^You doesn't trigger.
        if (inst.AbortReason is not null)
        {
            _echo($"[script] {inst.Name} stopped at line {line.LineNumber}: {inst.AbortReason}");
            inst.Running = false;
            ScriptFinished?.Invoke(inst.Name);
            return false;
        }

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
            bool present = inst.Vars.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);
            DbgEcho(inst, 3, $"if_{key} (%%{key}=\"{v ?? ""}\") = {present}");
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
                    _echo($"[script] {inst.Name}:{lineNo} '{lower}' missing 'then'");
                    return true;
                }
                var condText  = rest[..thenIdx].Trim();
                var afterThen = rest[(thenIdx + 4)..].Trim();
                bool cond = EvalConditionSafe(condText, inst);
                DbgEcho(inst, 3, $"{lower} ({condText}) = {cond}");
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

                // Genie's ';' separates multiple commands in a single put. Each
                // is queued and drained one-per-tick so the type-ahead budget
                // is respected per command, not per put statement.
                var parts = SplitSemicolons(rest);
                if (parts.Count == 0) return true;

                var first = parts[0];
                bool sendsToGame = first.Length > 0 && first[0] != '#' && first[0] != '.';

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

                for (int p = 1; p < parts.Count; p++)
                    inst.PendingSends.Enqueue(parts[p]);

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
                if (inst.Labels.TryGetValue(rest.Trim(), out var gi))
                {
                    inst.Pc = gi + 1;
                    DbgEcho(inst, 1, $"goto {rest.Trim()} → line {gi + 1}");
                }
                else { _echo($"[script] unknown label: {rest}"); inst.Running = false; }
                return true;

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
                    DbgEcho(inst, 1, "gosub clear (stack emptied)");
                    return true;
                }
                if (!inst.Labels.TryGetValue(label.Trim(), out var ss))
                { _echo($"[script] unknown label: {label}"); inst.Running = false; return false; }
                inst.GosubStack.Push(inst.Pc);
                inst.Pc = ss + 1;
                DbgEcho(inst, 1, $"gosub {label.Trim()} → line {ss + 1}" +
                    (string.IsNullOrEmpty(gosubArgs) ? "" : $" args: {gosubArgs}"));
                // Gosub arguments populate $0..$9 on a NEW stack frame — they
                // do NOT touch %0..%9 (script args). On return the frame is
                // popped, restoring the caller's $-scope. Matches Genie4.
                var frame = new string[10];
                frame[0] = gosubArgs ?? string.Empty;
                if (!string.IsNullOrEmpty(gosubArgs))
                {
                    var parts = gosubArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int a = 0; a < 9; a++)
                        frame[a + 1] = a < parts.Length ? parts[a] : string.Empty;
                }
                inst.DollarStack.Push(frame);
                return true;
            }

            case "return":
                if (inst.GosubStack.Count > 0)
                {
                    var retPc = inst.GosubStack.Pop();
                    if (inst.DollarStack.Count > 1) inst.DollarStack.Pop();
                    DbgEcho(inst, 1, $"return → line {retPc}");
                    inst.Pc = retPc;
                }
                else inst.Running = false;
                return true;

            case "exit":
                inst.Running = false;
                ScriptFinished?.Invoke(inst.Name);
                return false;

            case "match":
            {
                var (label, pat) = SplitCmd(rest);
                if (!string.IsNullOrEmpty(label))
                {
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
                return true;
            }

            case "shift":
            {
                // Shifts %1→drop, %2→%1, etc. — for walking command-line args.
                // Genie4 shifts up to argcount, which can exceed 9 (e.g. an
                // 11-move walk). Find the highest numeric arg currently set
                // and shift the entire range so values past %9 migrate down.
                int max = 0;
                for (int k = 1; k <= 99; k++)
                    if (inst.Vars.ContainsKey(k.ToString())) max = k;
                for (int k = 1; k < max; k++)
                {
                    if (inst.Vars.TryGetValue((k + 1).ToString(), out var nv))
                        inst.Vars[k.ToString()] = nv;
                    else
                        inst.Vars.Remove(k.ToString());
                }
                if (max > 0) inst.Vars.Remove(max.ToString());
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

            // Genie4 Jint/plugin commands. Not ported — Genie5 has no embedded
            // JS engine or .NET plugin host. Emit a one-time per-script echo
            // so the user knows the call was a no-op, and (for jscall/plugin
            // forms that take a result var) clear the target var so downstream
            // logic doesn't read stale values.
            case "js":
            case "javascript":
                _echo($"[script] {inst.Name}:{lineNo} '{lower}' is not supported in Genie5 (no JS engine)");
                return true;

            case "jscall":
            {
                var (vn, _) = SplitCmd(rest);
                if (vn.Length > 0) inst.Vars[vn.Trim()] = string.Empty;
                _echo($"[script] {inst.Name}:{lineNo} 'jscall' is not supported in Genie5; cleared %{vn.Trim()}");
                return true;
            }

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
                    var result = ScriptExpression.Eval(expr, inst, Globals);
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
                _inFlight++;
                EchoCommand?.Invoke(inst.Name, text);
                Extensions.DispatchCommand(text);
                _sendCommand(text);
                return true;
            }
        }
    }

    /// <summary>
    /// Evaluate a condition, treating empty input and parse errors as false.
    /// Genie scripts routinely test variables that are not yet set; an undefined
    /// <c>$hidden</c> substitutes to <c>""</c> and we want <c>if ($hidden)</c>
    /// to silently mean false rather than crashing.
    /// </summary>
    private bool EvalConditionSafe(string condText, ScriptInstance inst)
    {
        if (string.IsNullOrWhiteSpace(condText)) return false;

        // Strip a single layer of empty parens / whitespace; "(  )" → false.
        var stripped = condText.Trim();
        if (stripped.Length >= 2 && stripped[0] == '(' && stripped[^1] == ')')
        {
            var inner = stripped[1..^1].Trim();
            if (inner.Length == 0) return false;
        }

        try { return ScriptExpression.EvalBool(condText, inst, Globals); }
        catch { return false; }
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
            case "#var":
            {
                // #var Name Value — script-local
                var (name, value) = SplitCmd(rest);
                if (name.Length > 0) inst.Vars[name] = value;
                return;
            }
            case "#echo":
            {
                // #echo [>Window] [#RRGGBB | ColorName] message
                // Mirrors Genie4: foreground may be a hex code or a KnownColor name
                // (e.g. Crimson, DodgerBlue) — the downstream Brush.Parse handles both.
                string? window = null;
                string? color  = null;
                var msg = rest;
                while (msg.Length > 0)
                {
                    var (tok, after) = SplitCmd(msg);
                    if (tok.Length > 0 && tok[0] == '>')
                    { window = tok[1..]; msg = after; continue; }
                    if (IsEchoColor(tok))
                    { color = tok; msg = after; continue; }
                    break;
                }
                if ((window != null || color != null) && EchoTo != null)
                    EchoTo(msg, window, color);
                else
                    _echo(msg);
                return;
            }
            // #goto / #go2 fall through to the default case below, which
            // forwards them to the host (→ CommandEngine → MapperGoto → the
            // App's mapper walk). Don't handle them here.
            case "#mapper":
                // No-op for now; mapper reset is informational only.
                return;
            case "#parse":
                // Inject fake game text — feeds match/action/waitfor pipelines
                // as if the server had emitted it.
                OnGameLine(rest);
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
            var pat = SubstituteVars(trimmed[7..].Trim(), inst);
            int n = inst.Actions.RemoveAll(a =>
                string.Equals(a.Pattern, pat, StringComparison.OrdinalIgnoreCase));
            if (n == 0) _echo($"[script] action: no trigger matched '{pat}'");
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

    private static (string cmd, string rest) SplitCmd(string s)
    {
        if (string.IsNullOrEmpty(s)) return (string.Empty, string.Empty);
        var i = s.IndexOf(' ');
        if (i < 0) return (s, string.Empty);
        return (s[..i], s[(i + 1)..].Trim());
    }

    private string SubstituteVars(string text, ScriptInstance inst)
    {
        if (text.IndexOf('%') < 0 && text.IndexOf('$') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '%' && c != '$') { sb.Append(c); continue; }
            // `%%name` / `$$name` forces a second lookup: first fetch the
            // named var's value, then use that value as a variable name for
            // a second lookup. Matches Genie4's double-evaluation semantics.
            bool doubleEval = false;
            int nameStart = i + 1;
            if (i + 1 < text.Length && text[i + 1] == c)
            { doubleEval = true; nameStart = i + 2; }
            int j = nameStart;
            // Variable names allow letters, digits, _, . and -.  During the
            // INNER pass of a double-eval, '.' and '-' terminate the name so
            // that "%%a.b" / "%%a-b" resolve %a first, then append the suffix
            // to form the outer name — matching Genie4's right-to-left
            // expansion (e.g. %%spell.Prep).
            while (j < text.Length &&
                   (char.IsLetterOrDigit(text[j]) || text[j] == '_' ||
                    (!doubleEval && (text[j] == '.' || text[j] == '-'))))
                j++;
            if (j == nameStart) { sb.Append(c); continue; }

            // Genie4 parity: shrink the candidate from the right until we
            // find a defined var. So `%caravan-there` resolves the full
            // name when it exists, but `%count-1` still falls back to
            // `%count` followed by literal "-1" when only `count` is defined.
            // If nothing in the candidate resolves, the full candidate is
            // consumed and substituted as empty (preserving prior behavior).
            int nameEnd = j;
            string name = string.Empty;
            string value = string.Empty;
            bool resolved = false;
            while (nameEnd > nameStart)
            {
                name = text[nameStart..nameEnd];
                if (TryResolveVar(name, c, inst, out value))
                { resolved = true; break; }
                nameEnd--;
            }
            if (!resolved)
            {
                // Undefined variable → empty string, for BOTH local %vars and
                // global $vars. This matches Genie 4 (Lists/Globals.cs returns
                // the line with the token resolved to nothing) and the
                // Genie5.Kzin engine, which both treat an unset variable as
                // empty rather than aborting. Scripts routinely compare against
                // optional globals — e.g. travel.cmd's
                // `if "$charactername" = "$char1"` where $char1 may never have
                // been created — and rely on the empty result so the branch
                // simply doesn't match. Scripts that need to distinguish
                // "set to empty" from "never set" use the def() helper.
                name = text[nameStart..j];
                value = string.Empty;
                nameEnd = j;
            }
            j = nameEnd;
            if (doubleEval && !string.IsNullOrEmpty(value))
            {
                // The resolved value becomes the PREFIX of the outer name.
                // If the text continues with identifier chars (e.g. ".Prep"),
                // append them so "%%spell.Prep" → %<value-of-spell>.Prep.
                int k = j;
                while (k < text.Length &&
                       (char.IsLetterOrDigit(text[k]) || text[k] == '_' ||
                        text[k] == '.' || text[k] == '-'))
                    k++;
                var outerName = value + text[j..k];
                value = c == '$'
                    ? (Globals.TryGetValue(outerName, out var g2) ? g2 : string.Empty)
                    : (inst.Vars.TryGetValue(outerName, out var l2) ? l2 : string.Empty);
                j = k;
            }
            // Array indexing: %Bags(0) splits the pipe-delimited value and
            // returns the element at that index (0-based). The index itself
            // may already have been substituted (e.g. %Bags(%BagLoop)).
            if (j < text.Length && text[j] == '(')
            {
                int close = text.IndexOf(')', j + 1);
                if (close > j)
                {
                    var idxRaw = text[(j + 1)..close].Trim();
                    // The index may itself contain %vars (e.g. %Bags(%BagLoop)),
                    // so substitute before parsing as an integer.
                    var idxStr = SubstituteVars(idxRaw, inst);
                    if (int.TryParse(idxStr, NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out var arrIdx))
                    {
                        var parts = value.Split('|');
                        value = arrIdx >= 0 && arrIdx < parts.Length
                            ? parts[arrIdx]
                            : string.Empty;
                    }
                    j = close + 1;
                }
            }
            sb.Append(value);
            i = j - 1;
        }
        return sb.ToString();
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
            // $name: locals first (when non-empty), then globals.
            if (inst.Vars.TryGetValue(name, out var sv) && !string.IsNullOrEmpty(sv))
            { value = sv; return true; }
            if (Globals.TryGetValue(name, out var gv))
            { value = gv ?? string.Empty; return true; }
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
        // the live list if a start/stop mutates it mid-read.
        var names = _instances.ToArray().Where(i => i.Running).Select(i => i.Name);
        var joined = string.Join("|", names);
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
            }
            for (int i = 0; i < 10; i++) frame[i] = string.Empty;
            frame[0] = m.Value;
            for (int i = 1; i < m.Groups.Count && i <= 9; i++)
                frame[i] = m.Groups[i].Value;
        }
        return true;
    }
}
