using System.Diagnostics;

namespace Genie.Core.Diagnostics;

/// <summary>
/// The instrumented stages of the live game-feed pipeline. Each value names a
/// place where per-line work happens that, if slow, could stall the feed —
/// exactly the surfaces the performance overlay lets a user watch.
/// </summary>
public enum PipelineStage
{
    Parse,        // DrXmlParser.Feed (+ EmitChunks) — network read thread
    Scripts,      // ScriptEngine.OnGameLine — match/waitfor + EXP/info trackers
    Triggers,     // user-defined trigger regexes
    Highlights,   // highlight tokenize
    Substitutes,  // substitute regex replace
    Gags,         // gag regex match
    Plugins,      // third-party plugin dispatch
    JavaScript,   // .js line-dispatch (waking waitFor/matchWait waiters) — note this
                  // also runs inside the Scripts pass, so its time is counted there too
    Shunts,       // #shunt regex match (public #248) — UI thread, after Gags
}

/// <summary>One stage's rolling stats, as read by the perf overlay.</summary>
public readonly record struct StageSnapshot(
    PipelineStage Stage,
    long          Count,
    double        AvgMs,
    double        PeakMs,
    double        LastMs,
    long          Timeouts);

/// <summary>
/// Thread-safe collector of per-stage timing samples for the live performance
/// overlay. Writers are the network read thread (Parse / Scripts / Triggers /
/// Plugins) and the UI thread (Substitutes / Gags / Highlights); the reader is
/// the overlay's refresh timer. All access is under one lock — a sample is taken
/// at most per game line (a few hundred/sec), so contention is negligible.
///
/// <para>When <see cref="Enabled"/> is false, <see cref="Record"/> / <see cref="Time"/>
/// are a cheap no-op so instrumentation costs nothing while the overlay is
/// hidden. Timeout counts are recorded regardless — a caught regex timeout is a
/// real safety event worth surfacing whether or not the overlay is open.</para>
/// </summary>
public sealed class PipelineMetrics
{
    private const int Window = 128;   // rolling sample window per stage

    private sealed class Bucket
    {
        public readonly double[] Samples = new double[Window];
        public int    Head;
        public int    Filled;
        public long   Count;
        public double Peak;     // all-time worst — catches the rare spike
        public double Last;
        public long   Timeouts;
    }

    private readonly System.Threading.Lock _gate = new();
    private readonly Bucket[] _buckets;

    public PipelineMetrics()
    {
        var n = Enum.GetValues<PipelineStage>().Length;
        _buckets = new Bucket[n];
        for (int i = 0; i < n; i++) _buckets[i] = new Bucket();
    }

    /// <summary>Master switch. False = per-stage timing is not collected.</summary>
    public bool Enabled { get; set; }

    /// <summary>Record one stage timing in milliseconds. No-op when disabled.</summary>
    public void Record(PipelineStage stage, double ms)
    {
        if (!Enabled) return;
        var b = _buckets[(int)stage];
        lock (_gate)
        {
            b.Samples[b.Head] = ms;
            b.Head = (b.Head + 1) % Window;
            if (b.Filled < Window) b.Filled++;
            b.Count++;
            b.Last = ms;
            if (ms > b.Peak) b.Peak = ms;
        }
    }

    /// <summary>Time <paramref name="action"/> into <paramref name="stage"/> when
    /// enabled; otherwise just run it with zero overhead.</summary>
    public void Time(PipelineStage stage, Action action)
    {
        if (!Enabled) { action(); return; }
        var start = Stopwatch.GetTimestamp();
        try { action(); }
        finally { Record(stage, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    /// <summary>Time a function into <paramref name="stage"/> and return its value.</summary>
    public T Time<T>(PipelineStage stage, Func<T> func)
    {
        if (!Enabled) return func();
        var start = Stopwatch.GetTimestamp();
        try { return func(); }
        finally { Record(stage, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    /// <summary>Count a caught regex match-timeout for a stage (always recorded).</summary>
    public void RecordTimeout(PipelineStage stage)
    {
        var b = _buckets[(int)stage];
        lock (_gate) { b.Timeouts++; }
    }

    /// <summary>Clear all counters — e.g. at the start of a new connection.</summary>
    public void Reset()
    {
        lock (_gate)
            foreach (var b in _buckets)
            {
                b.Head = b.Filled = 0;
                b.Count = b.Timeouts = 0;
                b.Peak  = b.Last = 0;
                Array.Clear(b.Samples);
            }
    }

    /// <summary>Snapshot every stage's current stats for display.</summary>
    public StageSnapshot[] Snapshot()
    {
        lock (_gate)
        {
            var result = new StageSnapshot[_buckets.Length];
            for (int i = 0; i < _buckets.Length; i++)
            {
                var b = _buckets[i];
                double sum = 0;
                for (int s = 0; s < b.Filled; s++) sum += b.Samples[s];
                double avg = b.Filled > 0 ? sum / b.Filled : 0;
                result[i] = new StageSnapshot((PipelineStage)i, b.Count, avg, b.Peak, b.Last, b.Timeouts);
            }
            return result;
        }
    }
}
