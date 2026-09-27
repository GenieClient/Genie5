using System.Collections.Concurrent;
using System.Text;

namespace Genie.Core.WindowLogging;

/// <summary>
/// Per-window log writer (public #270, Genie 4 Window Logger parity): taps the
/// game's stream text and appends each line whose stream has an enabled
/// <see cref="WindowLogRule"/> to that rule's file.
///
/// <para><b>Never blocks or breaks the pipeline.</b> <see cref="Observe"/> runs
/// on the game thread and only enqueues; every file operation — template
/// expansion, opening, writing, flushing — happens in <see cref="Flush"/>, which
/// a timer runs every <see cref="FlushInterval"/> off the game thread. Nothing
/// here throws to its caller: an unwritable path is reported once through
/// <see cref="Warning"/> and its lines are dropped until a retry succeeds.</para>
///
/// <para><b>Shared files.</b> Writers are keyed by the RESOLVED path, not by the
/// rule, and the queue is a single FIFO, so two rules naming the same file
/// (Genie 4's talk + whispers conversation log) interleave in exactly the order
/// the lines arrived.</para>
///
/// <para><b>Rollover.</b> The path is expanded per line from the line's own
/// arrival time, so a <c>{yyyy}</c> (or <c>{MM}</c>, <c>{dd}</c>) file moves to
/// the next one at the boundary. A writer left idle for
/// <see cref="IdleClose"/> is closed, which retires last year's file.</para>
/// </summary>
public sealed class WindowLogSink : IDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan IdleClose     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfter   = TimeSpan.FromSeconds(30);

    /// <summary>Lines held while the disk is slower than the game; beyond this
    /// the newest are dropped (and counted) rather than growing without bound.</summary>
    public const int MaxPending = 50_000;

    private readonly record struct Pending(WindowLogRule Rule, string Stream, string Text,
                                           string Character, string Game, DateTime Time);

    private sealed class OpenFile(StreamWriter writer, DateTime now)
    {
        public StreamWriter Writer   { get; } = writer;
        public DateTime     LastUsed { get; set; } = now;
        public bool         Dirty    { get; set; }
    }

    private readonly WindowLogRules _rules;
    private readonly Func<string> _logDir;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentQueue<Pending> _queue = new();
    private int _pending;
    private int _dropped;
    private readonly object _drainGate = new();
    private readonly Dictionary<string, OpenFile> _files =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failedUntil =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _disposed;

    /// <param name="rules">The rule set to honour (read per line, so edits apply live).</param>
    /// <param name="logDir">The Logs folder, read at write time so a <c>#config logdir</c>
    /// change applies to the next file opened.</param>
    /// <param name="clock">Line timestamps; injectable for rollover tests.</param>
    /// <param name="startTimer">False for tests that drive <see cref="Flush"/> by hand.</param>
    public WindowLogSink(WindowLogRules rules, Func<string> logDir,
                         Func<DateTime>? clock = null, bool startTimer = true)
    {
        _rules  = rules;
        _logDir = logDir;
        _clock  = clock ?? (() => DateTime.Now);
        if (startTimer)
            _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public WindowLogRules Rules => _rules;

    /// <summary>A one-line user-facing problem (unwritable path, bad template).
    /// Raised from the flush thread, once per distinct problem.</summary>
    public event Action<string>? Warning;

    /// <summary>Every stream id that has carried text this session — the
    /// dynamic list <c>#windowlog streams</c> shows, instead of a fixed set.</summary>
    public IReadOnlyList<string> SeenStreams =>
        _seen.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Files currently open for writing.</summary>
    public IReadOnlyList<string> OpenFiles
    {
        get { lock (_drainGate) return _files.Keys.OrderBy(k => k).ToList(); }
    }

    /// <summary>Lines dropped because the queue was full.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Record that <paramref name="stream"/> carried text this session.</summary>
    public void NoteStream(string stream)
    {
        if (!string.IsNullOrEmpty(stream)) _seen.TryAdd(stream, 0);
    }

    /// <summary>True when an enabled rule would log <paramref name="stream"/> —
    /// lets the caller skip preparing a line nobody will write.</summary>
    public bool Covers(string stream) =>
        !_disposed && _rules.Snapshot.TryGetValue(stream, out var r) && r.Enabled;

    /// <summary>
    /// Offer one displayed line. Game thread; O(1) and allocation-light when
    /// no rule covers the stream. Never throws.
    /// </summary>
    public void Observe(string? stream, string? text, string? characterName, string? gameName)
    {
        try
        {
            var s = string.IsNullOrEmpty(stream) ? "main" : stream;
            _seen.TryAdd(s, 0);
            if (_disposed) return;
            if (!_rules.Snapshot.TryGetValue(s, out var rule) || !rule.Enabled) return;

            if (Interlocked.Increment(ref _pending) > MaxPending)
            {
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _dropped);
                return;
            }
            _queue.Enqueue(new Pending(rule, s, text ?? string.Empty,
                                       characterName ?? string.Empty, gameName ?? string.Empty,
                                       _clock()));
        }
        catch
        {
            // A logging sink must never take the pipeline down.
        }
    }

    /// <summary>
    /// Write everything queued, then flush the files it touched. Serialized, so
    /// the timer and an explicit call cannot interleave. Never throws.
    /// </summary>
    public void Flush()
    {
        lock (_drainGate)
        {
            try { DrainLocked(); }
            catch (Exception ex) { WarnOnce("flush", $"[windowlog] write failed: {ex.Message}"); }
        }
    }

    private void DrainLocked()
    {
        var now = _clock();
        var logDir = _logDir();
        var line = new StringBuilder(256);

        while (_queue.TryDequeue(out var p))
        {
            Interlocked.Decrement(ref _pending);
            var ctx = new WindowLogContext(p.Character, p.Game, p.Stream, p.Time);
            var path = WindowLogPath.Resolve(logDir, WindowLogPath.Expand(p.Rule.File, ctx), out var error);
            if (path is null)
            {
                WarnOnce("rule:" + p.Rule.Stream + "|" + p.Rule.File,
                    $"[windowlog] {p.Rule.Stream}: '{p.Rule.File}' is not usable ({error}). Lines for it are not being logged.");
                continue;
            }

            var file = GetOrOpen(path, now);
            if (file is null) continue;

            line.Clear();
            var stamp = WindowLogPath.FormatTimestamp(p.Rule.TimestampFormat, p.Time);
            if (stamp is not null) line.Append('[').Append(stamp).Append("] ");
            line.Append(p.Text);
            try
            {
                file.Writer.WriteLine(line.ToString());
                file.Dirty = true;
                file.LastUsed = now;
            }
            catch (Exception ex)
            {
                WarnOnce("write:" + path, $"[windowlog] could not write '{path}': {ex.Message}");
                CloseLocked(path);
                _failedUntil[path] = now + RetryAfter;
            }
        }

        foreach (var (path, f) in _files.ToList())
        {
            if (f.Dirty)
            {
                try { f.Writer.Flush(); f.Dirty = false; }
                catch (Exception ex)
                {
                    WarnOnce("write:" + path, $"[windowlog] could not write '{path}': {ex.Message}");
                    CloseLocked(path);
                    _failedUntil[path] = now + RetryAfter;
                }
            }
            else if (now - f.LastUsed >= IdleClose)
            {
                CloseLocked(path);   // e.g. last year's file after a {yyyy} rollover
            }
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            Warning?.Invoke($"[windowlog] the disk fell behind; {dropped} line(s) were not logged.");
    }

    private OpenFile? GetOrOpen(string path, DateTime now)
    {
        if (_files.TryGetValue(path, out var open)) return open;
        if (_failedUntil.TryGetValue(path, out var until) && now < until) return null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // FileShare.Read: the player can tail or grep the file while it is open.
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var f = new OpenFile(new StreamWriter(fs, new UTF8Encoding(false)), now);
            _files[path] = f;
            _failedUntil.Remove(path);
            _warned.Remove("open:" + path);
            return f;
        }
        catch (Exception ex)
        {
            WarnOnce("open:" + path, $"[windowlog] could not open '{path}': {ex.Message}. Retrying in {RetryAfter.TotalSeconds:0}s.");
            _failedUntil[path] = now + RetryAfter;
            return null;
        }
    }

    private void CloseLocked(string path)
    {
        if (!_files.Remove(path, out var f)) return;
        try { f.Writer.Dispose(); } catch { /* already reported or harmless */ }
    }

    /// <summary>Write what is queued and close every file — the disconnect path.
    /// Rules stay; the next line reopens what it needs.</summary>
    public void CloseAll()
    {
        lock (_drainGate)
        {
            try { DrainLocked(); } catch { /* best effort on the way out */ }
            foreach (var path in _files.Keys.ToList()) CloseLocked(path);
            _failedUntil.Clear();
            _warned.Clear();
        }
    }

    private void WarnOnce(string key, string message)
    {
        if (!_warned.Add(key)) return;
        try { Warning?.Invoke(message); } catch { /* a bad listener must not stop the flush */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer?.Dispose();
        _timer = null;
        CloseAll();
        _disposed = true;
    }
}
