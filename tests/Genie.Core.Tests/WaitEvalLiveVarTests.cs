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

    [Fact]
    public void Waiteval_that_is_already_true_falls_straight_through()
    {
        _engine.Globals["mana"] = "90";
        Start("s", "waiteval $mana > 80\necho PASSED\n");
        Pump();
        Assert.True(Echoed("PASSED"), string.Join(" | ", _echoed));
    }
}
