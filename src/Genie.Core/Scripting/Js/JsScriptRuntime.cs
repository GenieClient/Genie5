using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using Jint;
using Jint.Runtime;

namespace Genie.Core.Scripting.Js;

/// <summary>One running <c>.js</c> script's stats for the performance overlay
/// and the Script Manager's status snapshots. <paramref name="SourcePath"/>
/// defaults to empty so existing consumers are unaffected.</summary>
public readonly record struct JsScriptStat(string Name, double ElapsedSec, bool Paused, string SourcePath = "");

/// <summary>
/// Owns the set of running <c>.js</c> scripts and the Jint engines that execute
/// them. Sits beside the cooperative <see cref="ScriptEngine"/> tick loop: the
/// engine delegates <c>.js</c> launches here from its single <c>TryStart</c>
/// front door, and forwards game lines / prompts so JS waiters can wake. Stop /
/// pause / list operations are JS-aware through the engine too, so a user can
/// control .cmd and .js scripts the same way.
///
/// <para>Each script runs on its own dedicated background thread (not the thread
/// pool — a script may block for minutes inside <c>waitFor</c>, which would
/// starve a pooled worker). Jint engines are single-threaded and only ever
/// touched from their own script thread; shared state (<see cref="_globals"/>) is
/// concurrent.</para>
/// </summary>
internal sealed class JsScriptRuntime
{
    private readonly string                                  _scriptsDir;
    private readonly Action<string>                          _send;
    private readonly Action<string>                          _echo;
    private readonly ConcurrentDictionary<string, string>    _globals;
    private readonly Func<int>                               _roundtimeRemaining;
    private readonly Action<string>                          _onStarted;
    private readonly Action<string>                          _onFinished;
    private readonly Action<string, string?, string?>?       _echoTo;

    private readonly List<JsScriptInstance>                  _instances  = new();
    private readonly object                                  _listGate   = new();
    private readonly ConcurrentDictionary<string, Regex>     _regexCache = new();

    /// <summary>Runaway-loop backstop: abort a <c>.js</c> that runs this many
    /// statements with NO <c>genie.*</c> call (a tight CPU loop). High enough that
    /// heavy-but-finite compute between host calls won't trip; a true infinite
    /// loop hits it in ~1-2s.</summary>
    private const long MaxStatementsBetweenYields = 200_000_000;

    /// <summary>Companion budget to <see cref="MaxStatementsBetweenYields"/>:
    /// abort a <c>.js</c> that allocates this much with NO <c>genie.*</c> call.
    /// Measured since the last host call, never since the script started.
    ///
    /// A tight loop reaches this long before the statement cap — Jint boxes every
    /// numeric result, so even <c>while(true){x++}</c> allocates steadily — which
    /// is why the statement cap on its own was effectively unreachable (#330).</summary>
    private const long MaxBytesBetweenYields = 128L * 1024 * 1024;

    public JsScriptRuntime(
        string                               scriptsDir,
        Action<string>                       send,
        Action<string>                       echo,
        ConcurrentDictionary<string, string> globals,
        Func<int>                            roundtimeRemaining,
        Action<string>                       onStarted,
        Action<string>                       onFinished,
        Action<string, string?, string?>?    echoTo = null)
    {
        _scriptsDir         = scriptsDir;
        _send               = send;
        _echo               = echo;
        _globals            = globals;
        _roundtimeRemaining = roundtimeRemaining;
        _onStarted          = onStarted;
        _onFinished         = onFinished;
        _echoTo             = echoTo;
    }

    public bool AnyRunning
    {
        get { lock (_listGate) return _instances.Any(i => i.Running); }
    }

