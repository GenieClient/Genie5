using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Thread-safety guards for <see cref="ScriptEngine"/> (issue #242).
///
/// <para>The engine is driven from two independent sources — the thread that
/// starts a script (<c>TryStart</c>) and the thread that delivers game text
/// (<c>OnGameLine</c> → <c>Tick</c>). Its instance list is a plain
/// <see cref="List{T}"/> that both paths add to, remove from, and enumerate, so
/// before the fix an interleaving threw <i>"Collection was modified;
/// enumeration operation may not execute"</i>. The desktop app happened not to
/// hit it only because the read loop's continuations were marshalled back to
/// the UI thread by an omitted <c>ConfigureAwait(false)</c> — an accident, not
/// a design, and one a routine cleanup would have removed.</para>
///
/// <para>These tests drive the entry points from several threads at once. They
/// are necessarily probabilistic — a race is a race — so each hammers hard
/// enough that the pre-fix code failed essentially every run, and asserts that
/// <b>no</b> exception escaped. They also assert the engine is still
/// functionally intact afterwards, so a fix that "passes" by deadlocking or by
/// swallowing work is caught too.</para>
/// </summary>
public class ScriptEngineThreadSafetyTests
{
    /// <summary>A script long enough to stay resident across many ticks, with a
    /// pause so it never runs to completion inside a single tick budget.</summary>
    private const string LoopScript = """
        LOOP:
        pause 0.01
        goto LOOP
        """;

    private static (ScriptEngine engine, string dir) NewEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 8; i++)
            File.WriteAllText(Path.Combine(dir, $"s{i}.cmd"), LoopScript);

        var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: _ => { });
        return (engine, dir);
    }

    /// <summary>Run <paramref name="workers"/> concurrently for
    /// <paramref name="ms"/>, collecting anything they throw. Every worker is
    /// released from the same barrier so they genuinely overlap.</summary>
    private static List<Exception> Hammer(int ms, params Action<CancellationToken>[] workers)
    {
        var errors = new List<Exception>();
        var gate   = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var tasks = new Task[workers.Length];
        for (int i = 0; i < workers.Length; i++)
        {
            var work = workers[i];
            tasks[i] = Task.Factory.StartNew(() =>
            {
                gate.Wait();
                try { work(cts.Token); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }, TaskCreationOptions.LongRunning);
        }

        gate.Set();
        Thread.Sleep(ms);
        cts.Cancel();

        // Generous: a deadlocked engine fails here rather than hanging the suite.
        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)),
                    "workers did not finish — the engine is likely deadlocked");
        return errors;
    }

    // ── The reported crash ───────────────────────────────────────────────────

    /// <summary>
    /// The issue's repro, distilled: game lines arriving while scripts start and
    /// stop. Pre-fix this threw <c>InvalidOperationException</c> ("Collection was
    /// modified") from the instance-list enumeration in <c>OnGameLine</c>/<c>Tick</c>.
    /// </summary>
    [Fact]
    public void ConcurrentGameLinesAndScriptStarts_DoNotThrow()
    {
        var (engine, dir) = NewEngine();
        try
        {
            var errors = Hammer(1500,
                ct =>                                   // the "socket" thread
                {
                    int n = 0;
                    while (!ct.IsCancellationRequested)
                        engine.OnGameLine($"You see a passing line {n++}.");
                },
                ct =>                                   // the "socket" thread's prompts
                {
                    while (!ct.IsCancellationRequested) engine.OnPrompt();
                },
                ct =>                                   // the "UI" thread starting scripts
                {
                    int n = 0;
                    while (!ct.IsCancellationRequested)
                        engine.TryStart($"s{n++ % 8}", new List<string>());
                },
                ct =>                                   // the heartbeat tick
                {
                    while (!ct.IsCancellationRequested) engine.Tick();
                });

            Assert.Empty(errors);
        }
        finally { Cleanup(engine, dir); }
    }

    /// <summary>
    /// Lifecycle churn from a second thread — <c>Stop</c> / <c>StopAll</c> /
    /// pause / resume — against a live line feed. These mutate the same list
    /// <c>Tick</c> walks, so they need the same serialization the two entry
    /// points in the issue title do.
    /// </summary>
    [Fact]
    public void ConcurrentLifecycleChurn_DoesNotThrow()
    {
        var (engine, dir) = NewEngine();
        try
        {
            var errors = Hammer(1500,
                ct =>
                {
                    int n = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        engine.OnGameLine($"Ambient noise {n++}.");
                        engine.Tick();
                    }
                },
                ct =>
                {
                    int n = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        engine.TryStart($"s{n % 8}", new List<string>());
                        engine.PauseScript($"s{n % 8}");
                        engine.ResumeScript($"s{n % 8}");
                        engine.Stop($"s{n % 8}");
                        if (n % 17 == 0) engine.StopAll();
                        n++;
                    }
                },
                ct =>
                {
                    while (!ct.IsCancellationRequested) { engine.PauseAll(); engine.ResumeAll(); }
                });

            Assert.Empty(errors);
        }
        finally { Cleanup(engine, dir); }
    }

    /// <summary>
    /// The read-only surfaces the UI polls on a timer — status/vars/trace dumps
    /// and the <c>Instances</c> snapshot — enumerate the same list. Handing out
    /// the live list was itself a defect: no lock inside the engine can protect
    /// an enumeration a caller runs outside it, which is why
    /// <see cref="ScriptEngine.Instances"/> now returns a copy.
    /// </summary>
    [Fact]
    public void ConcurrentStatusReads_DoNotThrow()
    {
        var (engine, dir) = NewEngine();
        try
        {
            var errors = Hammer(1500,
                ct =>
                {
                    int n = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        engine.TryStart($"s{n % 8}", new List<string>());
                        engine.OnGameLine($"line {n}");
                        if (n % 5 == 0) engine.Stop($"s{n % 8}");
                        n++;
                    }
                },
                ct =>                                   // the Scripts panel poll
                {
                    while (!ct.IsCancellationRequested)
                    {
                        foreach (var inst in engine.Instances) _ = inst.Name;
                        _ = engine.AnyRunning;
                        _ = engine.RunningScriptNames();
                        _ = engine.GetStatuses();
                    }
                },
                ct =>                                   // #script status / #var / #trace
                {
                    while (!ct.IsCancellationRequested)
                    {
                        _ = engine.StatusLines(null);
                        _ = engine.VarsLines(null, string.Empty);
                        _ = engine.TraceDumpLines(null);
                    }
                });

            Assert.Empty(errors);
        }
        finally { Cleanup(engine, dir); }
    }

    // ── The fix must not break the engine ────────────────────────────────────

    /// <summary>
    /// A guard against passing the tests above by breaking the engine: after the
    /// concurrent churn, a plain single-threaded start still runs to completion
    /// and echoes. Also catches a lock left held on some path.
    /// </summary>
    [Fact]
    public void EngineStillWorksAfterConcurrentUse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var echoed = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(dir, "loop.cmd"), LoopScript);
            File.WriteAllText(Path.Combine(dir, "ok.cmd"),   "echo DONE-OK");

            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { },
                                          echo: l => { lock (echoed) echoed.Add(l); });

            var errors = Hammer(600,
                ct => { while (!ct.IsCancellationRequested) engine.OnGameLine("noise"); },
                ct => { while (!ct.IsCancellationRequested) engine.TryStart("loop", new List<string>()); },
                ct => { while (!ct.IsCancellationRequested) engine.Tick(); });
            Assert.Empty(errors);

            engine.StopAll();
            for (int i = 0; i < 50; i++) engine.Tick();

            Assert.True(engine.TryStart("ok", new List<string>()), "engine refused a fresh start");
            for (int i = 0; i < 200; i++) engine.Tick();

            lock (echoed)
                Assert.Contains(echoed, l => l.Contains("DONE-OK", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Re-entrancy: the engine calls back out under the lock (the
    /// <c>ScriptStarted</c> handler in the script bar synchronously calls
    /// <c>IsJavaScript</c> and <c>GetTrace</c>), so the guard must be re-entrant.
    /// A <c>SemaphoreSlim</c> or any non-recursive primitive would deadlock here
    /// — which is exactly how the App is wired, so this is not hypothetical.
    /// </summary>
    [Fact]
    public async Task ReentrantCallbackFromScriptStarted_DoesNotDeadlock()
    {
        var (engine, dir) = NewEngine();
        try
        {
            int seen = 0;
            engine.ScriptStarted += name =>
            {
                // Same shape as ScriptBarViewModel: re-enter the engine from the
                // event, on the engine's own thread, while it holds the lock.
                _ = engine.IsJavaScript(name);
                _ = engine.GetTrace(name);
                _ = engine.Instances;
                Interlocked.Increment(ref seen);
            };

            var done    = Task.Run(() => engine.TryStart("s0", new List<string>()));
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));
            Assert.Same(done, await Task.WhenAny(done, timeout));
            Assert.Equal(1, seen);
        }
        finally { Cleanup(engine, dir); }
    }

    private static void Cleanup(ScriptEngine engine, string dir)
    {
        try { engine.StopAll(); } catch { /* best effort */ }
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
