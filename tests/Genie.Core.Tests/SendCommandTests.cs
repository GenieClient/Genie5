using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#send</c> vs <c>#put</c> dispatch (Genie 4 Command.cs <c>Send()</c> parity).
/// <c>#put</c> sends to the game immediately; <c>#send</c> is NOT an alias — it
/// routes through the roundtime-gated <see cref="CommandQueue"/> with an optional
/// leading numeric delay, and <c>#send clear</c> empties the queue. hunt.cmd
/// depends on this (e.g. <c>#send 5 $lastcommand</c> to retry after a web).
/// The delay parse is shared with the in-script <c>send</c> verb
/// (<c>ScriptEngine.ParseSendDelay</c>) so both forms behave identically.
/// </summary>
public class SendCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public SendCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_send_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieSendTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (FakeCommandHost host, CommandQueue queue, CommandEngine engine) NewEngine()
    {
        var host  = new FakeCommandHost();
        var queue = new CommandQueue();
        var engine = new CommandEngine(_config, queue, new EventQueue(), host);
        return (host, queue, engine);
    }

    [Fact]
    public void Send_with_leading_number_queues_delay_and_does_not_send_immediately()
    {
        var (host, queue, engine) = NewEngine();
        engine.ProcessInput("#send 5 fire");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(5.0, item.Delay);
        Assert.Equal("fire", item.Action);
        Assert.True(item.Restrictions.WaitForRoundtime);
        Assert.Empty(host.SendToGameCalls); // queued, not fired inline
    }

    [Fact]
    public void Send_without_number_queues_with_zero_delay()
    {
        var (host, queue, engine) = NewEngine();
        engine.ProcessInput("#send kneel");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.0, item.Delay);
        Assert.Equal("kneel", item.Action);
        Assert.True(item.Restrictions.WaitForRoundtime);
        Assert.Empty(host.SendToGameCalls);
    }

    [Fact]
    public void Send_multiword_body_is_queued_intact()
    {
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send release bond");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.0, item.Delay);
        Assert.Equal("release bond", item.Action);
    }

    [Fact]
    public void Send_clear_empties_the_queue()
    {
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send 3 stand");
        Assert.Single(queue.EventList);

        engine.ProcessInput("#send clear");
        Assert.Empty(queue.EventList);
    }

    [Fact]
    public void Send_clear_is_case_insensitive()
    {
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send 3 stand");
        engine.ProcessInput("#send CLEAR");
        Assert.Empty(queue.EventList);
    }

    [Fact]
    public void Send_negative_number_queues_eager_matching_the_in_script_verb()
    {
        // ParseSendDelay treats a leading '-N' as "send eagerly" (a past
        // fire-time), so #send stays consistent with the bare `send` verb.
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send -1 flee");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(-1.0, item.Delay);
        Assert.Equal("flee", item.Action);
    }

    [Fact]
    public void Send_number_without_trailing_space_stays_literal()
    {
        // A number must be space-delimited to count as a delay; "5fire" is a
        // command, not a 5s delay (shared boundary rule with the send verb).
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send 5fire");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.0, item.Delay);
        Assert.Equal("5fire", item.Action);
    }

    [Fact]
    public void Send_bare_dash_verb_queues_the_stripped_verb_eagerly()
    {
        // Community "fire eagerly" idiom: `send -cast` must reach the game as
        // `cast`, not the hyphen-prefixed form DR rejects. Shared ParseSendDelay
        // strip, exercised through the #send path.
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send -cast");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.0, item.Delay);
        Assert.Equal("cast", item.Action);
    }

    [Fact]
    public void Send_bare_dash_multiword_verb_is_stripped_intact()
    {
        var (_, queue, engine) = NewEngine();
        engine.ProcessInput("#send -touch my orb");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.0, item.Delay);
        Assert.Equal("touch my orb", item.Action);
    }

    [Fact]
    public void Put_leaves_a_leading_dash_verb_verbatim()
    {
        // Regression guard: the eager-dash strip is a send/#send behavior only.
        // #put stays immediate and verbatim (Genie 4 parity) — a `-cast` typed
        // via #put is the user's literal text.
        var (host, queue, engine) = NewEngine();
        engine.ProcessInput("#put -cast");

        var sent = Assert.Single(host.SendToGameCalls);
        Assert.Equal("-cast", sent);
        Assert.Empty(queue.EventList);
    }

    [Fact]
    public void Send_with_no_arguments_is_a_no_op()
    {
        var (host, queue, engine) = NewEngine();
        engine.ProcessInput("#send");

        Assert.Empty(queue.EventList);
        Assert.Empty(host.SendToGameCalls);
    }

    [Fact]
    public void Put_still_sends_immediately_and_does_not_queue()
    {
        // Regression guard on the send/put split: #put keeps its immediate,
        // no-delay-parse behavior — "5 foo" is sent verbatim.
        var (host, queue, engine) = NewEngine();
        engine.ProcessInput("#put 5 glance left");

        var sent = Assert.Single(host.SendToGameCalls);
        Assert.Equal("5 glance left", sent);
        Assert.Empty(queue.EventList);
    }

    /// <summary>
    /// Minimal <see cref="ICommandHost"/> double: records <see cref="SendToGame"/>
    /// text in order; everything else is a no-op.
    /// </summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> SendToGameCalls { get; } = new();

        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;

        public void Echo(string text) { }
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => SendToGameCalls.Add(text);
        public void RunScript(string text) { }
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void SetTraceLevelAll(int level) { }
        public IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
        public void SetGlobalVariable(string name, string value) => Globals[name] = value;
        public void RemoveGlobalVariable(string name) => Globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) { }
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) { }
        public void MapperGoto(string args) { }
        public void MapperCommand(string args) { }
        public void MapperReset() { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
