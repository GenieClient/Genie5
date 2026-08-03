using System;
using System.IO;
using System.Linq;
using Genie.Core.Analytics;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Skill-history JSONL storage (Analytics) — writer/reader round-trips,
/// per-line crash tolerance (a torn tail line never poisons the file), and
/// orphan-session recovery (snapshots without a session row synthesize a
/// <c>"recovered":true</c> summary).
/// </summary>
public class SkillHistoryStorageTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "Genie5Test-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SnapshotRecord Snap(string sid, DateTime at, long el, params (string Skill, int R, int P, int M)[] skills)
    {
        var s = new SnapshotRecord { SessionId = sid, AtUtc = at, Elapsed = el };
        foreach (var (skill, r, p, m) in skills) s.Skills[skill] = new[] { r, p, m };
        return s;
    }

    [Fact]
    public void Snapshot_And_Session_RoundTrip()
    {
        var writer = new SkillHistoryWriter(_dir);
        var at = new DateTime(2026, 7, 7, 1, 31, 45, DateTimeKind.Utc);
        writer.WriteSnapshot(Snap("s1", at, 60, ("Small Edged", 142, 73, 13), ("Attunement", 550, 74, 5)));
        writer.WriteSession(new SessionRecord
        {
            Id = "s1", Character = "Tirost", Account = "ACCT",
            StartUtc = at.AddMinutes(-1), EndUtc = at.AddHours(2), Seconds = 7260,
            TdpStart = 3017, TdpEnd = 3042,
            Skills = { ["Small Edged"] = new SkillSpan { RankStart = 142, PercentStart = 71, RankEnd = 144, PercentEnd = 10 } },
        });

        var store = new SkillHistoryStore(Path.GetDirectoryName(_dir)!);
        string slug = Path.GetFileName(_dir);

        var sessions = store.LoadSessions(slug);
        var s = Assert.Single(sessions);
        Assert.Equal("s1", s.Id);
        Assert.Equal(3042, s.TdpEnd);
        Assert.Equal(1.39, s.Skills["Small Edged"].Gain, 2);   // 144.10 − 142.71

        var snaps = store.LoadSnapshots(slug, "s1");
        var snap = Assert.Single(snaps);
        Assert.Equal(60, snap.Elapsed);
        Assert.Equal(new[] { 142, 73, 13 }, snap.Skills["Small Edged"]);
        Assert.Equal(at, snap.AtUtc);
    }

    [Fact]
    public void TornOrForeignLines_AreSkipped_PriorRowsSurvive()
    {
        var writer = new SkillHistoryWriter(_dir);
        var at = new DateTime(2026, 7, 7, 2, 0, 0, DateTimeKind.Utc);
        writer.WriteSnapshot(Snap("s1", at, 60, ("Attunement", 550, 74, 5)));

        // Simulate a power-cut tail + an unknown future row type.
        string shard = Path.Combine(_dir, SkillHistoryWriter.SnapshotFileFor(at));
        File.AppendAllText(shard, "{\"t\":\"snap\",\"v\":1,\"sid\":\"s1\",\"at\":\"2026-07-07T0");   // torn
        File.AppendAllText(shard, "\nnot json at all\n");

        var store = new SkillHistoryStore(Path.GetDirectoryName(_dir)!);
        var snaps = store.LoadSnapshots(slug: Path.GetFileName(_dir), "s1");
        Assert.Single(snaps);   // the good row survives; garbage is skipped
    }

    [Fact]
    public void SnapshotShards_AreMonthly_AndWindowPruned()
    {
        var writer = new SkillHistoryWriter(_dir);
        writer.WriteSnapshot(Snap("a", new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), 10, ("X", 1, 0, 1)));
        writer.WriteSnapshot(Snap("b", new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc), 10, ("X", 2, 0, 1)));

        Assert.True(File.Exists(Path.Combine(_dir, "snapshots-202605.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "snapshots-202607.jsonl")));

        var store = new SkillHistoryStore(Path.GetDirectoryName(_dir)!);
        string slug = Path.GetFileName(_dir);
        var july = store.LoadSnapshots(slug,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(july);
        Assert.Equal("b", july[0].SessionId);
    }

    [Fact]
    public void RecoverOrphanSessions_SynthesizesRecoveredSummary()
    {
        var writer = new SkillHistoryWriter(_dir);
        var t0 = new DateTime(2026, 7, 6, 20, 0, 0, DateTimeKind.Utc);
        // Session "dead1" has snapshots but no session row (app crashed).
        writer.WriteSnapshot(Snap("dead1", t0, 30, ("Small Edged", 100, 10, 5)));
        writer.WriteSnapshot(Snap("dead1", t0.AddMinutes(30), 1830, ("Small Edged", 101, 40, 9)));
        // Session "ok" closed cleanly.
        writer.WriteSnapshot(Snap("ok", t0.AddHours(3), 10, ("Attunement", 5, 0, 1)));
        writer.WriteSession(new SessionRecord { Id = "ok", StartUtc = t0.AddHours(3), EndUtc = t0.AddHours(4), Seconds = 3600 });

        int recovered = writer.RecoverOrphanSessions("Tirost", "ACCT");
        Assert.Equal(1, recovered);

        var store = new SkillHistoryStore(Path.GetDirectoryName(_dir)!);
        var sessions = store.LoadSessions(Path.GetFileName(_dir));
        Assert.Equal(2, sessions.Count);
        var dead = sessions.Single(s => s.Id == "dead1");
        Assert.True(dead.Recovered);
        Assert.Equal(1830, dead.Seconds);                       // last snapshot's elapsed
        Assert.Equal(100, dead.Skills["Small Edged"].RankStart);
        Assert.Equal(101, dead.Skills["Small Edged"].RankEnd);
        Assert.Equal(1.30, dead.Skills["Small Edged"].Gain, 2); // 101.40 − 100.10

        // Idempotent: a second scan finds nothing new.
        Assert.Equal(0, writer.RecoverOrphanSessions("Tirost", "ACCT"));
    }

    [Fact]
    public void RecoverOrphanSessions_SkipsSessionsStartedAtOrAfterCutoff()
    {
        // Recovery runs on a background task at recorder startup, so THIS
        // run's session can have snapshots on disk before its session row
        // lands. Sids at/after the cutoff (the recorder's construction second)
        // are the live session — never synthesize a summary for them.
        var writer = new SkillHistoryWriter(_dir);
        var cutoff = new DateTime(2026, 7, 7, 9, 0, 0, DateTimeKind.Utc);

        writer.WriteSnapshot(Snap("20260706T200000Z-dead", cutoff.AddHours(-13), 30, ("X", 1, 0, 1)));  // prior run
        writer.WriteSnapshot(Snap("20260707T090000Z-live", cutoff, 10, ("X", 2, 0, 1)));               // same second
        writer.WriteSnapshot(Snap("dead1", cutoff.AddHours(1), 10, ("X", 3, 0, 1)));                   // unparseable id

        Assert.Equal(2, writer.RecoverOrphanSessions("Tirost", "ACCT", cutoff));

        var sessions = new SkillHistoryStore(Path.GetDirectoryName(_dir)!)
            .LoadSessions(Path.GetFileName(_dir));
        Assert.Equal(new[] { "20260706T200000Z-dead", "dead1" },
            sessions.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void RanksPerHour_UsesElapsedSeconds()
    {
        Assert.Equal(2.0, SkillHistoryJson.RanksPerHour(1.0, 1800), 3);
        Assert.Equal(0.0, SkillHistoryJson.RanksPerHour(1.0, 0));    // no div-by-zero
    }
}
