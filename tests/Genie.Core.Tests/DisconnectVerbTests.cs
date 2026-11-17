using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Genie.Core.Connection;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #419: a script's <c>put quit</c> was not recognised as a deliberate
/// disconnect, so the drop that followed auto-reconnected. Genie 4 checked every
/// send in <c>Game.SendText</c> with a <c>qui</c>/<c>exi</c> prefix rule; Genie 5
/// now checks every command on <see cref="GenieCore.SentCommands"/>, which carries
/// typed and scripted sends alike.
/// </summary>
public class DisconnectVerbTests
{
    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    [InlineData("QUIT")]
    [InlineData("Exit")]
    [InlineData("qui")]        // Genie 4 matches the abbreviation
    [InlineData("exi")]
    [InlineData("  quit")]
    [InlineData("quit now")]
    public void Quit_and_exit_and_their_abbreviations_are_disconnect_verbs(string cmd)
        => Assert.True(GenieCore.IsDisconnectVerb(cmd));

    [Theory]
    [InlineData("")]
    [InlineData("look")]
    [InlineData("qu")]
    [InlineData("ex")]
    [InlineData("say quit")]
    [InlineData("requit")]
    [InlineData("#echo quit")]
    public void Other_commands_are_not(string cmd)
        => Assert.False(GenieCore.IsDisconnectVerb(cmd));

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitFor(Func<bool> done, int ms = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(15);
    }

    /// <summary>Connect a real core to a DevReplay server and record
    /// <see cref="GenieCore.SentCommands"/> while <paramref name="act"/> runs.</summary>
    private static async Task<List<string>> SentWhileAsync(Func<GenieCore, List<string>, Task> act)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_quit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var replay = Path.Combine(dir, "replay.xml");
        File.WriteAllText(replay, "<settingsInfo/>\n");

        var port = FreeTcpPort();
        await using var server = new DevReplayServer(replay, port: port, speed: 0.0, hangAfterStream: true);
        server.Start();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            var sent = new List<string>();
            using var sub = core.SentCommands.Subscribe(c => { lock (sent) sent.Add(c); });

            await core.ConnectAsync(new ConnectionConfig
            {
                Mode          = ConnectionMode.DevReplay,
                LichProxyHost = "127.0.0.1",
                LichProxyPort = port,
            });

            await act(core, sent);
            lock (sent) return new List<string>(sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private static bool SawQuit(List<string> sent)
    {
        lock (sent) return sent.Exists(GenieCore.IsDisconnectVerb);
    }

    [Fact]
    public async Task A_script_put_quit_is_seen_as_a_disconnect_verb()
    {
        var sent = await SentWhileAsync(async (core, sent) =>
        {
            Directory.CreateDirectory(core.Config.ScriptDir);
            File.WriteAllText(Path.Combine(core.Config.ScriptDir, "leave.cmd"), "put quit\n");
            core.Scripts.TryStart("leave", new List<string>());
            await WaitFor(() => SawQuit(sent));
        });

        Assert.Contains("quit", sent);
    }

    [Fact]
    public async Task A_typed_quit_is_still_seen()
    {
        var sent = await SentWhileAsync(async (core, sent) =>
        {
            core.ProcessInput("quit");
            await WaitFor(() => SawQuit(sent));
        });

        Assert.Contains("quit", sent);
    }
}
