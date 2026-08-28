using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #309 — the send-gate match-replay fix. Genie 4's script `put` never
/// blocks, so `match … / put a / put b / matchwait` arms its match list before
/// any server response returns. Genie 5 pipelines game-bound script sends one
/// deep per prompt, which parks the script between the puts — responses that
/// land in that window must be buffered and replayed when matchwait (or
/// waitfor) arms, or the classic look-then-match idiom (ap.cmd, travel
/// scripts) silently falls through to its error label.
/// </summary>
public class SendGateMatchReplayTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly List<string> _sent = new();
    private readonly ScriptEngine _engine;

    public SendGateMatchReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_gatereplay_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Matchwait_fires_on_response_that_arrived_while_second_put_was_gated()
    {
        // The ap.cmd shape: the FIRST put's response carries the match text,
        // and it lands while the SECOND put is still held by the 1-deep gate.
        Start("ap",
            "match ok the silvery shard Asharshpar'i\n" +
            "match fallback Obvious exits:\n" +
            "put look shard\n" +
            "put look\n" +
            "matchwait\n" +
            "echo NO-MATCH\n" +
            "exit\n" +
            ":ok\n" +
            "echo MATCHED-OK\n" +
            "exit\n" +
            ":fallback\n" +
            "echo MATCHED-FALLBACK\n" +
            "exit\n");
        Pump();
        Assert.Contains("look shard", _sent);
        Assert.DoesNotContain("look", _sent);   // second put still gated

        // First put's response arrives BEFORE matchwait could arm.
        _engine.OnGameLine("[Assuming you mean the silvery shard Asharshpar'i.]");
        // Prompt frees the gate: `put look` dispatches, matchwait arms and
        // must replay the buffered response.
        _engine.OnPrompt();
        Pump();

        Assert.Contains("look", _sent);          // both puts reached the game (G4 shape)
        Assert.Contains("MATCHED-OK", _echoed);
        Assert.DoesNotContain("MATCHED-FALLBACK", _echoed);
        Assert.DoesNotContain("NO-MATCH", _echoed);
    }

    [Fact]
    public void Matchwait_fallback_still_wins_when_the_match_text_never_arrived()
    {
        Start("ap",
            "match ok the silvery shard Asharshpar'i\n" +
            "match fallback Obvious exits:\n" +
            "put look shard\n" +
            "put look\n" +
            "matchwait\n" +
            ":ok\n" +
            "echo MATCHED-OK\n" +
            "exit\n" +
            ":fallback\n" +
            "echo MATCHED-FALLBACK\n" +
            "exit\n");
        Pump();
        _engine.OnGameLine("Some unrelated text.");   // gated-window line, no match
        _engine.OnPrompt();
        Pump();
        _engine.OnGameLine("Obvious exits: south.");  // live matchwait hit
        Pump();

        Assert.Contains("MATCHED-FALLBACK", _echoed);
        Assert.DoesNotContain("MATCHED-OK", _echoed);
    }

    [Fact]
    public void Waitfor_is_satisfied_by_response_that_arrived_while_gated()
    {
        Start("doors",
            "put open door\n" +
            "put go door\n" +
            "waitfor The door opens\n" +
            "echo AFTER-WAITFOR\n" +
            "exit\n");
        Pump();
        _engine.OnGameLine("The door opens with a creak.");   // response to put #1
        _engine.OnPrompt();
        Pump();

        Assert.Contains("go door", _sent);
        Assert.Contains("AFTER-WAITFOR", _echoed);
    }

    [Fact]
    public void Script_authored_pause_clears_the_replay_buffer()
    {
        // G4 parity boundary: a line that lands during the send gate but is
        // followed by a script-authored pause is NOT replayable — in Genie 4
        // the script would have been sitting in the pause (matcher unarmed)
        // when that line arrived, so it was missed there too.
        Start("pauser",
            "match ok TARGET-LINE\n" +
            "put cmd1\n" +
            "put cmd2\n" +
            "pause 0.1\n" +
            "matchwait 0.2\n" +
            "echo NO-MATCH\n" +
            "exit\n" +
            ":ok\n" +
            "echo MATCHED-OK\n" +
            "exit\n");
        Pump();
        _engine.OnGameLine("TARGET-LINE arrives during the gate.");
        _engine.OnPrompt();
        Pump();                          // cmd2 dispatches, pause starts (buffer cleared)
        Thread.Sleep(150);
        Pump();                          // pause expires, matchwait arms — nothing to replay
        Thread.Sleep(250);
        Pump();                          // matchwait times out

        Assert.Contains("NO-MATCH", _echoed);
        Assert.DoesNotContain("MATCHED-OK", _echoed);
    }

    [Fact]
    public void Fired_match_reports_label_and_line_at_debug_level_2()
    {
        Start("dbg",
            "debug 2\n" +
            "match ok TARGET-LINE\n" +
            "put look\n" +
            "matchwait\n" +
            ":ok\n" +
            "exit\n");
        Pump();
        _engine.OnGameLine("TARGET-LINE here.");   // live matchwait hit
        Pump();

        Assert.Contains(_echoed, l => l.Contains("match ok") && l.Contains("TARGET-LINE here."));
    }
}
