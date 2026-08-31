using System.Collections.Concurrent;

namespace Genie.Core.Runtime;

/// <summary>
/// The dedicated game thread (#251 / docs/internal/GAME_THREAD_DESIGN.md): a
/// single background thread draining a FIFO work queue, plus a timer service
/// whose callbacks run ON the loop (inherently serialized with posted work).
///
/// This is the only concurrency primitive the game-thread design adds. The
/// entire per-line pipeline — parser feed, state engine, script tick, triggers,
/// plugin dispatch, command processing — runs as loop items, so line N is fully
/// processed before line N+1 starts (exactly the old dispatcher-job semantics,
/// minus the UI thread). FIFO order is the ordering guarantee the whole design
/// leans on.
///
/// <para><b>Deadlock policy (hard rule):</b> all cross-thread communication is
/// one-way <see cref="Post"/>. There is no synchronous Send/Invoke here on
/// purpose — with only one-way edges the UI↔game-thread graph cannot deadlock.</para>
///
/// <para><b>Watchdog:</b> a cheap thread-pool timer checks once a second whether
/// a single work item has been running longer than <see cref="StallThreshold"/>;
/// if so it raises <see cref="Stalled"/> (once per item) so the host can tell
/// the user the game pipeline is wedged — the UI itself stays alive.</para>
///
/// <para>Lifetime: one loop per <c>GenieCore</c>, created in its constructor and
/// running for the core's whole life (spanning connects/reconnects, like the old
/// App heartbeat). Disposed in <c>GenieCore.DisposeAsync</c>.</para>
/// </summary>
public sealed class GameLoop : IDisposable
{
    /// <summary>How long one work item may run before the watchdog reports a
    /// stall. Phase 0's regex timeouts / JS wall-clock caps shrink the wedge
    /// classes; this surfaces whatever remains.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly System.Threading.Timer _watchdog;

    // Timer service: a min-heap of (due, seq) → action, guarded by _timerGate.
    // Serviced by the loop's own TryTake timeout — NOT System.Threading.Timer —
    // so timer callbacks are inherently serialized with posted work.
    private readonly object _timerGate = new();
    private readonly PriorityQueue<TimerEntry, (long DueTicks, long Seq)> _timers = new();
    private long _timerSeq;

    // Watchdog state: start timestamp of the item currently executing
    // (0 = idle), and whether the current item has already been reported.
    private long _itemStartedTicks;
    private int _stallReported;

    private volatile bool _shuttingDown;

    /// <summary>Raised (on a thread-pool thread) when one work item has been
    /// running longer than <see cref="StallThreshold"/> — at most once per item.
    /// Carries the stall duration so far.</summary>
    public event Action<TimeSpan>? Stalled;

    /// <summary>Sink for exceptions escaping a work item (the loop itself never
    /// dies from one). Wired by the host to its diagnostics echo.</summary>
    public Action<Exception>? ItemFailed { get; set; }

