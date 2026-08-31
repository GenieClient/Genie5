using System.Threading;

namespace Genie.Core.Scripting.Js;

/// <summary>
/// One running <c>.js</c> script. Unlike a <see cref="ScriptInstance"/> (which is
/// driven cooperatively by the engine's tick loop), a JS script runs on its own
/// dedicated thread: a JavaScript function executes top-to-bottom and blocking
/// host calls like <c>genie.waitFor()</c> must actually park the call stack. This
/// class owns that thread's lifecycle and the synchronization primitives the host
/// API uses to block until a game event, timer, or stop wakes it.
///
/// <para>All the wait/sleep methods observe <see cref="Token"/>: stopping a script
/// cancels the token, which unblocks any park and surfaces as a
/// <see cref="ScriptAbortException"/> that unwinds the JS call.</para>
/// </summary>
internal sealed class JsScriptInstance
{
    public string Name { get; }

    /// <summary>Full path of the source file (set at start) — surfaced in the
    /// Script Manager's status snapshots so Edit/Open actions can target it.</summary>
    public string SourcePath = string.Empty;

    /// <summary>Script-local variables (genie.getVar/setVar). Seeded with the
    /// launch args as 1..N, the joined args as 0, and the script name.</summary>
    public Dictionary<string, string> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Thread? Thread { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public CancellationToken Token => Cts.Token;
    public volatile bool Running = true;

    /// <summary>Set on the reload path so the corpse doesn't fire the
    /// finished-event/echo (mirrors the .cmd engine's reload caution, which
    /// avoids re-entrancy in finish listeners).</summary>
    public bool SuppressFinishEvent;

    // pause/resume: set = running, reset = paused. Host calls wait on this so a
    // pause takes effect at the next host interaction.
    private readonly ManualResetEventSlim _resume = new(true);

    // line waiter
    private readonly object _gate = new();
    private Func<string, bool>? _linePredicate;
    private string? _matchedLine;
    private readonly ManualResetEventSlim _lineWake = new(false);

    // prompt waiter
    private readonly ManualResetEventSlim _promptWake = new(false);
    private volatile bool _waitingPrompt;

    public JsScriptInstance(string name) => Name = name;

    /// <summary>Wall-clock since this script was created (≈ since it started) —
    /// surfaced in the performance overlay's running-<c>.js</c> list.</summary>
    public readonly System.Diagnostics.Stopwatch RunClock = System.Diagnostics.Stopwatch.StartNew();

    public bool Paused => !_resume.IsSet;
    public void Pause()  => _resume.Reset();
    public void Resume() => _resume.Set();

    /// <summary>Cancel the script and release every park so the thread unwinds.</summary>
    public void Stop()
    {
        Running = false;
        try { Cts.Cancel(); } catch { /* already disposed */ }
        _resume.Set();      // let a paused script observe the cancellation
        _lineWake.Set();
        _promptWake.Set();
    }

    /// <summary>Set by the runtime to reset the runaway-loop statement counter
    /// whenever a host (<c>genie.*</c>) call happens — proof the script is
    /// interacting rather than spinning in a tight CPU loop. Null until the
    /// engine is built.</summary>
    internal Action? ResetGuard;

    /// <summary>Called at the top of every host API method: blocks while the
    /// script is user-paused and throws <see cref="ScriptAbortException"/> if it
    /// has been stopped. This is how pause/stop take effect mid-run.</summary>
    public void Checkpoint()
    {
        if (!Running || Cts.IsCancellationRequested) throw new ScriptAbortException();
        ResetGuard?.Invoke();   // a host call happened → clear the runaway-loop counter
        try { _resume.Wait(Cts.Token); }
        catch (OperationCanceledException) { throw new ScriptAbortException(); }
        if (!Running || Cts.IsCancellationRequested) throw new ScriptAbortException();
    }

    /// <summary>Park until a game line satisfies <paramref name="predicate"/>, the
    /// timeout elapses, or the script is stopped. Returns the matched line, or
    /// null on timeout. <paramref name="timeout"/> == <see cref="TimeSpan.Zero"/>
    /// means wait forever.</summary>
    public string? WaitForLine(Func<string, bool> predicate, TimeSpan timeout)
    {
        lock (_gate) { _linePredicate = predicate; _matchedLine = null; _lineWake.Reset(); }
        int idx;
        try
        {
            idx = WaitHandle.WaitAny(
                new[] { _lineWake.WaitHandle, Cts.Token.WaitHandle },
                timeout == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : timeout);
        }
        finally { lock (_gate) _linePredicate = null; }

        if (Cts.IsCancellationRequested) throw new ScriptAbortException();
        if (idx == WaitHandle.WaitTimeout) return null;
        lock (_gate) { var m = _matchedLine; _matchedLine = null; return m; }
    }

    /// <summary>Game-event thread: offer a line to a parked waiter. A predicate
    /// failure (notably a <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>
    /// from a pathological <c>waitForRe</c> pattern under the RegexSafety
    /// timeout) is treated as a no-match — the pipeline must never absorb an
    /// exception from a script's pattern.</summary>
    public void FeedLine(string line)
    {
        lock (_gate)
        {
            bool matched;
            try { matched = _linePredicate is { } p && p(line); }
            catch { matched = false; }
            if (matched)
            {
                _matchedLine = line;
                _lineWake.Set();
            }
        }
    }

    /// <summary>Park until the next game prompt, timeout, or stop. Returns true
    /// if a prompt arrived, false on timeout.</summary>
    public bool WaitForPrompt(TimeSpan timeout)
    {
        _promptWake.Reset();
        _waitingPrompt = true;
        try
        {
            int idx = WaitHandle.WaitAny(
                new[] { _promptWake.WaitHandle, Cts.Token.WaitHandle },
                timeout == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : timeout);
            if (Cts.IsCancellationRequested) throw new ScriptAbortException();
            return idx == 0;
        }
        finally { _waitingPrompt = false; }
    }

    public void FeedPrompt()
    {
        if (_waitingPrompt) _promptWake.Set();
    }

    // ── stopwatch (genie.timerStart / timerStop / timerElapsed) ──────────────
    // Per-script stopwatch, mirroring the .cmd engine's `timer` command + %timer.
    private DateTime? _timerStart;

    /// <summary>Start (or restart) this script's stopwatch.</summary>
    public void TimerStart() => _timerStart = DateTime.UtcNow;

    /// <summary>Stop and clear the stopwatch (subsequent elapsed reads return 0).</summary>
    public void TimerStop() => _timerStart = null;

    /// <summary>Seconds since the last <see cref="TimerStart"/> (0 if never started).</summary>
    public double TimerElapsed() =>
        _timerStart is { } t ? (DateTime.UtcNow - t).TotalSeconds : 0.0;

    /// <summary>Sleep for <paramref name="duration"/>, interruptible by stop.</summary>
    public void Sleep(TimeSpan duration)
    {
        // WaitOne returns true when the token handle is signalled (stopped).
        if (Cts.Token.WaitHandle.WaitOne(duration)) throw new ScriptAbortException();
    }
}

/// <summary>Thrown by host calls to unwind a stopped JS script. Caught at the
/// top of the script thread; never surfaces to the user as an error.</summary>
internal sealed class ScriptAbortException : Exception { }
