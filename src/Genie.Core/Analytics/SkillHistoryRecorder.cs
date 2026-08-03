using Genie.Core.Config;
using Genie.Core.Events;
using Genie.Core.Extensions.Builtin;

namespace Genie.Core.Analytics;

/// <summary>
/// Session-scoped orchestrator for skill-history recording. Subscribes to
/// <see cref="ExperienceExtension"/>'s change events (already deduplicated —
/// the extension only fires when a skill's rank/percent/mindstate actually
/// changed) and to the connection state stream for session boundaries.
///
/// <para>Threading: skill events arrive on the connection read-loop thread and
/// only mutate an in-memory pending dictionary under a lock — no IO on the
/// parser path. A timer flushes the pending deltas every
/// <c>#config analyticsinterval</c> seconds; disconnect forces a final flush
/// plus a <see cref="SessionRecord"/> summary. All writes are latched-no-op on
/// IO failure (see <see cref="SkillHistoryWriter"/>) so analytics can never
/// take the game session down.</para>
///
/// <para>Gating: <c>#config analytics off</c> stops recording live (reads the
/// config per event); replay sessions record only when
/// <c>#config analyticsreplay on</c>, and their rows carry
/// <c>"replay":true</c>. Elapsed time uses <see cref="Environment.TickCount64"/>
/// so wall-clock changes can't corrupt XP/hour.</para>
/// </summary>
public sealed class SkillHistoryRecorder : IDisposable
{
    private readonly GenieConfig _config;
    private readonly ExperienceExtension? _exp;
    private readonly IDisposable? _connSub;
    private readonly SkillHistoryWriter _writer;
    private readonly string _character;
    private readonly string _account;
    private readonly bool _isReplay;
    private readonly Action<string>? _log;
    private readonly System.Threading.Timer _timer;

