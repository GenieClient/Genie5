using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Pins the Jint <c>Constraint</c>-backed guards that stand between a bad JS
/// script and the client: the js/jscall wall-clock budget, <c>LimitRecursion</c>
/// and <c>LimitMemory</c>.
///
/// These exist for the dependency bumps (#289). Compiling proves the constraint
/// API still EXISTS after a Jint upgrade; only running proves it still FIRES —
/// a custom <c>Constraint</c> whose <c>Check()</c> quietly stopped being called
/// between statements would leave a runaway script pegging a thread with nothing
/// left to notice it, and every one of these tests would still compile.
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

    // ── LimitMemory(128 MB) on the threaded .js runtime ──────────────────────

    /// <summary>A memory bomb in a standalone <c>.js</c> script must be aborted by
    /// the engine's memory cap and reported, not left to exhaust the process.
    /// The threaded runtime has no wall-clock guard (a .js script is meant to run
    /// for hours), so this isolates <c>LimitMemory</c>.</summary>
    [Fact]
    public void JsScript_MemoryBomb_IsAbortedByTheMemoryLimit()
    {
        var sink = new Sink();
        var dir  = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bomb.js"),
                "var s = 'x';\nwhile (true) { s += s; }\n");

            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: sink.Add);
            Assert.True(engine.TryStart("bomb", new List<string>()));

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline &&
                   !sink.Any(l => l.Contains("memory limit")))
                Thread.Sleep(50);

            Assert.Contains(sink.Snapshot(), l => l.Contains("memory limit (128 MB) exceeded"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