    public IReadOnlyList<string> RunningNames()
    {
        lock (_listGate) return _instances.Where(i => i.Running).Select(i => i.Name).ToList();
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    public bool TryStart(string name, IReadOnlyList<string> args, string path, bool abortDupe = true)
    {
        string source;
        try { source = File.ReadAllText(path); }
        catch (Exception ex) { _echo($"[script] cannot read {name}: {ex.Message}"); return false; }

        // Reload semantics (gated by AbortDupeScript, default true): stop a
        // same-named instance first, silently (no finished-event for the corpse
        // — same caution as the .cmd engine). With it off, a second copy runs.
        if (abortDupe)
            StopInternal(name, suppressFinish: true);

        var inst = new JsScriptInstance(name) { SourcePath = Path.GetFullPath(path) };
        for (int i = 0; i < args.Count; i++) inst.Locals[(i + 1).ToString()] = args[i];
        inst.Locals["0"]          = string.Join(" ", args);
        inst.Locals["scriptname"] = name;

        lock (_listGate) _instances.Add(inst);

        var thread = new Thread(() => RunBody(inst, source))
        {
            IsBackground = true,
            Name         = $"js:{name}",
        };
        inst.Thread = thread;

        _echo($"[script] {name} started (js)");
        _onStarted(name);
        thread.Start();
        return true;
    }

    private void RunBody(JsScriptInstance inst, string source)
    {
        try
        {
            var host  = new JsHostApi(this, inst);
            var guard = new RunawayLoopGuard(MaxStatementsBetweenYields, MaxBytesBetweenYields);
            inst.ResetGuard = guard.ResetCounter;
            var engine = new Engine(opts =>
            {
                // Aborts long-running / infinite JS even with no host calls,
                // so Stop() reliably tears a script down.
                opts.CancellationToken(inst.Token);
                opts.LimitRecursion(128);

                // Runaway guard: trips on a huge run of statements OR a large
                // amount of allocation with NO genie.* call — both measured since
                // the last one (Checkpoint resets them on every host call). That
                // covers the memory bomb (`let s=""; while(true) s+="x"`) too: a
                // bomb never yields, so it never gets a fresh budget.
                //
                // We deliberately do NOT use TimeoutInterval / MaxStatements, nor
                // Jint's own LimitMemory: .js scripts are meant to run for hours
                // (hunt loops with pause/waitFor), and every one of those is a
                // LIFETIME cap a legitimate script eventually reaches just by
                // running. LimitMemory was exactly that bug — the runtime
                // executes a whole script as ONE Execute, so its per-Execute reset
                // never came round and it became a one-way allowance on work done
                // (#330). Anchoring both budgets to the yield seam is what makes
                // "runs for hours" and "a runaway is caught" compatible.
                opts.Constraints.Constraints.Add(guard);
            });
            engine.SetValue("__genieHost", host);
            engine.Execute(Prelude);
            engine.Execute(source);
        }
        catch (ScriptAbortException)      { /* stopped via host checkpoint/park */ }
        catch (ExecutionCanceledException) { /* stopped via Jint cancellation */ }
        catch (RunawayLoopException ex)
        {
            _echo($"[script] {inst.Name} aborted: runaway loop — {ex.Message} without a single " +
                  "genie.* call. Add a genie.pause/waitFor inside the loop.");
        }
        catch (JavaScriptException jse)
        {
            _echo($"[script] {inst.Name} JS error: {jse.Message}");
        }
        catch (Exception ex)
        {
            _echo($"[script] {inst.Name} error: {ex.Message}");
        }
        finally
        {
            inst.Running = false;
            lock (_listGate) _instances.Remove(inst);

            if (!inst.SuppressFinishEvent)
            {
                // Natural completion echoes "finished"; a Stop() (token cancelled)
                // already echoed "stopped", so don't double up.
                if (!inst.Token.IsCancellationRequested)
                    _echo($"[script] {inst.Name} finished");
                _onFinished(inst.Name);
            }
        }
    }

    public void Stop(string name)  => StopInternal(name, suppressFinish: false);

    private void StopInternal(string name, bool suppressFinish)
    {
        List<JsScriptInstance> hits;
        lock (_listGate)
            hits = _instances.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var i in hits)
        {
            i.SuppressFinishEvent = suppressFinish;
            if (!suppressFinish) _echo($"[script] {name} stopped");
            i.Stop();   // RunBody's finally removes it and fires the event (unless suppressed)
        }
    }

    public void StopAll()
    {
        List<JsScriptInstance> all;
        lock (_listGate) all = _instances.ToList();
        foreach (var i in all) i.Stop();
    }

    public void Pause(string name)  => ForEachNamed(name, i => i.Pause(),  "paused");
    public void Resume(string name) => ForEachNamed(name, i => i.Resume(), "resumed");

    public void PauseAll()
    {
        List<JsScriptInstance> all;
        lock (_listGate) all = _instances.ToList();
        foreach (var i in all) i.Pause();
    }

    public void ResumeAll()
    {
        List<JsScriptInstance> all;
        lock (_listGate) all = _instances.ToList();
        foreach (var i in all) i.Resume();
    }

