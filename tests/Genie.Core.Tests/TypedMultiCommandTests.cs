using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Multi-command chains end to end THROUGH THE TICK PUMP — the stage the
/// QuickSendTests stop short of (they assert the dash segment lands on the
/// CommandQueue, not that it fires). Two entry points, one per test: the shape
/// users type at the command bar (bare `put`, no leading '#') and the same
/// chain run from a real .cmd script. Both must end with the queued segment
/// actually SENT once its delay elapses outside roundtime — the full chain
/// behind the community report "put health;-0.8 encumbrance just puts out
/// health and that's it".
/// </summary>
public class TypedMultiCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public TypedMultiCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_typed_multi_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieTypedMultiTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class RecordingHost : ICommandHost
    {
        public List<string> Sent { get; } = new();
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
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) => Sent.Add(text);
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

    [Fact]
    public void Typed_bare_put_chain_fires_both_segments()
    {
        var host = new RecordingHost();
        var queue = new CommandQueue();
        var engine = new CommandEngine(_config, queue, new EventQueue(), host);

        engine.ProcessInput("put health;-0.1 encumbrance");

        // Head goes out immediately (literal "put health" — the game's put
        // passthrough runs `health`).
        Assert.Equal(new[] { "put health" }, host.Sent);

        // Dash segment is queued with its delay, RT-gated.
        var item = Assert.Single(queue.EventList);
        Assert.Equal("encumbrance", item.Action);
        Assert.Equal(0.1, item.Delay);

        // Before the delay elapses a tick must NOT fire it.
        engine.Tick(inRoundtime: false);
        Assert.Equal(new[] { "put health" }, host.Sent);

        // After the delay elapses, a tick outside roundtime must fire it.
        System.Threading.Thread.Sleep(250);
        engine.Tick(inRoundtime: false);
        Assert.Equal(new[] { "put health", "encumbrance" }, host.Sent);
    }

    [Fact]
    public void Script_put_chain_fires_both_segments_through_the_tick_pump()
    {
        // Same chain, run from a real .cmd script: ScriptEngine.HandleSendPut
        // strips `put `, sends the head, rewrites the dash segment to its
        // `#send 0.1 encumbrance` form, and forwards it (via the script tick's
        // PendingSends drain) to the host CommandEngine, which queues it on
        // the same RT-gated CommandQueue the typed path uses. The app pumps
        // BOTH engines on one heartbeat (OnScriptHeartbeat: Scripts.Tick then
        // Commands.Tick) — mirrored here by the pump loop.
        var host = new RecordingHost();
        var queue = new CommandQueue();
        var commandEngine = new CommandEngine(_config, queue, new EventQueue(), host);

        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "multitest.cmd"), "put health;-0.1 encumbrance\n");

        var scripts = new ScriptEngine(dir, new TypeAheadSession(),
                                       sendCommand: host.Sent.Add,
                                       echo: _ => { },
                                       handleHashCmd: s => commandEngine.ProcessInput(s));
        Assert.True(scripts.TryStart("multitest", new List<string>()));

        // Pump the script until the head is sent and the dash segment has
        // drained through PendingSends onto the host CommandQueue.
        for (int i = 0; i < 10; i++) scripts.Tick();
        Assert.Equal(new[] { "health" }, host.Sent);
        var item = Assert.Single(queue.EventList);
        Assert.Equal("encumbrance", item.Action);
        Assert.Equal(0.1, item.Delay);

        // Before the delay elapses a command tick must NOT fire it.
        commandEngine.Tick(inRoundtime: false);
        Assert.Equal(new[] { "health" }, host.Sent);

        // Roundtime holds it even after the delay …
        System.Threading.Thread.Sleep(250);
        commandEngine.Tick(inRoundtime: true);
        Assert.Equal(new[] { "health" }, host.Sent);

        // … and it fires on the first tick after RT clears.
        commandEngine.Tick(inRoundtime: false);
        Assert.Equal(new[] { "health", "encumbrance" }, host.Sent);
    }
}
