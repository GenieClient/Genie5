using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #247 (carried over from Genie 4 #47) — <c>$scriptlistactive</c> and
/// <c>$scriptlistpaused</c>, the pause-state halves of the existing
/// <c>$scriptlist</c>.
///
/// <para>They exist to be fed straight back into <c>#script</c>:
/// <c>#script pause $scriptlistactive</c> then
/// <c>#script resume $scriptlistpaused</c> is the pattern the original report
/// wanted, so the separator and the empty-set sentinel have to match
/// <c>$scriptlist</c> exactly or the composition breaks.</para>
/// </summary>
public class ScriptListVarsTests : IDisposable
{
    private readonly string _dir;

    public ScriptListVarsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_sl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ScriptEngine NewEngine() =>
        new(_dir, new TypeAheadSession(), sendCommand: _ => { }, echo: _ => { });

    /// <summary>Resolve a reserved var the way a running script would.</summary>
    private static string Resolve(ScriptEngine engine, string name) =>
        engine.ExpandGlobalVars("$" + name);

    /// <summary>
    /// Write a script that never finishes on its own and start it, so the
    /// engine has something running to report.
    ///
    /// <para>TryStart loads from DISK — its second argument is the script's
    /// ARGUMENTS, not its lines — so the file has to exist before the call.
    /// The pause keeps the instance alive across the pump instead of running to
    /// completion on the first tick.</para>
    /// </summary>
    private void Start(ScriptEngine engine, string name)
    {
        File.WriteAllLines(Path.Combine(_dir, name + ".cmd"),
            new[] { "loop:", "pause 30", "goto loop" });
        Assert.True(engine.TryStart(name, Array.Empty<string>()), $"{name} failed to start");
        Pump(engine);
    }

    private static void Pump(ScriptEngine engine)
    {
        for (int i = 0; i < 20; i++)
        {
            engine.Tick();
            engine.OnPrompt();
        }
    }

    // ── Nothing running ──────────────────────────────────────────────────────

    /// <summary>The "none" sentinel, matching $scriptlist. `#script` maps it to
    /// "act on nothing", which is the whole reason it has to be this exact
    /// string rather than an empty one.</summary>
    [Fact]
    public void With_nothing_running_both_lists_read_none()
    {
        var engine = NewEngine();

        Assert.Equal("none", Resolve(engine, "scriptlistactive"));
        Assert.Equal("none", Resolve(engine, "scriptlistpaused"));
    }

    // ── Running and paused ───────────────────────────────────────────────────

    [Fact]
    public void A_running_script_is_active_and_not_paused()
    {
        var engine = NewEngine();
        Start(engine, "hunt");

        Assert.Equal("hunt", Resolve(engine, "scriptlistactive"));
        Assert.Equal("none", Resolve(engine, "scriptlistpaused"));
    }

    [Fact]
    public void Pausing_moves_a_script_between_the_two_lists()
    {
        var engine = NewEngine();
        Start(engine, "hunt");
        engine.PauseScript("hunt");

        Assert.Equal("none", Resolve(engine, "scriptlistactive"));
        Assert.Equal("hunt", Resolve(engine, "scriptlistpaused"));

        engine.ResumeScript("hunt");

        Assert.Equal("hunt", Resolve(engine, "scriptlistactive"));
        Assert.Equal("none", Resolve(engine, "scriptlistpaused"));
    }

    /// <summary>The two lists partition the whole of $scriptlist — no script
    /// appears in both, and none goes missing.</summary>
    [Fact]
    public void The_two_lists_partition_scriptlist()
    {
        var engine = NewEngine();
        Start(engine, "hunt");
        Start(engine, "favors");
        Start(engine, "exptally");
        engine.PauseScript("favors");

        var all     = Split(Resolve(engine, "scriptlist"));
        var active  = Split(Resolve(engine, "scriptlistactive"));
        var paused  = Split(Resolve(engine, "scriptlistpaused"));

        Assert.Equal(new[] { "favors" }, paused);
        Assert.Empty(active.Intersect(paused));
        Assert.Equal(all.OrderBy(x => x), active.Concat(paused).OrderBy(x => x));
    }

    /// <summary>Pipe-separated, like $scriptlist — the separator #script splits
    /// on.</summary>
    [Fact]
    public void Multiple_scripts_are_pipe_separated()
    {
        var engine = NewEngine();
        Start(engine, "hunt");
        Start(engine, "favors");

        var active = Resolve(engine, "scriptlistactive");

        Assert.Contains('|', active);
        Assert.Equal(2, Split(active).Length);
    }

    /// <summary>A #var of the same name still shadows these, like every other
    /// computed reserved var.</summary>
    [Fact]
    public void A_user_variable_still_shadows_the_reserved_name()
    {
        var engine = NewEngine();
        Start(engine, "hunt");
        engine.UserVarLookup = n =>
            n.Equals("scriptlistactive", StringComparison.OrdinalIgnoreCase) ? "mine" : null;

        Assert.Equal("mine", Resolve(engine, "scriptlistactive"));
    }

    private static string[] Split(string list) =>
        list == "none" ? Array.Empty<string>()
                       : list.Split('|', StringSplitOptions.RemoveEmptyEntries);
}