    private void ForEachNamed(string name, Action<JsScriptInstance> action, string verb)
    {
        List<JsScriptInstance> hits;
        lock (_listGate)
            hits = _instances.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var i in hits) { action(i); _echo($"[script] {name} {verb}"); }
    }

    // ── game-event fan-out (called on the game-event thread) ─────────────────

    /// <summary>Optional sink for per-line JS dispatch time (ms) — waking
    /// <c>waitFor</c>/<c>matchWait</c> waiters. Wired by the host (the origin
    /// perf overlay) to <c>PipelineMetrics</c>; null = no timing, zero overhead.
    /// The work it measures also runs inside the host's Scripts pass, so the
    /// overlay labels the JavaScript row to make that nesting clear.</summary>
    public Action<double>? DispatchMsSink;

    public void OnGameLine(string line)
    {
        JsScriptInstance[] snapshot;
        lock (_listGate)
        {
            if (_instances.Count == 0) return;
            snapshot = _instances.ToArray();
        }
        var sink  = DispatchMsSink;
        var start = sink is null ? 0L : System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var i in snapshot) if (i.Running) i.FeedLine(line);
        sink?.Invoke(start == 0L ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>Snapshot of the currently-running <c>.js</c> scripts for the
    /// performance overlay: name, wall-clock seconds since start, paused state.</summary>
    public IReadOnlyList<JsScriptStat> RunningStats()
    {
        lock (_listGate)
        {
            var list = new List<JsScriptStat>(_instances.Count);
            foreach (var i in _instances)
                if (i.Running)
                    list.Add(new JsScriptStat(i.Name, i.RunClock.Elapsed.TotalSeconds, i.Paused, i.SourcePath));
            return list;
        }
    }

    public void OnPrompt()
    {
        JsScriptInstance[] snapshot;
        lock (_listGate)
        {
            if (_instances.Count == 0) return;
            snapshot = _instances.ToArray();
        }
        foreach (var i in snapshot) if (i.Running) i.FeedPrompt();
    }

    // ── host services (called from script threads) ───────────────────────────

    public void   Send(string command) => _send(command);
    public void   Echo(string text)    => _echo(text);

    /// <summary>Directed echo to a named window / colour. Falls back to the plain
    /// script echo when the host hasn't supplied a directed sink (e.g. headless
    /// tests), so output is never silently dropped.</summary>
    public void   EchoTo(string text, string? window, string? color)
    {
        if (_echoTo is not null) _echoTo(text, window, color);
        else                     _echo(text);
    }

    public string GetGlobal(string n)  => _globals.TryGetValue(n, out var v) ? v : "";
    public void   SetGlobal(string n, string v) { if (!string.IsNullOrEmpty(n)) _globals[n] = v; }
    public int    RoundtimeRemaining() => Math.Max(0, _roundtimeRemaining());

    public Regex GetRegex(string pattern) =>
        _regexCache.GetOrAdd(pattern, p =>
        {
            // RegexSafety match-timeout (deep-dive Phase 0): waitForRe patterns
            // run inside FeedLine ON the game pipeline — a catastrophic pattern
            // must time out (swallowed in JsScriptInstance.FeedLine) instead of
            // wedging the pipeline.
            try { return Diagnostics.RegexSafety.Build(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, safe: true); }
            catch { return Diagnostics.RegexSafety.Build(Regex.Escape(p), RegexOptions.Compiled | RegexOptions.IgnoreCase, safe: true); }
        });

    /// <summary>
    /// JS prelude run before every user script: builds the lowercase
    /// <c>genie</c> (and <c>game</c>) facade over the CLR host object. Keeping
    /// the surface here means the C# stays idiomatic PascalCase while scripts get
    /// a clean lowercase API, and the whole contract is documented in one place.
    /// </summary>
    private const string Prelude = @"
// Normalise a patterns argument (string | array of strings) into the
// newline-delimited form the host's MatchWait expects. Game lines are
// single-line, so newline is a safe, collision-free separator.
function __geniePatterns(p){
  if (p === undefined || p === null) return '';
  if (Object.prototype.toString.call(p) === '[object Array]')
    return p.map(function(x){ return String(x); }).join('\n');
  return String(p);
}
var genie = {
  put:           function(c){ __genieHost.Put(String(c)); },
  send:          function(c){ __genieHost.Put(String(c)); },
  echo:          function(t){ __genieHost.Echo(String(t)); },
  log:           function(t){ __genieHost.Echo(String(t)); },
  echoTo:        function(w, t, c){ __genieHost.EchoTo(t==null?'':String(t), w==null?null:String(w), c==null?null:String(c)); },
  waitFor:       function(t, s){ return __genieHost.WaitFor(String(t), s===undefined?0:s); },
  waitForRe:     function(p, s){ return __genieHost.WaitForRe(String(p), s===undefined?0:s); },
  waitForPrompt: function(s){ return __genieHost.WaitForPrompt(s===undefined?0:s); },
  matchWait:     function(p, s){ return __genieHost.MatchWait(__geniePatterns(p), s===undefined?0:s); },
  matchWaitRe:   function(p, s){ return __genieHost.MatchWaitRe(__geniePatterns(p), s===undefined?0:s); },
  pause:         function(s){ __genieHost.Pause(s===undefined?1:s); },
  timerStart:    function(){ __genieHost.TimerStart(); },
  timerStop:     function(){ __genieHost.TimerStop(); },
  timerElapsed:  function(){ return __genieHost.TimerElapsed(); },
  get:           function(n){ return __genieHost.Get(String(n)); },
  set:           function(n, v){ __genieHost.Set(String(n), String(v)); },
  getVar:        function(n){ return __genieHost.GetVar(String(n)); },
  setVar:        function(n, v){ __genieHost.SetVar(String(n), String(v)); },
  roundtime:     function(){ return __genieHost.Roundtime(); },
  stop:          function(){ __genieHost.Stop(); }
};
var game = genie;
";
}

