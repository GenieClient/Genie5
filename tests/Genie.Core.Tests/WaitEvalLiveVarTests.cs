using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>waiteval &lt;expr&gt;</c> must re-read LIVE variable state on every
/// re-evaluation. StepOne pre-substituted every dispatched line, so the
/// expression reached the waiteval case already frozen at its arming-time
/// values — <c>waiteval $mana &gt; 80</c> was stored as the literal
/// <c>50 &gt; 80</c> and could never flip true, hanging the script forever
/// even after mana recovered. Genie 4 makes exactly this distinction:
/// <c>waitfor</c> dispatches the substituted <c>ParsedLine</c>, <c>waiteval</c>
/// dispatches the raw <c>oLine.sRowContent</c> and re-runs ParseVariables at
/// each re-evaluation (Script.cs:2702-2708 and :1453).
/// </summary>
public class WaitEvalLiveVarTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly ScriptEngine _engine;

    public WaitEvalLiveVarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_waiteval_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                   sendCommand: _ => { }, echo: l => _echoed.Add(l));
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

    private void Pump(int ticks = 50) { for (int i = 0; i < ticks; i++) _engine.Tick(); }

    private bool Echoed(string fragment) =>
        _echoed.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void Waiteval_unblocks_when_a_global_flips_true()
    {
        _engine.Globals["mana"] = "50";
        Start("s", "waiteval $mana > 80\necho PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        _engine.Globals["mana"] = "90";
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    [Fact]
    public void Waiteval_unblocks_with_a_parenthesized_expression()
    {
        // The community idiom is `waiteval ($mana > 80)`.
        _engine.Globals["mana"] = "50";
        Start("s", "waiteval ($mana > 80)\necho PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        _engine.Globals["mana"] = "90";
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    [Fact]
    public void Waiteval_unblocks_from_inside_an_if_block()
    {
        // The reported shape: a mana-recovery block that ends in a waiteval.
        _engine.Globals["mana"] = "50";
        Start("s",
            "if ($mana < 60) then\n" +
            "{\n" +
            "  echo ENTERED\n" +
            "  waiteval ($mana > 80)\n" +
            "}\n" +
            "echo PASSED\n");
        Pump();
        Assert.True(Echoed("ENTERED"));
        Assert.False(Echoed("PASSED"));

        _engine.Globals["mana"] = "90";
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    [Fact]
    public void Waiteval_unblocks_when_a_local_flips_true_from_an_action()
    {
        Start("s",
            "action var m 90 when ^recovered\n" +
            "var m 50\n" +
            "waiteval %m > 80\n" +
            "echo PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        _engine.OnGameLine("recovered");
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    /// <summary>
    /// Genie 4 #179 (reported by HanryuDR): <c>$unixtime</c> unusable inside
    /// <c>waiteval</c>. Genie 5 reproduced it — not as an <c>$unixtime</c>
    /// special case but as the general variable-freeze above, which caught the
    /// clock pseudo-var too: the arming-time epoch was baked in, so the
    /// expression compared a constant against a constant forever. Guards the
    /// port-fidelity half of GenieClient/Genie5#249.
    /// </summary>
    [Fact]
    public void Waiteval_re_reads_the_unixtime_clock_pseudo_var()
    {
        // Wait out the current second first. $unixtime has whole-second
        // granularity, so a target of "now + 1" gives however much of this
        // second happens to be left — and if that is a few milliseconds, the
        // deadline passes before the Assert.False below and the test fails on
        // a correct engine. Crossing the boundary first buys the arming step a
        // near-full second regardless of when the test starts.
        var second = DateTimeOffset.Now.ToUnixTimeSeconds();
        while (DateTimeOffset.Now.ToUnixTimeSeconds() == second)
            System.Threading.Thread.Sleep(5);

        // One second out — the shortest transition $unixtime can express.
        var target = DateTimeOffset.Now.ToUnixTimeSeconds() + 1;
        Start("s", $"waiteval $unixtime >= {target}\necho PASSED\n");

        // Not yet: the deadline hasn't passed, and (pre-fix) never would.
        Pump();
        Assert.False(Echoed("PASSED"));

        var giveUp = DateTime.UtcNow.AddSeconds(5);
        while (!Echoed("PASSED") && DateTime.UtcNow < giveUp)
        {
            System.Threading.Thread.Sleep(25);
            _engine.Tick();
        }
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    // ── #428: release on a change, not on a condition already true ─────────
    // Genie 4 never checks a waiteval when the script reaches it; it re-checks
    // when a variable named in the condition changes (Script.cs EvalWaitEval,
    // TriggerVariableChanged). Scripts guard for that themselves:
    // `if (%depth > 1) then waiteval ((1 <= %depth) || …)` in automapper.cmd.

    [Fact]
    public void Waiteval_that_is_already_true_waits_for_a_change()
    {
        _engine.Globals["mana"] = "90";
        Start("s", "waiteval $mana > 80\necho PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        // A change that leaves it true releases it.
        _engine.Globals["mana"] = "95";
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    [Fact]
    public void Automapper_waits_for_the_move_in_flight_to_land()
    {
        // automapper.cmd MOVE.ICE: two moves out (depth 2), wait until one
        // lands (an action drops depth to 1), then carry on.
        Start("s",
            "action math depth subtract 1 when ^Obvious paths\n" +
            "var depth 2\n" +
            "var depthtimeout 0\n" +
            "eval depthtimeout $unixtime + 300\n" +
            "if (%depth > 1) then waiteval ((1 <= %depth) || ($unixtime >= %depthtimeout))\n" +
            "echo PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        _engine.OnGameLine("Obvious paths: east, northwest.");
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }

    [Fact]
    public void A_change_that_leaves_it_false_keeps_waiting()
    {
        _engine.Globals["mana"] = "50";
        Start("s", "waiteval $mana > 80\necho PASSED\n");
        Pump();
        _engine.Globals["mana"] = "60";
        Pump();
        Assert.False(Echoed("PASSED"));
    }

    [Fact]
    public void A_clock_ticking_does_not_release_a_condition_already_true()
    {
        // $unixtime changes every second; it is not a "change" (Genie 4's
        // clock variables are computed on read and never fire one).
        _engine.Globals["mana"] = "90";
        Start("s", "waiteval ($mana > 80) && ($unixtime > 0)\necho PASSED\n");

        var second = DateTimeOffset.Now.ToUnixTimeSeconds();
        var giveUp = DateTime.UtcNow.AddSeconds(3);
        while (DateTimeOffset.Now.ToUnixTimeSeconds() <= second + 1 && DateTime.UtcNow < giveUp)
        {
            System.Threading.Thread.Sleep(25);
            _engine.Tick();
        }
        Assert.False(Echoed("PASSED"));
    }

    [Fact]
    public void A_bare_variable_releases_on_any_change()
    {
        _engine.Globals["standing"] = "0";
        Start("s", "waiteval $standing\necho PASSED\n");
        Pump();
        Assert.False(Echoed("PASSED"));

        _engine.Globals["standing"] = "1";
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }
}
