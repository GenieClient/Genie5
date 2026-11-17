using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Genie.Core.Connection;
using Genie.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #433 — a waitfor/matchwait satisfied by a live server line resumes the
/// script only after the rest of that network read has been processed (Genie 4
/// Script.cs SetBufferWait / m_bBufferEnd, released by Connection.DataRecieveEnd).
/// automapper.cmd's MOVE.WAIT turns its "Obvious paths" action off, waits for
/// "You reach…", then turns it back on; the room text arrives in the same read
/// and must be processed while the action is still off, or the move list shifts
/// twice and #goto falls a step out of sync on every timed climb.
/// </summary>
public class ScriptReadEndResumeTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly List<string> _sent = new();
    private readonly ScriptEngine _engine;

    public ScriptReadEndResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_readend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                   sendCommand: c => _sent.Add(c), echo: l => _echoed.Add(l));
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Start(string name, string body)
    {
        File.WriteAllText(Path.Combine(_dir, name + ".cmd"), body);
        Assert.True(_engine.TryStart(name, Array.Empty<string>()));
    }

    private void Pump(int ticks = 20)
    {
        for (int i = 0; i < ticks; i++) _engine.Tick();
    }

    private const string MoveWaitScript =
        "var shifts 0\n" +
        "action (mapper) math shifts add 1 when ^Obvious paths\n" +
        "action (mapper) off\n" +
        "waitforre ^You reach\n" +
        "action (mapper) on\n" +
        "echo SHIFTS=%shifts\n" +
        "exit\n";

    private void FeedClimbArrival(bool fromServerRead)
    {
        _engine.OnGameLine("You reach the end of the terrace stairs.", fromServerRead);
        _engine.OnGameLine("[Aesry Surlaenis'a, Shadaer Jama]", fromServerRead);
        _engine.OnGameLine("Obvious paths: west.", fromServerRead);
    }

    [Fact]
    public void Waitforre_match_lets_the_rest_of_the_read_reach_actions_before_resuming()
    {
        // Report only after a later line, so an "Obvious paths" that reached the
        // action after it was turned back on is counted.
        Start("am",
            "var shifts 0\n" +
            "action (mapper) math shifts add 1 when ^Obvious paths\n" +
            "action (mapper) off\n" +
            "waitforre ^You reach\n" +
            "action (mapper) on\n" +
            "waitfor REPORT\n" +
            "echo SHIFTS=%shifts\n" +
            "exit\n");
        Pump();

        FeedClimbArrival(fromServerRead: true);
        _engine.OnServerReadEnd();
        Pump();
        _engine.OnGameLine("REPORT");
        Pump();

        // The arrival's "Obvious paths" landed while the action was still off.
        Assert.Contains("SHIFTS=0", _echoed);
        Assert.DoesNotContain("SHIFTS=1", _echoed);
    }

    [Fact]
    public void Waitforre_match_stays_parked_until_the_read_ends()
    {
        Start("am", MoveWaitScript);
        Pump();

        FeedClimbArrival(fromServerRead: true);
        Pump();   // timers firing mid-read must not resume it
        Assert.DoesNotContain(_echoed, l => l.StartsWith("SHIFTS=", StringComparison.Ordinal));

        _engine.OnServerReadEnd();
        Assert.Contains("SHIFTS=0", _echoed);
    }

    [Fact]
    public void Matchwait_match_resumes_after_the_read_ends()
    {
        Start("mw",
            "var shifts 0\n" +
            "action (mapper) math shifts add 1 when ^Obvious paths\n" +
            "action (mapper) off\n" +
            "matchre arrived ^You reach\n" +
            "matchwait\n" +
            ":arrived\n" +
            "action (mapper) on\n" +
            "echo SHIFTS=%shifts\n" +
            "exit\n");
        Pump();

        FeedClimbArrival(fromServerRead: true);
        Pump();
        Assert.DoesNotContain(_echoed, l => l.StartsWith("SHIFTS=", StringComparison.Ordinal));

        _engine.OnServerReadEnd();
        Assert.Contains("SHIFTS=0", _echoed);
    }

    [Fact]
    public void Synthetic_lines_still_resume_at_once()
    {
        // #parse, typed input and automapper signals are not part of a server
        // read — Genie 4 feeds them with bBufferWait=false, so no read end follows.
        Start("wf",
            "waitfor YOU HAVE ARRIVED\n" +
            "echo RESUMED\n" +
            "exit\n");
        Pump();

        _engine.OnGameLine("YOU HAVE ARRIVED!");
        Pump();
        Assert.Contains("RESUMED", _echoed);
    }

    [Fact]
    public void Read_end_with_nothing_parked_is_harmless()
    {
        Start("wf",
            "waitfor never\n" +
            "echo RESUMED\n" +
            "exit\n");
        Pump();
        _engine.OnServerReadEnd();
        Pump();
        Assert.DoesNotContain("RESUMED", _echoed);
    }

    private sealed class Collector<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public Collector(Action<T> onNext) => _onNext = onNext;
        public void OnNext(T value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void Connection_signals_read_end_once_after_the_reads_chunks()
    {
        var conn = new GameConnection(new ConnectionConfig(), null!, NullLogger<GameConnection>.Instance);
        var order = new List<string>();
        using var a = conn.RawXmlStream.Subscribe(new Collector<string>(c => order.Add("chunk")));
        using var b = conn.ReadEndStream.Subscribe(new Collector<bool>(_ => order.Add("END")));

        conn.EmitChunks(new StringBuilder("You reach the end.\n<prompt time=\"1\">&gt;</prompt>\npartial"));

        Assert.Equal("END", order[^1]);
        Assert.Single(order, s => s == "END");
        Assert.True(order.Count > 1);
    }

    [Fact]
    public void Connection_does_not_signal_read_end_when_nothing_was_published()
    {
        var conn = new GameConnection(new ConnectionConfig(), null!, NullLogger<GameConnection>.Instance);
        int ends = 0;
        using var b = conn.ReadEndStream.Subscribe(new Collector<bool>(_ => ends++));

        conn.EmitChunks(new StringBuilder("partial line with no newline"));

        Assert.Equal(0, ends);
    }
}
