using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Genie.Core;
using Genie.Core.Analytics;
using Genie.App.Controls;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the Analytics dock panel — charts over the local skill-history store
/// (<see cref="SkillHistoryRecorder"/> writes it; this VM only reads).
///
/// Three tabs:
/// <list type="bullet">
/// <item><b>This Session</b> — live XP/hour headline, per-skill gain bars, and
///   a rank-over-time line for a picked skill; refreshed on each snapshot
///   flush.</item>
/// <item><b>History</b> — long-horizon gain curve per skill from the daily
///   rollups plus raw snapshots still inside the retention window.</item>
/// <item><b>Sessions</b> — session summary rows with a compare-2-or-3
///   normalized-gain chart and a show-replay filter.</item>
/// </list>
///
/// Produces renderer-neutral <see cref="ChartSeries"/> data — the chart layer
/// (ChartCanvas today) is swappable without touching this class. Disk reads
/// are lazy (nothing loads until the panel asks) and all store reads are
/// tolerant of torn/foreign lines.
/// </summary>
public class AnalyticsViewModel : ReactiveObject
{
    private GenieCore? _core;
    private SkillHistoryRecorder? _recorder;
    private IDisposable? _connSub;
    private readonly List<SnapshotRecord> _liveSnaps = new();
    private readonly System.Threading.Lock _liveLock = new();

    private const int MaxBarSkills = 12;

    // ── Shared ────────────────────────────────────────────────────────────

    /// <summary>Character folders available in the store (slug = Char-Acct).</summary>
    public ObservableCollection<string> Characters { get; } = new();

    [Reactive] public string? SelectedCharacter { get; set; }

    /// <summary>Bumps to force ChartCanvas repaints after in-place updates.</summary>
    [Reactive] public int RenderTick { get; private set; }

    [Reactive] public string StatusText { get; private set; } =
        "(no history yet — analytics records as you play)";

    // ── This Session tab ──────────────────────────────────────────────────

    [Reactive] public string SessionHeadline { get; private set; } = "";
    [Reactive] public IReadOnlyList<ChartSeries>? SessionBars { get; private set; }
    [Reactive] public IReadOnlyList<ChartSeries>? SessionLine { get; private set; }
    public ObservableCollection<string> SessionSkills { get; } = new();
    [Reactive] public string? SelectedSessionSkill { get; set; }

    // ── History tab ───────────────────────────────────────────────────────

    public ObservableCollection<string> HistorySkills { get; } = new();
    [Reactive] public string? SelectedHistorySkill { get; set; }
    /// <summary>Window in days; 0 = all history.</summary>
    [Reactive] public int HistoryDays { get; set; } = 30;
    [Reactive] public IReadOnlyList<ChartSeries>? HistorySeries { get; private set; }

    // ── Sessions tab ──────────────────────────────────────────────────────

    public ObservableCollection<SessionRow> Sessions { get; } = new();
    [Reactive] public bool ShowReplay { get; set; }
    [Reactive] public IReadOnlyList<ChartSeries>? CompareSeries { get; private set; }

    public sealed class SessionRow : ReactiveObject
    {
        public SessionRecord Record { get; }
        public SessionRow(SessionRecord r) => Record = r;

        public string Start      => Record.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string Duration   => FormatDuration(Record.Seconds);
        public string TotalGain  => FormatGain(Record.TotalGain);
        public string GainPerHour =>
            SkillHistoryJson.RanksPerHour(Record.TotalGain, Record.Seconds).ToString("0.00");
        public string TopSkill   =>
            Record.Skills.OrderByDescending(kv => kv.Value.Gain).FirstOrDefault().Key ?? "";
        public string Flags      =>
            (Record.Replay ? "replay " : "") + (Record.Recovered ? "recovered" : "");

        /// <summary>Checked = included in the comparison chart (max 3).</summary>
        [Reactive] public bool Compare { get; set; }
    }

    /// <summary>Range picker index for the History tab: 7 / 30 / 90 days / All.</summary>
    [Reactive] public int HistoryRangeIndex { get; set; } = 1;
    private static readonly int[] RangeDays = { 7, 30, 90, 0 };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    // ── Inline settings — write #config values quietly (the Experience
    //    density-slider idiom) so panel, command line, and settings.cfg stay
    //    in sync. Seeded from config in Attach.

    private bool _recordingEnabled = true;
    public bool RecordingEnabled
    {
        get => _recordingEnabled;
        set
        {
            if (_recordingEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _recordingEnabled, value);
            if (_core is { } c)
            {
                c.Config.SetSetting("analytics", value.ToString(), showException: false);
                c.Config.Save();
            }
        }
    }

