using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The #251 game-thread primitive (docs/internal/GAME_THREAD_DESIGN.md §3.1):
/// one dedicated thread, FIFO work queue, loop-serviced timers. FIFO order is
/// the ordering guarantee the whole pipeline design leans on, so it gets the
/// most attention here.
/// </summary>
public class GameLoopTests
{
    [Fact]
    public void Posted_work_runs_in_fifo_order_on_the_loop_thread()
    {
        using var loop = new GameLoop();
        var order = new List<int>();
        var threads = new HashSet<int>();
        using var done = new ManualResetEventSlim(false);

        for (int i = 0; i < 100; i++)
        {
            int n = i;
            loop.Post(() => { order.Add(n); threads.Add(Environment.CurrentManagedThreadId); });
        }
        loop.Post(() => done.Set());

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "loop never drained");
        Assert.Equal(100, order.Count);
        for (int i = 0; i < 100; i++) Assert.Equal(i, order[i]);
        Assert.Single(threads);   // every item ran on the one loop thread
        Assert.NotEqual(Environment.CurrentManagedThreadId, Assert.Single(threads));
    }

    [Fact]
    public void IsOnLoop_is_true_inside_work_and_false_outside()
    {
        using var loop = new GameLoop();
        bool? insideValue = null;
        using var done = new ManualResetEventSlim(false);
        loop.Post(() => { insideValue = loop.IsOnLoop; done.Set(); });
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(insideValue);
        Assert.False(loop.IsOnLoop);
    }

    [Fact]
    public void PostDelayed_fires_after_the_delay_and_serialized_with_posted_work()
    {
        using var loop = new GameLoop();
        var order = new List<string>();
        using var done = new ManualResetEventSlim(false);

        loop.PostDelayed(TimeSpan.FromMilliseconds(50), () => { order.Add("timer"); done.Set(); });
        loop.Post(() => order.Add("posted"));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "timer never fired");
        // The immediate post ran first (the timer was 50ms out), and both on the
        // same serialized loop — no torn interleaving is possible by construction.
        Assert.Equal(new[] { "posted", "timer" }, order);
    }

    [Fact]
    public void PostDelayed_cancel_prevents_the_callback()
    {
        using var loop = new GameLoop();
        int fired = 0;
        var handle = loop.PostDelayed(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref fired));
        handle.Dispose();
        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref fired));
    }

    [Fact]
    public void PostRepeating_fires_repeatedly_until_disposed()
    {
        using var loop = new GameLoop();
        int fired = 0;
        var handle = loop.PostRepeating(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref fired));

        // Wait until it has fired at least 3 times (bounded).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref fired) < 3 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(Volatile.Read(ref fired) >= 3, "repeating timer did not fire repeatedly");

        handle.Dispose();
        Thread.Sleep(100);
        int after = Volatile.Read(ref fired);
        Thread.Sleep(150);
        Assert.Equal(after, Volatile.Read(ref fired));   // no more firings after dispose
    }

    [Fact]
    public void A_throwing_item_is_contained_and_the_loop_keeps_running()
    {
        using var loop = new GameLoop();
        Exception? seen = null;
        loop.ItemFailed = ex => seen = ex;
        using var done = new ManualResetEventSlim(false);

        loop.Post(() => throw new InvalidOperationException("boom"));
        loop.Post(() => done.Set());

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "loop died after a work-item exception");
        Assert.IsType<InvalidOperationException>(seen);
    }

    [Fact]
    public void Shutdown_drains_already_queued_work()
    {
        var loop = new GameLoop();
        int ran = 0;
        for (int i = 0; i < 50; i++) loop.Post(() => Interlocked.Increment(ref ran));
        loop.Shutdown(TimeSpan.FromSeconds(5));
        Assert.Equal(50, Volatile.Read(ref ran));

        // Post after shutdown is a silent no-op, not an exception.
        loop.Post(() => Interlocked.Increment(ref ran));
        Assert.Equal(50, Volatile.Read(ref ran));
        loop.Dispose();
    }

    [Fact]
    public void Watchdog_reports_a_wedged_item_and_the_loop_recovers()
    {
        using var loop = new GameLoop();
        using var stallSeen = new ManualResetEventSlim(false);
        using var release   = new ManualResetEventSlim(false);
        loop.Stalled += _ => stallSeen.Set();

        // Wedge the loop: the item blocks until we release it.
        loop.Post(() => release.Wait(TimeSpan.FromSeconds(15)));

        // StallThreshold is 5s, polled at 1s — allow up to 8s.
        Assert.True(stallSeen.Wait(TimeSpan.FromSeconds(8)),
            "watchdog never reported the wedged item");

        // Un-wedge; queued work must then run (the loop survived).
        release.Set();
        using var done = new ManualResetEventSlim(false);
        loop.Post(() => done.Set());
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "loop did not recover after the wedge");
    }

    [Fact]
    public async Task Concurrent_posters_all_get_their_work_run()
    {
        using var loop = new GameLoop();
        int ran = 0;
        var posters = new Task[8];
        for (int t = 0; t < posters.Length; t++)
            posters[t] = Task.Run(() =>
            {
                for (int i = 0; i < 250; i++) loop.Post(() => Interlocked.Increment(ref ran));
            });
        await Task.WhenAll(posters);

        using var done = new ManualResetEventSlim(false);
        loop.Post(() => done.Set());
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(8 * 250, Volatile.Read(ref ran));
    }
}
