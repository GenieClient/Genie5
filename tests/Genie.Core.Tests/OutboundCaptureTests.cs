using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Capture;
using Genie.Core.Events;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Outbound command logging for analyst captures.
///
/// <para>
/// Motivation (2026-09-03): "Please rephrase that command." had been arriving
/// in pairs across live sessions for months — 44 occurrences in one August
/// session alone — and was unattributable, because every artifact the client
/// produced (raw session XML, analyst capture, display log) recorded only the
/// <b>inbound</b> stream. The server's reaction was visible; the command that
/// caused it never was. Sends the client makes on its own — the injuries poll,
/// extension refreshes, <c>;</c>-split segments — are echoed nowhere at all.
/// </para>
///
/// <para>
/// The fix is a sent-command observable published from the one socket-level
/// choke point (<c>GameConnection.SendCommandAsync</c>, after the flush, so it
/// records what actually reached the wire in wire order), relayed through
/// <c>GenieCore.SentCommands</c>, and interleaved into the capture transcript
/// as <c>[SENT]</c> lines. These tests pin the capture-side contract and — most
/// importantly — the redaction gate, since a capture exists to be handed to
/// someone else and the outbound side carries credentials.
/// </para>
/// </summary>
public class OutboundCaptureTests : IDisposable
{
    private readonly string _dir;

    public OutboundCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_outbound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static (AnalystCapture cap, TestStream<string> raw, TestStream<GameEvent> ev, TestStream<string> sent)
        StartCapture(string dir)
    {
        var raw  = new TestStream<string>();
        var ev   = new TestStream<GameEvent>();
        var sent = new TestStream<string>();
        var cap  = new AnalystCapture(dir);
        cap.Start(raw, ev, "Renucci", DateTime.UtcNow, sentCommands: sent);
        return (cap, raw, ev, sent);
    }

    private static string StreamsText(AnalystCapture cap, string basePath) => File.ReadAllText(basePath + "_streams.txt");

    // ── the core contract ────────────────────────────────────────────────────

    [Fact]
    public void SentCommands_AreWrittenToTheTranscript()
    {
        var (cap, raw, ev, sent) = StartCapture(_dir);
        var basePath = cap.BasePath!;

        sent.OnNext("health");
        cap.Stop();

        Assert.Contains("[SENT] health", StreamsText(cap, basePath));
    }

    [Fact]
    public void SentCommands_InterleaveWithGameTextInOrder()
    {
        // The whole point: a reply must be attributable to the command before it.
        // Neutral game text on purpose — third-person speech ("Lomtaun says, …")
        // is dropped by the G2 content pass, which is correct but would make
        // this test about redaction rather than ordering.
        var (cap, raw, ev, sent) = StartCapture(_dir);
        var basePath = cap.BasePath!;

        sent.OnNext("ask lomtaun about magic");
        ev.OnNext(new TextEvent("main", "Your body feels at full strength."));
        sent.OnNext("health");
        ev.OnNext(new TextEvent("main", "Please rephrase that command."));
        cap.Stop();

        var lines = File.ReadAllLines(basePath + "_streams.txt");
        Assert.Equal(
            new[]
            {
                "[SENT] ask lomtaun about magic",
                "Your body feels at full strength.",
                "[SENT] health",
                "Please rephrase that command.",
            },
            lines);
    }

    [Fact]
    public void MechanicalCommands_SurviveVerbatim()
    {
        // If these were redacted the log could not answer the question it exists
        // to answer, so the redactor must be a narrow allow-through by default.
        var r = new CaptureRedactor();
        foreach (var cmd in new[]
                 {
                     "health", "north", "go bridge", "exp all", "attack kobold",
                     "prep moongate", "cast Grazhir", "_magic ask -11301 Riftal Summons",
                 })
            Assert.Equal(cmd, r.RedactOutboundCommand(cmd));

        Assert.Equal(0, r.RedactedCommands);
    }

