using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Extensions.Builtin;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Smoke 2026-08-03 finding #9: uber.cmd sends <c>put /track clear</c> at hunt
/// start (uber.cmd:2127) — the Genie 4 EXPTracker plugin's gain-reset command.
/// G4 ran every outbound send through the plugins' ParseInput at the send sink
/// (FormMain ClassCommand_SendText:4122), so the plugin consumed it; in Genie 5
/// the line leaked to DR and bounced with "Please rephrase that command."
/// The builtin Experience extension now claims the whole /track namespace via
/// OnSlashCommand — which the script engine already offers every game-bound
/// <c>/…</c> segment (ScriptEngine.TrySlashCommand).
/// </summary>
public class TrackSlashCommandTests
{
    private static (List<string> sent, List<string> echoed) RunScript(string body)
    {
        var sent   = new List<string>();
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_track_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c),
                                          echo: l => echoed.Add(l));
            engine.Extensions.Register(new ExperienceExtension());
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 100; i++) engine.Tick();
            return (sent, echoed);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Put_track_clear_is_claimed_client_side_and_never_sent()
    {
        var (sent, echoed) = RunScript("put /track clear\necho done\n");

        Assert.DoesNotContain(sent, c => c.Contains("/track"));
        Assert.Contains(echoed, l => l.Contains("gain tracking reset"));
        Assert.Contains("done", echoed);
    }

    [Fact]
    public void Unknown_track_subcommand_is_still_claimed_with_usage()
    {
        // G4's plugin owned the whole namespace — an unrecognized subcommand
        // must get usage, not leak to the game and bounce.
        var (sent, echoed) = RunScript("put /track bogus\necho done\n");

        Assert.DoesNotContain(sent, c => c.Contains("/track"));
        Assert.Contains(echoed, l => l.Contains("/track clear"));
    }

    [Fact]
    public void Trackreset_one_word_variant_is_claimed_and_resets()
    {
        // Typed from G4 muscle memory during the 2026-08-04 live smoke —
        // "/trackreset" (no space) bounced off DR with "Please rephrase".
        var (sent, echoed) = RunScript("put /trackreset\necho done\n");

        Assert.DoesNotContain(sent, c => c.Contains("/trackreset"));
        Assert.Contains(echoed, l => l.Contains("gain tracking reset"));
    }

    [Fact]
    public void Similar_but_different_command_is_not_claimed()
    {
        // "/tracker" must not be swallowed by the /track prefix check — it's
        // not ours, so it follows the normal unclaimed-slash fall-through.
        var (sent, _) = RunScript("put /tracker on\necho done\n");
        Assert.Contains(sent, c => c.Contains("/tracker on"));
    }

    [Fact]
    public void Queued_send_slash_command_is_claimed_at_the_command_engine()
    {
        // The third origin: `#send /track clear` rides the CommandQueue and
        // re-enters CommandEngine.ProcessInput on the pump tick — the
        // ClientSlashCommand hook must claim it before SendToGame (G4 ran the
        // plugin ParseInput at the send sink, covering this path too).
        var root = Path.Combine(Path.GetTempPath(), "gc_trackq_" + Guid.NewGuid().ToString("N"));
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieTrackTest", root);
        lds.UseExplicitRoot(root);
        try
        {
            var config = new Genie.Core.Config.GenieConfig(lds);
            var host   = new FakeCommandHost();
            var queue  = new Genie.Core.Queue.CommandQueue();
            var engine = new Genie.Core.Commanding.CommandEngine(
                             config, queue, new Genie.Core.Queue.EventQueue(), host);
            var claimed = new List<string>();
            engine.ClientSlashCommand = s => { claimed.Add(s); return true; };

            engine.ProcessInput("#send /track clear");
            engine.Tick();

            Assert.Empty(host.SendToGameCalls);
            Assert.Equal("/track clear", Assert.Single(claimed));
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
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
