using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity for <c>delay</c> vs roundtime (docs/scripting-engine.md,
/// wiki/Scripting-Reference.md): <c>delay N</c> is the webbed/stunned sleep —
/// when its timer expires the script resumes even while roundtime is active,
/// and keeps executing until it next blocks. G4 reference: Script.cs:1541
/// skips TickScript's RT early-return while the state is <c>delayed</c>, and
/// the resumed RunScript burst has no per-line RT checks, so the bypass lasts
/// until the script hits another blocking statement. <c>pause</c> expires as
/// a pure timer but the next statement stays RT-gated.
///
/// Regression: the bypass used to be dead code — the tick loop's unblock
/// reset PauseMode to None before the RT gate ever looked at it, so
/// <c>delay</c> behaved identically to <c>pause</c> and a webbed script's
/// sleep loop hung until RT drained.
/// </summary>
public class DelayRoundtimeBypassTests
{
    private sealed class Harness : IDisposable
    {
        public readonly ScriptEngine Engine;
        public readonly List<string> Sent = new();
        public readonly List<string> Echoed = new();
        public bool InRt;
        private readonly string _dir;

        public Harness(string script)
        {
            _dir = Path.Combine(Path.GetTempPath(), "gc_delayrt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "t.cmd"), script);
            // The echo sink also carries engine chatter ("[script] t started",
            // extension load lines) — capture only this script's own `echo T:…`
            // output, minus the marker.
            Engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                      sendCommand: Sent.Add,
                                      echo: l => { if (l.StartsWith("T:")) Echoed.Add(l[2..]); })
            {
                InRoundtime = () => InRt,
            };
            Assert.True(Engine.TryStart("t", new List<string>()));
        }

        public void Pump(int ticks = 10) { for (int i = 0; i < ticks; i++) Engine.Tick(); }

        /// <summary>Sleep past a 0.05s script timer, with slack for CI.</summary>
        public static void LetTimerExpire() => Thread.Sleep(150);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void Delay_expiry_resumes_the_script_while_in_roundtime()
    {
        // The documented webbed-sleep shape: each delay expiry must fire the
        // following statements even though RT never clears.
        using var h = new Harness(
            "echo T:start\n" +
            "delay 0.05\n" +
            "echo T:one\n" +
            "delay 0.05\n" +
            "echo T:two\n");

        h.Pump();                       // runs "start", blocks on the first delay
        Assert.Equal(new[] { "start" }, h.Echoed);

        h.InRt = true;                  // webbed — RT never drains from here on
        Harness.LetTimerExpire();
        h.Pump();
        Assert.Equal(new[] { "start", "one" }, h.Echoed);

        Harness.LetTimerExpire();
        h.Pump();
        Assert.Equal(new[] { "start", "one", "two" }, h.Echoed);
    }

    [Fact]
    public void Delay_expiry_lets_a_game_command_dispatch_during_roundtime()
    {
        // e.g. a stunned-recovery script doing `delay 1` / `put release spell`.
        using var h = new Harness(
            "delay 0.05\n" +
            "put release spell\n");

        h.Pump();                       // blocks on the delay
        Assert.Empty(h.Sent);

        h.InRt = true;
        Harness.LetTimerExpire();
        h.Pump();
        Assert.Equal(new[] { "release spell" }, h.Sent);
    }

    [Fact]
    public void Pause_expiry_stays_gated_until_roundtime_drains()
    {
        // Contrast case — pause's timer expires independently of RT, but the
        // next statement must not fire until RT clears.
        using var h = new Harness(
            "echo T:start\n" +
            "pause 0.05\n" +
            "echo T:after\n");

        h.Pump();
        Assert.Equal(new[] { "start" }, h.Echoed);

        h.InRt = true;
        Harness.LetTimerExpire();
        h.Pump(50);
        Assert.Equal(new[] { "start" }, h.Echoed);   // still gated

        h.InRt = false;
        h.Pump();
        Assert.Equal(new[] { "start", "after" }, h.Echoed);
    }

    [Fact]
    public void Bypass_window_closes_at_the_next_blocking_statement()
    {
        // G4: the resumed burst runs RT-free only until the script blocks
        // again — after that, RT gating applies as normal. Here the burst
        // ("a", "b") fires during RT, then the pause re-enters the gated
        // world and "c" must wait for RT to drain even though the pause
        // timer itself has long expired.
        using var h = new Harness(
            "delay 0.05\n" +
            "echo T:a\n" +
            "echo T:b\n" +
            "pause 0.05\n" +
            "echo T:c\n");

        h.Pump();                       // blocks on the delay
        h.InRt = true;
        Harness.LetTimerExpire();
        h.Pump();
        Assert.Equal(new[] { "a", "b" }, h.Echoed);

        Harness.LetTimerExpire();       // pause timer expires under RT
        h.Pump(50);
        Assert.Equal(new[] { "a", "b" }, h.Echoed);  // "c" still gated

        h.InRt = false;
        h.Pump();
        Assert.Equal(new[] { "a", "b", "c" }, h.Echoed);
    }
}
