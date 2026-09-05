using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Genie.Core.Connection;
using Genie.Core.Events;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Per-line pipeline fault containment (2026-08-31 stability review).
///
/// <para>The pipeline runs inline on one thread with every consumer chained off two
/// Subjects, and <c>Subject.OnNext</c> does not isolate its subscribers: the first to
/// throw aborts the walk and every later subscriber silently misses the value. On the
/// game-event stream the UI relay subscribes last, so one throwing consumer stopped
/// the player's game text with no visible error; with the game loop off the same throw
/// escaped into <c>ReadLoopAsync</c> and disconnected a live session outright.</para>
/// </summary>
public class PipelineContainmentTests
{
    // ── the containment primitive ────────────────────────────────────────────

    [Fact]
    public void A_throwing_subscriber_does_not_starve_the_ones_behind_it()
    {
        var src = new Subject<string>();
        var faults = new List<Exception>();
        var contained = src.Contained(faults.Add);

        var late = new List<string>();
        contained.Subscribe(_ => throw new InvalidOperationException("bad consumer"));
        contained.Subscribe(late.Add);

        src.OnNext("a line");

        Assert.Equal(new[] { "a line" }, late);   // the later subscriber still ran
        Assert.Single(faults);
        Assert.IsType<InvalidOperationException>(faults[0]);
    }

    [Fact]
    public void The_stream_keeps_delivering_after_a_fault()
    {
        var src = new Subject<int>();
        var faults = new List<Exception>();
        var contained = src.Contained(faults.Add);

        var seen = new List<int>();
        contained.Subscribe(v => { if (v == 2) throw new InvalidOperationException("boom"); seen.Add(v); });

        src.OnNext(1);
        src.OnNext(2);   // this one faults for this subscriber
        src.OnNext(3);

        Assert.Equal(new[] { 1, 3 }, seen);   // only the faulting value is lost
        Assert.Single(faults);
    }

    [Fact]
    public void A_faulting_subscriber_never_reaches_the_publisher()
    {
        // The property that matters at the connection boundary: OnNext must return
        // normally, because its caller is the read loop.
        var src = new Subject<string>();
        var contained = src.Contained(_ => { });
        contained.Subscribe(_ => throw new InvalidOperationException("boom"));

        var ex = Record.Exception(() => src.OnNext("x"));

        Assert.Null(ex);
    }

    [Fact]
    public void A_throwing_fault_reporter_is_itself_contained()
    {
        var src = new Subject<string>();
        var contained = src.Contained(_ => throw new InvalidOperationException("reporter is broken too"));
        contained.Subscribe(_ => throw new InvalidOperationException("consumer"));

        var ex = Record.Exception(() => src.OnNext("x"));

        Assert.Null(ex);
    }

    [Fact]
    public void Operators_composed_on_a_contained_stream_are_still_guarded()
    {
        // The connect-ready subscription is `events.OfType<T>().Take(1)`; a throw in
        // its handler has to be caught through the operator chain, not just off a
        // direct Subscribe.
        var src = new Subject<object>();
        var faults = new List<Exception>();
        var contained = src.Contained(faults.Add);

        contained.OfType<string>().Take(1).Subscribe(_ => throw new InvalidOperationException("ready handler"));

        var ex = Record.Exception(() => src.OnNext("ready"));

        Assert.Null(ex);
        Assert.Single(faults);
    }

    // ── wired end-to-end against a live connection ───────────────────────────

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitFor(Func<bool> done, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(15);
    }

    /// <summary>A consumer that throws on every event must not cost the session.
    /// Runs with the game loop OFF — the configuration where an escaping exception
    /// reached <c>ReadLoopAsync</c> and emitted Disconnected, and the one every other
    /// unit test uses.</summary>
    [Fact]
    public async Task A_throwing_event_consumer_does_not_disconnect_the_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_contain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var replay = Path.Combine(dir, "replay.xml");
        File.WriteAllText(replay,
            "<settingsInfo/>\n" +
            "<pushStream id='main'/>The sun rises over Crossing.\n" +
            "<prompt time='1756000000'>&gt;</prompt>\n");

        var port = FreeTcpPort();
        await using var server = new DevReplayServer(replay, port: port, speed: 0.0, hangAfterStream: true);
        server.Start();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);

            var disconnects = 0;
            core.ConnectionState.Subscribe(e =>
            {
                if (e.Kind == ConnectionEventKind.Disconnected) disconnects++;
            }, static _ => { });

            // A UI-style consumer that throws on everything it is handed.
            var throwsSeen = 0;
            core.GameEvents.Subscribe(_ =>
            {
                throwsSeen++;
                throw new InvalidOperationException("consumer blew up");
            }, static _ => { });

            await core.ConnectAsync(new ConnectionConfig
            {
                Mode = ConnectionMode.DevReplay, LichProxyHost = "127.0.0.1", LichProxyPort = port,
            });

            await WaitFor(() => throwsSeen > 0);

            Assert.True(throwsSeen > 0, "the throwing consumer never ran, so nothing was contained");
            Assert.Equal(0, disconnects);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
