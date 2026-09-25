using System;
using System.Collections.Generic;
using System.Diagnostics;
using Genie.Core.Scripting;
using Genie.Core.Scripting.Js;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Deep-dive Phase 0 (docs/internal/GAME_THREAD_DESIGN.md §5): the
/// single-statement freeze classes must be time-bounded — a catastrophic
/// script regex hits the RegexSafety match-timeout instead of wedging the
/// pipeline forever, and a runaway <c>js</c>/<c>jscall</c> body hits the
/// wall-clock cap instead of burning seconds of statement budget.
/// </summary>
public class ScriptStallHardeningTests
{
    /// <summary>~2^40 backtracking steps without a timeout — effectively an
    /// infinite hang if the RegexSafety timeout wiring were missing.</summary>
    private const string CatastrophicPattern = "(a+)+$";
    private static readonly string NonMatchingInput = new string('a', 40) + "b";

    [Fact]
    public void Catastrophic_waitforre_pattern_times_out_instead_of_hanging()
    {
        var echoes = new List<string>();
        var scriptsDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "se_stall_" + Guid.NewGuid().ToString("N"));
        var engine = new ScriptEngine(scriptsDir, new TypeAheadSession(), _ => { }, echoes.Add);
        System.IO.File.WriteAllText(System.IO.Path.Combine(scriptsDir, "cata.cmd"),
            $"waitforre {CatastrophicPattern}\n");

        Assert.True(engine.TryStart("cata", Array.Empty<string>()));
        for (int i = 0; i < 5; i++) engine.Tick();   // reach the waitforre

        // Feed the pathological line: bounded by the 100 ms match-timeout —
        // well under a second; unbounded it would sit here for ~2^40 steps.
        var sw = Stopwatch.StartNew();
        engine.OnGameLine(NonMatchingInput);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"waitforre match took {sw.Elapsed} — the RegexSafety timeout is not applied");
        engine.StopAll();
    }

    [Fact]
    public void Js_wall_clock_cap_bounds_a_tight_loop()
    {
        var echoes = new List<string>();
        var ctx = new JsLibraryContext(
            getVar:    _ => "",
            setVar:    (_, _) => { },
            getGlobal: _ => "",
            setGlobal: (_, _) => { },
            echo:      echoes.Add,
            put:       _ => { });

        var sw = Stopwatch.StartNew();
        var result = ctx.Evaluate("(function(){ while(true){} })()");
        sw.Stop();

        Assert.Equal("", result);
        // The 250 ms wall-clock cap fires long before the 50M-statement cap
        // (which takes seconds). Allow generous slack for slow CI.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"js evaluation ran {sw.Elapsed} — the wall-clock cap is not applied");
        Assert.Contains(echoes, e => e.Contains("wall-clock"));
    }

    [Fact]
    public void Js_wall_clock_cap_leaves_normal_evaluation_alone()
    {
        var echoes = new List<string>();
        var ctx = new JsLibraryContext(
            getVar:    _ => "",
            setVar:    (_, _) => { },
            getGlobal: _ => "",
            setGlobal: (_, _) => { },
            echo:      echoes.Add,
            put:       _ => { });

        Assert.Equal("6", ctx.Evaluate("2 * 3"));
        Assert.Empty(echoes);
    }

    /// <summary>The prelude that defines getVar/setVar/getGlobal/setGlobal runs in
    /// the constructor with the wall-clock guard DISARMED. It used to inherit the
    /// guard's armed default (Jint re-baselines the 250 ms budget on every
    /// Execute), so a cold Jint on a loaded CI runner threw JsWallClockException
    /// out of the constructor and the .cmd script died at its first &lt;% %&gt;
    /// block with no output (ScriptInlineJsBlockTests.GlobalsBridgeWorksFromBlock,
    /// 2026-09-23). A clock that jumps ten seconds on every read makes every
    /// ARMED call time out at its first check, so a context that comes up with a
    /// working bridge proves the prelude ran unarmed — no slow machine needed.</summary>
    [Fact]
    public void Prelude_is_not_bounded_by_the_js_wall_clock_budget()
    {
        long clock = 0;
        var echoes = new List<string>();
        string? stored = null;
        var ctx = new JsLibraryContext(
            getVar:    n => n == "y" ? "seen" : null,
            setVar:    (n, v) => stored = n + "=" + v,
            getGlobal: _ => "",
            setGlobal: (_, _) => { },
            echo:      echoes.Add,
            put:       _ => { },
            clock:     () => clock += 10_000);

        // The guard is live on this clock: an armed js one-liner is cut off at
        // its very first constraint check…
        Assert.Equal("", ctx.Evaluate("(function(){ while(true){} })()"));
        Assert.Contains(echoes, e => e.Contains("wall-clock"));
        echoes.Clear();

        // …while an unarmed block, like the prelude before it, runs to the end
        // and still finds the bridge the prelude defined.
        Assert.True(ctx.ExecuteBlock("setVar('x', getVar('y'));"));
        Assert.Equal("x=seen", stored);
        Assert.Empty(echoes);
    }
}