/// <summary>
/// A Jint constraint that aborts a script doing a large amount of work without
/// ever calling back into the host (<c>genie.*</c>) — a tight runaway loop
/// pegging a CPU thread. "A large amount of work" has two shapes, so there are
/// two triggers: statements executed and bytes allocated.
///
/// Both are measured SINCE THE LAST HOST CALL, never since the script started:
/// <see cref="JsScriptInstance.Checkpoint"/> resets them on every <c>genie.*</c>
/// call, so a normal script (which puts / waits / pauses constantly) never trips
/// either one no matter how many hours it runs.
///
/// The allocation trigger replaces Jint's own <c>LimitMemory</c> (#330), which
/// counted allocation for the LIFETIME of the engine. Nothing ever re-baselined
/// it — the runtime executes a whole script as one Execute — so it was a
/// one-way allowance: a script was killed for having allocated 128 MB over its
/// life, however little it held at the time. Anchoring to the yield seam keeps a
/// genuine bomb bounded (a bomb never yields) without charging a well-behaved
/// script for its own history.
///
/// <see cref="Check"/> (called by Jint between statements) and the resets all run
/// on the single script thread, so no locking is needed — and
/// <c>GC.GetAllocatedBytesForCurrentThread</c> measures exactly this script for
/// the same reason: each .js script owns its thread.
/// </summary>
internal sealed class RunawayLoopGuard : Jint.Constraint
{
    private readonly long _maxStatements;
    private readonly long _maxBytes;
    private long _count;
    private long _bytesAtLastYield;

    public RunawayLoopGuard(long maxStatementsBetweenYields, long maxBytesBetweenYields)
    {
        _maxStatements    = maxStatementsBetweenYields;
        _maxBytes         = maxBytesBetweenYields;
        _bytesAtLastYield = GC.GetAllocatedBytesForCurrentThread();
    }

    public override void Check()
    {
        if (++_count > _maxStatements)
            throw new RunawayLoopException($"ran {_maxStatements:N0} statements");

        if (GC.GetAllocatedBytesForCurrentThread() - _bytesAtLastYield > _maxBytes)
            throw new RunawayLoopException($"allocated {_maxBytes / (1024 * 1024)} MB");
    }

    public override void Reset() => ResetCounter();   // Jint calls this at the start of each Execute

    /// <summary>Called from <see cref="JsScriptInstance.Checkpoint"/> on every host
    /// call: the script yielded, so both budgets start over.</summary>
    public void ResetCounter()
    {
        _count            = 0;
        _bytesAtLastYield = GC.GetAllocatedBytesForCurrentThread();
    }
}

/// <summary>Thrown by <see cref="RunawayLoopGuard"/> when a budget is exhausted
/// with no host call in between; the message says which one. Caught in
/// <c>RunBody</c>.</summary>
internal sealed class RunawayLoopException : Exception
{
    public RunawayLoopException(string what) : base(what) { }
}