    private readonly object _lock = new();
    private string? _sid;
    private DateTime _startUtc;
    private long _startTick;
    private int _tdpStart;
    private int _tdpLast;
    private readonly Dictionary<string, int[]> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Rank, int Percent)> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Rank, int Percent, int Mind)> _last = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>A delta snapshot just hit disk — the live dashboard refreshes
    /// from this. Raised off the UI thread; marshal before touching controls.</summary>
    public event Action<SnapshotRecord>? SnapshotFlushed;

    /// <summary>Character slug (folder name) this recorder writes under.</summary>
    public string CharacterSlug { get; }

    /// <summary>The per-character folder rows are written to.</summary>
    public string Directory => _writer.Directory;

    public SkillHistoryRecorder(
        GenieConfig config,
        ExperienceExtension? experience,
        IObservable<ConnectionEvent>? connectionState,
        string characterName,
        string accountName,
        bool isReplay,
        Action<string>? log = null)
    {
        _config    = config;
        _exp       = experience;
        _character = characterName;
        _account   = accountName;
        _isReplay  = isReplay;
        _log       = log;

        CharacterSlug = GenieConfig.CharacterSlug(characterName, accountName);
        _writer = new SkillHistoryWriter(Path.Combine(config.AnalyticsDir, CharacterSlug), log);
        _timer  = new System.Threading.Timer(_ => OnTimer(), null,
                      System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        if (_exp is not null)
        {
            _exp.SkillUpdated += OnSkillUpdated;
            _exp.TdpUpdated   += OnTdpUpdated;
        }
        _connSub = connectionState?.Subscribe(OnConnectionEvent);

        // Startup housekeeping off the connect path: synthesize summaries for
        // sessions that died without a disconnect row, then fold+prune shards
        // past the retention window. Both are cheap when there's nothing to do.
        // The cutoff (construction time, floored to the second to match the
        // sid-embedded precision) keeps the scan from racing THIS run's own
        // session — its snapshots can hit disk before its session row does.
        DateTime createdUtc = DateTime.UtcNow;
        DateTime recoveryCutoff = createdUtc.AddTicks(-(createdUtc.Ticks % TimeSpan.TicksPerSecond));
        if (Enabled())
            _ = Task.Run(() =>
            {
                try
                {
                    int recovered = _writer.RecoverOrphanSessions(_character, _account, recoveryCutoff);
                    if (recovered > 0)
                        _log?.Invoke($"[analytics] recovered {recovered} interrupted session(s).");
                    SkillHistoryRollup.ApplyRetention(
                        _writer.Directory, _config.AnalyticsRetentionDays, DateTime.UtcNow, _log);
                }
                catch (Exception ex) { _log?.Invoke($"[analytics] startup housekeeping failed ({ex.Message})."); }
            });
    }

    private bool Enabled() =>
        _config.Analytics && (!_isReplay || _config.AnalyticsReplay);

    private void OnConnectionEvent(ConnectionEvent e)
    {
        try
        {
            switch (e.Kind)
            {
                case ConnectionEventKind.Connected: BeginSession(); break;
                case ConnectionEventKind.Disconnected:
                case ConnectionEventKind.Error: EndSession(); break;
            }
        }
        catch (Exception ex) { _log?.Invoke($"[analytics] session boundary failed ({ex.Message})."); }
    }

    internal void BeginSession()
    {
        if (!Enabled()) return;
        lock (_lock) { BeginSessionLocked(); }
    }

    internal void OnSkillUpdated(string name, int rank, int percent, int mindstate)
    {
        if (!Enabled()) return;
        lock (_lock)
        {
            // The login `exp` dump can land before (or without) a Connected
            // event in odd orderings — open the session lazily so no data is
            // dropped on the floor.
            if (_sid is null) BeginSessionLocked();
            _pending[name]  = new[] { rank, percent, mindstate };
            _last[name]     = (rank, percent, mindstate);
            _baseline.TryAdd(name, (rank, percent));
        }
    }

    internal void OnTdpUpdated(int tdp)
    {
        if (!Enabled()) return;
        lock (_lock)
        {
            if (_sid is null) return;       // TDP alone doesn't open a session
            if (_tdpStart == 0) _tdpStart = tdp;
            _tdpLast = tdp;
        }
    }

    private void BeginSessionLocked()
    {
        if (_sid is not null) return;       // reconnect chatter — session already open
        _startUtc  = DateTime.UtcNow;
        _startTick = Environment.TickCount64;
        _sid = $"{_startUtc:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}"[..21];
        _tdpStart = 0; _tdpLast = 0;
        _pending.Clear(); _baseline.Clear(); _last.Clear();
        int intervalMs = IntervalMs();
        // Timer.Change is non-reentrant-safe here: the callback takes _lock,
        // Change itself never invokes it synchronously.
        _timer.Change(intervalMs, intervalMs);
    }

    private int IntervalMs() => Math.Clamp(_config.AnalyticsInterval, 10, 600) * 1000;

    private void OnTimer()
    {
        try { if (Enabled()) FlushNow(); }
        catch (Exception ex) { _log?.Invoke($"[analytics] flush failed ({ex.Message})."); }
    }

    /// <summary>Write the pending deltas as one snapshot row (no-op when
    /// nothing changed). Returns the written row, or null.</summary>
    internal SnapshotRecord? FlushNow()
    {
        SnapshotRecord snap;
        lock (_lock)
        {
            if (_sid is null || _pending.Count == 0) return null;
            snap = new SnapshotRecord
            {
                SessionId = _sid,
                AtUtc     = DateTime.UtcNow,
                Elapsed   = (Environment.TickCount64 - _startTick) / 1000,
                Replay    = _isReplay,
                Tdp       = _tdpLast,
                Skills    = new Dictionary<string, int[]>(_pending, StringComparer.OrdinalIgnoreCase),
            };
            _pending.Clear();
        }
        _writer.WriteSnapshot(snap);
        SnapshotFlushed?.Invoke(snap);
        return snap;
    }

    /// <summary>Close the session: final delta flush plus the summary row.
    /// Idempotent — safe to call from both Disconnected and Dispose.</summary>
    internal void EndSession()
    {
        _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        FlushNow();

        SessionRecord rec;
        lock (_lock)
        {
            if (_sid is null) return;
            rec = BuildSessionLocked(endUtc: DateTime.UtcNow);
            _sid = null;
            _pending.Clear(); _baseline.Clear(); _last.Clear();
        }
        // Only write a summary when the session actually saw skill data —
        // a connect that never trained anything isn't history worth keeping.
        if (rec.Skills.Count > 0 || rec.TdpEnd > 0)
            _writer.WriteSession(rec);
    }

    /// <summary>Summary of the in-flight session so far (null when idle) —
    /// feeds the dashboard's "This Session" view without touching disk.</summary>
    public SessionRecord? LiveSessionSnapshot()
    {
        lock (_lock)
        {
            if (_sid is null) return null;
            return BuildSessionLocked(endUtc: DateTime.UtcNow);
        }
    }

    private SessionRecord BuildSessionLocked(DateTime endUtc)
    {
        var rec = new SessionRecord
        {
            Id        = _sid!,
            Character = _character,
            Account   = _account,
            StartUtc  = _startUtc,
            EndUtc    = endUtc,
            Seconds   = (Environment.TickCount64 - _startTick) / 1000,
            Replay    = _isReplay,
            TdpStart  = _tdpStart,
            TdpEnd    = _tdpLast,
        };
        foreach (var (skill, last) in _last)
        {
            var (r0, p0) = _baseline.TryGetValue(skill, out var b) ? b : (last.Rank, last.Percent);
            rec.Skills[skill] = new SkillSpan
            {
                RankStart = r0, PercentStart = p0,
                RankEnd = last.Rank, PercentEnd = last.Percent,
            };
        }
        return rec;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { EndSession(); } catch { /* never throw from teardown */ }
        if (_exp is not null)
        {
            _exp.SkillUpdated -= OnSkillUpdated;
            _exp.TdpUpdated   -= OnTdpUpdated;
        }
        _connSub?.Dispose();
        _timer.Dispose();
    }
}
