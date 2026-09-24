using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// 2026-08-31 stability review — an action body's send reached the send gate while the gate
/// was closed (routine: the script's own command is still in flight when its
/// response lines fire the action). The gate's defer path did <c>Pc--</c>,
/// which is only right for a statement the script itself just advanced past;
/// an action owns no Pc slot, so the action's put was dropped and the script
/// was rewound one line — a double send, or at matchwait a re-armed empty
/// match list that hung forever. Gated action sends are now queued, and
/// drained even while the script is parked at its own wait.
/// </summary>
public class ActionSendGateTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly List<string> _sent = new();
    private readonly ScriptEngine _engine;

    public ActionSendGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_actiongate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                   sendCommand: c => _sent.Add(c), echo: l => _echoed.Add(l));
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Start(string body)
    {
        File.WriteAllText(Path.Combine(_dir, "t.cmd"), body);
        Assert.True(_engine.TryStart("t", Array.Empty<string>()));
    }

    private void Pump(int ticks = 20)
    {
        for (int i = 0; i < ticks; i++) _engine.Tick();
    }

    private int SentCount(string cmd) => _sent.Count(s => s == cmd);

    [Fact]
    public void Gated_action_put_at_matchwait_is_sent_and_the_matchwait_still_resolves()
    {
        Start(
            "action put stand when ^You are knocked down\n" +
            "match done You arrive\n" +
            "put look\n" +
            "matchwait\n" +
            "echo NO-MATCH\n" +
            "exit\n" +
            ":done\n" +
            "echo DONE\n");
        Pump();
        Assert.Equal(1, SentCount("look"));        // in flight; script parked at matchwait

        _engine.OnGameLine("You are knocked down!");   // gate closed: `look` still in flight
        _engine.OnPrompt();                            // gate opens
        Pump();

        Assert.Equal(1, SentCount("stand"));       // was silently dropped
        Assert.Equal(1, SentCount("look"));        // no rewind re-send

        _engine.OnGameLine("You arrive at the gate.");
        Pump();
        Assert.Contains("DONE", _echoed);
        Assert.DoesNotContain("NO-MATCH", _echoed);
    }

    [Fact]
    public void Gated_action_put_while_the_script_runs_does_not_rewind_it()
    {
        Start(
            "action put stand when ^You are knocked down\n" +
            "put look\n" +
            "put inventory\n" +
            "echo END\n");
        Pump();
        Assert.Equal(new[] { "look" }, _sent);     // `put inventory` held by the 1-deep gate

        _engine.OnGameLine("You are knocked down!");
        for (int i = 0; i < 4; i++) { _engine.OnPrompt(); Pump(); }

        Assert.Equal(1, SentCount("look"));        // the rewind re-sent the previous line
        Assert.Equal(1, SentCount("inventory"));
        Assert.Equal(1, SentCount("stand"));
        Assert.Contains("END", _echoed);
    }

    [Fact]
    public void Gated_action_put_during_a_pause_fires_without_waiting_out_the_pause()
    {
        Start(
            "action put stand when ^You are knocked down\n" +
            "put look\n" +
            "pause 30\n" +
            "echo END\n");
        Pump();

        _engine.OnGameLine("You are knocked down!");
        _engine.OnPrompt();
        Pump();

        Assert.Equal(1, SentCount("stand"));
        Assert.DoesNotContain("END", _echoed);     // still paused
    }

    [Fact]
    public void Ungated_action_put_is_unchanged()
    {
        Start(
            "action put stand when ^You are knocked down\n" +
            "pause 30\n");
        Pump();

        _engine.OnGameLine("You are knocked down!");  // nothing in flight: sends inline
        Assert.Equal(new[] { "stand" }, _sent);
    }

    [Fact]
    public void Gated_bare_command_action_is_queued_too()
    {
        Start(
            "action stand when ^You are knocked down\n" +
            "put look\n" +
            "pause 30\n");
        Pump();

        _engine.OnGameLine("You are knocked down!");
        _engine.OnPrompt();
        Pump();

        Assert.Equal(1, SentCount("stand"));
        Assert.Equal(1, SentCount("look"));
    }
}