    private int _retentionDays = 90;
    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            if (_retentionDays == value) return;
            this.RaiseAndSetIfChanged(ref _retentionDays, value);
            if (_core is { } c)
            {
                c.Config.SetSetting("analyticsretentiondays", value.ToString(), showException: false);
                c.Config.Save();
            }
        }
    }

    public AnalyticsViewModel()
    {
        RefreshCommand = ReactiveCommand.Create(RefreshAll);

        // Re-derive dependent views when pickers change.
        this.WhenAnyValue(x => x.SelectedSessionSkill).Subscribe(_ => RebuildSessionLine());
        this.WhenAnyValue(x => x.HistoryRangeIndex)
            .Subscribe(i => HistoryDays = RangeDays[Math.Clamp(i, 0, RangeDays.Length - 1)]);
        this.WhenAnyValue(x => x.SelectedHistorySkill, x => x.HistoryDays, x => x.SelectedCharacter)
            .Subscribe(_ => RebuildHistory());
        this.WhenAnyValue(x => x.ShowReplay, x => x.SelectedCharacter)
            .Subscribe(_ => ReloadSessions());
    }

    /// <summary>One-time wiring to the persistent core. The recorder itself is
    /// per-connection, so we re-grab it on every Connected event.</summary>
    public void Attach(GenieCore core)
    {
        _core = core;
        _connSub = core.ConnectionState.Subscribe(e =>
        {
            if (e.Kind == Genie.Core.Events.ConnectionEventKind.Connected)
                Avalonia.Threading.Dispatcher.UIThread.Post(HookRecorder);
            else if (e.Kind is Genie.Core.Events.ConnectionEventKind.Disconnected
                            or Genie.Core.Events.ConnectionEventKind.Error)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { ReloadSessions(); RefreshSessionTab(); });
        });
        HookRecorder();
        // Seed the inline settings from config without echoing back.
        _recordingEnabled = core.Config.Analytics;
        this.RaisePropertyChanged(nameof(RecordingEnabled));
        _retentionDays = core.Config.AnalyticsRetentionDays;
        this.RaisePropertyChanged(nameof(RetentionDays));
        RefreshAll();
    }

    private SkillHistoryStore? Store =>
        _core is null ? null : new SkillHistoryStore(_core.Config.AnalyticsDir);

    private void HookRecorder()
    {
        var rec = _core?.SkillHistory;
        if (ReferenceEquals(rec, _recorder)) return;

        if (_recorder is not null) _recorder.SnapshotFlushed -= OnSnapshotFlushed;
        _recorder = rec;
        lock (_liveLock) _liveSnaps.Clear();

        if (rec is not null)
        {
            rec.SnapshotFlushed += OnSnapshotFlushed;
            // The recorder's character becomes the default picker selection.
            if (!Characters.Contains(rec.CharacterSlug)) Characters.Add(rec.CharacterSlug);
            SelectedCharacter ??= rec.CharacterSlug;
            // Panel may open mid-session — seed the live buffer from disk.
            if (rec.LiveSessionSnapshot() is { } live && Store is { } store)
            {
                var snaps = store.LoadSnapshots(rec.CharacterSlug, live.Id);
                lock (_liveLock) { _liveSnaps.Clear(); _liveSnaps.AddRange(snaps); }
            }
        }
        RefreshSessionTab();
    }

    private void OnSnapshotFlushed(SnapshotRecord snap)
    {
        lock (_liveLock) _liveSnaps.Add(snap);
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshSessionTab);
    }

    /// <summary>Full lazy (re)load — called when the panel is shown or the
    /// user hits Refresh.</summary>
    public void RefreshAll()
    {
        ReloadCharacters();
        RefreshSessionTab();
        ReloadSessions();
        RebuildHistory();
    }

    private void ReloadCharacters()
    {
        if (Store is not { } store) return;
        foreach (var slug in store.ListCharacters())
            if (!Characters.Contains(slug))
                Characters.Add(slug);
        SelectedCharacter ??= _recorder?.CharacterSlug ?? Characters.FirstOrDefault();
    }

    // ── This Session ─────────────────────────────────────────────────────

    private void RefreshSessionTab()
    {
        var live = _recorder?.LiveSessionSnapshot();
        if (live is null || live.Skills.Count == 0)
        {
            SessionHeadline = "No skill data this session yet — train a skill, or type 'exp'.";
            SessionBars = null;
            RebuildSessionLine();
            return;
        }

        double totalGain = live.TotalGain;
        double perHour   = SkillHistoryJson.RanksPerHour(totalGain, live.Seconds);
        SessionHeadline =
            $"Session {FormatDuration(live.Seconds)}   " +
            $"gained {FormatGain(totalGain)} ranks   " +
            $"({perHour:0.00}/hour)";

        var gains = live.Skills
            .Select(kv => (Skill: kv.Key, Gain: kv.Value.Gain))
            .Where(g => g.Gain > 0)
            .OrderByDescending(g => g.Gain)
            .ToList();

        var bars = new ChartSeries { Name = "Ranks gained", Kind = ChartSeriesKind.Bar };
        foreach (var (skill, gain) in gains.Take(MaxBarSkills))
            bars.Points.Add(new ChartPoint(0, Math.Round(gain, 2), skill));
        SessionBars = bars.Points.Count > 0 ? new[] { bars } : null;

        // Keep the skill picker in sync (add-only; the selection sticks).
        foreach (var (skill, _) in gains)
            if (!SessionSkills.Contains(skill)) SessionSkills.Add(skill);
        SelectedSessionSkill ??= gains.FirstOrDefault().Skill;

        RebuildSessionLine();
        RenderTick++;
        StatusText = "";
    }

    private void RebuildSessionLine()
    {
        string? skill = SelectedSessionSkill;
        if (skill is null) { SessionLine = null; return; }

        List<SnapshotRecord> snaps;
        lock (_liveLock) snaps = _liveSnaps.ToList();

        var line = new ChartSeries { Name = skill, Kind = ChartSeriesKind.Line };
        foreach (var s in snaps)
            if (s.Skills.TryGetValue(skill, out var v) && v is { Length: >= 2 })
                line.Points.Add(new ChartPoint(s.Elapsed, v[0] + v[1] / 100.0));
        SessionLine = line.Points.Count > 1 ? new[] { line } : null;
        RenderTick++;
    }

    // ── History ──────────────────────────────────────────────────────────

    private void RebuildHistory()
    {
        if (Store is not { } store || SelectedCharacter is not { } slug)
        { HistorySeries = null; return; }

        DateTime? from = HistoryDays > 0 ? DateTime.UtcNow.AddDays(-HistoryDays) : null;

        // Skill picker: union of skills across sessions (cheap single file).
        var sessions = store.LoadSessions(slug, from);
        foreach (var s in sessions)
        foreach (var skill in s.Skills.Keys)
            if (!HistorySkills.Contains(skill)) HistorySkills.Add(skill);
        SelectedHistorySkill ??= HistorySkills.FirstOrDefault();
        if (SelectedHistorySkill is not { } pick) { HistorySeries = null; return; }

        // Day → end-of-day absolute rank, from rollups + raw snapshots (the
        // snapshot value wins when both cover a day — it's the fresher source).
        var byDay = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var d in store.LoadDaily(slug, from))
            if (d.Skills.TryGetValue(pick, out var ds))
                byDay[d.Day] = ds.Rank + ds.Percent / 100.0;
        foreach (var snap in store.LoadSnapshots(slug, from ?? DateTime.MinValue, DateTime.UtcNow))
            if (snap.Skills.TryGetValue(pick, out var v) && v is { Length: >= 2 })
                byDay[snap.AtUtc.ToString("yyyy-MM-dd")] = v[0] + v[1] / 100.0;

        var line = new ChartSeries { Name = pick, Kind = ChartSeriesKind.Line };
        foreach (var (day, rank) in byDay)
            if (DateTime.TryParse(day, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AssumeUniversal |
                                  System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                line.Points.Add(new ChartPoint(dt.Ticks / (double)TimeSpan.TicksPerDay, rank));

        HistorySeries = line.Points.Count > 0 ? new[] { line } : null;
        RenderTick++;
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    private void ReloadSessions()
    {
        if (Store is not { } store || SelectedCharacter is not { } slug) return;

        Sessions.Clear();
        foreach (var s in store.LoadSessions(slug).Reverse())    // newest first
        {
            if (s.Replay && !ShowReplay) continue;
            var row = new SessionRow(s);
            row.WhenAnyValue(x => x.Compare).Subscribe(_ => RebuildCompare());
            Sessions.Add(row);
        }
        RebuildCompare();
    }

    private void RebuildCompare()
    {
        if (Store is not { } store || SelectedCharacter is not { } slug)
        { CompareSeries = null; return; }

        var picked = Sessions.Where(r => r.Compare).Take(3).ToList();
        if (picked.Count == 0) { CompareSeries = null; RenderTick++; return; }

        var series = new List<ChartSeries>();
        int color = 0;
        foreach (var row in picked)
        {
            var snaps = store.LoadSnapshots(slug, row.Record.Id);
            var line = new ChartSeries
            {
                Name = row.Record.StartUtc.ToLocalTime().ToString("MM-dd HH:mm"),
                Kind = ChartSeriesKind.Line,
                ColorIndex = color++,
            };
            // Cumulative total gain over elapsed time, normalized to a common
            // t=0 start so sessions of different days overlay directly.
            var baseline = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var current  = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var snap in snaps)
            {
                foreach (var (skill, v) in snap.Skills)
                {
                    if (v is not { Length: >= 2 }) continue;
                    double val = v[0] + v[1] / 100.0;
                    baseline.TryAdd(skill, val);
                    current[skill] = val;
                }
                double gain = current.Sum(kv => kv.Value - baseline[kv.Key]);
                line.Points.Add(new ChartPoint(snap.Elapsed, Math.Round(gain, 2)));
            }
            if (line.Points.Count > 1) series.Add(line);
        }
        CompareSeries = series.Count > 0 ? series : null;
        RenderTick++;
    }

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                                 : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Signed 2-dp gain ("+2.34"), invariant culture — matches the
    /// Experience window's gain-column format.</summary>
    private static string FormatGain(double gain) =>
        (gain >= 0 ? "+" : "") + gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
