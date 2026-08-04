using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Smoke 2026-08-03 finding #8: every script death must raise
/// <see cref="ScriptEngine.ScriptFinished"/> exactly once — the App's script
/// bar removes its chip on that event. `goto`/`gosub` to an unknown label,
/// the gosub depth limit, and `return` on an empty stack used to set
/// <c>Running = false</c> silently; the tick loop then pruned the dead
/// instance without notifying, leaving a stale "uber ⏸" chip after
/// "[script] unknown label:". The engine now funnels every death through a
/// once-guarded NotifyFinished (the prune is a structural catch-all), so the
/// event fires for every path and never twice.
/// </summary>
public class ScriptFinishedEventTests
{
    private static (List<string> finished, List<string> echoed) Run(string body, int ticks = 50)
    {
        var finished = new List<string>();
        var echoed   = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_finev_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.ScriptFinished += n => finished.Add(n);
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < ticks; i++) engine.Tick();
            return (finished, echoed);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Goto_unknown_label_fires_finished_exactly_once()
    {
        // The uber.cmd death shape: `goto` with a bad/empty target.
        var (finished, echoed) = Run("echo start\ngoto nosuchlabel\n");

        Assert.Equal("t", Assert.Single(finished));
        Assert.Contains(echoed, l => l.Contains("unknown label"));
    }

    [Fact]
    public void Gosub_unknown_label_fires_finished_exactly_once()
    {
        var (finished, _) = Run("gosub nowhere\n");
        Assert.Equal("t", Assert.Single(finished));
    }

    [Fact]
    public void Return_on_empty_stack_fires_finished_exactly_once()
    {
        var (finished, _) = Run("echo hi\nreturn\n");
        Assert.Equal("t", Assert.Single(finished));
    }

    [Fact]
    public void Normal_completion_still_fires_exactly_once()
    {
        // The done path already notified; the prune catch-all must not make it
        // fire a second time (mapper replan-on-finish would double-run).
        var (finished, echoed) = Run("echo hi\n");

        Assert.Equal("t", Assert.Single(finished));
        Assert.Contains(echoed, l => l.Contains("t done"));
    }

    [Fact]
    public void Exit_fires_finished_exactly_once()
    {
        var (finished, _) = Run("exit\necho unreachable\n");
        Assert.Equal("t", Assert.Single(finished));
    }
}
