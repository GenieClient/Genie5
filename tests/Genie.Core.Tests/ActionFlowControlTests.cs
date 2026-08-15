using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Flow-control commands (<c>goto</c> / <c>gosub</c> / <c>return</c> /
/// <c>exit</c>) inside an <c>action</c> body threw
/// "Index was out of range … (Parameter 'index')" and were swallowed into
/// "[script] &lt;name&gt; action error: …".
///
/// Cause: <c>FireActions</c> dispatches an action body with
/// <c>currentIdx = -1</c> (an action body is not a line in <c>inst.Lines</c>),
/// but the four flow-control cases read <c>inst.Lines[currentIdx].Origin</c>
/// unconditionally to stamp the debug trace. The <c>List&lt;T&gt;</c> indexer
/// threw before the jump ever happened, so the action silently did nothing.
///
/// Real-world impact: automapper.cmd drives nearly all of its movement
/// recovery through <c>action (mapper) goto move.…</c>, so every closed shop /
/// failed move / retreat / stand handler was dead — the visible symptom was
/// "go crude hut" → action error → "Bonk! You smash your nose." with none of
/// the SHOP IS CLOSED handling that should have followed.
/// </summary>
public class ActionFlowControlTests
{
    private static List<string> Run(string body, params string[] gameLines)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_actflow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            Pump(engine);
            foreach (var gl in gameLines)
            {
                engine.OnGameLine(gl);
                Pump(engine);
            }
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>The park loop's <c>pause</c> is wall-clock, so ticks need real
    /// time to pass or the script never advances past the jump target.</summary>
    private static void Pump(ScriptEngine engine)
    {
        for (int i = 0; i < 60; i++)
        {
            engine.Tick();
            engine.OnPrompt();
            System.Threading.Thread.Sleep(5);
        }
    }

    /// <summary>Keeps the instance (and its actions) alive while lines are fed.</summary>
    private const string Park = "loop:\npause 0.01\ngoto loop\n";

    [Fact]
    public void Goto_in_action_body_jumps_without_error()
    {
        var o = Run("action goto closed when ^Bonk\\! You smash your nose\n"
                    + Park
                    + "closed:\necho SHOP IS CLOSED\nexit\n",
                    "Bonk! You smash your nose.");

        Assert.DoesNotContain(o, l => l.Contains("action error"));
        Assert.Contains(o, l => l.Contains("SHOP IS CLOSED"));
    }

    [Fact]
    public void Goto_in_multi_statement_action_body_jumps_without_error()
    {
        // automapper.cmd's exact shape: `action (mapper) var closed 1;goto move.closed when …`
        var o = Run("action (mapper) var closed 1;goto closed when ^Bonk\\! You smash your nose\n"
                    + Park
                    + "closed:\necho CLOSED=%closed\nexit\n",
                    "Bonk! You smash your nose.");

        Assert.DoesNotContain(o, l => l.Contains("action error"));
        Assert.Contains(o, l => l.Contains("CLOSED=1"));
    }

    [Fact]
    public void Gosub_in_action_body_runs_and_returns_without_error()
    {
        var o = Run("action gosub handler when ^TRIGGER\n"
                    + Park
                    + "handler:\necho HANDLED\nreturn\n",
                    "TRIGGER now");

        Assert.DoesNotContain(o, l => l.Contains("action error"));
        Assert.Contains(o, l => l.Contains("HANDLED"));
    }

    [Fact]
    public void Exit_in_action_body_stops_the_script_without_error()
    {
        var o = Run("action echo BYE;exit when ^TRIGGER\n" + Park, "TRIGGER now");

        Assert.DoesNotContain(o, l => l.Contains("action error"));
        Assert.Contains(o, l => l.Contains("BYE"));
    }

    [Fact]
    public void Return_in_action_body_does_not_error()
    {
        // A bare `return` with an empty gosub stack is a no-op in Genie 4, but
        // it must not blow up the action.
        var o = Run("action echo PRE;return when ^TRIGGER\n" + Park, "TRIGGER now");

        Assert.DoesNotContain(o, l => l.Contains("action error"));
        Assert.Contains(o, l => l.Contains("PRE"));
    }
}
