using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Pins the Jint <c>Constraint</c>-backed guards that stand between a bad JS
/// script and the client: the js/jscall wall-clock budget, <c>LimitRecursion</c>,
/// and the runaway guard's two triggers (statements and allocation).
///
/// These exist for the dependency bumps (#289). Compiling proves the constraint
/// API still EXISTS after a Jint upgrade; only running proves it still FIRES —
/// a custom <c>Constraint</c> whose <c>Check()</c> quietly stopped being called
/// between statements would leave a runaway script pegging a thread with nothing
/// left to notice it, and every one of these tests would still compile.
///
/// The three at the bottom additionally pin #330: budgets that are anchored to
/// the yield seam rather than to the script's whole lifetime.
/// </summary>
public class JsEngineGuardTests
{
    private sealed class Sink
    {
        private readonly List<string> _lines = new();
        public void Add(string l) { lock (_lines) _lines.Add(l); }
        public List<string> Snapshot() { lock (_lines) return new List<string>(_lines); }
        public bool Any(Func<string, bool> p) { lock (_lines) return _lines.Exists(l => p(l)); }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_jsguard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Start a <c>.js</c> script and wait until one of the echoes matches
    /// <paramref name="done"/>. Returns everything echoed.</summary>
    private static List<string> RunJs(string source, Func<string, bool> done, int timeoutSeconds = 120)
    {
        var sink = new Sink();
        var dir  = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.js"), source);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: sink.Add);
            Assert.True(engine.TryStart("t", new List<string>()));

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline && !sink.Any(done)) Thread.Sleep(25);

            Assert.True(sink.Any(done),
                        "script never reached a terminal state; echoed:\n  " +
                        string.Join("\n  ", sink.Snapshot()));
            return sink.Snapshot();
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── wall-clock guard (js / jscall) ───────────────────────────────────────