    public GameLoop(string threadName = "genie-game")
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name         = threadName,
        };
        _thread.Start();
        _watchdog = new System.Threading.Timer(WatchdogCheck, null,
            dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
    }

    /// <summary>True when the calling thread IS the game loop thread. Work
    /// already on the loop calls engines directly (never self-posts), preserving
    /// synchronous re-entrancy semantics (#parse, triggeroninput).</summary>
    public bool IsOnLoop => Thread.CurrentThread == _thread;

    /// <summary>Queue <paramref name="work"/> to run on the loop, FIFO. Never
    /// blocks the caller. Work posted after shutdown begins is silently dropped.</summary>
    public void Post(Action work)
    {
        if (_shuttingDown) return;
        try { _queue.Add(work); }
        catch (InvalidOperationException) { /* CompleteAdding raced — dropping is shutdown semantics */ }
    }

    /// <summary>Schedule <paramref name="work"/> to run on the loop after
    /// <paramref name="delay"/>. Dispose the returned handle to cancel.</summary>
    public IDisposable PostDelayed(TimeSpan delay, Action work)
    {
        var entry = new TimerEntry(work, repeating: false, interval: TimeSpan.Zero);
        Schedule(entry, delay);
        return entry;
    }

    /// <summary>Run <paramref name="work"/> on the loop every
    /// <paramref name="interval"/> (first run one interval from now). Dispose the
    /// returned handle to stop.</summary>
    public IDisposable PostRepeating(TimeSpan interval, Action work)
    {
        var entry = new TimerEntry(work, repeating: true, interval: interval);
        Schedule(entry, interval);
        return entry;
    }

    private void Schedule(TimerEntry entry, TimeSpan delay)
    {
        if (_shuttingDown) return;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var due = DateTime.UtcNow.Ticks + delay.Ticks;
        lock (_timerGate)
            _timers.Enqueue(entry, (due, _timerSeq++));
        // Wake the loop (it may be parked on an infinite/long TryTake computed
        // before this timer existed) so it recomputes its wait.
        Post(static () => { });
    }

    /// <summary>Drain the queue (bounded by <paramref name="drainTimeout"/>) and
    /// join the thread. Pending timers are dropped. Idempotent.</summary>
    public void Shutdown(TimeSpan drainTimeout)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _watchdog.Dispose();
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        if (!_thread.Join(drainTimeout))
        {
            // The loop is wedged in a work item. The thread is background, so it
            // dies with the process; nothing more to safely do here.
        }
    }

    public void Dispose() => Shutdown(TimeSpan.FromSeconds(2));

    // ── loop body ────────────────────────────────────────────────────────────

    private void Run()
    {
        while (true)
        {
            // 1. Run every due timer (their actions execute on this thread, so
            //    they serialize with posted work by construction).
            var wait = RunDueTimersAndGetWait();

            // 2. Park for the next item, but no longer than the next timer due.
            Action? item = null;
            try
            {
                if (!_queue.TryTake(out item, wait))
                {
                    if (_queue.IsCompleted) return;
                    continue;   // timer came due (or spurious) — loop re-checks
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }   // completed+empty

            if (item is null)
            {
                if (_queue.IsCompleted) return;
                continue;
            }

            Execute(item);
        }
    }

    /// <summary>Run all due timers; return how long the queue take may park
    /// (until the next timer's due time, or infinite when none pending).</summary>
    private int RunDueTimersAndGetWait()
    {
        while (true)
        {
            TimerEntry? dueEntry = null;
            lock (_timerGate)
            {
                if (_timers.TryPeek(out var entry, out var key))
                {
                    var now = DateTime.UtcNow.Ticks;
                    if (key.DueTicks <= now)
                    {
                        _timers.Dequeue();
                        dueEntry = entry;
                    }
                    else
                    {
                        var remaining = TimeSpan.FromTicks(key.DueTicks - now);
                        // TryTake takes an int ms; clamp long waits, the loop
                        // just re-checks after.
                        return (int)Math.Clamp(remaining.TotalMilliseconds + 1, 1, 60_000);
                    }
                }
                else
                {
                    return Timeout.Infinite;
                }
            }

            if (dueEntry is { Cancelled: false })
            {
                Execute(dueEntry.Work);
                if (dueEntry.Repeating && !dueEntry.Cancelled && !_shuttingDown)
                    Schedule(dueEntry, dueEntry.Interval);
            }
        }
    }

    private void Execute(Action item)
    {
        Volatile.Write(ref _itemStartedTicks, Environment.TickCount64);
        Volatile.Write(ref _stallReported, 0);
        try
        {
            item();
        }
        catch (Exception ex)
        {
            // A work-item exception must never kill the loop — report and go on.
            try { ItemFailed?.Invoke(ex); } catch { /* diagnostics must not recurse */ }
        }
        finally
        {
            Volatile.Write(ref _itemStartedTicks, 0);
        }
    }

    private void WatchdogCheck(object? _)
    {
        var started = Volatile.Read(ref _itemStartedTicks);
        if (started == 0) return;   // idle
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        if (elapsed < StallThreshold) return;
        if (Interlocked.Exchange(ref _stallReported, 1) == 1) return;   // once per item
        try { Stalled?.Invoke(elapsed); } catch { /* observer must not kill the timer */ }
    }

    /// <summary>A scheduled (possibly repeating) timer. Dispose = cancel; a
    /// cancelled entry is skipped when it comes due (cheap lazy removal — no
    /// heap surgery needed).</summary>
    private sealed class TimerEntry : IDisposable
    {
        public readonly Action   Work;
        public readonly bool     Repeating;
        public readonly TimeSpan Interval;
        public volatile bool     Cancelled;

        public TimerEntry(Action work, bool repeating, TimeSpan interval)
        {
            Work      = work;
            Repeating = repeating;
            Interval  = interval;
        }

        public void Dispose() => Cancelled = true;
    }
}
