namespace Genie.Core.Analytics;

/// <summary>
/// Append-only JSONL writer for one character's skill-history folder
/// (<c>{AnalyticsDir}\{Char}-{Acct}\</c>). Appends are O(1) and per-line
/// crash-tolerant: a torn final line from a power cut is skipped by the
/// tolerant reader and every prior line survives.
///
/// <para>Never throws into the game pipeline: the first IO failure logs once
/// (via the optional sink) and latches the writer into a silent no-op.</para>
/// </summary>
public sealed class SkillHistoryWriter
{
    public const string SessionsFile = "sessions.jsonl";
    public const string DailyFile    = "daily.jsonl";

    private readonly object _io = new();
    private readonly Action<string>? _log;
    private bool _failed;

    /// <summary>The per-character folder all files live in.</summary>
    public string Directory { get; }

    public SkillHistoryWriter(string characterDirectory, Action<string>? log = null)
    {
        Directory = characterDirectory;
        _log = log;
    }

    /// <summary>Monthly shard name for a snapshot timestamp.</summary>
    public static string SnapshotFileFor(DateTime atUtc) => $"snapshots-{atUtc:yyyyMM}.jsonl";

    public void WriteSnapshot(SnapshotRecord snap) =>
        AppendLine(SnapshotFileFor(snap.AtUtc), SkillHistoryJson.ToLine(snap));

    public void WriteSession(SessionRecord session) =>
        AppendLine(SessionsFile, SkillHistoryJson.ToLine(session));

    private void AppendLine(string file, string line)
    {
        if (_failed) return;
        lock (_io)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, file), line + "\n");
            }
            catch (Exception ex)
            {
                // Disk full / locked / permission — warn once, then go quiet.
                // Analytics must never take the game session down with it.
                _failed = true;
                _log?.Invoke($"[analytics] history write failed ({ex.Message}) — recording disabled for this session.");
            }
        }
    }

    /// <summary>
    /// Crash recovery: any session id that has snapshots but no session row
    /// (the app died before the disconnect write) gets a summary synthesized
    /// from its first/last snapshots, marked <c>"recovered":true</c>. Runs at
    /// recorder startup; cheap when there's nothing to do. Returns how many
    /// sessions were recovered.
    ///
    /// <para><paramref name="startedBeforeUtc"/> guards against the scan racing
    /// the CURRENT process: recovery runs on a background task, so this run's
    /// own session can land its first snapshot before its session row. A
    /// session whose id-embedded start second is at/after the cutoff belongs to
    /// this run and is never an orphan — skip it. Ids that don't parse are
    /// treated as old so genuine orphans always recover.</para>
    /// </summary>
    public int RecoverOrphanSessions(string character, string account, DateTime? startedBeforeUtc = null)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return 0;

            var known = new HashSet<string>(StringComparer.Ordinal);
            string sessionsPath = Path.Combine(Directory, SessionsFile);
            if (File.Exists(sessionsPath))
                foreach (var line in File.ReadLines(sessionsPath))
                    if (SkillHistoryJson.TryParse<SessionRecord>(line) is { } s && s.Id.Length > 0)
                        known.Add(s.Id);

            // Fold every orphan sid's snapshots (in file order — shards sort
            // chronologically by name) into first/last per-skill spans.
            var orphans = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
            foreach (var shard in System.IO.Directory.GetFiles(Directory, "snapshots-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            foreach (var line in File.ReadLines(shard))
            {
                if (SkillHistoryJson.TryParse<SnapshotRecord>(line) is not { } snap
                    || snap.SessionId.Length == 0 || known.Contains(snap.SessionId))
                    continue;

                if (startedBeforeUtc is { } cutoff
                    && TryParseSessionStart(snap.SessionId) is { } started
                    && started >= cutoff)
                    continue;   // this run's live session, not a crash orphan

                if (!orphans.TryGetValue(snap.SessionId, out var rec))
                {
                    rec = new SessionRecord
                    {
                        Id = snap.SessionId,
                        Character = character,
                        Account = account,
                        StartUtc = snap.AtUtc,
                        Replay = snap.Replay,
                        Recovered = true,
                        TdpStart = snap.Tdp,
                    };
                    orphans[snap.SessionId] = rec;
                }
                rec.EndUtc  = snap.AtUtc;
                rec.Seconds = snap.Elapsed;
                if (snap.Tdp > 0)
                {
                    if (rec.TdpStart == 0) rec.TdpStart = snap.Tdp;
                    rec.TdpEnd = snap.Tdp;
                }
                foreach (var (skill, v) in snap.Skills)
                {
                    if (v is not { Length: >= 2 }) continue;
                    if (!rec.Skills.TryGetValue(skill, out var span))
                    {
                        span = new SkillSpan { RankStart = v[0], PercentStart = v[1] };
                        rec.Skills[skill] = span;
                    }
                    span.RankEnd = v[0];
                    span.PercentEnd = v[1];
                }
            }

            foreach (var rec in orphans.Values)
                WriteSession(rec);
            return orphans.Count;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[analytics] orphan-session recovery failed ({ex.Message}).");
            return 0;
        }
    }

    /// <summary>Start second embedded in a session id
    /// (<c>yyyyMMddTHHmmssZ-…</c>, see SkillHistoryRecorder), or null.</summary>
    private static DateTime? TryParseSessionStart(string sessionId)
    {
        if (sessionId.Length < 16) return null;
        return DateTime.TryParseExact(sessionId[..16], "yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var t) ? t : null;
    }
}
