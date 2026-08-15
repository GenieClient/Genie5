using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity for the script <c>timer</c> command and its <c>%t</c>
/// variable (G4 <c>Script.cs</c> <c>EvalTimer</c>:2980 + the <c>@timer@</c>
/// live substitution at :2392).
///
/// <para>The verb shipped in Genie 5 but diverged from Genie 4 four ways, all
/// of which fail SILENTLY in community scripts:</para>
/// <list type="bullet">
///   <item>the value was readable only as <c>%timer</c>; G4 names it
///         <c>%t</c>, so every ported script read nothing;</item>
///   <item><c>timer start</c> after a stop restarted at zero instead of
///         RESUMING from the retained elapsed;</item>
///   <item><c>timer stop</c> discarded the elapsed, so the standard
///         <c>timer stop</c> / <c>echo %t</c> idiom read <c>0</c>;</item>
///   <item><c>timer setstart &lt;datetime&gt;</c> was rejected outright.</item>
/// </list>
///
/// <para>Elapsed is driven with <c>timer setstart</c> against a fixed past
/// date rather than a sleep, so the assertions are deterministic: a correct
/// implementation reports a large elapsed, and every one of the old bugs
/// collapses it to ~0.</para>
/// </summary>
public class ScriptTimerParityTests
{
    /// <summary>A baseline far enough in the past that any retained/resumed
    /// reading is unmistakably non-zero.</summary>
    private const string LongAgo = "2020-01-01 00:00:00";

    private static List<string> RunFixture(string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_timer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 400; i++) engine.Tick();
            return echoed.FindAll(l => l.StartsWith("T:"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>The echoed "T:&lt;number&gt;" line as a double.</summary>
    private static double Value(string line) =>
        double.Parse(line.Substring(2), System.Globalization.CultureInfo.InvariantCulture);

    // ── %t — the Genie 4 name ────────────────────────────────────────────────

    [Fact]
    public void Percent_t_reads_the_running_timer()
    {
        // The whole point of the fix: G4 scripts say %t, not %timer.
        var outp = RunFixture($"timer setstart {LongAgo}\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"expected a large elapsed, got '{outp[0]}'");
    }

    [Fact]
    public void Percent_timer_alias_still_reads_the_same_value()
    {
        // The G5 name shipped first and stays supported — same underlying state.
        var outp = RunFixture($"timer setstart {LongAgo}\necho T:%t\necho T:%timer\n");

        Assert.Equal(2, outp.Count);
        Assert.True(Math.Abs(Value(outp[0]) - Value(outp[1])) < 5,
                    $"%t and %timer disagree: '{outp[0]}' vs '{outp[1]}'");
    }

    [Fact]
    public void Percent_t_is_case_insensitive()
    {
        var outp = RunFixture($"timer setstart {LongAgo}\necho T:%T\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"expected a large elapsed, got '{outp[0]}'");
    }

    // ── stop retains the elapsed ─────────────────────────────────────────────

    [Fact]
    public void Stop_retains_the_final_elapsed()
    {
        // G4's `timer stop` / `echo %t` idiom. The old G5 nulled the baseline,
        // so this read 0.
        var outp = RunFixture($"timer setstart {LongAgo}\ntimer stop\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"stop discarded the elapsed: '{outp[0]}'");
    }

    [Fact]
    public void Stop_while_already_stopped_reads_zero()
    {
        var outp = RunFixture("timer stop\necho T:%t\n");

        Assert.Single(outp);
        Assert.Equal(0d, Value(outp[0]));
    }

    // ── start resumes rather than restarting ─────────────────────────────────

    [Fact]
    public void Start_after_stop_resumes_from_the_retained_elapsed()
    {
        // G4 back-dates the new start by the retained elapsed. The old G5 reset
        // the baseline to now, silently losing everything counted so far.
        var outp = RunFixture($"timer setstart {LongAgo}\ntimer stop\ntimer start\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"start did not resume: '{outp[0]}'");
    }

    [Fact]
    public void Start_while_running_does_not_restart_the_clock()
    {
        var outp = RunFixture($"timer setstart {LongAgo}\ntimer start\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"a redundant start reset the clock: '{outp[0]}'");
    }

    // ── clear ────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_zeroes_both_the_baseline_and_the_retained_value()
    {
        var outp = RunFixture($"timer setstart {LongAgo}\ntimer stop\ntimer clear\necho T:%t\n");

        Assert.Single(outp);
        Assert.Equal(0d, Value(outp[0]));
    }

    [Fact]
    public void Clear_then_start_counts_from_zero_again()
    {
        // Distinguishes clear from stop: after a clear there is nothing to
        // resume, so the restarted timer begins at zero.
        var outp = RunFixture($"timer setstart {LongAgo}\ntimer stop\ntimer clear\n" +
                              "timer start\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) < 5, $"clear left a value to resume from: '{outp[0]}'");
    }

    // ── setstart ─────────────────────────────────────────────────────────────

    [Fact]
    public void Setstart_with_an_invalid_datetime_warns_and_leaves_the_timer_alone()
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_timer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                              "timer setstart not-a-date\necho T:%t\n");
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 400; i++) engine.Tick();

            Assert.Contains(echoed, l => l.Contains("invalid datetime"));
            Assert.Contains(echoed, l => l == "T:0");   // timer untouched
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── %t must not hijack a script's own variable ───────────────────────────

    [Fact]
    public void A_stored_local_named_t_shadows_the_timer()
    {
        // `t` is a common loop/temp name. In G4 the timer writes into the same
        // local list, so an explicit assignment simply wins.
        var outp = RunFixture($"timer setstart {LongAgo}\nvar t hello\necho T:%t\n");

        Assert.Single(outp);
        Assert.Equal("T:hello", outp[0]);
    }

    [Fact]
    public void A_timer_command_takes_the_name_back_from_a_stored_local()
    {
        // ...and the next timer command reclaims it — G4's last-write-wins.
        var outp = RunFixture($"var t hello\ntimer setstart {LongAgo}\necho T:%t\n");

        Assert.Single(outp);
        Assert.True(Value(outp[0]) > 1000, $"timer did not reclaim %t: '{outp[0]}'");
    }
}
