using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Extensions;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Scripts must be able to drive extension console commands (/calc, /sort, /tt, …)
/// the way typed input can: a `put /cmd`, a semicolon-split tail, or a bare `/cmd`
/// line is offered to OnSlashCommand first, and a claimed command never reaches the
/// game socket. Unclaimed slashes keep the typed-input fall-through and go to the
/// game verbatim.
/// </summary>
public class ScriptSlashCommandTests
{
    private sealed class ProbeExtension : IGameExtension
    {
        public string Name        => "Probe";
        public string Version     => "1.0";
        public string Description => "test probe";
        public bool   Enabled     { get; set; } = true;
        public List<string> Claimed { get; } = new();

        public void Initialize(IExtensionHost host) { }
        public void OnGameLine(string line) { }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void Shutdown() { }

        public bool OnSlashCommand(string input)
        {
            if (!input.StartsWith("/probe", StringComparison.OrdinalIgnoreCase)) return false;
            Claimed.Add(input);
            return true;
        }
    }

    private static (ProbeExtension probe, List<string> sent) RunFixture(string body)
    {
        var sent = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_slashtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "slashtest.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            var probe = new ProbeExtension();
            engine.Extensions.Register(probe);
            engine.TryStart("slashtest", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return (probe, sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Put_slash_command_is_claimed_and_not_sent_to_game()
    {
        var (probe, sent) = RunFixture("put /probe weapon\n");

        Assert.Contains("/probe weapon", probe.Claimed);
        Assert.Empty(sent);
    }

    [Fact]
    public void Bare_slash_line_is_claimed_and_not_sent_to_game()
    {
        var (probe, sent) = RunFixture("/probe bare\n");

        Assert.Contains("/probe bare", probe.Claimed);
        Assert.Empty(sent);
    }

    [Fact]
    public void Semicolon_tail_slash_command_is_claimed()
    {
        // First segment claimed inline; the tail drains through PendingSends,
        // which must offer it to the extensions too. Neither reaches the game,
        // so no _inFlight is consumed and the tail isn't throttled.
        var (probe, sent) = RunFixture("put /probe first;/probe second\n");

        Assert.Contains("/probe first",  probe.Claimed);
        Assert.Contains("/probe second", probe.Claimed);
        Assert.Empty(sent);
    }

    [Fact]
    public void Send_slash_command_is_claimed_and_not_sent_to_game()
    {
        // `send` shares the put dispatch path — a slash first segment is
        // offered to the extensions the same way.
        var (probe, sent) = RunFixture("send /probe weapon\n");

        Assert.Contains("/probe weapon", probe.Claimed);
        Assert.Empty(sent);
    }

    [Fact]
    public void Delayed_send_slash_command_is_claimed()
    {
        // `send <delay> /cmd` takes the other route: the segment is enqueued
        // into PendingSends and fires from the drain once the delay elapses,
        // which must offer it to the extensions too.
        var sent = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_slashtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "slashtest.cmd"), "send 0.05 /probe delayed\n");
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            var probe = new ProbeExtension();
            engine.Extensions.Register(probe);
            engine.TryStart("slashtest", new List<string>());
            for (int i = 0; i < 60 && probe.Claimed.Count == 0; i++)
            {
                engine.Tick();
                System.Threading.Thread.Sleep(5);   // let the 0.05s send gate elapse
            }

            Assert.Contains("/probe delayed", probe.Claimed);
            Assert.Empty(sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Unclaimed_slash_command_is_held_back_from_the_game()
    {
        // Typed-input parity — and the earlier version of this test had that
        // parity backwards. GenieCore.SendToGame gates the socket write on
        // Config.MyCommandChar, so a typed '/unknown thing' is echoed and
        // dropped; only the script path bypassed that sink and leaked the line
        // to DragonRealms ("Please rephrase that command.").
        var (probe, sent) = RunFixture("put /unknown thing\n");

        Assert.Empty(probe.Claimed);
        Assert.Empty(sent);
    }

    [Fact]
    public void Claimed_slash_does_not_consume_typeahead_budget()
    {
        // A claimed slash must not bump _inFlight — otherwise the following
        // game-bound send would wait forever for a prompt that never comes.
        var (probe, sent) = RunFixture("put /probe a\nput look\n");

        Assert.Contains("/probe a", probe.Claimed);
        Assert.Contains("look", sent);
    }
}
