namespace Genie.Core.Scripting;

public enum PauseMode { None, Pause, Wait, Delay, Move }

public sealed record ScriptLine(int LineNumber, string Origin, string Raw, string Trimmed, int Indent);

/// <summary>One queued command from a multi-part put/send statement.
/// <paramref name="Delay"/> is the leading number of seconds a dashless `send`
/// segment asked to wait before being dispatched (0 for `put` tails and for
/// quick-send segments, whose wait lives inside their rewritten
/// `#send N …` command — see Commanding.QuickSend, public #278).</summary>
public readonly record struct PendingSend(string Command, double Delay);

public sealed class ScriptInstance
{
    public string Name = string.Empty;
    public List<ScriptLine> Lines = new();
    public Dictionary<string, int> Labels = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase);
    public Stack<int> GosubStack = new();

    /// <summary>Lazy per-script JavaScript library context (#104): created on the
    /// first <c>include &lt;file&gt;.js</c> / <c>js</c> / <c>jscall</c>. Holds the
    /// persistent Jint engine whose functions read/write THIS script's variables.
    /// Null until first use; lives for the script's run.</summary>
    internal Js.JsLibraryContext? JsLib;

    // $0..$9 are a SEPARATE scope from %0..%9. They hold gosub arguments or
    // the most recent regex capture groups, and are pushed/popped with the
    // gosub stack — unlike script args (%N), which live in Vars for the
    // entire script lifetime. Matches Genie4's per-frame ArgList semantics.
    public Stack<string[]> DollarStack = new();

    // Arg count of each DollarStack frame, kept in lockstep (pushed/popped at
    // the same sites). Backs $argcount — Genie 4 substitutes it from the SAME
    // ArgList that $0..$9 read (Script.cs:2335, ArgList.Count - 1), so the
    // count must travel with the frame: script args at top level, gosub args
    // in a gosub, capture-group count after a capturing match.
    public Stack<int> DollarCounts = new();

    // if-block jump tables (built at parse time)
    public Dictionary<int, int> IfFalseJump = new(); // if/elseif line idx → target when condition false
    public Dictionary<int, int> ElseJump   = new(); // else line idx → target after else block
    // When a closing '}' belongs to a true branch inside an if/elseif chain,
    // it must skip over the remaining elseif/else branches. Keyed on the
    // '}' line idx; value is the first line past the whole chain.
    public Dictionary<int, int> BraceEndJump = new();
    // When a closing '}' terminates a `while`'s body, control returns to
    // the while-line so the condition is re-evaluated.
    public Dictionary<int, int> WhileBackJump = new();

    public int  Pc;
    public bool Running = true;

    /// <summary>Full path of the file this script was parsed from — set by the
    /// engine at start. Used by hot reload (<c>#script reload</c>) and the
    /// <c>#script</c> listing's "(file)" suffix.</summary>
    public string SourcePath = string.Empty;

    /// <summary>Include base directory (the script's own folder) — needed to
    /// re-parse with identical include resolution on hot reload.</summary>
    public string BaseDir = string.Empty;

    /// <summary>Wall-clock run time for the <c>#script</c> listing (Genie 4
    /// <c>RunTimeSeconds</c>). The .js counterpart is
    /// <c>JsScriptInstance.RunClock</c>.</summary>
    public readonly System.Diagnostics.Stopwatch RunClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Set by <c>#script reload</c>; consumed at the script's next
    /// <c>goto</c>, where the engine re-reads the file and jumps to the target
    /// label in the new text (Genie 4 hot-reload semantics).</summary>
    public bool PendingReload;

    /// <summary>Rolling control-flow trace for <c>#script trace</c>.</summary>
    public readonly ScriptTrace Trace = new();

    /// <summary>Debug verbosity: 0=off, 1=goto/gosub/return, 2=+pause/wait,
    /// 3=+if, 4=+var/math, 5=+actions, 10=all rows.</summary>
    public int  DebugLevel;

    /// <summary>
    /// Line number of the most recent <c>dbg:10</c> trace. Used to suppress
    /// duplicate row-trace echoes when a statement backs off (e.g. <c>put</c>
    /// re-attempts while <c>_inFlight</c> is full, decrementing <c>Pc</c> and
    /// returning to the same line next tick).
    /// </summary>
    public int  LastDebugLine = -1;

    /// <summary>
    /// Sources ("line N" / "waiteval") that already produced a bad-condition
    /// warning this run — a condition that failed to PARSE warns once, not on
    /// every loop iteration or waiteval tick. Fresh per instance, so re-running
    /// the script warns again.
    /// </summary>
    public HashSet<string> WarnedBadConditions = new(StringComparer.Ordinal);

    /// <summary>True once <c>ScriptFinished</c> has been raised for this instance —
    /// the engine's NotifyFinished once-guard, so a death path and the tick loop's
    /// dead-instance prune can both notify without double-firing listeners.</summary>
    public bool FinishedNotified;

    // Pause / sleep state
    // PauseMode distinguishes the three blocking commands:
    //   None  — not paused
    //   Pause — blocks for duration AND until roundtime resolves (whichever is last)
    //   Wait  — blocks until next game prompt AND until roundtime resolves
    //   Delay — blocks for duration only (ignores roundtime and prompts)
    public PauseMode PauseMode;
    public bool      Paused;
    public DateTime  PauseUntil = DateTime.MinValue;

    // match / matchwait state
    public bool     InMatchWait;
    public DateTime MatchWaitDeadline = DateTime.MaxValue;
    public List<(string Label, string Pattern, bool IsRegex)> PendingMatches = new();

    // waitfor / waitforre state
    public string?  WaitForPattern;
    public bool     WaitForIsRegex;
    public DateTime WaitForDeadline = DateTime.MaxValue;

    // waiteval state — block until expression becomes true
    public string?  WaitEvalExpr;
    public DateTime WaitEvalDeadline = DateTime.MaxValue;

    // Send-gate replay buffer (public #309). SendGateBlocked is true while the
    // last StepOne attempt ended parked in an ENGINE-imposed send gate (the
    // 1-deep put pipeline, the type-ahead budget, or a queued-send delay) —
    // gates Genie 4 doesn't have: its put/send never block the script, so a
    // G4 script reaches its matchwait/waitfor before any response returns.
    // Lines arriving while gated are buffered here and replayed through the
    // matcher when matchwait/waitfor arms, restoring the visibility G4 would
    // have had. Script-authored blocks (pause/wait/delay/move/nextroom/
    // waiteval) clear the buffer — lines missed during those are missed in
    // G4 too, so replaying them would over-match.
    public bool SendGateBlocked;
    public readonly List<string> GateReplay = new();

    public bool IsBlocked => Paused || InMatchWait || WaitForPattern != null || WaitEvalExpr != null;

    /// <summary>Set when a <c>delay</c> timer expires; lets the tick loop run
    /// this instance through the roundtime gate until it next blocks. Genie 4
    /// parity: TickScript's RT early-return is skipped while the state is
    /// <c>delayed</c>, and the resumed RunScript burst executes without RT
    /// checks until the script hits another blocking statement
    /// (Genie4 Script.cs:1541). Cleared as soon as <see cref="IsBlocked"/>
    /// turns true again (or the script stops).</summary>
    public bool RtBypass;

    /// <summary>User-initiated pause from the script bar. While true the
    /// tick loop skips this instance entirely.</summary>
    public bool UserPaused;

    // action triggers — persistent, fire whenever a matching line arrives
    public List<ScriptAction> Actions = new();
    public bool ActionsEnabled = true;

    // Pending sends from a single put/send statement that contained multiple
    // semicolon-separated commands; drained one-per-tick respecting type-ahead.
    // A `send` segment may carry a leading delay (seconds to wait before it is
    // dispatched — Genie4 CommandQueue parity); `put` always enqueues Delay=0.
    public Queue<PendingSend> PendingSends = new();

    /// <summary>
    /// Earliest <see cref="DateTime.UtcNow"/> at which the current head of
    /// <see cref="PendingSends"/> may be dispatched. Armed from a `send`
    /// segment's leading delay; <see cref="DateTime.MinValue"/> means no wait.
    /// This stacks on top of the engine-level roundtime gate, so a delayed
    /// send fires at max(roundtime-end, delay-end).
    /// </summary>
    public DateTime NextSendAt = DateTime.MinValue;

    // 'timer start' baseline for the %t / %timer pseudo-variable. Null while
    // the timer is stopped.
    public DateTime? TimerStart;

    // Elapsed milliseconds retained from the last 'timer stop' (Genie 4
    // Script.cs m_dTimerLastTime). Two jobs, both G4 parity: %t keeps reading
    // the final elapsed after a stop, and the next 'timer start' RESUMES from
    // it rather than restarting at zero. Zeroed by 'timer clear'.
    public double TimerLastMs;

    // Shared RNG for the 'random' command.
    public Random Rng = new();
}

public sealed class ScriptAction
{
    public string Label    = string.Empty; // Genie 'action (label) ...' name; "" = anonymous
    public string Pattern  = string.Empty;
    public string Command  = string.Empty; // statement to run on match (script-level, not raw send)
    public bool   IsRegex;
    public bool   IsEval;                  // 'when eval <expr>' form — Pattern holds the expression
    public bool   Enabled  = true;
    public bool   LastEvalResult;          // used to detect rising-edge for when-eval actions
    /// <summary>Pre-compiled regex for IsRegex=true actions. Built once at
    /// registration so the per-line FireActions hot path doesn't recompile
    /// (with dozens of registered actions, that's a 30x+ savings per line).
    /// Null for substring/eval actions and on compile failure.</summary>
    public System.Text.RegularExpressions.Regex? CompiledRegex;
}