    /// <summary>An infinite loop inside a synchronous <c>js</c> body must be cut
    /// off by the 250 ms wall-clock budget and the .cmd script must carry on.
    /// This is the load-bearing proof for the whole family: the wall-clock guard
    /// is a custom <see cref="Jint.Constraint"/> and only trips if Jint still
    /// calls <c>Check()</c> between statements.</summary>
    [Fact]
    public void JsExpression_InfiniteLoop_IsCutOffByTheWallClockBudget()
    {
        var sink = new Sink();
        var dir  = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                "js (function(){ while(true){} })()\n" +
                "echo T:survived\n");

            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: sink.Add);
            var sw = Stopwatch.StartNew();
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 50; i++) engine.Tick();
            sw.Stop();

            Assert.Contains(sink.Snapshot(), l => l.Contains("wall-clock budget"));
            Assert.Contains(sink.Snapshot(), l => l == "T:survived");
            // The guard bounds one call at 250 ms; anything near this ceiling
            // means it stopped bounding and the tick loop was wedged.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── LimitRecursion(128) ──────────────────────────────────────────────────

    /// <summary>Runaway recursion must surface as a reported script error, never
    /// as a CLR stack overflow (which is uncatchable and kills the process).
    /// A shallow call in the same context still has to work, so the cap is a cap
    /// and not a blanket ban on recursion.</summary>
    [Fact]
    public void DeepRecursion_IsReported_AndShallowRecursionStillWorks()
    {
        var sink = new Sink();
        var dir  = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                "jscall shallow (function f(n){ return n <= 0 ? 0 : f(n-1)+1; })(100)\n" +
                "echo T:shallow=%shallow\n" +
                "jscall deep (function f(n){ return n <= 0 ? 0 : f(n-1)+1; })(5000)\n" +
                "echo T:survived\n");

            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: sink.Add);
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 50; i++) engine.Tick();

            var lines = sink.Snapshot();
            Assert.Contains(lines, l => l == "T:shallow=100");
            Assert.Contains(lines, l => l == "T:survived");
            // The recursion-limit exception type has moved between Jint majors;
            // what matters is that the call is refused and reported, not which
            // exception carried it.
            Assert.Contains(lines, l => l.StartsWith("[script] js:", StringComparison.Ordinal)
                                     && (l.Contains("error") || l.Contains("aborted")));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── the runaway guard's two triggers, both anchored to the yield seam ────

    /// <summary>#330. A script that allocates steadily but YIELDS must keep
    /// running. Under the old lifetime memory cap this died around iteration 11
    /// — not for holding anything (each buffer is dropped immediately) but for
    /// having allocated across its life, which every hours-long hunt loop does.
    /// 100 iterations here is ~10x past where that cap used to bite.</summary>
    [Fact]
    public void JsScript_ThatAllocatesAndYields_IsNotKilledByALifetimeBudget()
    {
        var lines = RunJs(
            "for (var i = 0; i < 100; i++) {\n" +
            "  var a = new Array(250000);\n" +
            "  for (var j = 0; j < 250000; j++) a[j] = j;\n" +
            "  a = null;\n" +
            "  genie.echo('tick ' + i);\n" +   // the yield: resets both budgets
            "}\n" +
            "genie.echo('DONE');\n",
            l => l.Contains("DONE") || l.Contains("aborted"));

        Assert.DoesNotContain(lines, l => l.Contains("aborted"));
        Assert.Contains(lines, l => l.Contains("DONE"));
        Assert.Contains(lines, l => l.Contains("tick 99"));
    }

    /// <summary>#330. The diagnostic half: a tight integer loop is a runaway loop
    /// and must say so. It allocates only because Jint boxes each numeric result,
    /// so under the old lifetime cap it reported "memory limit (128 MB) exceeded"
    /// — pointing the reader at memory the script does not use, while the advice
    /// that would actually fix it (yield inside the loop) never printed. The
    /// statement trigger alone could never catch this: the loop dies around 5M
    /// iterations and that trigger is armed at 200M.</summary>
    [Fact]
    public void JsScript_TightIntegerLoop_ReportsARunaway_NotAMemoryLimit()
    {
        var lines = RunJs("var x = 0;\nwhile (true) { x++; }\n",
                          l => l.Contains("aborted"));

        var abort = lines.Find(l => l.Contains("aborted"))!;
        Assert.Contains("runaway loop", abort);
        Assert.Contains("genie.pause/waitFor", abort);
        Assert.DoesNotContain("memory limit", abort);
    }

    /// <summary>A genuine memory bomb never yields, so it never gets a fresh
    /// budget and is still stopped — that is what makes anchoring the budget to
    /// the yield seam safe rather than a hole.</summary>
    [Fact]
    public void JsScript_MemoryBomb_IsStillAborted()
    {
        var lines = RunJs("var s = 'x';\nwhile (true) { s += s; }\n",
                          l => l.Contains("aborted"));

        var abort = lines.Find(l => l.Contains("aborted"))!;
        Assert.Contains("runaway loop", abort);
        Assert.Contains("allocated 128 MB", abort);
    }

    /// <summary>The synchronous js/jscall path was never affected by #330 and must
    /// stay that way: its engine re-baselines per call (Jint resets constraints at
    /// the top of every Evaluate), so repeated allocating calls do not accumulate.
    /// 100 calls here allocate several times the 128 MB cap between them.</summary>
    [Fact]
    public void JsCall_AllocationDoesNotAccumulateAcrossCalls()
    {
        var sink = new Sink();
        var dir  = NewDir();
        try
        {
            var cmd = new StringBuilder();
            for (int i = 0; i < 100; i++)
                cmd.AppendLine("jscall n (function(){ var a = new Array(100000); " +
                               "for (var j = 0; j < 100000; j++) a[j] = j; return a.length; })()");
            cmd.AppendLine("echo T:n=%n");

            File.WriteAllText(Path.Combine(dir, "t.cmd"), cmd.ToString());
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: sink.Add);
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 400; i++) engine.Tick();

            var lines = sink.Snapshot();
            // The last call has to return a real value, not "" from a failed one.
            Assert.Contains(lines, l => l == "T:n=100000");
            Assert.DoesNotContain(lines, l => l.Contains("memory limit"));
            Assert.DoesNotContain(lines, l => l.Contains("runaway"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
