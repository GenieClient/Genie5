using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Genie.Core.Connection;
using Genie.Plugins;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="IGeniePlugin.OnCommandSent"/> — the observe-only hook whose
/// contract reads "a command was actually sent to the game (user, alias, script,
/// or link click) … after OnInput transforms".
///
/// <para>Found dead while reviewing public #325:
/// <c>PluginManager.DispatchCommand</c> had existed since the plugin host landed
/// but had no caller anywhere in the tree, so <c>OnCommandSent</c> had never once
/// fired for any plugin. It is now raised from the
/// <c>GameConnection.SentCommandStream</c> subscription — the single choke point
/// every outbound command passes through, published after the flush in wire
/// order, whatever the source.</para>
/// </summary>
public class PluginCommandSentDispatchTests
{
    private sealed class FakePlugin : IGeniePlugin
    {
        public string Id             => "test.commandsent";
        public string Name           => "Command Sent Test";
        public string Version        => "1.0";
        public string Author         => "test";
        public string Description    => "records OnCommandSent calls";
        public string MinHostVersion => "";
        public bool   Enabled { get; set; } = true;

        public readonly List<string> Sent = new();
        public bool ThrowOnCommandSent;

        public void Initialize(IPluginHost host) { }
        public void Shutdown() { }

        public string? OnGameText(string text, string stream) => text;
        public string? OnInput(string input) => input;
        public void OnXml(string xml) { }
        public void OnCommandSent(string command)
        {
            if (ThrowOnCommandSent) throw new InvalidOperationException("boom");
            lock (Sent) Sent.Add(command);
        }
        public void OnPrompt() { }
        public void OnVariableChanged(string name, string value) { }
    }

    private sealed class QuietPlugin : IGeniePlugin
    {
        public string Id             => "test.commandsent.quiet";
        public string Name           => "Quiet";
        public string Version        => "1.0";
        public string Author         => "test";
        public string Description    => "second in the chain";
        public string MinHostVersion => "";
        public bool   Enabled { get; set; } = true;

        public readonly List<string> Sent = new();

        public void Initialize(IPluginHost host) { }
        public void Shutdown() { }
        public string? OnGameText(string text, string stream) => text;
        public string? OnInput(string input) => input;
        public void OnXml(string xml) { }
        public void OnCommandSent(string command) { lock (Sent) Sent.Add(command); }
        public void OnPrompt() { }
        public void OnVariableChanged(string name, string value) { }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Connect a real core to a DevReplay server, run <paramref name="act"/>,
    /// and return what the plugins observed. This is an end-to-end test on purpose:
    /// the bug being guarded against is a missing CALLER, which a unit test on
    /// PluginManager alone would not have caught.</summary>
    private static async Task<(FakePlugin a, QuietPlugin b)> RunConnectedAsync(
        Func<GenieCore, FakePlugin, QuietPlugin, Task> act,
        bool aEnabled = true, bool aThrows = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_cmdsent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var replay = Path.Combine(dir, "replay.xml");
        // Enough for the connect to reach "server ready": GenieCore sends its
        // initial `look` on <settingsInfo/>.
        File.WriteAllText(replay, "<settingsInfo/>\n");

        var port = FreeTcpPort();
        await using var server = new DevReplayServer(replay, port: port, speed: 0.0,
                                                     hangAfterStream: true);
        server.Start();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir,
                                                 gameThreadOverride: false);
            var a = new FakePlugin { Enabled = aEnabled, ThrowOnCommandSent = aThrows };
            var b = new QuietPlugin();
            Assert.True(core.Plugins.Register(a));
            Assert.True(core.Plugins.Register(b));

            await core.ConnectAsync(new ConnectionConfig
            {
                Mode          = ConnectionMode.DevReplay,
                LichProxyHost = "127.0.0.1",
                LichProxyPort = port,
            });

            await act(core, a, b);
            return (a, b);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>Poll until <paramref name="done"/> or the timeout — the send sink
    /// publishes after the socket flush, so the assertion can't be synchronous.</summary>
    private static async Task WaitFor(Func<bool> done, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(15);
    }

    private static bool Saw(List<string> log, string cmd)
    {
        lock (log) return log.Contains(cmd);
    }

    [Fact]
    public async Task Typed_command_reaches_plugin_OnCommandSent()
    {
        var (a, _) = await RunConnectedAsync(async (core, a, _) =>
        {
            core.ProcessInput("bob");
            await WaitFor(() => Saw(a.Sent, "bob"));
        });

        Assert.True(Saw(a.Sent, "bob"));
    }

    [Fact]
    public async Task Script_send_reaches_plugin_OnCommandSent()
    {
        // The script path uses its own send delegate, not the command engine —
        // it must still reach the hook, since the contract names "script".
        var (a, _) = await RunConnectedAsync(async (core, a, _) =>
        {
            var scriptsDir = core.Config.ScriptDir;
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, "cs.cmd"), "put waggle\n");
            core.Scripts.TryStart("cs", new List<string>());
            await WaitFor(() => Saw(a.Sent, "waggle"));
        });

        Assert.True(Saw(a.Sent, "waggle"));
    }

    [Fact]
    public async Task Every_enabled_plugin_observes_the_send()
    {
        var (a, b) = await RunConnectedAsync(async (core, a, b) =>
        {
            core.ProcessInput("bob");
            await WaitFor(() => Saw(a.Sent, "bob") && Saw(b.Sent, "bob"));
        });

        Assert.True(Saw(a.Sent, "bob"));
        Assert.True(Saw(b.Sent, "bob"));
    }

    [Fact]
    public async Task Disabled_plugin_does_not_observe_the_send()
    {
        var (a, b) = await RunConnectedAsync(async (core, _, b) =>
        {
            core.ProcessInput("bob");
            await WaitFor(() => Saw(b.Sent, "bob"));
        }, aEnabled: false);

        Assert.True(Saw(b.Sent, "bob"));    // the chain continues
        lock (a.Sent) Assert.Empty(a.Sent);
    }

    [Fact]
    public async Task Throwing_plugin_does_not_break_the_send_relay()
    {
        // PluginManager.Each swallows per-plugin failures; a bad hook must not
        // take down the relay for the plugins behind it.
        var (_, b) = await RunConnectedAsync(async (core, _, b) =>
        {
            core.ProcessInput("bob");
            await WaitFor(() => Saw(b.Sent, "bob"));
        }, aThrows: true);

        Assert.True(Saw(b.Sent, "bob"));
    }
}