    [Fact]
    public void CaptureWithoutSentStream_StillWorks()
    {
        // sentCommands is optional — a caller that passes nothing must not break.
        var raw = new TestStream<string>();
        var ev  = new TestStream<GameEvent>();
        var cap = new AnalystCapture(_dir);
        cap.Start(raw, ev, "Renucci", DateTime.UtcNow);
        var basePath = cap.BasePath!;

        ev.OnNext(new TextEvent("main", "You nod."));
        cap.Stop();

        Assert.Contains("You nod.", File.ReadAllText(basePath + "_streams.txt"));
    }

    // ── redaction gate (policy G2 + credentials) ─────────────────────────────

    [Theory]
    [InlineData("#connect MONIL hunter2 Renucci",     "#connect")]
    [InlineData("#lichconnect MONIL hunter2",         "#lichconnect")]
    [InlineData("#reconnect MONIL hunter2",           "#reconnect")]
    public void CredentialCommands_AreRedactedToTheVerb(string command, string verb)
    {
        var r = new CaptureRedactor();
        var got = r.RedactOutboundCommand(command);

        Assert.Equal(verb + " [redacted: credentials]", got);
        Assert.DoesNotContain("hunter2", got);
        Assert.DoesNotContain("MONIL",   got);
        Assert.Equal(1, r.RedactedCommands);
    }

    [Theory]
    [InlineData("whisper Naper meet me at the shard", "whisper")]
    [InlineData("tell Shroom the map is fixed",       "tell")]
    [InlineData("say hello everyone",                 "say")]
    [InlineData("think I am going to the guild",      "think")]
    [InlineData("ooc brb",                            "ooc")]
    public void SocialCommands_KeepTheVerbAndDropTheBody(string command, string verb)
    {
        var r = new CaptureRedactor();
        var got = r.RedactOutboundCommand(command);

        Assert.Equal(verb + " [redacted: social]", got);
        Assert.Equal(1, r.RedactedCommands);
    }

    [Fact]
    public void CredentialsNeverReachTheCaptureFile()
    {
        // End-to-end: the password must not exist anywhere in the artifact.
        var (cap, raw, ev, sent) = StartCapture(_dir);
        var basePath = cap.BasePath!;

        sent.OnNext("#connect MONIL hunter2 Renucci");
        sent.OnNext("whisper Naper see you at the volcano");
        cap.Stop();

        var text = StreamsText(cap, basePath);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("volcano", text);
        Assert.Contains("[SENT] #connect [redacted: credentials]", text);
        Assert.Contains("[SENT] whisper [redacted: social]",       text);
    }

    [Fact]
    public void MetaRecordsSentAndRedactedCounts()
    {
        var (cap, raw, ev, sent) = StartCapture(_dir);
        var basePath = cap.BasePath!;

        sent.OnNext("health");
        sent.OnNext("north");
        sent.OnNext("#connect MONIL hunter2");
        cap.Stop();

        var meta = File.ReadAllText(basePath + ".meta.json");
        Assert.Contains("\"sentCommands\": 3",     meta);
        Assert.Contains("\"redactedCommands\": 1", meta);
    }

    [Fact]
    public void SendsAfterStop_AreIgnored()
    {
        var (cap, raw, ev, sent) = StartCapture(_dir);
        var basePath = cap.BasePath!;

        sent.OnNext("health");
        cap.Stop();
        sent.OnNext("this must not be written");   // subscription disposed by Stop

        Assert.DoesNotContain("must not be written", StreamsText(cap, basePath));
    }
}

/// <summary>
/// Minimal hot observable. The test project does not reference System.Reactive,
/// and a Subject is more than these tests need: push a value, every current
/// subscriber sees it, disposing the subscription stops delivery.
/// </summary>
internal sealed class TestStream<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _subs = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        _subs.Add(observer);
        return new Unsub(() => _subs.Remove(observer));
    }

    public void OnNext(T value)
    {
        foreach (var o in _subs.ToArray()) o.OnNext(value);
    }

    private sealed class Unsub : IDisposable
    {
        private readonly Action _dispose;
        public Unsub(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
